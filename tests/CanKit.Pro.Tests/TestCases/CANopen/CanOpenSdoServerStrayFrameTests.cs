using System;
using System.Collections.Concurrent;
using System.Linq;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// Frames on the SDO server's request COB-ID that the open session does not expect (#357 C-2,
/// #378): a block follow-up frame outside its phase, and a command specifier no session takes.
/// Driven by a raw-frame client on a <see cref="ControllableBus"/>; every answer the server
/// gives is read off its SDO response COB-ID.
/// </summary>
public class CanOpenSdoServerStrayFrameTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte ServerNodeId = 0x7F;
    private const ushort Index = 0x2100;

    public static TheoryData<string, string> StrayBlockFrames => new()
    {
        { "upload, awaiting the start", "sub-block ack" },
        { "upload, awaiting the start", "end response" },
        { "upload, awaiting the sub-block ack", "end response" },
        { "upload, awaiting the sub-block ack", "download end" },
        { "upload, awaiting the end response", "sub-block ack" },
        { "download, awaiting the end", "upload start" },
    };

    // #378: a block follow-up frame the open block session does not expect in its phase used to
    // fall through to the classic server. Its abort named 0000:00 -- these frames carry no
    // multiplexer -- which a client matching aborts by object ignores as somebody else's, and
    // the block session stayed in the slot until its idle timeout aborted it a second time. The
    // shape #371 gave the out-of-phase start: the session itself is aborted with 0504 0001h,
    // naming its own object, and is gone -- the next initiate is answered with no supersede abort.
    [Theory]
    [MemberData(nameof(StrayBlockFrames))]
    public void A_Block_Follow_Up_Frame_Outside_Its_Phase_Aborts_The_Block_Session(string phase, string stray)
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-stray-block-{Guid.NewGuid():N}");
        using var toClient = TapResponses(bus);
        using var server = OpenOnStoppedClock(bus);
        server.ObjectDictionary.AddDomain(Index, 0x00,
            Enumerable.Range(0, 20).Select(i => (byte)(0x60 + i)).ToArray()); // three segments

        switch (phase)
        {
            case "download, awaiting the end":
                FromClient(bus, SdoBlockFrames.BuildBlockDownloadInit(Index, 0x00,
                    clientCrcSupported: false, sizeIndicated: true, totalSize: 7));
                (Next(toClient)[0] & 0xE3).Should().Be(SdoBlockFrames.ScsBlockDownloadInitResponseBase);
                FromClient(bus, SdoBlockFrames.BuildSegment(1, isLastSegment: true, new byte[] { 1, 2, 3, 4, 5, 6, 7 }));
                Next(toClient)[0].Should().Be(SdoBlockFrames.ScsBlockDownloadSubBlockAck);
                break;
            default:
                FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 3, pst: 0));
                (Next(toClient)[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase);
                if (phase == "upload, awaiting the start") break;
                FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
                for (var i = 0; i < 3; i++) Next(toClient);
                if (phase == "upload, awaiting the sub-block ack") break;
                FromClient(bus, SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck,
                    lastAckedSeq: 3, nextBlockSize: 3));
                (Next(toClient)[0] & 0xC3).Should().Be(SdoBlockFrames.ScsBlockUploadEndBase);
                break;
        }

        FromClient(bus, stray switch
        {
            "sub-block ack" => SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck, 1, 3),
            "end response" => SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadEndResponse),
            "download end" => SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase, 0, 0),
            _ => SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart),
        });
        var answer = Next(toClient);
        answer[0].Should().Be(SdoFrames.CsAbort, "an unexpected command specifier is aborted, not acted on");
        SdoFrames.ReadAbortCode(answer).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid);
        SdoFrames.ReadIndex(answer).Should().Be((Index, (byte)0x00), "the abort names the transfer it ends");

        FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 3, pst: 0));
        (Next(toClient)[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase,
            "the abort ended the session, so there is none left to supersede");
    }

    public static TheoryData<string> UnknownSpecifiers => new()
    {
        "a block sub-block ack", "a block upload start", "a block download end", "ccs 7",
    };

    // #357 C-2: a frame whose command specifier no session here takes -- a late block control
    // frame of an earlier transfer, or one no service defines -- used to be answered by the
    // classic abort path, which also cleared the open classic session, and named a multiplexer
    // read from bytes 1..3 of a frame that carries none. It is answered with 0504 0001h naming
    // 0000:00, and the segmented upload that is running goes on.
    [Theory]
    [MemberData(nameof(UnknownSpecifiers))]
    public void An_Unknown_Command_Specifier_Is_Refused_Without_Ending_The_Open_Session(string stray)
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-stray-classic-{Guid.NewGuid():N}");
        using var toClient = TapResponses(bus);
        using var server = OpenOnStoppedClock(bus);
        var value = Enumerable.Range(0, 20).Select(i => (byte)(0x60 + i)).ToArray();
        server.ObjectDictionary.AddDomain(Index, 0x00, value, OdAccess.ReadOnly);

        FromClient(bus, SdoFrames.BuildUploadInit(Index, 0x00));
        Next(toClient)[0].Should().Be(SdoFrames.ScsUploadInitSegmented);

        // Bytes 1..3 spell this very transfer's object, so an abort that read them as a
        // multiplexer would name it.
        var frame = stray switch
        {
            "a block sub-block ack" => SdoBlockFrames.BuildSubBlockAck(SdoBlockFrames.CcsBlockUploadSubBlockAck, 0x00, 0x21),
            "a block upload start" => SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart),
            "a block download end" => SdoBlockFrames.BuildEnd(SdoBlockFrames.CcsBlockDownloadEndBase, 0, 0),
            _ => new byte[] { 0xE0, 0, 0, 0, 0, 0, 0, 0 },
        };
        frame[1] = (byte)(Index & 0xFF);
        frame[2] = (byte)(Index >> 8);
        frame[3] = 0x00;
        FromClient(bus, frame);
        var answer = Next(toClient);
        answer[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(answer).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid);
        SdoFrames.ReadIndex(answer).Should().Be(((ushort)0, (byte)0), "the frame carries no multiplexer");

        FromClient(bus, SdoFrames.BuildSegment(SdoFrames.CcsUploadSegmentBase, toggle: false, lastSegment: false,
            ReadOnlySpan<byte>.Empty));
        var segment = Next(toClient);
        segment[0].Should().Be(SdoFrames.ScsUploadSegmentBase, "the upload is still open: its first segment, toggle 0");
        segment.Skip(1).Should().Equal(value.Take(7));
    }

    // The other half of C-2's rule: a frame that could belong to the open session ends it. A
    // download segment during a segmented upload is the classic protocol gone wrong, and the
    // abort names the session's object -- a segment carries no multiplexer of its own.
    [Fact]
    public void A_Segment_Of_The_Other_Direction_Aborts_The_Open_Session_By_Its_Object()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-stray-direction-{Guid.NewGuid():N}");
        using var toClient = TapResponses(bus);
        using var server = OpenOnStoppedClock(bus);
        server.ObjectDictionary.AddDomain(Index, 0x00, new byte[20], OdAccess.ReadOnly);

        FromClient(bus, SdoFrames.BuildUploadInit(Index, 0x00));
        Next(toClient)[0].Should().Be(SdoFrames.ScsUploadInitSegmented);

        FromClient(bus, SdoFrames.BuildSegment(SdoFrames.CcsDownloadSegmentBase, toggle: false, lastSegment: false,
            new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 }));
        var answer = Next(toClient);
        answer[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(answer).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid);
        SdoFrames.ReadIndex(answer).Should().Be((Index, (byte)0x00), "the abort names the transfer it ends");

        // The session is gone: its segment request now meets no session.
        FromClient(bus, SdoFrames.BuildSegment(SdoFrames.CcsUploadSegmentBase, toggle: false, lastSegment: false,
            ReadOnlySpan<byte>.Empty));
        var after = Next(toClient);
        after[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadIndex(after).Should().Be(((ushort)0, (byte)0));
    }

    public static TheoryData<string, string> MalformedInitiates => new()
    {
        { "classic", "the open transfer" },
        { "classic", "another object" },
        { "classic", "another sub-index of the object" },
        { "block", "the open transfer" },
        { "block", "another object" },
        { "block", "another sub-index of the object" },
    };

    // An upload initiate with reserved bits set (ccs = 2, here 0x44) is the one frame that reaches
    // the unknown-specifier answer carrying a multiplexer, and the abort names it. A client that
    // matches aborts by object ends its transfer on that abort, so when the abort names the open
    // session that session ends here too -- otherwise the next initiate meets a supersede abort
    // for a transfer the client already gave up (Codex on #392). An abort naming anything else,
    // down to the sub-index, ends nothing.
    [Theory]
    [MemberData(nameof(MalformedInitiates))]
    public void A_Malformed_Initiate_Ends_The_Open_Session_Only_When_Its_Abort_Names_It(string session, string named)
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-stray-initiate-{Guid.NewGuid():N}");
        using var toClient = TapResponses(bus);
        using var server = OpenOnStoppedClock(bus);
        var value = Enumerable.Range(0, 20).Select(i => (byte)(0x60 + i)).ToArray();
        server.ObjectDictionary.AddDomain(Index, 0x00, value, OdAccess.ReadOnly);

        bool block = session == "block";
        if (block)
        {
            FromClient(bus, SdoBlockFrames.BuildBlockUploadInit(Index, 0x00, clientCrcSupported: false, blockSize: 3, pst: 0));
            (Next(toClient)[0] & 0xE0).Should().Be(SdoBlockFrames.ScsBlockUploadInitResponseBase);
        }
        else
        {
            FromClient(bus, SdoFrames.BuildUploadInit(Index, 0x00));
            Next(toClient)[0].Should().Be(SdoFrames.ScsUploadInitSegmented);
        }

        var (targetIndex, targetSub) = named switch
        {
            "another object" => ((ushort)(Index + 1), (byte)0x00),
            "another sub-index of the object" => (Index, (byte)0x01),
            _ => (Index, (byte)0x00),
        };
        var malformed = SdoFrames.BuildUploadInit(targetIndex, targetSub);
        malformed[0] = 0x44;
        FromClient(bus, malformed);
        var answer = Next(toClient);
        answer[0].Should().Be(SdoFrames.CsAbort);
        SdoFrames.ReadAbortCode(answer).Should().Be((uint)SdoAbortCode.CommandSpecifierInvalid);
        SdoFrames.ReadIndex(answer).Should().Be((targetIndex, targetSub), "the frame names its object");

        if (named != "the open transfer")
        {
            if (block)
            {
                FromClient(bus, SdoBlockFrames.BuildEndResponse(SdoBlockFrames.CcsBlockUploadStart));
                Next(toClient)[0].Should().Be(0x01, "the open block upload goes on: segment 1");
            }
            else
            {
                FromClient(bus, SdoFrames.BuildSegment(SdoFrames.CcsUploadSegmentBase, toggle: false, lastSegment: false,
                    ReadOnlySpan<byte>.Empty));
                Next(toClient)[0].Should().Be(SdoFrames.ScsUploadSegmentBase, "the open upload goes on");
            }
            return;
        }

        FromClient(bus, SdoFrames.BuildUploadInit(Index, 0x00));
        Next(toClient)[0].Should().Be(SdoFrames.ScsUploadInitSegmented,
            "the abort ended the session, so there is none left to supersede");
    }

    // The server's session timeout (SdoServerTimeout, 5 s) runs on a clock nothing moves (#362,
    // #405). On the wall clock a runner that stalls the test between two frames for longer than
    // that aborts the open session with 0504 0000h, and the test reads that abort, or finds no
    // session, where it asserts the answer to its own frame.
    private static CanOpenNode OpenOnStoppedClock(ControllableBus bus)
        => new(new CanBusService(bus), ServerNodeId, new CanOpenNodeOptions(), ownsService: true,
            timeSource: new ManualTimeSource());

    private static BlockingCollection<byte[]> TapResponses(ControllableBus bus)
    {
        var toClient = new BlockingCollection<byte[]>();
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoTx(ServerNodeId)) toClient.Add(frame.Data.ToArray());
        };
        return toClient;
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
