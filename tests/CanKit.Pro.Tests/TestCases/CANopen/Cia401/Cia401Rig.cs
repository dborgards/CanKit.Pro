using System.Collections.Generic;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// A CiA 401 device on the virtual bus and the master beside it (<see cref="CanOpenProfileRig"/>):
/// the device is a <see cref="CanOpenNode"/> opened with <see cref="Cia401Device.AddTo"/>.
/// </summary>
/// <remarks>
/// <para>The default PDOs of the device (CiA 401-1 §6.2.5 to §6.2.8): TPDO 1
/// (<see cref="CanOpenProfileRig.Tpdo1"/>) carries the digital inputs, RPDO 1 the digital outputs,
/// TPDO 2 the analog inputs 1 to 4, valid by default with transmission type 255 (Table 18), and
/// RPDO 2 the analog outputs 1 to 4, enabled by default.</para>
/// <para>A tool node with a bound description of the device joins the same bus in
/// <see cref="Cia401ToolRig"/>. The barriers are those of <see cref="CanOpenProfileRig"/>.</para>
/// </remarks>
internal sealed class Cia401Rig : CanOpenProfileRig
{
    /// <summary>Opens the device for <paramref name="configuration"/>. <paramref name="options"/> are
    /// what the test starts from (the defaults when null); the rig hands them to
    /// <see cref="Cia401Device.AddTo"/>.</summary>
    internal Cia401Rig(Cia401Configuration configuration, CanOpenNodeOptions? options = null)
        : this(new[] { configuration }, options)
    {
    }

    /// <summary>Opens the device for <paramref name="configuration"/> with the plant model
    /// <paramref name="process"/>.</summary>
    internal Cia401Rig(Cia401Configuration configuration, ICia401Process process, CanOpenNodeOptions? options = null)
        : this(new[] { configuration }, options, process)
    {
    }

    /// <summary>Opens one node with a device for each of <paramref name="configurations"/>, attached
    /// in that order: several logical devices of one node.</summary>
    internal Cia401Rig(IReadOnlyList<Cia401Configuration> configurations, CanOpenNodeOptions? options = null,
        ICia401Process? process = null)
        : this(DevicesFor(configurations, process), description: null, options)
    {
    }

    /// <summary>Opens a node from <paramref name="description"/> (none when null) with
    /// <paramref name="device"/> attached: a device built with <see cref="Cia401Device.FromDescription"/>,
    /// or one built from a configuration, on a described node. An open that fails throws here, with the
    /// rig's channels closed.</summary>
    internal Cia401Rig(Cia401Device device, CanOpenDeviceDescription? description, CanOpenNodeOptions? options = null)
        : this(new[] { device }, description, options)
    {
    }

    /// <summary>Opens a node from <paramref name="description"/> (none when null) with
    /// <paramref name="devices"/> attached in that order, built either way.</summary>
    internal Cia401Rig(IReadOnlyList<Cia401Device> devices, CanOpenDeviceDescription? description, CanOpenNodeOptions? options)
        : base("cia401", Compose(devices, options), description)
    {
        Devices = devices;
    }

    private static IReadOnlyList<Cia401Device> DevicesFor(IReadOnlyList<Cia401Configuration> configurations, ICia401Process? process)
    {
        var devices = new List<Cia401Device>();
        foreach (var configuration in configurations)
            devices.Add(new Cia401Device(configuration, devices.Count == 0 ? process : null));
        return devices;
    }

    private static CanOpenNodeOptions Compose(IReadOnlyList<Cia401Device> devices, CanOpenNodeOptions? options)
    {
        var composed = options ?? new CanOpenNodeOptions();
        foreach (var device in devices) composed = Cia401Device.AddTo(composed, device);
        return composed;
    }

    /// <summary>The first device, the only one in most tests.</summary>
    internal Cia401Device Device => Devices[0];

    internal IReadOnlyList<Cia401Device> Devices { get; }

    /// <summary><see cref="CanOpenProfileRig.StartAsync"/>, then waits for the TPDO on
    /// <paramref name="cobId"/> (TPDO 1 unless given) that entering Operational sends (CiA 401-1
    /// §6.1), so that what a test counts afterwards is its own.</summary>
    internal async Task StartAndTakeFirstTpdoAsync(uint cobId = Tpdo1)
    {
        int before = Count(cobId);
        await StartAsync();
        await WaitForCountAsync(cobId, before + 1);
    }
}
