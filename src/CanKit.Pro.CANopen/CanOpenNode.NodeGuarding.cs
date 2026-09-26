using System;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// Node-Guarding (CiA 301 §7.2.8.3.3, FR-CO-009) partial of <see cref="CanOpenNode"/>. Runs
/// on the actor loop like every other protocol subsystem and shares the heartbeat COB-ID
/// range (<c>0x700 + node-id</c>) with the heartbeat producer/consumer.
/// </summary>
/// <remarks>
/// <para>Consumer role: <see cref="StartNodeGuardingConsumer"/> periodically transmits a
/// remote-transmission-request (RTR) frame on <c>0x700 + producerNodeId</c> and arms a
/// life-time deadline of <c>guardTime × lifeTimeFactor</c>. Every valid response rearms the
/// life-time deadline and raises <see cref="ICanOpenNode.NodeGuardingReceived"/>.</para>
/// <para>Producer role: an RTR arriving on <c>0x700 + our node-id</c> is answered with a
/// one-byte data frame whose bit 7 is the alternating toggle bit and bits 0..6 carry the
/// current NMT state. CiA 301 §7.2.8.3 requires heartbeat and node-guarding to be mutually
/// exclusive on a given producer node; this implementation honours that by refusing to reply
/// while the heartbeat producer is active.</para>
/// </remarks>
internal sealed partial class CanOpenNode
{
    /// <inheritdoc />
    public void StartNodeGuardingConsumer(byte producerNodeId, TimeSpan guardTime, byte lifeTimeFactor)
    {
        ThrowIfDisposed();
        CanOpenCobId.ValidateNodeId(producerNodeId);
        if (guardTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(guardTime), guardTime,
                "guardTime must be positive.");
        if (lifeTimeFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(lifeTimeFactor), lifeTimeFactor,
                "lifeTimeFactor must be >= 1 (CiA 301 §7.2.8.3.3).");

        _actor.Post(() =>
        {
            if (_nodeGuardingConsumers.TryGetValue(producerNodeId, out var existing))
            {
                existing.PollHandle?.Dispose();
                existing.LifeTimeDeadline?.Dispose();
            }
            var consumer = new NodeGuardingConsumer(producerNodeId, guardTime, lifeTimeFactor);
            _nodeGuardingConsumers[producerNodeId] = consumer;
            // Send the first RTR immediately so lifeTimeFactor=1 cannot expire before the
            // initial poll/response is even possible; ScheduleNodeGuardingPoll only arms the
            // subsequent periodic polls after guardTime.
            SendNodeGuardingRtr(producerNodeId);
            ScheduleNodeGuardingPoll(consumer);
            var lifeTime = ScaleLifeTime(guardTime, lifeTimeFactor);
            consumer.LifeTimeDeadline = _deadlines.Arm(lifeTime,
                () => OnNodeGuardingTimeout(producerNodeId));
        });
    }

    /// <inheritdoc />
    public void StopNodeGuardingConsumer(byte producerNodeId)
    {
        if (_disposed != 0) return;
        _actor.Post(() =>
        {
            if (_nodeGuardingConsumers.TryGetValue(producerNodeId, out var consumer))
            {
                consumer.PollHandle?.Dispose();
                consumer.LifeTimeDeadline?.Dispose();
                _nodeGuardingConsumers.Remove(producerNodeId);
            }
        });
    }

    // =========================================================================================
    // Consumer helpers.
    // =========================================================================================
    private void ScheduleNodeGuardingPoll(NodeGuardingConsumer consumer)
    {
        // Subsequent polls fire after each guardTime interval. The initial RTR is sent
        // synchronously from StartNodeGuardingConsumer so the life-time window starts with a
        // real request already on the wire.
        var producer = consumer.ProducerNodeId;
        consumer.PollHandle = _actor.Schedule(consumer.GuardTime, () =>
        {
            try
            {
                if (_disposed != 0) return;
                if (!_nodeGuardingConsumers.TryGetValue(producer, out var current)
                    || !ReferenceEquals(current, consumer))
                {
                    return; // consumer replaced or removed while we slept
                }
                SendNodeGuardingRtr(producer);
            }
            finally
            {
                if (_disposed == 0
                    && _nodeGuardingConsumers.TryGetValue(producer, out var still)
                    && ReferenceEquals(still, consumer))
                {
                    ScheduleNodeGuardingPoll(consumer);
                }
            }
        });
    }

    private void SendNodeGuardingRtr(byte producerNodeId)
    {
        // RTR (remote transmission request) on 0x700 + producer. CiA 301 §7.2.8.3.2.1 Figure 44
        // draws the request with DLC 1, the length of the response it asks for. CanKit derives a
        // frame's DLC from its data length and refuses data on a remote frame
        // (CanFrame.Classic throws), so the RTR this node can send carries DLC 0 — a limitation
        // of the upstream frame model, recorded in #59, not a choice made here. Every producer
        // answers a guarding RTR by CAN-ID, and the reply's own DLC is what matters to the
        // consumer.
        // Preserving IsRemoteFrame end-to-end depends on the reader loop forwarding it into
        // HandleIncoming and on the adapter (Virtual: preserves via Duplicate) round-tripping it.
        var frame = CanFrame.Classic(
            unchecked((int)CanOpenCobId.Heartbeat(producerNodeId)),
            ReadOnlyMemory<byte>.Empty,
            isExtendedFrame: false,
            isRemoteFrame: true);
        var svc = _service;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var conf = await svc.SendConfirmed(frame).ConfigureAwait(false);
                if (!conf.Confirmed)
                {
                    RaiseBackgroundException(new CanOpenTransportException(
                        $"Node-guarding RTR on COB-ID 0x{CanOpenCobId.Heartbeat(producerNodeId):X3} failed: {conf.FailureReason}."));
                }
            }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void OnNodeGuardingTimeout(byte producerNodeId)
    {
        if (!_nodeGuardingConsumers.TryGetValue(producerNodeId, out var consumer)) return;
        // Rearm so subsequent misses still fire.
        consumer.LifeTimeDeadline?.Dispose();
        var lifeTime = ScaleLifeTime(consumer.GuardTime, consumer.LifeTimeFactor);
        consumer.LifeTimeDeadline = _deadlines.Arm(lifeTime,
            () => OnNodeGuardingTimeout(producerNodeId));
        RaiseNodeGuardingTimeout(producerNodeId, consumer.GuardTime, consumer.LifeTimeFactor);
    }

    /// <summary>
    /// Called by <see cref="HandleIncoming"/> when a data frame arrives on
    /// <c>0x700 + producerNodeId</c> and a node-guarding consumer for that producer is
    /// registered. Rearms the life-time deadline and raises the event.
    /// </summary>
    private void HandleNodeGuardingResponse(byte producerNodeId, byte[] data)
    {
        if (data.Length < 1) return;
        if (!_nodeGuardingConsumers.TryGetValue(producerNodeId, out var consumer)) return;

        byte b = data[0];

        // The boot-up message is one byte of 0x00 on this very COB-ID (CiA 301 7.3.2), and it is
        // not a guarding response: it answers no poll, and a node that answers guarding RTRs is
        // never in Initializing -- HandleNmtCommand leaves that state in the same statement pair
        // that enters it, and HandleNodeGuardingRtrForSelf reports whatever _state then holds.
        //
        // Read as a response it seeded LastToggle = false, and the producer's *first real* reply
        // -- also toggle 0, because the producer's toggle starts there -- was then discarded by
        // the alternation check as a repeat (#43). With lifeTimeFactor 1 that costs the entire
        // life-time window, so NodeGuardingTimeout fires while the producer is answering
        // correctly.
        //
        // It is still information, and the right kind: the producer restarted, so its toggle
        // restarts at 0 too. Dropping the baseline rather than ignoring the frame is what makes
        // that next reply acceptable -- "no baseline yet", not "toggle 0". The life-time deadline
        // is deliberately not rearmed: a node that has just restarted has not answered our poll.
        if (b == (byte)NmtState.Initializing)
        {
            consumer.HasSeenResponse = false;
            // The restart still has to be observable. HandleIncoming routes this COB-ID here and
            // returns once a guarding consumer is registered for the producer, so HandleHeartbeat
            // never sees it -- and ICanOpenNode.HeartbeatReceived is documented for "a heartbeat
            // (or bootup) frame". Returning silently made a producer's reset invisible to every
            // subscriber, which the first revision of this fix did (#122, Codex).
            //
            // Raised directly rather than through HandleHeartbeat: that path would also rearm a
            // heartbeat consumer's deadline, which nothing did on this branch before, and a
            // boot-up is not a heartbeat response.
            RaiseHeartbeatReceived(producerNodeId, NmtState.Initializing, DateTime.UtcNow);
            return;
        }

        // An unsolicited frame that is *not* boot-up would still get through here, because the
        // wire carries nothing that distinguishes it from a toggle-0 reply. This node's own
        // producer no longer emits one in the configuration node guarding runs in: the
        // state-change heartbeat of ApplyNmtTransition goes out only while the heartbeat
        // protocol is in use (1017h != 0), and CiA 301 7.2.8.3.2.2 makes the two protocols
        // mutually exclusive on a producer (#43, second half). A foreign producer that does
        // emit such frames is judged by the toggle alone, which is what the norm provides.
        //
        // A one-bit "a poll is outstanding" gate was written here for that and taken back out:
        // it has to be spent by the frame that arrives, and the alternation check below is what
        // decides whether that frame was a reply, so the gate is always spent before the answer
        // is known. A delayed reply or a state-change heartbeat consumed it and the producer's
        // real reply was then dropped outright -- worse than the case it was meant to catch
        // (Bugbot, plus two adjacent findings from Codex on the same mechanism).
        bool toggle = (b & 0x80) != 0;
        byte stateByte = (byte)(b & 0x7F);
        NmtState state = stateByte switch
        {
            0x00 => NmtState.Initializing,      // Bootup / freshly reset.
            0x04 => NmtState.Stopped,
            0x05 => NmtState.Operational,
            0x7F => NmtState.PreOperational,
            _ => NmtState.Initializing,
        };

        // CiA 301 §7.2.8.3.3: a reply that does not alternate the toggle bit is invalid for
        // resetting the life-time window (stale/repeated frames must not keep the consumer
        // alive). The first observed reply establishes the baseline; every later reply must
        // flip bit 7 relative to the previous accepted response. Invalid toggles are dropped
        // entirely — do not raise NodeGuardingReceived for them.
        if (consumer.HasSeenResponse && toggle == consumer.LastToggle)
            return;

        consumer.HasSeenResponse = true;
        consumer.LastToggle = toggle;

        var deadline = consumer.LifeTimeDeadline;
        var lifeTime = ScaleLifeTime(consumer.GuardTime, consumer.LifeTimeFactor);
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled || !deadline.Rearm(lifeTime))
        {
            deadline?.Dispose();
            consumer.LifeTimeDeadline = _deadlines.Arm(lifeTime,
                () => OnNodeGuardingTimeout(producerNodeId));
        }

        RaiseNodeGuardingReceived(producerNodeId, state, toggle, DateTime.UtcNow);
    }

    // =========================================================================================
    // Producer role.
    // =========================================================================================
    private void HandleNodeGuardingRtrForSelf()
    {
        if (!_options.RespondToNodeGuardingRtr) return;

        // CiA 301 §7.2.8.3.2.2: "It is not allowed to use both error control mechanisms guarding
        // protocol and heartbeat protocol on one NMT slave at the same time. If the heartbeat
        // producer time is unequal 0 the heartbeat protocol is used." So while 1017h ≠ 0 the RTR
        // is ignored and the consumer falls back on heartbeat error control.
        if (_heartbeatProducerInterval > TimeSpan.Zero) return;

        byte state = (byte)_state;
        byte payload = (byte)((_nodeGuardingProducerToggle ? 0x80 : 0x00) | (state & 0x7F));
        _nodeGuardingProducerToggle = !_nodeGuardingProducerToggle;
        // The life time first, the reply second: the reply leaves through a thread-pool hop and
        // can be on the bus before this callback has moved on, so anything that reads "the reply
        // is on the wire" as "guarding has started" — a consumer, a test moving a clock — must
        // find the deadline armed by then (#141).
        OnGuardingPollReceived();
        // Through the chain every frame of this node on 0x700 + id goes through: a poll already
        // in the mailbox when a reset ran is answered behind the reset's boot-up, not ahead of it
        // — two toggle-0 frames in the wrong order read as a guarding error (Bugbot on #133).
        _ = EmitHeartbeat(payload);
    }

    // =========================================================================================
    // Producer-side life guarding (CiA 301 §7.2.8.2.2 / §7.2.8.3.2.1, objects 100Ch / 100Dh).
    // =========================================================================================

    /// <summary>
    /// "Guarding starts for the NMT slave when the first RTR for its guarding CAN-ID is
    /// received" (§7.2.8.2.2): every poll re-arms the node life time, and a poll after the
    /// life guarding event had occurred resolves it (§7.2.8.2.2.2).
    /// </summary>
    private void OnGuardingPollReceived()
    {
        if (_guardTime <= TimeSpan.Zero || _lifeTimeFactor == 0) return;
        var lifeTime = ScaleLifeTime(_guardTime, _lifeTimeFactor);
        var deadline = _lifeGuardingDeadline;
        if (deadline is null || deadline.IsExpired || deadline.IsCancelled || !deadline.Rearm(lifeTime))
        {
            deadline?.Dispose();
            _lifeGuardingDeadline = _deadlines.Arm(lifeTime, OnLifeGuardingExpired);
        }
        if (_lifeGuardingOccurred)
        {
            _lifeGuardingOccurred = false;
            RaiseLifeGuardingEvent(LifeGuardingState.Resolved);
        }
    }

    private void OnLifeGuardingExpired()
    {
        if (_guardTime <= TimeSpan.Zero || _lifeTimeFactor == 0) return;
        // One indication per lapse: the event is resolved by the next poll, not repeated by the
        // clock. A master that never returns produces exactly one "occurred".
        _lifeGuardingOccurred = true;
        RaiseLifeGuardingEvent(LifeGuardingState.Occurred);
    }

    /// <summary>100Ch / 100Dh changed in the OD: "The value of 0000h shall disable the life
    /// guarding" (§7.5.2.11), likewise a life time factor of 00h (§7.5.2.12). A running
    /// life time takes the new length; otherwise guarding starts with the next poll.</summary>
    private void ApplyLifeGuardingConfiguration()
    {
        var ms = (ushort)_od.ReadUnsigned(Co.GuardTime, 0x00);
        _guardTime = ms == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        _lifeTimeFactor = (byte)_od.ReadUnsigned(Co.LifeTimeFactor, 0x00);
        if (_guardTime <= TimeSpan.Zero || _lifeTimeFactor == 0)
        {
            ResetLifeGuardingState();
            return;
        }
        if (_lifeGuardingDeadline is { } running && !running.IsExpired && !running.IsCancelled)
        {
            var lifeTime = ScaleLifeTime(_guardTime, _lifeTimeFactor);
            if (!running.Rearm(lifeTime))
            {
                running.Dispose();
                _lifeGuardingDeadline = _deadlines.Arm(lifeTime, OnLifeGuardingExpired);
            }
        }
    }

    private void ResetLifeGuardingState()
    {
        _lifeGuardingDeadline?.Dispose();
        _lifeGuardingDeadline = null;
        _lifeGuardingOccurred = false;
    }

    private void RaiseLifeGuardingEvent(LifeGuardingState state)
    {
        var args = new LifeGuardingEventArgs(state, _guardTime, _lifeTimeFactor);
        EnqueueEvent(() =>
        {
            try { LifeGuardingEvent?.Invoke(this, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private static TimeSpan ScaleLifeTime(TimeSpan guardTime, byte lifeTimeFactor)
    {
        // guardTime * lifeTimeFactor as long-integer ticks; both operands are bounded so
        // overflow is impractical for real-world values, but clamp to TimeSpan.MaxValue just
        // in case an application picks a pathological guardTime.
        long ticks = guardTime.Ticks;
        long scaled;
        try { scaled = checked(ticks * lifeTimeFactor); }
        catch (OverflowException) { scaled = long.MaxValue; }
        return TimeSpan.FromTicks(scaled);
    }

    private void RaiseNodeGuardingReceived(byte producer, NmtState state, bool toggle, DateTime ts)
    {
        var args = new NodeGuardingReceivedEventArgs(producer, state, toggle, ts);
        EnqueueEvent(() =>
        {
            try { NodeGuardingReceived?.Invoke(this, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void RaiseNodeGuardingTimeout(byte producer, TimeSpan guardTime, byte lifeTimeFactor)
    {
        var args = new NodeGuardingTimeoutEventArgs(producer, guardTime, lifeTimeFactor);
        EnqueueEvent(() =>
        {
            try { NodeGuardingTimeout?.Invoke(this, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true);
    }

    private sealed class NodeGuardingConsumer
    {
        public NodeGuardingConsumer(byte producerNodeId, TimeSpan guardTime, byte lifeTimeFactor)
        {
            ProducerNodeId = producerNodeId;
            GuardTime = guardTime;
            LifeTimeFactor = lifeTimeFactor;
        }

        public byte ProducerNodeId { get; }
        public TimeSpan GuardTime { get; }
        public byte LifeTimeFactor { get; }
        public IDisposable? PollHandle { get; set; }
        public IDeadline? LifeTimeDeadline { get; set; }
        /// <summary>True after the first response that was accepted for life-time rearm.</summary>
        public bool HasSeenResponse { get; set; }
        /// <summary>Toggle bit from the last response that rearmed the life-time deadline.</summary>
        public bool LastToggle { get; set; }
    }
}
