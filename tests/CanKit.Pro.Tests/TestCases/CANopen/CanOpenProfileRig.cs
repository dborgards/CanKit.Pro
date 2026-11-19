using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// What every device-profile rig shares: a device node on the virtual bus and the master beside it.
/// The device is a <see cref="CanOpenNode"/> opened with the options the profile's rig composes, on
/// a <see cref="ManualTimeSource"/>; the master is a raw channel that sends what a master sends
/// (NMT, SDO requests, RPDOs) and records every data frame it sees, per COB-ID and in wire order.
/// </summary>
/// <remarks>
/// <para>The master is raw rather than a second <see cref="CanOpenNode"/>: a node's SDO client
/// refuses every object but <c>1000h</c>, <c>1001h</c> and <c>1018h</c> of a server it has no
/// description for, and raw expedited transfers reach every object and show the abort codes as
/// they are on the wire.</para>
/// <para>Nothing here waits on a wall clock for a result. "Nothing was sent" and "exactly so many
/// frames" are asserted after <see cref="DrainAsync"/>, which says what it guarantees
/// (<see cref="CanOpenTransmitBarrier"/>): every frame the device decided in the actor jobs queued
/// before the call has been seen by the master's channel. The timeouts are hang guards only.</para>
/// </remarks>
internal abstract class CanOpenProfileRig : IDisposable
{
    internal const byte DeviceId = 0x11;

    /// <summary>TPDO 1 of the device in the pre-defined connection set (CiA 301 v4.2.0 §7.3.3,
    /// Table 39).</summary>
    internal const uint Tpdo1 = 0x180 + DeviceId;

    /// <summary>RPDO 1 of the device in the pre-defined connection set.</summary>
    internal const uint Rpdo1 = 0x200 + DeviceId;

    /// <summary>TPDO 2 of the device in the pre-defined connection set.</summary>
    internal const uint Tpdo2 = 0x280 + DeviceId;

    /// <summary>RPDO 2 of the device in the pre-defined connection set.</summary>
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

    /// <summary>Options under which a master may write the PDO records over SDO, which
    /// <see cref="DisableTpdoAsync"/> needs: they are read-only on the bus otherwise.</summary>
    internal static CanOpenNodeOptions WritablePdoRecords => new() { WritableCommunicationParameters = true };

    /// <summary>Opens the master on channel 0 and the device node on channel 1 of a new virtual
    /// session named after <paramref name="sessionName"/>, with <paramref name="options"/> as the
    /// profile's rig composed them and the device description <paramref name="description"/> (none
    /// when null). An open that fails throws here, with the rig's channels closed.</summary>
    protected CanOpenProfileRig(string sessionName, CanOpenNodeOptions options, CanOpenDeviceDescription? description)
    {
        var session = VirtualAdapterFixture.NewSession(sessionName);
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
            }
        };
        _deviceBus = VirtualAdapterFixture.Open(session, 1);
        Clock = new ManualTimeSource();
        try
        {
            Node = new CanOpenNode(new CanBusService(_deviceBus), DeviceId, options, ownsService: true, Clock, description);
        }
        catch
        {
            _deviceBus.Dispose();
            _masterBus.Dispose();
            throw;
        }
    }

    /// <summary>The virtual adapter session of the rig: channel 0 is the master, channel 1 the
    /// device; a further channel joins the same bus (a tool rig).</summary>
    internal string Session { get; }

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

    /// <summary>The barrier before asserting what EMCYs were not sent, or exactly how many were: see
    /// <see cref="CanOpenTransmitBarrier.DrainEmcyAsync"/>.</summary>
    internal Task DrainEmcyAsync() => CanOpenTransmitBarrier.DrainEmcyAsync(Node);

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

    /// <summary>Holds the device node's actor in a job of its own while <paramref name="whileHeld"/>
    /// runs, so that what the test does there lands before the actor comes round; then releases it
    /// and waits for the holding job to end. A signal, not a sleep: the hold begins when the job has
    /// started and ends when the test says so.</summary>
    internal async Task WhileActorHeldAsync(Func<Task> whileHeld)
    {
        using var release = new ManualResetEventSlim();
        var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = Node.PostToActorAsync(() =>
        {
            holding.SetResult(true);
            release.Wait();
        });
        try
        {
            if (await Task.WhenAny(holding.Task, Task.Delay(HangGuard)) != holding.Task)
                throw new TimeoutException("Timed out waiting for the actor to take the holding job.");
            await whileHeld();
        }
        finally
        {
            release.Set();
        }

        await hold;
    }

    /// <summary>Sends <paramref name="frames"/> from the master while the actor is held
    /// (<see cref="WhileActorHeldAsync"/>), and waits until the reader has handed each to the
    /// actor's mailbox: they then run, in that order, before anything queued afterwards.</summary>
    internal Task QueueFramesAsync(params (uint CobId, byte[] Data)[] frames)
    {
        int baseline = MailboxDepth(Node);
        foreach (var (cobId, data) in frames) Transmit(cobId, data);
        return WaitUntilAsync(() => MailboxDepth(Node) - baseline >= frames.Length,
            $"{frames.Length} frame(s) in the actor's mailbox");
    }

    /// <summary>The number of jobs waiting in the node's actor mailbox.</summary>
    internal static int MailboxDepth(CanOpenNode node)
    {
        var actor = typeof(CanOpenNode).GetField("_actor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!;
        var mailbox = actor.GetType().GetField("_mailbox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(actor)!;
        return (int)mailbox.GetType().GetProperty("Count")!.GetValue(mailbox)!;
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
    /// waits for its boot-up and settles its actor, so that the reset's callbacks have run. The
    /// boot-up is the frame <c>00h</c> on <see cref="Heartbeat"/>: a heartbeat of a state shares the
    /// COB-ID and must not pass for it.</summary>
    internal async Task ResetAsync(NmtCommand command)
    {
        int before = BootUps();
        Transmit(0x000, new[] { (byte)command, DeviceId });
        await WaitUntilAsync(() => BootUps() > before, $"a boot-up after {before}, saw {BootUps()}");
        Settle();
    }

    /// <summary>The boot-up frames seen so far: <c>00h</c> on <see cref="Heartbeat"/>
    /// (CiA 301 v4.2.0 §7.2.8.3.3).</summary>
    internal int BootUps() => Payloads(Heartbeat).Count(payload => payload.Length == 1 && payload[0] == 0x00);

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

    /// <summary>NMT Enter Pre-Operational to the device, and waits until it is there and its actor
    /// has settled.</summary>
    internal async Task EnterPreOperationalAsync()
    {
        Transmit(0x000, new[] { (byte)NmtCommand.EnterPreOperational, DeviceId });
        await WaitUntilAsync(() => Node.State == NmtState.PreOperational, "PreOperational");
        Settle();
    }

    /// <summary>
    /// Takes TPDO <paramref name="pdo"/> (1 to 4) out of service the way a master does over SDO
    /// before it changes the PDO (CiA 301 v4.2.0 §7.5.2.37): bit 31 of its COB-ID, "PDO exists / is
    /// valid", from 0 to 1, the CAN-ID left as the device has it. Needs a rig opened with
    /// <see cref="WritablePdoRecords"/>.
    /// </summary>
    internal async Task DisableTpdoAsync(int pdo)
    {
        var index = (ushort)(0x1800 + pdo - 1);
        uint cobId = await ReadU32Async(index, 0x01);
        await WriteU32Async(index, 0x01, cobId | 0x8000_0000u);
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

    /// <summary>The request of an expedited one-byte download.</summary>
    internal static byte[] DownloadU8(ushort index, byte subindex, byte value)
        => new byte[] { 0x2F, (byte)index, (byte)(index >> 8), subindex, value, 0, 0, 0 };

    /// <summary>The request of an expedited two-byte download.</summary>
    internal static byte[] DownloadU16(ushort index, byte subindex, ushort value)
        => new byte[] { 0x2B, (byte)index, (byte)(index >> 8), subindex, (byte)value, (byte)(value >> 8), 0, 0 };

    /// <summary>The request of an expedited four-byte download.</summary>
    internal static byte[] DownloadU32(ushort index, byte subindex, uint value)
        => new byte[]
        {
            0x23, (byte)index, (byte)(index >> 8), subindex,
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
        };

    /// <summary>An expedited one-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadU8Async(ushort index, byte subindex, byte value)
        => SdoAsync(DownloadU8(index, subindex, value));

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

    /// <summary>An expedited two-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadU16Async(ushort index, byte subindex, ushort value)
        => SdoAsync(DownloadU16(index, subindex, value));

    /// <summary>An expedited two-byte download that must be acknowledged.</summary>
    internal async Task WriteU16Async(ushort index, byte subindex, ushort value)
    {
        var response = await DownloadU16Async(index, subindex, value);
        response[0].Should().Be(0x60, $"the device acknowledges the download to {index:X4}h:{subindex:X2}h");
    }

    /// <summary>An upload of a two-byte entry: its value.</summary>
    internal async Task<ushort> ReadU16Async(ushort index, byte subindex)
    {
        var response = await SdoAsync(new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 });
        response[0].Should().Be(0x4B, $"the device answers the upload of {index:X4}h:{subindex:X2}h with two bytes");
        return (ushort)(response[4] | (response[5] << 8));
    }

    /// <summary>An expedited four-byte download; the response, an acknowledgement or an abort.</summary>
    internal Task<byte[]> DownloadU32Async(ushort index, byte subindex, uint value)
        => SdoAsync(DownloadU32(index, subindex, value));

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
