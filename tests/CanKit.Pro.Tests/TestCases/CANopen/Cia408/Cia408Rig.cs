using System.Collections.Generic;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// A CiA 408 device on the virtual bus and the master beside it (<see cref="CanOpenProfileRig"/>):
/// the device is a <see cref="CanOpenNode"/> opened with <see cref="Cia408Device.AddTo"/>, and every
/// <see cref="Cia408Device.StateChanged"/> it raises is recorded in <see cref="Changes"/>.
/// </summary>
/// <remarks>
/// <para>The default PDOs (<see cref="Cia408Configuration.DefaultPdos"/>): RPDO 1
/// (<see cref="CanOpenProfileRig.Rpdo1"/>) carries the control word, TPDO 1
/// (<see cref="CanOpenProfileRig.Tpdo1"/>) the status word, of the pair the configuration's width
/// uses.</para>
/// <para>The recorder subscribes before the node opens, so the start of the state machine (D0, D1)
/// is recorded too. The barriers are those of <see cref="CanOpenProfileRig"/>.</para>
/// </remarks>
internal sealed class Cia408Rig : CanOpenProfileRig
{
    private readonly Recorder _recorder;

    /// <summary>A 16-bit valve in spool position closed loop: <c>6040h</c>/<c>6041h</c> in the default
    /// PDOs, <c>604Fh</c> = 0.</summary>
    internal static Cia408Configuration Valve => new()
    {
        Resolution = Cia408Resolution.Valve16,
        ControlModes = new[] { Cia408ControlMode.SpoolPositionClosedLoop },
    };

    /// <summary>A drive in velocity control: <c>6044h</c>/<c>6045h</c> in the default PDOs,
    /// <c>604Fh</c> = 1.</summary>
    internal static Cia408Configuration Drive => new()
    {
        Resolution = Cia408Resolution.Valve32Drive32,
        ControlModes = new[] { Cia408ControlMode.VelocityControlAxis },
    };

    /// <summary>Opens a device for <paramref name="configuration"/>; <paramref name="options"/> are
    /// what the test starts from (the defaults when null).</summary>
    internal Cia408Rig(Cia408Configuration configuration, CanOpenNodeOptions? options = null)
        : this(new Cia408Device(configuration), options)
    {
    }

    /// <summary>Opens <paramref name="device"/>, which the test may have subscribed to already.</summary>
    internal Cia408Rig(Cia408Device device, CanOpenNodeOptions? options = null)
        : this(device, new Recorder(device), options)
    {
    }

    // The recorder is built in the chained call's argument list, so it is on the device before the
    // base constructor opens the node.
    private Cia408Rig(Cia408Device device, Recorder recorder, CanOpenNodeOptions? options)
        : base("cia408", Cia408Device.AddTo(options ?? new CanOpenNodeOptions(), device), description: null)
    {
        Device = device;
        _recorder = recorder;
    }

    internal Cia408Device Device { get; }

    /// <summary>Every <see cref="Cia408Device.StateChanged"/> so far, in the order raised.</summary>
    internal IReadOnlyList<Cia408StateChangedEventArgs> Changes => _recorder.Snapshot();

    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<Cia408StateChangedEventArgs> _changes = new();

        internal Recorder(Cia408Device device)
            => device.StateChanged += (_, e) =>
            {
                lock (_gate) _changes.Add(e);
            };

        internal Cia408StateChangedEventArgs[] Snapshot()
        {
            lock (_gate) return _changes.ToArray();
        }
    }
}
