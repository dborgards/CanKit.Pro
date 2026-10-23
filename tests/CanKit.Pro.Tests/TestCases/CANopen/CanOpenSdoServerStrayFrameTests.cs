using System;
using System.Collections.Concurrent;
using System.Linq;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
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
        using var server = CanOpen.OpenNode(bus, ServerNodeId);
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
