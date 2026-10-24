using System;
using System.IO;
using System.Threading;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;

namespace CanKit.Pro.CanOpenConformance;

/// <summary>
/// A CANopen device on a second virtual bus of the same session, so that every check in this tool
/// can be run with no hardware at all.
/// </summary>
/// <remarks>
/// <para>
/// What this is for: a conformance run against real hardware is only worth trusting if the runner
/// itself has been exercised, and the only way to exercise the runner without a device is to give
/// it one. <c>--simulate</c> is therefore the tool's own smoke test, and the one thing CI can
/// execute — it needs no driver, no adapter and no bus.
/// </para>
/// <para>
/// What it is not: evidence about any real device. It is this repository's own CANopen stack
/// answering this repository's own checks, so a passing <c>--simulate</c> run says the checks ask
/// coherent questions, not that a device would answer them. The report says so in as many words,
/// because a simulated run pasted into an issue as a hardware result would be worse than no
/// result at all.
/// </para>
/// </remarks>
internal sealed class SimulatedDevice : IDisposable
{
    private readonly ICanBus _bus;
    private readonly ICanOpenNode _node;
    private readonly Timer _dataChange;
    private ushort _counter;

    private SimulatedDevice(ICanBus bus, ICanOpenNode node)
    {
        _bus = bus;
        _node = node;

        // An event-driven TPDO transmits when its mapped data changes, so a device whose data never
        // changes is legitimately silent — and PDO-03 would then have nothing to observe. This
        // moves one mapped value, which is what a real sensor does.
        _dataChange = new Timer(_ =>
        {
            try
            {
                node.ObjectDictionary.WriteUnsigned(0x2000, 0x00, unchecked(++_counter));
            }
            catch (ObjectDisposedException)
            {
                // The run ended between the tick and the write.
            }
        }, null, dueTime: 200, period: 200);
    }

    /// <summary>The description the simulated device is built from, next to the executable.</summary>
    public static string DescriptionPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "simulated-device.eds");

    /// <summary>
    /// Starts the device. <paramref name="endpoint"/> is the tool's own endpoint; the device is
    /// put on a second channel of the same virtual session, which is how CanKit's virtual adapter
    /// wires two buses together — the same session name is the same cable.
    /// </summary>
    /// <remarks>
    /// The node is built from <see cref="DescriptionPath"/> rather than from a hand-assembled
    /// object dictionary, and the run binds the same file as the peer description. The device and
    /// the file then agree by construction, which is what lets a --simulate run exercise the eds
    /// group and every check the peer-SDO gate would otherwise block.
    /// </remarks>
    public static SimulatedDevice Start(string endpoint, byte nodeId, int bitrate)
    {
        var description = CanOpenDeviceDescription.Load(DescriptionPath);
        var session = SessionNameOf(endpoint);
        var bus = CanBus.Open($"virtual://{session}/sim", cfg =>
            cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(bitrate).SetWorkMode(ChannelWorkMode.Normal));

        var node = CanOpen.OpenNode(bus, nodeId, description, new CanOpenNodeOptions
        {
            Profile = CanOpenNodeProfile.Device,
            // The pdo group reads and the commission tier writes communication parameters; a node
            // that refuses them would report a device defect that is this option's doing.
            WritableCommunicationParameters = true,
            PdoCount = 2,
            Lss = new LssSlaveOptions(),
        });

        // 1017h in the description says 200 ms. Starting the producer with anything else would make
        // HBT-01 and HBT-02 disagree for a reason that is the simulation's, not a device's.
        node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(200));

        return new SimulatedDevice(bus, node);
    }

    /// <summary>The virtual session from an endpoint like <c>virtual://conformance/0</c>; buses
    /// sharing it share a bus.</summary>
    private static string SessionNameOf(string endpoint)
    {
        var withoutScheme = endpoint["virtual://".Length..];
        var slash = withoutScheme.IndexOf('/');
        return slash < 0 ? withoutScheme : withoutScheme[..slash];
    }

    public void Dispose()
    {
        _dataChange.Dispose();
        _node.Dispose();
        _bus.Dispose();
    }
}
