# CanKit.Pro.Actor

Generic protocol-instance actor/scheduler for [CanKit](https://github.com/pkuyo/CanKit) (arc42
§8.3, ADR-6; SRS FR-RAW-020..024): a documented, single-mailbox threading model that any protocol
layer (ISO-TP, J1939, CANopen, ...) can build on instead of hand-rolling locks, unsynchronized
`List`s, and busy-loop schedulers.

Status: 1.0.0 – 1.2.3 are **withdrawn from nuget.org** — they were published as stable before
the API had been reviewed. **1.3.0 will be the first release whose API is stable**; until it is
tagged there is no listed version to install, so the `dotnet add package` line below resolves
nothing and the withdrawn releases come back only on an exact version pin. The public surface
can still change until then. See [Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

This package has **no dependency on any other CanKit package** — it is a plain, reusable
single-writer executor plus an event-driven timer queue. Protocol layers compose it; it does not
know about CAN frames, buses, or adapters.

```csharp
using CanKit.Pro.Actor;

using var actor = new ProtocolActor(); // ActorExecutionMode.DedicatedThread by default
actor.BackgroundExceptionOccurred += (_, ex) => log.Error(ex, "protocol instance failed");

// Fire-and-forget: exceptions surface via BackgroundExceptionOccurred.
actor.Post(() => channelRegistry.Add(channel));

// Request/response: exceptions surface through the returned task instead.
var count = await actor.PostAsync(() => channelRegistry.Count);

// Event-driven timeout/STmin check -- no polling, no busy loop.
using var timeout = actor.Schedule(TimeSpan.FromMilliseconds(150), () => channel.OnN_BsTimeout());
```

## Guarantees

- **One mailbox, one loop** (FR-RAW-020/021): every posted work item and every fired timer
  callback runs strictly one at a time, in order. Protocol-instance state touched only through
  `Post`/`PostAsync`/`Schedule` never needs its own lock.
- **Event-driven, not polling** (FR-RAW-022): the loop blocks on a semaphore for either new
  mailbox work or the next timer deadline, whichever comes first. An idle actor uses ~0% CPU.
- **Timers are fair, even under bus load**: each pass processes a *snapshot* of the mailbox rather
  than draining it to empty, so an RX reader posting one work item per frame on a saturated bus
  cannot starve the timer list — every batch is followed by a due-timer check. Anything that
  arrives mid-batch is picked up on the next pass, which is entered without waiting.
- **Deadlines are measured on a monotonic clock**, never on the wall clock: `Stopwatch`
  timestamps, so an NTP step, a DST change, or an operator setting the system clock cannot make
  an armed timeout fire early, late, or all at once. A `TimeSpan` delay is elapsed time and is
  measured as elapsed time.
- **Background exceptions have exactly one channel** (FR-RAW-023): a throwing `Post`/`Schedule`
  item is caught by the loop and raised via `BackgroundExceptionOccurred` — never thrown on some
  unrelated caller thread, never lost as an unobserved task exception. `PostAsync` failures
  surface through the returned task instead, since the caller is already positioned to observe
  them by awaiting.
- **Configurable execution context** (FR-RAW-024): `ActorExecutionMode.DedicatedThread` (default)
  pins the loop to one real `Thread` for its entire lifetime — demonstrably the same thread for
  every callback. `ActorExecutionMode.ThreadPool` is cheaper for many short-lived instances but
  does not guarantee thread affinity. `ActorExecutionMode.SynchronizationContext` marshals every
  callback onto a caller-supplied context (e.g. a UI dispatcher) via a *blocking*
  `SynchronizationContext.Send`, so work is guaranteed to have actually run by the time it's
  considered processed — including during `Dispose`'s final drain.

Disposing an actor stops it from accepting new work (`Post`/`Schedule` throw
`ObjectDisposedException`) but runs whatever was already queued to completion first, so a caller
awaiting `PostAsync` right as `Dispose` happens still gets a real result instead of hanging.
Not-yet-due `Schedule` callbacks are discarded, not fired. `Dispose` waits up to five seconds for
the loop to actually finish; if a callback is still running when that elapses it returns anyway
(so `Dispose` never becomes the thing that hangs) and reports a `TimeoutException` through
`BackgroundExceptionOccurred` — a silent give-up would leave the caller unable to tell a clean
shutdown from a loop still mutating state it believes it now owns.

`IsOnCurrentActor` answers "is *this thread* currently running one of my callbacks?", which is
what makes a public sync API able to run inline instead of dead-locking on its own loop. It is
thread-scoped, so a `Task.Run` started from inside a callback correctly reports `false` — it is
not on the actor and must not touch actor state inline.

**`SynchronizationContext` mode caveat**: never call `Dispose()` synchronously from the actor's
own target context thread (e.g. from inside a UI event handler on that same dispatcher) — like any
synchronous wait on work that needs that same thread to run, it can deadlock. Dispose from a
different thread, or dispatch the call asynchronously.

## Install

```bash
dotnet add package CanKit.Pro.Actor
```

No dependencies beyond the .NET base class library.

Part of [CanKit.Pro](https://github.com/dborgards/CanKit.Pro) — higher CAN protocol layers
built **on top of** [CanKit](https://github.com/pkuyo/CanKit), which is consumed as a NuGet
package rather than forked.

## License

MIT — see [LICENSE](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE).
CanKit itself is a separate project licensed under Apache-2.0; see
[THIRD-PARTY-NOTICES.md](https://github.com/dborgards/CanKit.Pro/blob/main/THIRD-PARTY-NOTICES.md).
