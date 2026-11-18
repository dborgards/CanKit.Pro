using System;
using System.Collections.Generic;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// A CiA 401 device and a tool beside it: the <see cref="Cia401Rig"/> (the device node and the raw
/// master channel, which sends NMT and records every frame) and a second <see cref="CanOpenNode"/>,
/// the tool, on a third channel of the same virtual bus. The tool reads the device the way a real
/// tool does, through its SDO client and so through the peer SDO gate: a description of the device
/// is bound for it unless the test says otherwise.
/// </summary>
/// <remarks>
/// <para>The description is <c>Fixtures/cia401.eds</c>, which describes the device of
/// <see cref="FixtureConfiguration"/>: the core has no way to build a description from a node's own
/// dictionary, and the gate passes only the pairs a description declares.
/// <c>Cia401PeerTests.The_Fixture_Describes_What_The_Device_Declares</c> holds the file to the
/// catalogue and to the device.</para>
/// <para>Both nodes run on a <see cref="ManualTimeSource"/> that no test advances for the tool, so its
/// SDO timeout never fires: a transfer ends with the device's answer or not at all, and the timeouts
/// of the callers are hang guards only. The barriers of <see cref="Cia401Rig"/> apply unchanged.</para>
/// </remarks>
internal sealed class Cia401ToolRig : IDisposable
{
    internal const byte ToolId = 0x01;

    private readonly ICanBus _toolBus;

    /// <summary>The device of the fixture description: 16 digital inputs, 16 digital outputs, 2 analog
    /// inputs and 2 analog outputs in logical device 0.</summary>
    internal static Cia401Configuration FixtureConfiguration => new()
    {
        DigitalInputs = 16,
        DigitalOutputs = 16,
        AnalogInputs = 2,
        AnalogOutputs = 2,
    };

    /// <summary><c>Fixtures/cia401.eds</c>, parsed.</summary>
    internal static CanOpenDeviceDescription FixtureDescription
        => CanOpenDeviceDescription.ParseEds(CanOpenFixtures.Text("cia401.eds"));

    /// <summary>The device of <see cref="FixtureConfiguration"/>, with the fixture description bound
    /// for it in the tool unless <paramref name="bindDescription"/> is false.</summary>
    internal Cia401ToolRig(CanOpenNodeOptions? options = null, bool bindDescription = true)
        : this(new[] { FixtureConfiguration }, bindDescription ? FixtureDescription : null, options)
    {
    }

    /// <summary>One device node with a device for each of <paramref name="configurations"/>, and the
    /// tool with <paramref name="description"/> bound for it (nothing bound when null).</summary>
    internal Cia401ToolRig(IReadOnlyList<Cia401Configuration> configurations, CanOpenDeviceDescription? description,
        CanOpenNodeOptions? options = null)
    {
        Rig = new Cia401Rig(configurations, options);
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
        if (description is not null) Tool.BindPeerDeviceDescription(Cia401Rig.DeviceId, description);
    }

    /// <summary>The device node and the raw master.</summary>
    internal Cia401Rig Rig { get; }

    /// <summary>The tool node.</summary>
    internal CanOpenNode Tool { get; }

    /// <summary>The description bound for the device in the tool, or null.</summary>
    internal CanOpenDeviceDescription? Description { get; }

    /// <summary>The peer of logical device <paramref name="logicalDevice"/> of the device, made by
    /// the descriptor for the device type <paramref name="configuration"/> gives it (the first device
    /// of the rig when null). Performs no bus access.</summary>
    internal Cia401Peer Peer(int logicalDevice = 0, Cia401Configuration? configuration = null)
    {
        var type = (configuration ?? FixtureConfiguration).DeviceType;
        return (Cia401Peer)new Cia401Profile().CreatePeer(Tool, Cia401Rig.DeviceId, new CanOpenLogicalDevice(logicalDevice, type));
    }

    public void Dispose()
    {
        Tool.Dispose();
        _toolBus.Dispose();
        Rig.Dispose();
    }
}
