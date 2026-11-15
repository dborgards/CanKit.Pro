# Optional controller-overrun acknowledgement (#423)

Basis: user-supplied `301v04020008.pdf`, CiA 301 V4.2.0, §7.2.7.1, Table 26
(printed pp. 65–66), Figure 37 (p. 67), and §7.5.2.2, Table 54. Table 26 names
8110h "CAN overrun (objects lost)". It is optional and supplies no adapter repair
signal. Figure 37 supplies the generic error-state transitions, not a concrete
application acknowledgement API. No such API is attributed to the standard.

## Adapter evidence

The pinned CanKit v0.5.6 sources were checked at commit
`bec6c3389f4e386beedce3c9d5e4fc0eb56690b6`. `ICanErrorInfo.ControllerStatus`
contains flags `CanControllerStatus.RxOverflow` and `TxOverflow`. They accompany
`ICanBus.ErrorFrameReceived`; `BusState` has only None, ErrActive, ErrWarning,
ErrPassive, BusOff and Unknown. SocketCAN maps both overflow flags; ControlCAN
and ZLG also have receive-overflow mappings. This establishes the input shape,
not equal error-notification support across all adapters or complete loss counts.

## Library contract

`EmitControllerOverrunEmcy`, default false, is independent of the existing 8120h
and 8140h options. It installs one direct error-frame subscription, without a
bus-state poller or changing the adapter's capture configuration. A subscription
failure fails opening an opted-in node; polling cannot recover a lost notification.
The feature reports adapter notifications, not RawCan subscriber-buffer drops.

RxOverflow, TxOverflow or both latch one communication-error cause. Notifications
are accepted after entry to Pre-Operational, including while Stopped. Initialising
an unconfigured LSS node does not retain an old notification for its later boot.
The first report emits 8110h, contributing error-register bits 4/0 while preserving
application bits. Repeated notifications before acknowledgement remain one cause.
Normal controller levels, error-frame Active flags and bus-off recovery never
acknowledge it: they cannot restore lost objects.

After handling the consequences of the loss, the application calls the additive
`CanOpenNodeExtensions.AcknowledgeControllerOverrun(ICanOpenNode)` extension.
It leaves the published interface and all existing method signatures unchanged.
It rejects null, disposed local nodes and unsupported third-party implementations;
on a live local node it is a no-op if the option is off or no cause remains.
It runs synchronously on the node actor (inline from an actor callback), clears
only this latch and ends only its communication-error cause. The existing EMCY
chain emits 0000h only if this was the last reported node cause, retaining the
application's bits. Return means acknowledgement processed and any EMCY queued,
not transmitted. This does not command adapter recovery or recover lost data.

The actor orders acknowledgement against observations. A notification ordered
after the acknowledgement may start a new error, including a driver callback
racing the call. Notifications queued together are coalesced into one observation;
within one reset history, at most one further observation waits while another is processing. Coalescing is
for the latch, not an event counter. Each NMT reset invalidates the observation
history and clears the latch without an error-reset EMCY. Queued old observations
cannot relatch it after reset. New notifications after re-entry may report again.
Disposal and failed construction remove the handler and invalidate observations.
Non-fatal detach failures are reported through BackgroundExceptionOccurred while
the remaining cleanup continues. OutOfMemoryException, StackOverflowException
and AccessViolationException are not swallowed, consistent with the LSS reader.

Producer-disable retains the latch for one report on re-enable unless acknowledged
in the meantime. Acknowledging while disabled still clears a previously reported
register. Existing EMCY Stopped coalescing applies; acknowledging while
Stopped can replace the held 8110h with only the final 0000h. The new option is
preserved by the existing options-copy methods. EMCY inhibit time (1015h) remains
unimplemented: a device description can retain it as data with a NotImplemented
finding, but the node does not enforce it. This feature adds no inhibit timing.

This is an explicit opt-in application acknowledgement contract. It completes
the 8110h scope left after the 8140h feature in #528; it does not assert that
CiA 301 requires this particular lifetime or acknowledgement interface.

## Validation scope

`CanOpenOverrunEmcyTests.cs` exercises the input flags, latching/acknowledgement,
independent opt-in, overlapping causes/application bits, bus-off recovery, producer
disable/re-enable, Stopped, both resets and stale queued observations, LSS startup,
bounded mailbox traffic, copy methods, unsupported nodes and handler teardown.
Mutation results and executed build/test/package checks are recorded in the PR.
