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
/// Both tests drive the client through a channel double instead of a loaded runner, so the
/// ordering the CI failures produced by accident is produced here on purpose.
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
    /// Answers one positive RDBI response, ignoring the cancellation token so the delivery — not
    /// the deadline — completes the caller's wait.
    /// </summary>
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

        public StubChannel(TimeSpan deliverAfter, bool stampArrivalAtDelivery)
        {
            _deliverAfter = deliverAfter;
            _stampArrivalAtDelivery = stampArrivalAtDelivery;
        }

        public IsoTpEndpoint Endpoint { get; } = IsoTpEndpoint.Normal(0x7E0, 0x7E8);

        public IsoTpChannelOptions Options { get; } = new();

        public Task SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
        {
            // Arrival is "now" for the punctual case: inside the budget, long before delivery.
            _arrivalStamp = Stopwatch.GetTimestamp();
            return Task.CompletedTask;
        }

        public async Task<IsoTpReceivedPdu> ReceiveWithArrivalAsync(
            CancellationToken cancellationToken = default)
        {
            // Deliberately not observing the token: this models the write winning the race.
            if (RespondPendingFirst && !_pendingSent)
            {
                _pendingSent = true;
                await Task.Delay(ObservationDelayAfterPending, CancellationToken.None)
                    .ConfigureAwait(false);
                return new IsoTpReceivedPdu(Pending, _arrivalStamp);
            }

            await Task.Delay(_deliverAfter, CancellationToken.None).ConfigureAwait(false);

            if (RespondPendingFirst)
            {
                var late = _arrivalStamp
                    + (long)(PendingToFinalArrivalGap.TotalSeconds * Stopwatch.Frequency);
                return new IsoTpReceivedPdu(Response, late);
            }

            var stamp = _stampArrivalAtDelivery ? Stopwatch.GetTimestamp() : _arrivalStamp;
            return new IsoTpReceivedPdu(Response, stamp);
        }

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
