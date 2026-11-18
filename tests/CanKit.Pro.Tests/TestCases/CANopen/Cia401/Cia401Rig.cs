using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.Actor;
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
/// description for, and raw expedited transfers reach every object and show the abort codes as
/// they are on the wire. A tool node with a bound description of the device joins the same bus in
/// <see cref="Cia401ToolRig"/>.</para>
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

    /// <summary>TPDO 2 of the device, the default TPDO of its analog inputs 1 to 4 (CiA 401-1
    /// §6.2.8), disabled by default: <see cref="EnableTpdoAsync"/> creates it.</summary>
    internal const uint Tpdo2 = 0x280 + DeviceId;

    /// <summary>RPDO 2 of the device, the default RPDO of its analog outputs 1 to 4 (CiA 401-1
    /// §6.2.7), enabled by default.</summary>
    internal const uint Rpdo2 = 0x300 + DeviceId;

    /// <summary>The device's EMCY COB-ID (CiA 301 v4.2.0 §7.3.3, Table 39).</summary>
    internal const uint Emcy = 0x080 + DeviceId;

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

    /// <summary>Options under which a master may write the PDO records over SDO, which
    /// <see cref="EnableTpdoAsync"/> needs: they are read-only on the bus otherwise.</summary>
    internal static CanOpenNodeOptions WritablePdoRecords => new() { WritableCommunicationParameters = true };

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

    private static IReadOnlyList<Cia401Device> DevicesFor(IReadOnlyList<Cia401Configuration> configurations, ICia401Process? process)
    {
        var devices = new List<Cia401Device>();
        foreach (var configuration in configurations)
            devices.Add(new Cia401Device(configuration, devices.Count == 0 ? process : null));
        return devices;
    }

    /// <summary>Opens a node from <paramref name="description"/> (none when null) with
    /// <paramref name="devices"/> attached in that order, built either way.</summary>
    internal Cia401Rig(IReadOnlyList<Cia401Device> devices, CanOpenDeviceDescription? description, CanOpenNodeOptions? options)
    {
        var session = VirtualAdapterFixture.NewSession("cia401");
        Session = session;
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
        var composed = options ?? new CanOpenNodeOptions();
        foreach (var device in devices) composed = Cia401Device.AddTo(composed, device);

        Devices = devices;
        try
        {
            Node = new CanOpenNode(new CanBusService(_deviceBus), DeviceId, composed, ownsService: true, Clock, description);
        }
        catch
        {
            _deviceBus.Dispose();
            _masterBus.Dispose();
            throw;
        }
    }

    /// <summary>The virtual adapter session of the rig: channel 0 is the master, channel 1 the
    /// device; a further channel joins the same bus (<see cref="Cia401ToolRig"/>).</summary>
    internal string Session { get; }

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

    /// <summary>
    /// The barrier before asserting what EMCYs were not sent, or exactly how many were: settles the
    /// actor, so that every callback queued before the call has run and ordered its EMCYs, then
    /// waits until every EMCY ordered so far has been through the node's EMCY chain — the frame is
    /// out, which on the virtual adapter means the master's channel has seen it. The chain is not
    /// covered by <see cref="DrainAsync"/>: each step goes from a thread-pool continuation back to
    /// the actor before it reserves its send. The chain is a field the actor extends, so it is read
    /// on the actor and awaited until it is the same task twice (as the seam tests do).
    /// </summary>
    internal async Task DrainEmcyAsync()
    {
        await DrainAsync();
        var field = typeof(CanOpenNode).GetField("_emcySendChain", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CanOpenNode has no _emcySendChain field any more.");
        Task? last = null;
        while (true)
        {
            Task? current = null;
            await Node.PostToActorAsync(() => current = (Task)field.GetValue(Node)!);
            if (await Task.WhenAny(current!, Task.Delay(HangGuard)) != current)
                throw new TimeoutException("Timed out waiting for the EMCY chain.");
            if (ReferenceEquals(current, last)) break;
            last = current;
        }

        await DrainAsync();
    }

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

    /// <summary>RPDO 2 from the master with <paramref name="data"/>, then <see cref="FenceAsync"/>.</summary>
    internal async Task SendRpdo2Async(params byte[] data)
    {
        Transmit(Rpdo2, data);
        await FenceAsync();
    }

    /// <summary>The little-endian bytes of 16-bit values, as a PDO carries them.</summary>
    internal static byte[] Words(params short[] values)
    {
        var data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            data[2 * i] = (byte)values[i];
            data[(2 * i) + 1] = (byte)(values[i] >> 8);
        }

        return data;
    }

    /// <summary>Waits until the device is in <paramref name="state"/>.</summary>
    internal Task WaitForStateAsync(NmtState state)
        => WaitUntilAsync(() => Node.State == state, state.ToString());

    /// <summary><see cref="StartAsync"/>, then waits for the TPDO on <paramref name="cobId"/>
    /// (TPDO 1 unless given) that entering Operational sends (CiA 401-1 §6.1), so that what a test
    /// counts afterwards is its own.</summary>
    internal async Task StartAndTakeFirstTpdoAsync(uint cobId = Tpdo1)
    {
        int before = Count(cobId);
        await StartAsync();
        await WaitForCountAsync(cobId, before + 1);
    }

    /// <summary>NMT Enter Pre-Operational to the device, and waits until it is there and its actor
    /// has settled.</summary>
    internal async Task EnterPreOperationalAsync()
    {
        Transmit(0x000, new[] { (byte)NmtCommand.EnterPreOperational, DeviceId });
        await WaitUntilAsync(() => Node.State == NmtState.PreOperational, "PreOperational");
        Settle();
    }

    /// <summary>
    /// Creates TPDO <paramref name="pdo"/> (1 to 4) the way a master does over SDO (CiA 301 v4.2.0
    /// §7.5.2.37): bit 31 of its COB-ID, "PDO exists / is valid", from 1 to 0, the CAN-ID left as
    /// the device has it. Its mapping is the default the device set, so the re-mapping procedure of
    /// §7.5.2.38 has nothing to do. Needs a rig opened with <see cref="WritablePdoRecords"/>.
    /// </summary>
    internal async Task EnableTpdoAsync(int pdo)
    {
        var index = (ushort)(0x1800 + pdo - 1);
        uint cobId = await ReadU32Async(index, 0x01);
        await WriteU32Async(index, 0x01, cobId & ~0x8000_0000u);
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

    /// <summary>An expedited four-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadU32Async(ushort index, byte subindex, uint value)
        => SdoAsync(new byte[]
        {
            0x23, (byte)index, (byte)(index >> 8), subindex,
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
        });

    /// <summary>An expedited four-byte download that must be acknowledged.</summary>
    internal async Task WriteU32Async(ushort index, byte subindex, uint value)
    {
        var response = await DownloadU32Async(index, subindex, value);
        response[0].Should().Be(0x60, $"the device acknowledges the download to {index:X4}h:{subindex:X2}h");
    }

    /// <summary>An expedited download of an INTEGER32 that must be acknowledged.</summary>
    internal Task WriteI32Async(ushort index, byte subindex, int value)
        => WriteU32Async(index, subindex, unchecked((uint)value));

    /// <summary>An expedited two-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadI16Async(ushort index, byte subindex, short value)
        => SdoAsync(new byte[] { 0x2B, (byte)index, (byte)(index >> 8), subindex, (byte)value, (byte)(value >> 8), 0, 0 });

    /// <summary>An expedited download of an INTEGER16 that must be acknowledged.</summary>
    internal async Task WriteI16Async(ushort index, byte subindex, short value)
    {
        var response = await DownloadI16Async(index, subindex, value);
        response[0].Should().Be(0x60, $"the device acknowledges the download to {index:X4}h:{subindex:X2}h");
    }

    /// <summary>The save signature of <c>1010h</c>, "save" (CiA 301 v4.2.0 §7.5.2.13), written by the
    /// master to sub-index 01h: every parameter the node stores, the application's objects among
    /// them.</summary>
    internal Task SaveAsync() => WriteU32Async(0x1010, 0x01, 0x6576_6173);

    /// <summary>An upload of a four-byte entry: its value.</summary>
    internal async Task<uint> ReadU32Async(ushort index, byte subindex)
    {
        var response = await SdoAsync(new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 });
        response[0].Should().Be(0x43, $"the device answers the upload of {index:X4}h:{subindex:X2}h with four bytes");
        return (uint)(response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24));
    }

    /// <summary>An upload of an INTEGER16 entry: its value.</summary>
    internal async Task<short> ReadI16Async(ushort index, byte subindex)
    {
        var response = await SdoAsync(new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 });
        response[0].Should().Be(0x4B, $"the device answers the upload of {index:X4}h:{subindex:X2}h with two bytes");
        return (short)(response[4] | (response[5] << 8));
    }

    /// <summary>The abort code of an SDO response that must be an abort.</summary>
    internal static uint AbortCode(byte[] response)
    {
        response[0].Should().Be(0x80, "the server aborted the transfer");
        // Little-endian on the wire, whatever the host's byte order.
        return (uint)(response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24));
    }

    internal void Transmit(uint cobId, byte[] data) => _masterBus.Transmit(CanFrame.Classic((int)cobId, data));

    /// <summary>Waits until <paramref name="condition"/> holds. A hang guard bounds the wait; it
    /// asserts nothing about time.</summary>
    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + HangGuard;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(2);
        }
    }

    /// <summary>
    /// Settles the device node's actor, then waits until every
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> report made so far has been delivered.
    /// The node hands its reports to a dispatcher that delivers them on the thread pool, one at a
    /// time and in the order made (<see cref="BackgroundExceptionDispatcher"/>); a marker queued
    /// behind them runs after the last. So afterwards a count of reports is exact, "none" included,
    /// for whatever the actor did before the call.
    /// </summary>
    internal async Task FlushBackgroundReportsAsync()
    {
        Settle();
        var field = typeof(CanOpenNode).GetField("_backgroundReports", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CanOpenNode has no _backgroundReports field any more.");
        var dispatcher = (BackgroundExceptionDispatcher)field.GetValue(Node)!;
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() => delivered.TrySetResult(true));
        if (await Task.WhenAny(delivered.Task, Task.Delay(HangGuard)) != delivered.Task)
            throw new TimeoutException("Timed out waiting for the background reports to be delivered.");
    }

    public void Dispose()
    {
        Node.Dispose();
        _deviceBus.Dispose();
        _masterBus.Dispose();
    }
}
