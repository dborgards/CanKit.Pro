using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// A client SDO request the bus does not send fails its transfer at once with
/// <see cref="CanOpenTransportException"/>, rather than by the SDO timeout (#197, FR-CO-034). The
/// failure is still raised on <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> as well.
/// </summary>
/// <remarks>
/// The SDO timeout is set far past the test's own bound, so a transfer that is left to time out
/// fails the test instead of passing it late.
/// </remarks>
public class CanOpenSdoClientSendFailureTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    public static TheoryData<string> Transfers => new() { "upload", "download", "block upload" };

    [Theory]
    [MemberData(nameof(Transfers))]
    public async Task A_Rejected_Request_Fails_The_Transfer_With_The_Transport_Error(string transfer)
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-send-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var reported = new ConcurrentQueue<Exception>();
        client.BackgroundExceptionOccurred += (_, ex) => reported.Enqueue(ex);
        bus.AcceptTransmit = false;

        Func<Task> act = transfer switch
        {
            "upload" => () => client.SdoUploadAsync(0x11, 0x1000, 0x00),
            "download" => () => client.SdoDownloadAsync(0x11, 0x1000, 0x00, new byte[] { 1, 2, 3, 4 }),
            _ => () => client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block),
        };

        await FluentActions.Awaiting(() => act().WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
        reported.Should().Contain(ex => ex is CanOpenTransportException);
    }

    public static TheoryData<string> Uploads => new() { "upload", "block upload" };

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_Confirmation_That_Fails_After_The_Sdo_Timeout_Still_Ends_The_Transfer(string transfer)
    {
        // The confirmation window (CanBusService.DefaultConfirmTimeout) is longer than this SDO
        // timeout, and the echo is never delivered: the timeout elapses first, then the send
        // fails. Nothing reached the server, so the transfer must end with the send failure.
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-late-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        Func<Task> act = transfer == "upload"
            ? () => client.SdoUploadAsync(0x11, 0x1000, 0x00)
            : () => client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block);

        await FluentActions.Awaiting(() => act().WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_Confirmation_That_Arrives_After_The_Sdo_Timeout_Leaves_The_Timeout_Standing(string transfer)
    {
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-late-ok-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        var upload = transfer == "upload"
            ? client.SdoUploadAsync(0x11, 0x1000, 0x00)
            : client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        // Whether or not the SDO timeout has elapsed by now, a confirmed request that nobody
        // answers ends in the timeout: the send outcome only overrides it when the send failed.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        bus.DeferredEchoes.ReleaseAll();

        var abort = (await FluentActions.Awaiting(() => upload.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<SdoAbortException>()).Which;
        abort.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
        abort.Origin.Should().Be(SdoAbortOrigin.Local);
    }

    [Fact]
    public async Task An_Answer_After_The_Sdo_Timeout_Does_Not_Revive_The_Transfer()
    {
        // The SDO timeout has elapsed with the request unconfirmed. An answer arriving then is
        // not taken: the transfer is waiting only for its send's outcome.
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-late-answer-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(50) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        var upload = client.SdoUploadAsync(0x11, 0x1000, 0x00);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // well past the 50 ms SDO timeout
        bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
            new byte[] { 0x43, 0x00, 0x10, 0x00, 0x91, 0x01, 0x0F, 0x00 }), isEcho: false);
        bus.DeferredEchoes.ReleaseAll();

        var abort = (await FluentActions.Awaiting(() => upload.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<SdoAbortException>()).Which;
        abort.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
    }

    [Fact]
    public async Task A_Transmit_That_Throws_Fails_The_Transfer_With_A_Transport_Error()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-send-throws-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID == CanOpenCobId.SdoRx(0x11)) throw new InvalidOperationException("adapter fault");
        };

        await FluentActions.Awaiting(() => client.SdoUploadAsync(0x11, 0x1000, 0x00).WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Block_Segment_Whose_Transmit_Throws_Fails_The_Transfer()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-seg-throws-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var accepted = 0;
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID != CanOpenCobId.SdoRx(0x11)) return;
            if ((frame.Data.Span[0] & 0xE1) == SdoBlockFrames.CcsBlockDownloadInitBase && Interlocked.Exchange(ref accepted, 1) == 0)
            {
                _ = Task.Run(() => bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
                    SdoBlockFrames.BuildBlockDownloadInitResponse(0x1000, 0x00, serverCrcSupported: false, blockSize: 127)),
                    isEcho: false));
                return;
            }

            throw new InvalidOperationException("adapter fault");
        };

        await FluentActions.Awaiting(() => client.SdoDownloadAsync(0x11, 0x1000, 0x00, new byte[20], SdoTransferMode.Block)
                .WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Send_Outcome_After_Dispose_Is_Dropped()
    {
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-outcome-dispose-{Guid.NewGuid():N}");
        var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        var upload = client.SdoUploadAsync(0x11, 0x1000, 0x00);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        client.Dispose();
        bus.DeferredEchoes.ReleaseAll(); // the confirmation now lands on a disposed node

        await FluentActions.Awaiting(() => upload.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<ObjectDisposedException>("disposal completed the transfer, and nothing else reports it");
    }

    [Fact]
    public async Task A_Service_Disposed_Under_A_Pending_Send_Fails_The_Transfer()
    {
        // Disposing the shared service cancels the pending send: the frame was not confirmed,
        // and the transfer must hear that rather than wait for its SDO timeout.
        using var bus = ControllableBus.DeferredEchoCapable($"canopen-sdo-service-dispose-{Guid.NewGuid():N}");
        var service = new CanBusService(bus);
        using var client = CanOpen.OpenNode(service, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) }, leaveOpen: true);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(1, ShortTimeout); // the boot-up
        bus.DeferredEchoes.ReleaseAll();

        var upload = client.SdoUploadAsync(0x11, 0x1000, 0x00);
        await bus.DeferredEchoes.WaitForEnqueuedAsync(2, ShortTimeout);
        service.Dispose();

        await FluentActions.Awaiting(() => upload.WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Lost_Echo_Of_An_Answered_Send_Does_Not_Fail_The_Upload()
    {
        // The server answers the last sub-block ack with its end frame, so the ack reached it.
        // The ack's confirmation then fails -- a lost echo -- while the end acknowledgement is
        // still being sent. That failure must not fail an upload the server has completed. The
        // test decides both confirmations itself, in that order, so no clock is involved.
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-lost-echo-{Guid.NewGuid():N}");
        using var inner = new CanBusService(bus);
        var service = new HeldConfirmationService(inner);
        using var client = CanOpen.OpenNode(service, 0x7F,
            new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) }, leaveOpen: true);
        var server = unchecked((int)CanOpenCobId.SdoTx(0x11));
        var ackConfirmation = new TaskCompletionSource<TxConfirmation>();
        var endAckConfirmation = new TaskCompletionSource<TxConfirmation>();
        var endAckSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Hold = frame =>
        {
            if ((uint)frame.ID != CanOpenCobId.SdoRx(0x11)) return null;
            var cs = frame.Data.Span[0];
            if ((cs & 0xE3) == SdoBlockFrames.CcsBlockUploadInitBase)
            {
                Answer(bus, server, SdoBlockFrames.BuildBlockUploadInitResponse(
                    0x1000, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 4));
                return null;
            }

            if (cs == SdoBlockFrames.CcsBlockUploadStart)
            {
                Answer(bus, server, SdoBlockFrames.BuildSegment(seqno: 1, isLastSegment: true, new byte[] { 1, 2, 3, 4 }));
                return null;
            }

            if (cs == SdoBlockFrames.CcsBlockUploadSubBlockAck)
            {
                Answer(bus, server, SdoBlockFrames.BuildEnd(
                    SdoBlockFrames.ScsBlockUploadEndBase, unusedBytesInLastSegment: 3, crc: 0));
                return ackConfirmation.Task;
            }

            if (cs == SdoBlockFrames.CcsBlockUploadEndResponse)
            {
                endAckSent.TrySetResult(true);
                return endAckConfirmation.Task;
            }

            return null;
        };

        var upload = client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block);
        await endAckSent.Task.WithTimeoutAsync(ShortTimeout);
        // Without RunContinuationsAsynchronously the node's continuation runs inside SetResult,
        // so the ack's outcome is posted to the node before the end acknowledgement's is.
        ackConfirmation.SetResult(new TxConfirmation
        {
            Confirmed = false,
            FailureReason = TxConfirmFailureReason.Timeout,
            Timestamp = DateTime.UtcNow,
        });
        endAckConfirmation.SetResult(new TxConfirmation { Confirmed = true, Timestamp = DateTime.UtcNow });

        var data = await upload.WithTimeoutAsync(ShortTimeout);
        data.Should().Equal(1, 2, 3, 4);
    }

    private static void Answer(ControllableBus bus, int cobId, byte[] payload)
        => _ = Task.Run(() => bus.RaiseObserved(CanFrame.Classic(cobId, payload), isEcho: false));

    /// <summary>
    /// Delegates to a real service, except that <see cref="Hold"/> may hand back the
    /// confirmation of a frame for the test to complete.
    /// </summary>
    private sealed class HeldConfirmationService : ICanBusService
    {
        private readonly ICanBusService _inner;

        public HeldConfirmationService(ICanBusService inner) => _inner = inner;

        public Func<CanFrame, Task<TxConfirmation>?>? Hold { get; set; }

        public CanKit.Abstractions.API.Can.ICanBus Bus => _inner.Bus;

        public int SubscriptionCount => _inner.SubscriptionCount;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false) => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var held = Hold?.Invoke(frame);
            if (held is null) return _inner.SendConfirmed(frame, timeout, cancellationToken);
            _inner.SendConfirmed(frame, timeout, cancellationToken); // the frame still goes out
            return held;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_Block_Upload_Whose_End_Acknowledgement_Is_Rejected_Does_Not_Succeed()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-end-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var server = unchecked((int)CanOpenCobId.SdoTx(0x11));
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID != CanOpenCobId.SdoRx(0x11)) return;
            var cs = frame.Data.Span[0];
            // A server that runs the upload to its end, answering on its own thread.
            Action? answer = null;
            if ((cs & 0xE3) == SdoBlockFrames.CcsBlockUploadInitBase)
            {
                answer = () => bus.RaiseObserved(CanFrame.Classic(server, SdoBlockFrames.BuildBlockUploadInitResponse(
                    0x1000, 0x00, serverCrcSupported: false, sizeIndicated: true, totalSize: 4)), isEcho: false);
            }
            else if (cs == SdoBlockFrames.CcsBlockUploadStart)
            {
                answer = () => bus.RaiseObserved(CanFrame.Classic(server,
                    SdoBlockFrames.BuildSegment(seqno: 1, isLastSegment: true, new byte[] { 1, 2, 3, 4 })), isEcho: false);
            }
            else if (cs == SdoBlockFrames.CcsBlockUploadSubBlockAck)
            {
                answer = () =>
                {
                    // Everything the client sends from here on is rejected: that is only the end
                    // acknowledgement.
                    bus.AcceptTransmit = false;
                    bus.RaiseObserved(CanFrame.Classic(server, SdoBlockFrames.BuildEnd(
                        SdoBlockFrames.ScsBlockUploadEndBase, unusedBytesInLastSegment: 3, crc: 0)), isEcho: false);
                };
            }

            if (answer is not null) _ = Task.Run(answer);
        };

        await FluentActions.Awaiting(() => client.SdoUploadAsync(0x11, 0x1000, 0x00, SdoTransferMode.Block)
                .WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Rejected_Block_Segment_Stops_The_Sub_Block()
    {
        using var bus = ControllableBus.EchoCapable($"canopen-sdo-block-seg-fail-{Guid.NewGuid():N}");
        using var client = CanOpen.OpenNode(bus, 0x7F, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromSeconds(30) });
        var attemptsBeforeSegments = -1;
        bus.OnTransmitting = frame =>
        {
            if ((uint)frame.ID != CanOpenCobId.SdoRx(0x11)
                || (frame.Data.Span[0] & 0xE1) != SdoBlockFrames.CcsBlockDownloadInitBase)
            {
                return;
            }

            // The server accepts the block download; from here on every send is rejected, so
            // the first segment fails and none after it may be attempted.
            _ = Task.Run(() =>
            {
                bus.AcceptTransmit = false;
                Volatile.Write(ref attemptsBeforeSegments, bus.TransmitCount);
                bus.RaiseObserved(CanFrame.Classic(unchecked((int)CanOpenCobId.SdoTx(0x11)),
                    SdoBlockFrames.BuildBlockDownloadInitResponse(0x1000, 0x00, serverCrcSupported: false, blockSize: 127)),
                    isEcho: false);
            });
        };

        // 20 bytes are three segments in one sub-block.
        var payload = new byte[20];
        await FluentActions.Awaiting(() => client.SdoDownloadAsync(0x11, 0x1000, 0x00, payload, SdoTransferMode.Block)
                .WithTimeoutAsync(ShortTimeout))
            .Should().ThrowAsync<CanOpenTransportException>();

        // No event marks "no further segment": give a sender that kept going the time to show it.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        (bus.TransmitCount - Volatile.Read(ref attemptsBeforeSegments)).Should().Be(1,
            "the send loop stops at the first segment the bus rejects");
    }
}
