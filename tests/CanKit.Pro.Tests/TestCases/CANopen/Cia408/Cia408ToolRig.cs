using System;
using System.Collections.Generic;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// A CiA 408 device and a tool beside it: the <see cref="Cia408Rig"/> (the device node and the raw
/// master channel, which sends NMT and records every frame, the tool's SDO requests among them) and
/// a second <see cref="CanOpenNode"/>, the tool, on a third channel of the same virtual bus. The tool
/// reads the device the way a real tool does, through its SDO client and so through the peer SDO
/// gate: a description of the device is bound for it unless the test says otherwise.
/// </summary>
/// <remarks>
/// <para>The description is <c>Fixtures/cia408-pq-valve.eds</c>, which describes the device of
/// <see cref="FixtureConfiguration"/>. <c>Cia408PeerTests.The_Fixture_Describes_What_The_Device_Declares</c>
/// holds the file to the catalogue and to the device.</para>
/// <para>Both nodes run on a <see cref="ManualTimeSource"/> that no test advances for the tool, so its
/// SDO timeout never fires: a transfer ends with the device's answer or not at all, and the timeouts
/// of the callers are hang guards only.</para>
/// </remarks>
internal sealed class Cia408ToolRig : IDisposable
{
    internal const byte ToolId = 0x01;

    private readonly ICanBus _toolBus;

    /// <summary>The device of the fixture description: a pQ valve with 16-bit parameters, control
    /// mode 5 only, in logical device 0.</summary>
    internal static Cia408Configuration FixtureConfiguration => new()
    {
        Resolution = Cia408Resolution.Valve16,
        ControlModes = new[] { Cia408ControlMode.PQControl },
    };

    /// <summary><c>Fixtures/cia408-pq-valve.eds</c>, parsed.</summary>
    internal static CanOpenDeviceDescription FixtureDescription
        => CanOpenDeviceDescription.ParseEds(CanOpenFixtures.Text("cia408-pq-valve.eds"));

    /// <summary>The device of <see cref="FixtureConfiguration"/>, with the fixture description bound
    /// for it in the tool unless <paramref name="bindDescription"/> is false.</summary>
    internal Cia408ToolRig(bool bindDescription = true)
        : this(new[] { new Cia408Device(FixtureConfiguration) }, bindDescription ? FixtureDescription : null)
    {
    }

    /// <summary>One device node with <paramref name="devices"/> attached in order, and the tool with
    /// <paramref name="description"/> bound for it (nothing bound when null).</summary>
    internal Cia408ToolRig(IReadOnlyList<Cia408Device> devices, CanOpenDeviceDescription? description)
    {
        Rig = new Cia408Rig(devices, description: null);
        try
        {
            _toolBus = VirtualAdapterFixture.Open(Rig.Session, 2);
            Tool = new CanOpenNode(new CanBusService(_toolBus), ToolId, new CanOpenNodeOptions(), ownsService: true,
                new ManualTimeSource());
        }
        catch
        {
            _toolBus?.Dispose();
            Rig.Dispose();
            throw;
        }

        Description = description;
        if (description is not null) Tool.BindPeerDeviceDescription(CanOpenProfileRig.DeviceId, description);
    }

    /// <summary>The device node and the raw master.</summary>
    internal Cia408Rig Rig { get; }

    /// <summary>The tool node.</summary>
    internal CanOpenNode Tool { get; }

    /// <summary>The description bound for the device in the tool, or null.</summary>
    internal CanOpenDeviceDescription? Description { get; }

    /// <summary>The clock the peer's pauses run on (<see cref="Cia408PeerOptions.Delay"/>): each pause
    /// advances it by its length and completes at once, so no test waits on a wall clock.</summary>
    internal ManualTimeSource PauseClock { get; } = new();

    /// <summary>Every pause a peer of this rig took, in order.</summary>
    internal IReadOnlyList<TimeSpan> Pauses
    {
        get
        {
            lock (_pauses) return _pauses.ToArray();
        }
    }

    /// <summary>Runs inside each pause, after the clock was advanced: what the device does while the
    /// tool waits.</summary>
    internal Action? OnPause { get; set; }

    private readonly List<TimeSpan> _pauses = new();

    /// <summary>Options with <paramref name="reads"/> and <paramref name="pause"/> (the defaults when
    /// not given) whose delay is <see cref="PauseClock"/>.</summary>
    internal Cia408PeerOptions Options(int reads = Cia408PeerOptions.DefaultStatusReads, TimeSpan? pause = null) => new()
    {
        StatusReads = reads,
        StatusPause = pause ?? Cia408PeerOptions.DefaultStatusPause,
        Delay = (length, _) =>
        {
            lock (_pauses) _pauses.Add(length);
            PauseClock.Advance(length);
            OnPause?.Invoke();
            return System.Threading.Tasks.Task.CompletedTask;
        },
    };

    /// <summary>The peer of logical device <paramref name="logicalDevice"/> of the device, made by
    /// the descriptor for the device type <paramref name="configuration"/> gives it (the fixture's
    /// when null), with <paramref name="options"/> (<see cref="Options"/> with the defaults when null).
    /// Performs no bus access.</summary>
    internal Cia408Peer Peer(int logicalDevice = 0, Cia408Configuration? configuration = null, Cia408PeerOptions? options = null)
    {
        var type = (configuration ?? FixtureConfiguration).DeviceType;
        return (Cia408Peer)new Cia408Profile(options ?? Options())
            .CreatePeer(Tool, CanOpenProfileRig.DeviceId, new CanOpenLogicalDevice(logicalDevice, type));
    }

    public void Dispose()
    {
        Tool.Dispose();
        _toolBus.Dispose();
        Rig.Dispose();
    }
}
