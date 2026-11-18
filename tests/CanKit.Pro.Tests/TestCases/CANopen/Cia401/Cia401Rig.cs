using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// A CiA 401 device on the virtual bus and the master beside it: the device is a
/// <see cref="CanOpenNode"/> opened with <see cref="Cia401Device.AddTo"/> on a
/// <see cref="ManualTimeSource"/>; the master is a raw channel that sends what a master sends (NMT,
/// SDO requests) and records every data frame it sees, per COB-ID and in wire order.
/// </summary>
/// <remarks>
/// <para>The master is raw rather than a second <see cref="CanOpenNode"/>: a node's SDO client
/// refuses every object but <c>1000h</c>, <c>1001h</c> and <c>1018h</c> of a server it has no
/// description for, and the description of a CiA 401 device is what the tool side and the EDS
/// variant build later. Raw expedited transfers reach every object and show the abort codes as
/// they are on the wire.</para>
/// <para>Nothing here waits on a wall clock for a result. "Nothing was sent" and "exactly so many
/// frames" are asserted after <see cref="DrainAsync"/>, which says what it guarantees
/// (<see cref="CanOpenTransmitBarrier"/>): every frame the device decided in the actor jobs queued
/// before the call has been seen by the master's channel. The timeouts are hang guards only.</para>
/// </remarks>
internal sealed class Cia401Rig : IDisposable
{
    internal const byte DeviceId = 0x11;

    /// <summary>TPDO 1 of the device, the default TPDO of its digital inputs (CiA 401-1 §6.2.6).</summary>
    internal const uint Tpdo1 = 0x180 + DeviceId;

    /// <summary>RPDO 1 of the device, the default RPDO of its digital outputs (CiA 401-1 §6.2.5).</summary>
    internal const uint Rpdo1 = 0x200 + DeviceId;

    internal const uint SdoRequest = 0x600 + DeviceId;
    internal const uint SdoResponse = 0x580 + DeviceId;

    /// <summary>The device's boot-up and heartbeat COB-ID.</summary>
    internal const uint Heartbeat = 0x700 + DeviceId;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    private readonly ICanBus _masterBus;
    private readonly ICanBus _deviceBus;
    private readonly object _gate = new();
    private readonly Dictionary<uint, List<byte[]>> _frames = new();
    private readonly List<(uint CobId, byte[] Data)> _sequence = new();

    /// <summary>Opens the device for <paramref name="configuration"/>. <paramref name="options"/> are
    /// what the test starts from (the defaults when null); the rig hands them to
    /// <see cref="Cia401Device.AddTo"/>.</summary>
    internal Cia401Rig(Cia401Configuration configuration, CanOpenNodeOptions? options = null)
        : this(new[] { configuration }, options)
    {
    }

    /// <summary>Opens one node with a device for each of <paramref name="configurations"/>, attached
    /// in that order: several logical devices of one node.</summary>
    internal Cia401Rig(IReadOnlyList<Cia401Configuration> configurations, CanOpenNodeOptions? options = null)
    {
        var session = VirtualAdapterFixture.NewSession("cia401");
        _masterBus = VirtualAdapterFixture.Open(session, 0);
        _masterBus.FrameObserved += (_, e) =>
        {
            var frame = e.CanFrame;
            if (frame.IsRemoteFrame) return;
            var data = frame.Data.ToArray();
            lock (_gate)
            {
                if (!_frames.TryGetValue((uint)frame.ID, out var list)) _frames[(uint)frame.ID] = list = new List<byte[]>();
                list.Add(data);
                _sequence.Add(((uint)frame.ID, data));
            }
        };
        _deviceBus = VirtualAdapterFixture.Open(session, 1);
        Clock = new ManualTimeSource();
        var devices = new List<Cia401Device>();
        var composed = options ?? new CanOpenNodeOptions();
        foreach (var configuration in configurations)
        {
            var device = new Cia401Device(configuration);
            devices.Add(device);
            composed = Cia401Device.AddTo(composed, device);
        }

        Devices = devices;
        try
        {
            Node = new CanOpenNode(new CanBusService(_deviceBus), DeviceId, composed, ownsService: true, Clock);
        }
        catch
        {
            _deviceBus.Dispose();
            _masterBus.Dispose();
            throw;
        }
    }

    /// <summary>The first device, the only one in most tests.</summary>
    internal Cia401Device Device => Devices[0];

    internal IReadOnlyList<Cia401Device> Devices { get; }

    internal CanOpenNode Node { get; }

    internal ManualTimeSource Clock { get; }

    internal ObjectDictionary Od => Node.ObjectDictionary;

    // ---- frames -------------------------------------------------------------------------------

    internal int Count(uint cobId)
    {
        lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.Count : 0;
    }

    internal byte[][] Payloads(uint cobId)
    {
        lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.ToArray() : Array.Empty<byte[]>();
    }

    /// <summary>Waits until at least <paramref name="count"/> frames have been seen on
    /// <paramref name="cobId"/>. A hang guard bounds the wait; it asserts nothing about time.</summary>
    internal Task WaitForCountAsync(uint cobId, int count)
        => WaitUntilAsync(() => Count(cobId) >= count, $"{count} frame(s) on 0x{cobId:X3}, saw {Count(cobId)}");

    /// <summary>The barrier before asserting what was not sent: see <see cref="CanOpenTransmitBarrier"/>.
    /// Works in every NMT state.</summary>
    internal Task DrainAsync() => CanOpenTransmitBarrier.DrainAsync(Node);

    /// <summary>Runs two empty jobs through the device node's actor: whatever it was handed before,
    /// and whatever that queued in turn, has run.</summary>
    internal void Settle()
    {
        Node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
        Node.PostToActorAsync(() => { }).GetAwaiter().GetResult();
    }

    /// <summary>An SDO round trip with the device, then <see cref="Settle"/>: every frame the master
    /// sent before it has been handled on the device's actor (the reader hands frames to the actor
    /// in wire order, and the response is sent from the job that handles the request), and what that
    /// queued in turn, the device's <c>OnEntriesWritten</c> among it, has run. Not in Stopped, where
    /// the device has no SDO server (CiA 301 v4.2.0 §7.3.2.2.5, Table 37).</summary>
    internal async Task FenceAsync()
    {
        await SdoAsync(new byte[] { 0x40, 0x00, 0x10, 0x00, 0, 0, 0, 0 });
        Settle();
    }

    /// <summary>Advances the manual clock with the device's actor settled before and after, so that
    /// the deadlines it moves past have fired and their work has run.</summary>
    internal void Advance(TimeSpan by)
    {
        Settle();
        Clock.Advance(by);
        Settle();
    }

    // ---- NMT ----------------------------------------------------------------------------------

    /// <summary>NMT Start to the device, and waits until it is Operational.</summary>
    internal async Task StartAsync()
    {
        Transmit(0x000, new[] { (byte)NmtCommand.Start, DeviceId });
        await WaitUntilAsync(() => Node.State == NmtState.Operational, "Operational");
    }

    /// <summary>NMT Stop to the device; waits until it is Stopped and its actor has settled.</summary>
    internal async Task StopAsync()
    {
        Transmit(0x000, new[] { (byte)NmtCommand.Stop, DeviceId });
        await WaitUntilAsync(() => Node.State == NmtState.Stopped, "Stopped");
        Settle();
    }

    /// <summary>NMT <paramref name="command"/> (Reset Node or Reset Communication) to the device;
    /// waits for its boot-up and settles its actor, so that the reset's callbacks have run.</summary>
    internal async Task ResetAsync(NmtCommand command)
    {
        int before = Count(Heartbeat);
        Transmit(0x000, new[] { (byte)command, DeviceId });
        await WaitForCountAsync(Heartbeat, before + 1);
        Settle();
    }

    /// <summary>RPDO 1 from the master with <paramref name="data"/>, then <see cref="FenceAsync"/>:
    /// the device has handled it when this returns.</summary>
    internal async Task SendRpdo1Async(params byte[] data)
    {
        Transmit(Rpdo1, data);
        await FenceAsync();
    }

    /// <summary>Waits until the device is in <paramref name="state"/>.</summary>
    internal Task WaitForStateAsync(NmtState state)
        => WaitUntilAsync(() => Node.State == state, state.ToString());

    /// <summary><see cref="StartAsync"/>, then waits for the TPDO 1 that entering Operational sends
    /// (CiA 401-1 §6.1), so that what a test counts afterwards is its own.</summary>
    internal async Task StartAndTakeFirstTpdoAsync()
    {
        int before = Count(Tpdo1);
        await StartAsync();
        await WaitForCountAsync(Tpdo1, before + 1);
    }

    // ---- SDO, expedited, from the master side -----------------------------------------------------

    /// <summary>One SDO request to the device, and its response.</summary>
    internal async Task<byte[]> SdoAsync(byte[] request)
    {
        int before = Count(SdoResponse);
        Transmit(SdoRequest, request);
        await WaitForCountAsync(SdoResponse, before + 1);
        return Payloads(SdoResponse)[before];
    }

    /// <summary>An expedited one-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadU8Async(ushort index, byte subindex, byte value)
        => SdoAsync(new byte[] { 0x2F, (byte)index, (byte)(index >> 8), subindex, value, 0, 0, 0 });

    /// <summary>An expedited one-byte download that must be acknowledged.</summary>
    internal async Task WriteU8Async(ushort index, byte subindex, byte value)
    {
        var response = await DownloadU8Async(index, subindex, value);
        response[0].Should().Be(0x60, $"the device acknowledges the download to {index:X4}h:{subindex:X2}h");
    }

    /// <summary>An upload of a one-byte entry: its value.</summary>
    internal async Task<byte> ReadU8Async(ushort index, byte subindex)
    {
        var response = await SdoAsync(new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 });
        response[0].Should().Be(0x4F, $"the device answers the upload of {index:X4}h:{subindex:X2}h with one byte");
        return response[4];
    }

    /// <summary>The abort code of an SDO response that must be an abort.</summary>
    internal static uint AbortCode(byte[] response)
    {
        response[0].Should().Be(0x80, "the server aborted the transfer");
        // Little-endian on the wire, whatever the host's byte order.
        return (uint)(response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24));
    }

    internal void Transmit(uint cobId, byte[] data) => _masterBus.Transmit(CanFrame.Classic((int)cobId, data));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + HangGuard;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    public void Dispose()
    {
        Node.Dispose();
        _deviceBus.Dispose();
        _masterBus.Dispose();
    }
}
