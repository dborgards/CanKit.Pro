using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The block-upload server's sending side (FR-CO-004, #341): a sub-block goes out as one ordered
/// batch, and the batch must end with the session. Driven by a raw-frame client on a
/// <see cref="ControllableBus"/>, whose deferred echoes hold a segment's confirmation so that the
/// session can be ended while the batch is provably still running -- on a virtual bus the batch
/// outruns the test.
/// </summary>
public class CanOpenSdoBlockUploadServerTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte ServerNodeId = 0x7F;
    private const ushort Index = 0x2100;

    public static TheoryData<string> SessionEndings => new()
    {
        "the client aborts", "the server times out", "a new initiate supersedes it", "NMT Stop",
    };

    // The server has sent segment 1 of a 20-segment sub-block and its confirmation is held, so
    // the batch is still running when the session ends. A batch that does not notice sends the
    // other 19 -- to a client that has no session for them any more, and whose next transfer
    // gets them interleaved with its own frames.
    [Theory]
    [MemberData(nameof(SessionEndings))]
    public async Task A_Block_Upload_Whose_Session_Ends_Mid_SubBlock_Sends_No_More_Segments(string ending)
    {
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-block-server-end-{Guid.NewGuid():N}");
        var toClient = new List<byte[]>();
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(ServerNodeId)) lock (toClient) toClient.Add(frame.Data.ToArray());
        };
        // The idle timeout is the ending under test in one row. It is measured on a clock the
        // test moves (#92): on the wall clock a short timeout races the thread pool from the
        // start frame to segment 1 -- fired first, it aborts a session with no batch running,
        // segment 1 never comes and the row fails without testing anything.
        var clock = new ManualTimeSource();
        var serverTimeout = TimeSpan.FromSeconds(30);
        using var server = new CanOpenNode(new CanBusService(bus), ServerNodeId,
            new CanOpenNodeOptions { SdoServerTimeout = serverTimeout }, ownsService: true, timeSource: clock);
        var nmtSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.NmtCommandReceived += (_, e) =>
        {
            if (e.Command == NmtCommand.EnterPreOperational) nmtSeen.TrySetResult();
        };
        server.ObjectDictionary.AddDomain(Index, 0x00,
            Enumerable.Range(0, 140).Select(i => (byte)i).ToArray(), OdAccess.ReadOnly); // 20 segments
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 20, pst: 0));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout); // the initiate response
        bus.DeferredEchoes.ReleaseAll();
        FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(3, ShortTimeout); // segment 1, its confirmation held

        // Each ending leaves a frame or an event behind that says the node has handled it; the
        // batch is only let go once that has arrived.
        int expected;
        switch (ending)
        {
            case "the client aborts":
                FromClient(bus, SdoFrames.BuildAbort(Index, 0x00, (uint)SdoAbortCode.General));
                // Nothing answers an abort; an NMT command behind it is handled behind it.
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
                    new byte[] { (byte)NmtCommand.EnterPreOperational, ServerNodeId }), isEcho: false);
                await nmtSeen.Task.WithTimeoutAsync(ShortTimeout);
                expected = 2; // the initiate response and segment 1
                break;
            case "the server times out":
                // The start frame re-armed the deadline before the batch was handed segment 1, so
                // it is armed by now. Past it, any frame wakes the actor, which then fires it.
                clock.Advance(serverTimeout + TimeSpan.FromMilliseconds(1));
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
                    new byte[] { (byte)NmtCommand.EnterPreOperational, ServerNodeId }), isEcho: false);
                await bus.DeferredEchoes.WaitForEnqueuedAsync(4, ShortTimeout); // the timeout's abort
                expected = 3;
                break;
            case "a new initiate supersedes it":
                FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 20, pst: 0));
                await bus.DeferredEchoes.WaitForEnqueuedAsync(5, ShortTimeout); // the abort and the new initiate response
                expected = 4;
                break;
            default:
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
                    new byte[] { (byte)NmtCommand.Stop, ServerNodeId }), isEcho: false);
                await bus.DeferredEchoes.WaitForEnqueuedAsync(4, ShortTimeout); // the abort (device state)
                expected = 3;
                break;
        }

        // The session is over; let the batch carry on. A batch that ignores that sends segment 2
        // at once (and one more at every later release, each waiting for the one before).
        bus.DeferredEchoes.ReleaseAll();
        // No signal says "nothing more is coming", so this is a negative window: it can only pass
        // falsely on a slow host, never fail falsely.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        bus.DeferredEchoes.ReleaseAll();

        byte[][] sent;
        lock (toClient) sent = toClient.ToArray();
        sent.Length.Should().Be(expected, "nothing follows the end of the session");
        sent.Count(f => (f[0] & 0x80) == 0 && f[0] is >= 2 and <= 20).Should().Be(0, "no further segment was sent");
    }

    public static TheoryData<string> MidBatchAcks => new() { "a partial ack", "an ack of segments not yet sent" };

    // #378: the batch's stop check asks whether the session is still the open one, and a partial
    // ack that arrives while the batch is still going leaves it open: it starts the
    // retransmission as a second batch for the same session. The batch already running must
    // stop then, or its remaining segments reach the client interleaved with the
    // retransmission, each seqno twice. An ack that claims segments the batch has not handed
    // over yet confirms nothing that left; it is a protocol error like any ack past the
    // sub-block, and the session is aborted (Codex on #392) -- not moved on past data the client
    // was never sent.
    [Theory]
    [MemberData(nameof(MidBatchAcks))]
    public async Task An_Ack_While_The_SubBlock_Is_Still_Being_Sent_Stops_That_Batch(string ack)
    {
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-block-server-midack-{Guid.NewGuid():N}");
        var toClient = new List<byte[]>();
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(ServerNodeId)) lock (toClient) toClient.Add(frame.Data.ToArray());
        };
        using var server = new CanOpenNode(new CanBusService(bus), ServerNodeId,
            new CanOpenNodeOptions(), ownsService: true);
        var nmtSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.NmtCommandReceived += (_, e) =>
        {
            if (e.Command == NmtCommand.EnterPreOperational) nmtSeen.TrySetResult();
        };
        server.ObjectDictionary.AddDomain(Index, 0x00,
            Enumerable.Range(0, 140).Select(i => (byte)i).ToArray(), OdAccess.ReadOnly); // 20 segments
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 20, pst: 0));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout); // the initiate response
        bus.DeferredEchoes.ReleaseAll();
        FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
        await bus.DeferredEchoes.WaitForEnqueuedAsync(3, ShortTimeout); // segment 1, its confirmation held

        // Segment 1 is out: a partial ack confirms it and asks for the rest again; an ack of all
        // 20 claims 19 that have not left. An NMT command behind the ack is handled behind it.
        FromClient(bus, SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck,
            lastAckedSeq: ack == "a partial ack" ? (byte)1 : (byte)20, nextBlockSize: 20));
        bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.NmtCommand),
            new byte[] { (byte)NmtCommand.EnterPreOperational, ServerNodeId }), isEcho: false);
        await nmtSeen.Task.WithTimeoutAsync(ShortTimeout);

        // Let both sends run. The first batch, still sending, would go on with segment 2 at
        // once and one more segment at every later release. No signal says "nothing more is
        // coming", so this is a negative window: it can only pass falsely on a slow host.
        for (var i = 0; i < 3; i++)
        {
            bus.DeferredEchoes.ReleaseAll();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        bus.DeferredEchoes.ReleaseAll();

        byte[][] sent;
        lock (toClient) sent = toClient.ToArray();
        var seqnos = sent.Skip(1).Where(f => (f[0] & 0x7F) is >= 1 and <= 20).Select(f => f[0] & 0x7F).ToList();
        if (ack == "a partial ack")
        {
            seqnos.Should().HaveCountGreaterThan(1, "the retransmission is under way");
            seqnos.Should().Equal(Enumerable.Range(1, seqnos.Count),
                "the first batch stopped at the ack, so each segment went out once, in order");
            return;
        }
        seqnos.Should().Equal(new[] { 1 }, "the session ended at the ack; nothing more was sent");
        sent.Should().HaveCount(3, "the initiate response, segment 1 and the abort");
        sent[2][0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(sent[2]).Should().Be((uint)SdoAbortCode.General);
        SdoFrames.ReadIndex(sent[2]).Should().Be((Index, (byte)0x00));
    }

    // An empty object cannot be expedited and used to leave the block path without a segment
    // at all: the client waited for seqno 1, the server for an ack, and both timed out. The
    // encoding has the case: one last segment carrying nothing, n = 7 in the end frame.
    [Fact]
    public async Task A_Block_Upload_Of_An_Empty_Object_Sends_One_Empty_Last_Segment()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-server-empty-{Guid.NewGuid():N}");
        using var toClient = new BlockingCollection<byte[]>();
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(ServerNodeId)) toClient.Add(frame.Data.ToArray());
        };
        using var server = CanOpen.OpenNode(bus, ServerNodeId);
        server.ObjectDictionary.AddDomain(Index, 0x00, Array.Empty<byte>(), OdAccess.ReadOnly);

        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 127, pst: 0));
        var initResponse = Next(toClient);
        (initResponse[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase);
        FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));

        var segment = Next(toClient);
        segment[0].Should().Be(0x81, "the one segment is seqno 1 and the last (c = 1)");
        FromClient(bus, SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck, lastAckedSeq: 1, nextBlockSize: 127));

        var end = Next(toClient);
        (end[0] & 0xC3).Should().Be(SdoBlockFrames.ScsBlockUploadEndBase);
        SdoBlockFrames.ReadEndUnusedBytes(end[0]).Should().Be(7, "none of the segment's seven bytes is data");
    }

    // The client's own block upload of an empty object completes with the empty value, as the
    // classic path has done all along (Sdo_UploadOfEmptyOdValue_ReturnsEmptyArray_ViaSegmented).
    [Fact]
    public async Task A_Block_Upload_Of_An_Empty_Object_Returns_The_Empty_Value()
    {
        var session = $"canopen-{Guid.NewGuid():N}";
        using var busA = CanKit.Core.CanBus.Open($"virtual://{session}/0",
            cfg => cfg.SetProtocolMode(CanKit.Abstractions.API.Common.Definitions.CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));
        using var busB = CanKit.Core.CanBus.Open($"virtual://{session}/1",
            cfg => cfg.SetProtocolMode(CanKit.Abstractions.API.Common.Definitions.CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));
        using var client = CanOpen.OpenNode(busA, nodeId: 0x01);
        PeerSdoLaboratory.Bind(client, 0x11);
        using var server = CanOpen.OpenNode(busB, nodeId: 0x11);
        PeerSdoLaboratory.Bind(server, 0x11);
        server.ObjectDictionary.AddDomain(Index, 0x00, Array.Empty<byte>(), OdAccess.ReadOnly);

        var raw = await client.SdoUploadAsync(0x11, Index, 0x00, SdoTransferMode.Block).WithTimeoutAsync(ShortTimeout);
        raw.Should().BeEmpty();
    }

    // "Block upload start" (0xA3) belongs to the phase after the initiate response and nowhere
    // else. Repeated after the end frame it used to run the sender again and reset the phase,
    // so the end response no longer matched and the session only ended in its timeout; now it
    // is a command specifier the open session does not expect, and is answered as one: an
    // abort naming the session's object (a 0xA3 frame carries no multiplexer of its own, so
    // the classic path's abort would name 0000:00, which this package's client ignores as
    // somebody else's) -- and the session is gone with it, as the client side's
    // CommandSpecifierInvalid abort takes its session down. A next initiate is answered
    // directly, with no supersede abort for a session that was already aborted.
    [Fact]
    public async Task A_Repeated_Block_Upload_Start_Is_Refused_Outside_Its_Phase()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-server-restart-{Guid.NewGuid():N}");
        using var toClient = new BlockingCollection<byte[]>();
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(ServerNodeId)) toClient.Add(frame.Data.ToArray());
        };
        using var server = CanOpen.OpenNode(bus, ServerNodeId);
        server.ObjectDictionary.AddDomain(Index, 0x00,
            Enumerable.Range(0, 20).Select(i => (byte)(0x60 + i)).ToArray(), OdAccess.ReadOnly); // three segments

        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 3, pst: 0));
        (Next(toClient)[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase);
        FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
        for (var i = 0; i < 3; i++) Next(toClient);
        FromClient(bus, SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck, lastAckedSeq: 3, nextBlockSize: 3));
        (Next(toClient)[0] & 0xC3).Should().Be(SdoBlockFrames.ScsBlockUploadEndBase);

        // The start again, with the end frame out and its response due.
        FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
        var answer = Next(toClient);
        answer[0].Should().Be(SdoFrames.CsAbort, "an unexpected command specifier is aborted, not acted on");
        SdoFrames.ReadAbortCode(answer).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid);
        SdoFrames.ReadIndex(answer).Should().Be((Index, (byte)0x00), "the abort names the transfer it ends");

        // The abort ended the session: the next initiate is answered as the only open thing,
        // not preceded by a supersede abort for the one just aborted.
        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 3, pst: 0));
        var next = Next(toClient);
        (next[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase, "no session was left to supersede");
    }

    private static void FromClient(ControllableBus bus, byte[] payload)
        => bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoRx(ServerNodeId)), payload), isEcho: false);

    private static byte[] Next(BlockingCollection<byte[]> frames)
    {
        if (!frames.TryTake(out var frame, ShortTimeout))
            throw new TimeoutException($"No frame on COB-ID 0x{CanOpenCobId.SdoTx(ServerNodeId):X3} within {ShortTimeout}.");
        return frame;
    }
}
