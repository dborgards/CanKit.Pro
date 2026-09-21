using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Uds;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// #92 step 1. The P2 budget used to be enforced only by a race: <c>ReceiveWithTimeoutAsync</c>
/// arms a <see cref="CancellationTokenSource"/> for the remaining budget and waits on the
/// channel, and whichever of the two completes the wait first decides. A host that delays the
/// deadline callback past the response's arrival lets the response win, and the client returns
/// data for a request it had already abandoned.
///
/// Every test here drives the client through a channel double instead of a loaded runner, so
/// the ordering the CI failures produced by accident is produced here on purpose.
/// </summary>
public class UdsExpiredDeadlineTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(80);

    private static IUdsClient NewClient(StubChannel channel) => UdsClient.Create(
        channel,
        new UdsClientOptions
        {
            P2ClientMax = Budget,
            P2StarClientMax = Budget,
        });

    /// <summary>
    /// The response arrives after the budget, and the wait is completed by its arrival rather
    /// than by the deadline — the channel double never observes the token, which is what a
    /// delayed timer callback amounts to. The client must still time out.
    /// </summary>
    [Fact]
    public async Task A_Late_Response_That_Wins_The_Race_Is_Still_A_Timeout()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(240),
            stampArrivalAtDelivery: true);
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        await act.Should().ThrowAsync<UdsTimeoutException>(
            "a response that arrived after P2 must not answer the request it was too late for");
    }

    /// <summary>
    /// The mirror case, and the reason the check reads the arrival stamp rather than asking
    /// "am I past the deadline now": the response arrives well inside the budget but the client
    /// only gets to look at it afterwards. That is a punctual response and must be accepted, or
    /// the fix would trade a rare wrong accept for a frequent wrong reject under load.
    /// </summary>
    [Fact]
    public async Task B_A_Punctual_Response_Observed_Late_Is_Still_Accepted()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(240),
            stampArrivalAtDelivery: false);
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// P2* restarts when NRC 0x78 arrives, and it must restart from that response's *arrival*.
    /// Restarting from "now" would hand the ECU whatever scheduling delay the client just
    /// suffered on top of its budget — here the 0x78 arrives at 0 ms and the final response
    /// 200 ms later, which against an 80 ms P2* is late however long the client took to notice.
    /// </summary>
    [Fact]
    public async Task C_P2Star_Restarts_From_The_Pending_Response_Arrival()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(20),
            stampArrivalAtDelivery: false)
        {
            RespondPendingFirst = true,
            PendingToFinalArrivalGap = TimeSpan.FromMilliseconds(200),
            // The client is descheduled between the two: it sees the 0x78 long after it landed.
            ObservationDelayAfterPending = TimeSpan.FromMilliseconds(150),
        };
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        await act.Should().ThrowAsync<UdsTimeoutException>(
            "P2* runs from when the pending response arrived, not from when it was read");
    }

    /// <summary>
    /// The other half of C, and the regression the P2* restart introduced on its own: the same
    /// deschedule, but the final response arrived 40 ms after the 0x78 — well inside the 80 ms
    /// P2*. Measuring the remaining budget from the 0x78's arrival makes it negative before the
    /// client ever looks at the inbox, so the wait must not turn that into a timeout without
    /// looking. Only the arrival stamp decides, and it says this response was punctual.
    /// </summary>
    [Fact]
    public async Task D_A_Punctual_Response_Queued_While_P2Star_Ran_Out_Is_Still_Read()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(20),
            stampArrivalAtDelivery: false)
        {
            RespondPendingFirst = true,
            PendingToFinalArrivalGap = TimeSpan.FromMilliseconds(40),
            ObservationDelayAfterPending = TimeSpan.FromMilliseconds(150),
        };
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// The same wrong reject reached the other way: the wait is completed by the deadline
    /// callback — the channel double honours the token here — while a punctual response is
    /// already queued behind it. Which of the two the runtime happens to run first is not a fact
    /// about the response, so the timeout is only real once the inbox is empty.
    /// </summary>
    [Fact]
    public async Task E_A_Punctual_Response_Queued_When_The_Deadline_Fires_Is_Still_Read()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(240),
            stampArrivalAtDelivery: false)
        {
            HonorCancellation = true,
        };
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// P2 starts when the request was transmitted, and the client may not read that instant off
    /// its own clock: awaiting the send returns behind the bus TX confirmation, an actor hop and
    /// the thread pool, all of it after the peer already has the request. Here the request is on
    /// the wire immediately, the send is observed 200 ms later, and the response arrived 240 ms
    /// after transmission against an 80 ms P2. Timed from the transmit stamp it is late by
    /// 160 ms; timed from the client's own clock it measures 40 ms and is wrongly accepted —
    /// which is what CI recorded on macOS while every other leg was green.
    /// </summary>
    [Fact]
    public async Task F_A_Late_Response_Is_Late_However_Long_The_Send_Took_To_Be_Observed()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(20),
            stampArrivalAtDelivery: false)
        {
            SendObservationDelay = TimeSpan.FromMilliseconds(200),
            ResponseArrivalOffsetFromTransmit = TimeSpan.FromMilliseconds(240),
        };
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        await act.Should().ThrowAsync<UdsTimeoutException>(
            "P2 runs from when the request went out, not from when the client noticed it had");
    }

    /// <summary>
    /// The mirror, and the reason the fix is a stamp rather than simply starting the budget
    /// before the send: transmission time is not scheduling, and P2 genuinely starts after it —
    /// the ECU cannot answer a request it has not finished receiving. Here transmission takes
    /// 120 ms and the response arrived 60 ms after it, inside the 80 ms P2. Starting the budget
    /// when the send was requested would make that 180 ms and reject a punctual response.
    /// </summary>
    [Fact]
    public async Task G_Transmission_Time_Shifts_P2_Because_The_Ecu_Cannot_Answer_Sooner()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(20),
            stampArrivalAtDelivery: false)
        {
            TransmissionTime = TimeSpan.FromMilliseconds(120),
            ResponseArrivalOffsetFromTransmit = TimeSpan.FromMilliseconds(60),
        };
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// A channel that reports no transmit instant — zero — must not be read as a timestamp: zero
    /// means 1970 to a monotonic subtraction, so every response would measure as infinitely late
    /// and every request would time out. The client falls back to its own clock there, which is
    /// the behaviour this branch replaces, and worse only in the way that behaviour was worse.
    /// Reachable from outside the repository, because <c>IsoTp.Open</c> and
    /// <see cref="IIsoTpChannel"/> are public.
    /// </summary>
    [Fact]
    public async Task H_A_Channel_Reporting_No_Transmit_Instant_Falls_Back_To_The_Client_Clock()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(20),
            stampArrivalAtDelivery: false)
        {
            ReportNoTransmitStamp = true,
        };
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// Answers one positive RDBI response, ignoring the cancellation token so the delivery — not
    /// the deadline — completes the caller's wait.
    /// </summary>
    /// <summary>
    /// A multi-frame response whose First Frame arrived <em>before</em> the request reached the
    /// wire answers an earlier request, however punctual its completion looks: a negative
    /// interval clamps to zero, which would read as "in time" both while the reception is in
    /// progress and once the PDU is delivered (Codex on #143). Here the First Frame predates
    /// the transmit stamp by 20 ms and the transfer completes after the budget; the client must
    /// neither wait for it nor accept it.
    /// </summary>
    [Fact]
    public async Task I_A_Reception_That_Began_Before_The_Request_Went_Out_Is_Not_Waited_For()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(400),
            stampArrivalAtDelivery: true)
        {
            HonorCancellation = true,
            FirstFrameOffsetFromTransmit = TimeSpan.FromMilliseconds(-20),
        };
        using var client = NewClient(channel);

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);
        await act.Should().ThrowAsync<UdsTimeoutException>(
            "a response that began before the request was sent is an earlier request's");
        sw.Stop();

        // Timed out at the budget, not once the stale transfer completed: the 400 ms delivery
        // is a lower bound on what waiting for it would cost, against an 80 ms budget.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(400),
            "the stale reception must not extend the budget");
    }

    /// <summary>
    /// The same stale response, completing <em>inside</em> the budget: it is delivered, and the
    /// delivered PDU's first-frame stamp says it began before the request. A stray, not the
    /// answer.
    /// </summary>
    [Fact]
    public async Task J_A_Delivered_Response_That_Began_Before_The_Request_Went_Out_Is_A_Stray()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(30),
            stampArrivalAtDelivery: true)
        {
            HonorCancellation = true,
            FirstFrameOffsetFromTransmit = TimeSpan.FromMilliseconds(-20),
        };
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        await act.Should().ThrowAsync<UdsTimeoutException>(
            "a response that began before the request was sent answers an earlier request");
    }

    /// <summary>
    /// #146 — the transmit stamp is "no later than the driver accepted the frame", taken after a
    /// synchronous delivery or a completion callback, and a fast peer's answer can be stamped by
    /// the demux <em>before</em> it. Such a response is the request's, and a rule keyed on the
    /// transmit stamp rejected it as an earlier request's. Here the last frame's driver call
    /// took 2 ms and the response is stamped 1 ms before the acceptance stamp.
    /// </summary>
    [Fact]
    public async Task K_A_Response_Stamped_Before_The_Transmit_Confirmation_Is_Still_The_Response()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(5),
            stampArrivalAtDelivery: false)
        {
            TransmissionTime = TimeSpan.FromMilliseconds(20),
            LastFrameHandoffBeforeTransmit = TimeSpan.FromMilliseconds(2),
            ResponseArrivalOffsetFromTransmit = TimeSpan.FromMilliseconds(-1),
        };
        using var client = NewClient(channel);

        var data = await client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        data.Should().Equal(0xAA);
    }

    /// <summary>
    /// Codex on #147 — the cutoff for "an earlier request's response" is the channel's handoff
    /// of the request's <em>last</em> frame: a peer answers only a complete request, and a late
    /// response to the previous request that arrives while this one is still being sent is not
    /// this one's, whether it arrives after the client entered the channel or after the first
    /// frame went out. Here the request took 20 ms to send, the last frame was handed over 5 ms
    /// before the acceptance stamp, and the stale response arrived 10 ms before it — during the
    /// transmission.
    /// </summary>
    [Fact]
    public async Task L_A_Response_From_Before_The_Last_Frame_Handoff_Is_A_Stray()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromMilliseconds(5),
            stampArrivalAtDelivery: false)
        {
            TransmissionTime = TimeSpan.FromMilliseconds(20),
            LastFrameHandoffBeforeTransmit = TimeSpan.FromMilliseconds(5),
            ResponseArrivalOffsetFromTransmit = TimeSpan.FromMilliseconds(-10),
            HonorCancellation = true,
            FirstFrameOffsetFromTransmit = TimeSpan.FromMilliseconds(-10),
        };
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);

        await act.Should().ThrowAsync<UdsTimeoutException>(
            "a response that arrived before the request's last frame was handed over is an earlier request's");
    }

    /// <summary>
    /// #57 — <see cref="UdsTimeoutException.Elapsed"/> is the budget of the timer that expired
    /// on every path. Here the send's own await returns after the budget is spent and nothing
    /// is queued: the zero-remaining exit, which used to report how late the client noticed.
    /// </summary>
    [Fact]
    public async Task M_A_Timeout_Reports_The_Budget_Not_How_Late_The_Client_Noticed()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromSeconds(5),
            stampArrivalAtDelivery: true)
        {
            SendObservationDelay = TimeSpan.FromMilliseconds(160),
        };
        using var client = NewClient(channel);

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<UdsTimeoutException>()).Which;

        ex.Elapsed.Should().Be(Budget);
    }

    /// <summary>
    /// #57 — Dispose waits for a request in flight to release the lock; when it does not in
    /// time, the semaphore is left undisposed, so the holder's eventual Release does not throw
    /// ObjectDisposedException into an operation that was merely slow.
    /// </summary>
    [Fact]
    public async Task N_Dispose_Leaves_The_Lock_To_A_Holder_That_Outlasts_The_Wait()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.Zero,
            stampArrivalAtDelivery: true)
        { Gate = gate };
        using var client = NewClient(channel); // a second Dispose is idempotent
        ((UdsClientImpl)client).DisposeLockTimeout = TimeSpan.FromMilliseconds(100);

        var inFlight = client.ReadDataByIdentifierAsync(0xF190, CancellationToken.None);
        await Task.Delay(50); // let the request take the lock and block in the receive

        client.Dispose(); // returns after its 100 ms wait, the holder still inside

        gate.SetResult(true);
        Func<Task> act = () => inFlight;
        await act.Should().NotThrowAsync<ObjectDisposedException>(
            "the holder's Release must find its semaphore intact");
    }

    /// <summary>
    /// Codex on #150 — a suppressed send cancelled between the driver's acceptance and the
    /// confirmation is on the bus and may still be answered: its window is noted before the
    /// send, so the next request for the service still waits it out.
    /// </summary>
    [Fact]
    public async Task O_A_Suppressed_Send_Cancelled_Before_Confirmation_Still_Opens_Its_Window()
    {
        using var channel = new StubChannel(
            deliverAfter: TimeSpan.FromSeconds(5),
            stampArrivalAtDelivery: true)
        {
            TransmissionTime = TimeSpan.FromMilliseconds(200),
            CancellableSend = true,
            HonorCancellation = true,
        };
        using var client = NewClient(channel);

        using var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Func<Task> cancelled = () => client.SendRawAsync(new byte[] { 0x3E, 0x80 }, early.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        // The next TesterPresent must wait the suppressed send's window (P2 = 80 ms from the
        // note, taken just before the first send began) before it sends.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Func<Task> next = () => client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        await next.Should().ThrowAsync<Exception>(); // the stub never answers; what matters is when it sent

        long gapTicks;
        lock (channel.Sent)
        {
            channel.Sent.Should().HaveCount(2);
            gapTicks = channel.Sent[1].StartedAt - channel.Sent[0].StartedAt;
        }
        // The cancelled send started at ~0 ms and was cancelled at 30 ms; without the window the
        // second would start right then. 80 ms less a margin for the note preceding the send.
        TimeSpan.FromSeconds((double)gapTicks / Stopwatch.Frequency).Should().BeGreaterThanOrEqualTo(
            Budget - TimeSpan.FromMilliseconds(5),
            "the second send waited out the window the cancelled send opened");
    }

    private sealed class StubChannel : IIsoTpChannel
    {
        private static readonly byte[] Response = { 0x62, 0xF1, 0x90, 0xAA };

        private static readonly byte[] Pending = { 0x7F, 0x22, 0x78 };

        private readonly TimeSpan _deliverAfter;
        private readonly bool _stampArrivalAtDelivery;
        private long _arrivalStamp;
        private bool _pendingSent;

        /// <summary>Answer NRC 0x78 first, then the positive response.</summary>
        public bool RespondPendingFirst { get; init; }

        /// <summary>How much later than the 0x78 the final response arrived.</summary>
        public TimeSpan PendingToFinalArrivalGap { get; init; }

        /// <summary>How long the client is kept from noticing the 0x78 after it arrived.</summary>
        public TimeSpan ObservationDelayAfterPending { get; init; }

        /// <summary>
        /// Observe the cancellation token, so the deadline — not the delivery — ends the wait,
        /// and leave the response for <see cref="TryReceiveWithArrival"/> to hand over.
        /// </summary>
        public bool HonorCancellation { get; init; }

        /// <summary>How long the request itself takes to reach the wire.</summary>
        public TimeSpan TransmissionTime { get; init; }

        /// <summary>
        /// How long after the request reached the wire the caller's await of the send completes.
        /// Everything the real channel does between those two instants — the bus TX confirmation,
        /// the actor hop, the thread pool — happens when the peer already has the request.
        /// </summary>
        public TimeSpan SendObservationDelay { get; init; }

        /// <summary>How long after the request reached the wire the response arrived.</summary>
        public TimeSpan ResponseArrivalOffsetFromTransmit { get; init; }

        /// <summary>
        /// Report no transmit instant at all, as a foreign <see cref="IIsoTpChannel"/> that does
        /// not track one would. <see cref="IsoTp"/> is public, so this is reachable from outside
        /// the repository.
        /// </summary>
        public bool ReportNoTransmitStamp { get; init; }

        /// <summary>
        /// When set, the response is multi-frame: its First Frame arrived this long after the
        /// request reached the wire (negative: before it), it is reported as a reception in
        /// progress until delivered, and the delivered PDU carries that first-frame stamp.
        /// </summary>
        public TimeSpan? FirstFrameOffsetFromTransmit { get; init; }

        private bool _delivered;
        private long? _deliverAt;

        public StubChannel(TimeSpan deliverAfter, bool stampArrivalAtDelivery)
        {
            _deliverAfter = deliverAfter;
            _stampArrivalAtDelivery = stampArrivalAtDelivery;
        }

        public IsoTpEndpoint Endpoint { get; } = IsoTpEndpoint.Normal(0x7E0, 0x7E8);

        public IsoTpChannelOptions Options { get; } = new();

        /// <summary>
        /// How long before the transmit stamp the last frame was handed to the driver: the
        /// driver call's own duration, which on an in-process bus includes the peer's answer.
        /// Defaults to zero.
        /// </summary>
        public TimeSpan LastFrameHandoffBeforeTransmit { get; init; }

        /// <summary>When set, a receive blocks here first, ignoring cancellation (#57).</summary>
        public TaskCompletionSource<bool>? Gate { get; init; }

        /// <summary>Requests sent, in order, with the instant each send began (#150).</summary>
        public List<(byte[] Request, long StartedAt)> Sent { get; } = new();

        /// <summary>When set, a send observes the token while it waits for its confirmation.</summary>
        public bool CancellableSend { get; init; }

        public async Task<IsoTpTransmitStamps> SendWithTransmitStampAsync(ReadOnlyMemory<byte> pdu,
            CancellationToken cancellationToken = default)
        {
            lock (Sent) Sent.Add((pdu.ToArray(), Stopwatch.GetTimestamp()));
            if (TransmissionTime > TimeSpan.Zero)
                await Task.Delay(TransmissionTime, CancellableSend ? cancellationToken : CancellationToken.None).ConfigureAwait(false);

            // The wire instant. Everything the client is entitled to measure is relative to this
            // and to nothing else; arrival is "now" for the punctual case, inside the budget and
            // long before delivery.
            _arrivalStamp = Stopwatch.GetTimestamp();

            if (SendObservationDelay > TimeSpan.Zero)
                await Task.Delay(SendObservationDelay, CancellationToken.None).ConfigureAwait(false);

            if (ReportNoTransmitStamp) return default;
            return new IsoTpTransmitStamps(_arrivalStamp - Ticks(LastFrameHandoffBeforeTransmit), _arrivalStamp);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> pdu,
            CancellationToken cancellationToken = default)
            => await SendWithTransmitStampAsync(pdu, cancellationToken).ConfigureAwait(false);

        public async Task<IsoTpReceivedPdu> ReceiveWithArrivalAsync(
            CancellationToken cancellationToken = default)
        {
            if (Gate is not null)
                await Gate.Task.ConfigureAwait(false);

            // Deliberately not observing the token: this models the write winning the race.
            if (RespondPendingFirst && !_pendingSent)
            {
                _pendingSent = true;
                await Task.Delay(ObservationDelayAfterPending, CancellationToken.None)
                    .ConfigureAwait(false);
                return new IsoTpReceivedPdu(Pending, _arrivalStamp);
            }

            // Delivery is an instant, not a duration per call: a caller that waits in slices
            // and comes back is still waiting for the same delivery.
            _deliverAt ??= Stopwatch.GetTimestamp() + Ticks(_deliverAfter);
            var remaining = TimeSpan.FromSeconds(
                (_deliverAt.Value - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining,
                    HonorCancellation ? cancellationToken : CancellationToken.None)
                    .ConfigureAwait(false);

            if (RespondPendingFirst)
                return new IsoTpReceivedPdu(Response, FinalArrivalStamp());

            var stamp = _stampArrivalAtDelivery
                ? Stopwatch.GetTimestamp()
                : _arrivalStamp + Ticks(ResponseArrivalOffsetFromTransmit);
            _delivered = true;
            return new IsoTpReceivedPdu(Response, stamp, FirstFrameStamp() ?? stamp);
        }

        private long? FirstFrameStamp()
            => FirstFrameOffsetFromTransmit is { } offset ? _arrivalStamp + Ticks(offset) : null;

        /// <summary>
        /// Models the final response already sitting in the inbox once the 0x78 has been read:
        /// a caller past its deadline gets it handed over without waiting, and its stamp — not
        /// the caller's clock — decides whether it counts.
        /// </summary>
        private bool _handedOver;

        public bool TryReceiveWithArrival(out IsoTpReceivedPdu pdu)
        {
            // An inbox empties: the one queued response is handed over once (#150 -- a drain
            // that reads until the inbox is empty would otherwise never end here).
            if (!_handedOver && RespondPendingFirst && _pendingSent)
            {
                _handedOver = true;
                pdu = new IsoTpReceivedPdu(Response, FinalArrivalStamp());
                return true;
            }

            if (!_handedOver && HonorCancellation && FirstFrameOffsetFromTransmit is null)
            {
                // Stamped at the request: punctual, and waiting in the inbox all along.
                _handedOver = true;
                pdu = new IsoTpReceivedPdu(Response, _arrivalStamp);
                return true;
            }

            pdu = default;
            return false;
        }

        public IReadOnlyList<IsoTpReceptionInProgress> GetReceptionsInProgress()
            => FirstFrameStamp() is { } first && !_delivered
                ? new[] { new IsoTpReceptionInProgress(first, Response.Length, Response.AsMemory(0, 3)) }
                : Array.Empty<IsoTpReceptionInProgress>();

        private long FinalArrivalStamp() => _arrivalStamp + Ticks(PendingToFinalArrivalGap);

        private static long Ticks(TimeSpan span)
            => (long)(span.TotalSeconds * Stopwatch.Frequency);

        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
            => (await ReceiveWithArrivalAsync(cancellationToken).ConfigureAwait(false)).Pdu;

        public int DiscardPendingPdus() => 0;

        public IAsyncEnumerable<byte[]> ReceiveAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public event EventHandler<IsoTpDatagramReceivedEventArgs>? DatagramReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add { }
            remove { }
        }

        public void Dispose() { }
    }
}
