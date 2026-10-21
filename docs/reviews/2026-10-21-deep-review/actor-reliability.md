# Deep review 2026-10-21 — CanKit.Pro.Actor, CanKit.Pro.Reliability, cross-cutting test infrastructure

Repository `main` @ 36c97ca. Read-only; nothing was built or run.
Finding prefix: **A**. Previous review: `docs/reviews/2026-09-30-deep-review.md` (main @ 8cec2ee).

## 1. Scope read

Source (read completely):

| File | Lines |
| --- | --- |
| `src/CanKit.Pro.Actor/ProtocolActor.cs` | 977 |
| `src/CanKit.Pro.Actor/IProtocolActor.cs` | 75 |
| `src/CanKit.Pro.Actor/ClockTimeout.cs` | 46 |
| `src/CanKit.Pro.Actor/TickMath.cs` | 44 |
| `src/CanKit.Pro.Actor/ITimeSource.cs` | 58 |
| `src/CanKit.Pro.Actor/ActorExecutionMode.cs` | 42 |
| `src/CanKit.Pro.Actor/README.md`, `.csproj` | 110, 38 |
| `src/CanKit.Pro.Reliability/DeadlineScheduler.cs` | 253 |
| `src/CanKit.Pro.Reliability/BusStateMonitor.cs` | 337 |
| `src/CanKit.Pro.Reliability/BusStateExtensions.cs`, `IDeadline.cs`, `IDeadlineScheduler.cs` | 46, 72, 43 |
| `src/CanKit.Pro.Reliability/README.md`, `.csproj` | 152, 13 |

Test infrastructure (read completely): `VirtualClock.cs` (237), `ManualTimeSource.cs` (63), `ControllableBus.cs` (307), `DeferredEchoQueue.cs` (180), `EchoWorlds.cs` (115), `StarvedReaderBusService.cs` (171).

Tests (read completely, all under `tests/CanKit.Pro.Tests/TestCases/`): `ProtocolActorTests.cs` (680), `ProtocolActorTimerTests.cs` (333), `ProtocolActorLoopCountTests.cs` (58), `TickMathTests.cs` (89), `ClockTimeoutTests.cs` (75), `DeadlineTests.cs` (305), `BusStateMonitorTests.cs` (350), `Nfr006ErrorArchitectureTests.cs` (113), `PublicApiSurfaceTests.cs` (135).

Also consulted for reachability: the actor-disposal paths in `CanOpenNode.cs` (:280-310, :735-800, :870-960), `J1939NodeImpl.cs` (:1715-1760), `IsoTpChannel.cs` (:225-250, :640-680), `J1939TpChannel.cs` (:405-430), the `ClockTimeout` consumers in `UdsClientImpl.cs` (:1470-1505) and `LssMaster.cs` (:440-530), upstream `ICanBus.cs` at v0.5.6 (event signatures), PRs #260 and #280, issues #54, #240.

History since 8cec2ee on these paths (`git log 8cec2ee..HEAD`): e78bb23, 551261a, d0902e8 (PR #260, A1), 3de35a0 (PR #280, #240). Nothing else touched the two packages; the Reliability package is byte-identical to the previous review.

## 2. Status of previous findings

| ID | Status | Evidence |
| --- | --- | --- |
| **A1** tick arithmetic not exact, both directions | **Fixed** (PR #260, merge d78fd27; commits e78bb23, 551261a, d0902e8) | Direction 1 (delay → ticks): `ProtocolActor.cs:871-878` → `TickMath.DueAt` (`TickMath.cs:29-33`, decimal, ceiling). Direction 2 (ticks → delay): `ProtocolActor.cs:151-154` → `TickMath.RemainingFromTicks` (`TickMath.cs:37-38`, decimal, ceiling). `ManualTimeSource.Advance` (`ManualTimeSource.cs:59`) uses the same decimal ceiling, so `AdvanceAsync(d)` after `Schedule(d)` lands exactly on the due tick. Pinned by `TickMathTests` (4 frequencies incl. 10^7 and 10^9, every whole ms to 2 s, there-and-back), `ProtocolActorTimerTests.A_Timer_Is_Armed_Exactly_As_Far_Away_As_Asked` (:30-47, both Stopwatch frequencies, 1..2000 ms, through the real `Schedule`/`NextTimerDelayAsync` path) and `A_Timer_Fires_When_The_Clock_Has_Advanced_By_Exactly_Its_Delay` (:49-72, 1..400 ms). The wait computation `ToTimeoutMilliseconds` (`:884-890`) still goes through a double, which is correct by construction: it only decides how long to sleep, rounds up, and the loop re-checks `DueTimestamp <= now` on an integer before firing (:718). A wake-up a fraction early costs one extra 1 ms sleep, never an early fire. Not covered: frequencies that are not multiples of 1000 (an old-Windows 3 579 545 Hz QPC); there the round trip is inexact by one 100 ns tick, which only the exact-match test infrastructure would notice. Not a production source today. |
| §3 `A_Clean_Dispose_Reports_Nothing` 500 ms wall clock | **Fixed** (#260) | `ProtocolActorTimerTests.cs:294` passes `shutdownTimeout: null` (5 s default), comment :291-293 explains why. |
| §3 `Schedule_Fires_Callback_After_The_Configured_Delay` accepted 25 % early (actor-M1) | **Fixed** (#260) | `ProtocolActorTests.cs:217-220` asserts `>= 200 ms` for the 200 ms timer. |
| §3 Dispose-timeout `TimeoutException` raised off-loop; `IProtocolActor.cs:66-72` silent | **Open, never ticketed** | `ProtocolActor.cs:518-519` raises on the disposing thread, concurrently with the stuck callback; `IProtocolActor.cs:65-73` still describes the event as raised for "a posted work item … or a scheduled callback" only. See A-4. |
| §3 `WaitForLoopTask` catches with a wrong reason and swallows loop faults | **Open, never ticketed** | `ProtocolActor.cs:529-542` unchanged. Re-assessed: the loop task cannot complete with an `OperationCanceledException` (the loop catches it at :593-596) and nothing on the loop's own path throws (every user call sits under `RunSafely`), so the `catch (AggregateException)` is dead code with a misleading comment rather than a swallow. See A-5. |
| §3 `BusStateMonitor.PostRecheck` gate latches on a non-ODE exception | **Open, never ticketed** | `BusStateMonitor.cs:238-248` unchanged. With `ProtocolActor`, `Post` throws only `ObjectDisposedException` (`:431-439`) so the latch is unreachable; with a foreign `IProtocolActor` it is not. See A-3. |
| §3 `FaultOccurred` hint / throwing `BusState` getter / handler detach untested (reliability-M3/M4/M5 green) | **Open, unchanged** | `BusStateMonitorTests.cs` contains no `RaiseFault` call (the only `RaiseFault` in the suite is `TxConfirmTests.cs:631`, on a different component), no throwing `BusState` getter, and `Dispose_Stops_Further_StateChanged_Events_And_Is_Idempotent` (:112-139) changes the state and advances the clock without raising a hint after `Dispose`, so removing the two `-=` lines (:201-208) stays green. Mutations M4-M6 below re-run the three. |
| §3 `IsOnCurrentActor` only on `ProtocolActor`, not on `IProtocolActor` | **Open, by design until 2.0** | Interface unchanged (`IProtocolActor.cs`); ISO-TP still pattern-matches `_actor is ProtocolActor { IsOnCurrentActor: true }` (`IsoTpChannel.cs:403, :447`) and silently loses the inline path with a foreign actor. Adding the member is a break for implementers (`DeadlineTests.HoldingActor`, `BusStateMonitorTests.MailboxActor` are two in-repo ones). |
| **#240 / PR #280** "a due timer does not overtake earlier work" | **Fixed and pinned** (merge 5c74173, commit 3de35a0) | `ProtocolActor.cs:734-735`: a due timer stays in the list while the mailbox head is stamped before its due instant; `MailboxItem.PostedAt` (:902-914) is stamped under `_disposeGate` at enqueue (:436). `NextWaitTimeoutMilliseconds` (:639) returns 0 while the mailbox is non-empty, so the held-back timer is reached on the very next pass. Bounded under saturation: at most one extra batch, because every item enqueued after the due instant is stamped at or after it. The equality case (`<`, not `<=`) lets a frame stamped exactly at the due instant not hold the timer back, as the README says (:50-53). Pinned by `ProtocolActorTimerTests.A_Due_Timer_Does_Not_Overtake_Work_Posted_Before_It_Fell_Due` (:163-188), which gates the loop on a `ManualResetEventSlim` and drives a `ManualTimeSource`, so the ordering is forced rather than timed; PR #280 reports the mutation (old code → `{"timer","frame"}`), M1 re-runs it. I also checked the interaction with `VirtualClock.AdvanceAsync`'s two-round-trip barrier (`VirtualClock.cs:91-107`): the second poke is always enqueued after the first one ran, hence stamped after the advance, so it can never be "older" than a timer the advance made due; the guarantee "when `AdvanceAsync` returns, every timer that became due has already run its callback" (:11-12) still holds under the new rule. |

The previous review's §4 "tests that cannot fail" entries in this area (actor-M1) are closed; its timing table entry for this area (`A_Clean_Dispose_Reports_Nothing`) is closed.

## 3. New findings

### A-1 · A second `Dispose()` returns before the loop has stopped — Important

`src/CanKit.Pro.Actor/ProtocolActor.cs:485-488` (idempotency guard), `:494-504` (reentrant return), `:506-508` (join).
`Dispose` flips `_disposedFlag` under `_disposeGate` and **returns at once for every caller but the first**. The join (`Thread.Join` / `_loopTask.Wait`, :506-508) and the timeout report (:510-520) belong to the first caller only. Two reachable shapes:

1. A callback on the loop disposes the actor (the reentrant path, documented at :496-503: "once this call returns and the current work item's stack unwinds, the loop … tears itself down"). The owner's `Dispose` afterwards — a `using` block, a `finally`, `VirtualClock.Dispose` (`VirtualClock.cs:231-235`) — hits the guard and returns **while the callback that called the first `Dispose` is still running and `FinalDrain` has not run**. The owner then tears down the bus/service the callback is still using.
2. Two off-loop callers: the second returns immediately while the first is still joining (up to 5 s); the second caller's "after Dispose" code runs concurrently with queued work.

The README promises otherwise without qualification (`src/CanKit.Pro.Actor/README.md:78-82`: "Dispose waits up to five seconds for the loop to actually finish; if a callback is still running when that elapses it returns anyway … and reports a TimeoutException"). The 2026-09-30 §3 note "the actor looked disposed while a work item was still running on a live background thread" (:512-516) is exactly the state shape 1 leaves behind, now through a second call instead of a timeout.

Reachability: API use (any second `Dispose` call), no peer involved. Every protocol layer in the repository shields it today by its own idempotency guard sitting above the actor (`CanOpenNode.BeginDispose`, `J1939NodeImpl.TryDispose`, ISO-TP/J1939-TP `_disposed`) and by not disposing injected actors (`_ownsActor = actor is null`); CANopen's `DisposeCoreAsync` even awaits a `_disposeDone` of its own because the actor gives it nothing to wait on. The published `CanKit.Pro.Actor` package has no such shield. Severity Important (contract of a published type; cheap fix), confidence high on the mechanism (read directly, no race needed), medium on practical impact (the in-repo consumers are shielded). Test: none — `Dispose_Called_Reentrantly_From_A_Posted_Callback_Does_Not_Deadlock` (`ProtocolActorTests.cs:270-297`) asserts only that the reentrant call returns and that `Post` throws afterwards; no test calls `Dispose` twice.

Fix: make the join independent of who flipped the flag — `lock { first = Exchange(...) == 0 }`; `if (first) _stopCts.Cancel();` then `if (IsOnCurrentActor) return;` then always join (`Thread.Join` on a finished thread and `Task.Wait` on a completed task return immediately), and report the timeout once (a separate `Interlocked` flag) or per caller. Proposed test: post a work item that disposes the actor, blocks on a gate, then releases; call `Dispose` from the test thread while it is blocked and assert that `Dispose` does not return before the gate is released (or returns with the `TimeoutException` after a short `shutdownTimeout`).

### A-2 · `ClockTimeout` is orphaned by actor disposal, undocumented — Docs / Minor

`src/CanKit.Pro.Actor/ClockTimeout.cs:16-23, :29-37, :39-45`.
The timer-backed variant arms its cancellation with `ScheduleAt`; `WaitUntilAsync` completes `elapsed` from a scheduled callback. When the actor is disposed first, `FinalDrain` discards not-yet-due timers (`ProtocolActor.cs:612-631`), so the token never cancels and `WaitUntilAsync` never completes unless the **caller's** token does. Both current consumers link a lifetime token (`UdsClientImpl.cs:374-376` et al. via `_lifetimeCts`; `LssMaster.cs:377-390` via `_lifetime`), so no hang is reachable today — but the type carries nothing that says the caller must, and `ClockTimeout(clock, deadline)` throws `ObjectDisposedException` out of its constructor when the actor is already gone (`ScheduleAt` → `Insert` → `ThrowIfDisposed`), which `UdsClientImpl.CancelAt` (:1478-1481) and `LssMaster.ReceiveAsync` (:459-461) rely on their outer `ObjectDisposedException` handling for. Same shape as the documented `IDeadline` zombie (`IDeadline.cs:17-28`), only without the paragraph. Reachability: API misuse / a future caller without a lifetime token. Confidence high. Test: none for the orphan; `ClockTimeoutTests` cover expiry and withdrawal on a live actor. Fix: a `<remarks>` on `ClockTimeout` stating that the owning actor's disposal neither cancels the token nor completes `WaitUntilAsync`, so callers must link a lifetime token — or make `WaitUntilAsync` observe disposal by also registering on a token the actor exposes (bigger change, not needed today).

### A-3 · `BusStateMonitor.PostRecheck` leaves the coalescing gate latched when `Post` throws anything but `ObjectDisposedException` — Minor (carry-over)

`src/CanKit.Pro.Reliability/BusStateMonitor.cs:235-248`. `_recheckPending` is claimed at :235 and released only by `HintRecheckOnLoop` (:263) or the `catch (ObjectDisposedException)` (:242-248). Any other exception from `IProtocolActor.Post` escapes with the gate at 1, and every later hint is dropped for the life of the monitor; the poll floor keeps working, so the symptom is "hint latency silently became poll latency". Reachability: only with a foreign `IProtocolActor` (with `ProtocolActor`, `Post` throws only ODE, `ProtocolActor.cs:431-439`). Confidence high. Test: none. Fix: release the gate in a `catch` that rethrows, i.e. `catch { Volatile.Write(ref _recheckPending, 0); throw; }` with the ODE case folded in, or `catch (ObjectDisposedException) { release; } catch { release; throw; }`.

### A-4 · The dispose-timeout `TimeoutException` is raised off-loop and the event's contract does not say so — Docs (carry-over)

`src/CanKit.Pro.Actor/ProtocolActor.cs:510-520`; `src/CanKit.Pro.Actor/IProtocolActor.cs:65-73`; `src/CanKit.Pro.Actor/README.md:78-82`. Every other raise of `BackgroundExceptionOccurred` happens inside a callback scope on the loop (`RunSafely`, :766-836), which is what lets a subscriber touch single-writer state and dispose reentrantly. The timeout report runs on the disposing thread, concurrently with the callback that is still running; a subscriber written for the loop (every protocol layer's `OnActorBackgroundException`) then mutates actor state from a second thread, the one situation FR-RAW-021 exists to rule out. The README mentions the report; neither it nor the XML doc mentions the thread. Reachability: a callback that outlives the 5 s join. Confidence high. Test: `Dispose_Reports_A_TimeoutException_When_The_Loop_Cannot_Be_Joined` (`ProtocolActorTimerTests.cs:256-284`) covers the report, not the thread. Fix: one sentence on the event and in the README ("raised on the disposing thread, not the loop; the loop is still inside the callback"), and a subscriber guard in the layers if they rely on single-writer access there.

### A-5 · `WaitForLoopTask` has dead `catch` with a false justification — Minor / Docs (carry-over)

`src/CanKit.Pro.Actor/ProtocolActor.cs:529-542`. The comment says the loop "exited via OperationCanceledException"; it does not — `RunLoopAsync` catches it (:593-596) and completes normally. The loop task can only fault from an `ITimeSource` that throws (test seam) or a BCL failure in `SemaphoreSlim`/`CancellationTokenSource`; in that case the `catch` returns `true` ("joined") and drops the fault, which is not what "the loop did finish" was meant to cover. Confidence high. Test: none can reach it. Fix: either delete the `catch` (let a genuinely faulted loop surface its exception from `Dispose` — it is a bug, not a shutdown) or keep it and raise the inner exception through `RaiseBackgroundException`, and correct the comment either way.

### A-6 · `TickMath.DueAt` saturation test misbehaves for a negative `now` — Minor (unreachable)

`src/CanKit.Pro.Actor/TickMath.cs:32`: `ticks >= long.MaxValue - now` overflows (unchecked) when `now < 0`, turning every delay into `long.MaxValue`. `Stopwatch.GetTimestamp` is non-negative on every supported platform and `ManualTimeSource` starts at 0, so no source today produces it; a test source seeded negative would. Confidence high (arithmetic). Test: `TickMathTests.A_Span_No_Counter_Reaches_Saturates_Instead_Of_Overflowing` (:82-88) uses `now = 5`. Fix: `ticks > long.MaxValue - now` only when `now >= 0`, or compute in `decimal` and clamp.

### A-7 · `ProtocolActorTests` keeps a stale reason for its `WithinTenSeconds` helper — Docs

`tests/CanKit.Pro.Tests/TestCases/ProtocolActorTests.cs:154-155` says "Task.WaitAsync does not exist on every target framework this suite runs on"; the suite references Polyfill 11.4.5 for exactly that (`Directory.Packages.props:48-53`) and `ClockTimeoutTests.cs:29, :63, :70` and `DeferredEchoQueue.cs:99` use `WaitAsync`. Harmless; the helper also gives a better failure message, which would be the honest reason.

### A-8 · Orphaned XML doc on `TimeSource` — Docs

`src/CanKit.Pro.Actor/ProtocolActor.cs:88-115`: the `<summary>` written for `TimeSource` (:88-94) is followed by the `RunningLoopCount` block (:95-113), so the compiler attaches the clock's summary to `RunningLoopCount` and `TimeSource` (:115) has none. Both internal; a move of the block fixes it.

Notes that are not findings (checked, deliberate or harmless):

- `_signal` over-count (:61-66): after a burst drained in batches the loop runs up to (items − iterations) empty iterations before it can sleep; each is a few hundred nanoseconds, and the count cannot reach `int.MaxValue` without the mailbox having grown unboundedly first. Documented.
- `FinalDrain` fires due timers (:630) after the last mailbox item; a callback that re-arms (`Deadline.Rearm`, `Schedule`) during that pass gets `ObjectDisposedException` routed to `BackgroundExceptionOccurred`. Documented behaviour; the noise it produces at shutdown is a layer concern (open question 2).
- `ThreadPool` mode `Dispose` blocks a pool thread in `_loopTask.Wait` while the loop's `finally` needs a pool thread to run: under pool starvation (#130's net48 symptom) the 5 s join can report a `TimeoutException` for a loop that merely had no thread. Documentation-level; the layers all use `DedicatedThread`.
- `VirtualClock.AdvanceAsync` throws `ObjectDisposedException` synchronously if a tracked actor was disposed by someone else; today no layer disposes an injected actor, so unreachable from the suite.
- `WithdrawableCall` (:387-429), the three-state `TimerEntry` (:916-954) and the `_disposeGate` race closure (:171-178, :431-477) were walked for every interleaving I could construct (cancel vs. start, cancel vs. fire, dispose vs. insert, dispose vs. retire, double dispose, `Send` throwing before/after running the delegate); all hold. No `ValueTask` is used in these packages; `SynchronizationContext` is never captured (`ConfigureAwait(false)` in `RunLoopAsync` :591, `ClockTimeout.WaitUntilAsync` :36, `VirtualClock` throughout; `RunContinuationsAsynchronously` on every TCS). Exceptions from timer callbacks and `Deadline.Fire`'s `onExpired` (`DeadlineScheduler.cs:240`) reach `BackgroundExceptionOccurred` through `RunSafely`; a throwing subscriber is swallowed at :850-860, documented.
- Disposal from inside a callback: the reentrant path (:494-504), `Dispose` from a `BackgroundExceptionOccurred` handler on the loop (RunSafely's `catch` is inside the callback scope, :823-835; the `Send`-failure path enters the scope explicitly, :810-818, with a test), `Dispose` from a `PostAsync` continuation (off-loop by `RunContinuationsAsynchronously`), `Dispose` of actor B from actor A's callback (stack-shaped `t_runningActors`, :40-46) — none deadlocks. The only documented deadlock is `Dispose` on the `SynchronizationContext`'s own thread (`ActorExecutionMode.cs:35-38`, README :89-92).

## 4. Test quality and gaps

Covered well: tick exactness over the whole millisecond range at both Stopwatch frequencies, the #240 ordering with a forced interleaving, wall-clock independence (frozen source, `Timer_Due_Times_Ignore_The_Wall_Clock…`), starvation under a self-refilling mailbox, cancelled-entry reclamation with a blocker that defeats the head trim, the dispose-timeout report with a shortened timeout, the Send-failure paths in `SynchronizationContext` mode (task fault, background channel, no join from the handler), the concurrent Dispose/Post race (25 × 4 threads × 25 posts), withdrawal of a queued `PostAsync` while the loop is held, the `Deadline` generation guard with a holding actor that delivers after dispose, the hint-storm coalescing with a mailbox-counting actor, the loop-count release on a failed start.

Gaps:

- No test calls `ProtocolActor.Dispose` twice or after a reentrant dispose (A-1).
- `BusStateMonitor`: no `FaultOccurred` hint test, no throwing-`BusState` test (the `try/finally` in `PollTick` :527-537 is unexercised), no hint raised after `Dispose` (handler detach unexercised). M4-M6.
- `BusStateMonitor.PostRecheck` non-ODE path (A-3): unreachable with `ProtocolActor`; a `MailboxActor` variant whose `Post` throws would cover it.
- `ClockTimeout` after actor disposal (A-2): no test.
- `Deadline.Rearm` concurrent with `Fire` (the documented best-effort race, `DeadlineScheduler.cs:61-64`) has no test; `HoldingActor` covers the sequential stale-callback case only.
- `ProtocolActorTests.ThreadPool_Mode_Still_Serializes_Work_With_No_Lost_Updates` (:54-68) and `Concurrent_Callers_From_Real_Threads…` (:70-83) can pass by chance on a machine where contention never materialises; they are stress rather than proof, which the comments acknowledge.
- `TickMathTests` has no frequency that is not a multiple of 1000, so the one-tick inexactness of a 3 579 545 Hz source (see A1 status) is neither asserted nor documented.

Timing-gated tests in this area (host-perturbable quantity named per #92; "bound" = a hang guard that cannot go red by load alone):

| Test | Quantity the host perturbs | Margin | Direction |
| --- | --- | --- | --- |
| `DeadlineTests.Rearm_After_The_Deadline_Already_Resolved_Returns_False` (:169-176) | A stall of the test thread between `Arm(500 ms)` and `Complete()` / `Dispose()` (GC pause, host overload) against a real 500 ms deadline | 500 ms | **false red**; the only genuine wall-clock margin in this area; a `VirtualClock` removes it |
| `ProtocolActorTests.Post_Wakes_The_Loop_Promptly_Even_While_A_Long_Timer_Is_Pending` (:250-256) | Thread wake-up + one round trip against `< 2 s` | 2 s | false red, very unlikely |
| `ProtocolActorTimerTests.A_Clean_Dispose_Reports_Nothing` (:294-301) | Join of an idle loop against the 5 s default | 5 s | false red, very unlikely (was 500 ms; fixed) |
| `ProtocolActorTimerTests.A_Sub_Millisecond_Remaining_Delay_Waits_Instead_Of_Busy_Spinning` (:210-216) | `ReadCount < 20 000` over a 300 ms real wait; load *lowers* the count | ~30× | cannot go red; could go green on a throttled spin |
| `ProtocolActorTimerTests.Dispose_Reports_A_TimeoutException_When_The_Loop_Cannot_Be_Joined` (:264-280) | 100 ms join against a callback blocked on a gate | n/a | deterministic by construction (the join must time out) |
| `ProtocolActorTimerTests.Timer_Due_Times_Ignore_The_Wall_Clock…` (:99-109) | `Task.Delay(500)` then `> 200 ms` | 300 ms | cannot go red (delay is a floor) |
| `ProtocolActorTests.Schedule_Fires_Callback_After_The_Configured_Delay` (:215-220) | `>= 200 ms` after a 200 ms timer, 5 s bound | — | lower bound is the pin; cannot go red by load |
| Real 10/20/30/100 ms timers with 5 s / 2 s bounds: `DeadlineTests` :31-33, :178-180, :215-219; `BusStateMonitorTests` :74-81, :97-105; `ProtocolActorTests` :427-438, :550-555; `ProtocolActorTimerTests` :133-150 | Timer + pool latency against the bound | ≥ 2 s | bound only |
| `VirtualClock.WaitUntilTimerArmedAsync` / `RunUntilAsync` give-ups (`VirtualClock.cs:143-149, :197-206`) | `DateTime.UtcNow` give-up (5 s at every call site) | 5 s | bound only; note it is the wall clock, so an NTP step moves the bound — `Stopwatch` would be consistent with what the infrastructure preaches |
| `ProtocolActorTests.Concurrent_Dispose_Never_Accepts_Work…` (:390-392) | 2 s per accepted task after `Dispose` joined | 2 s | bound only |

Negative windows (`Task.Delay(…)` as "nothing may come"): none in this area; the negative assertions all run on a frozen or virtual clock.

## 5. Mutations proposed

| ID | Edit (exact) | Expected |
| --- | --- | --- |
| M1 | `src/CanKit.Pro.Actor/ProtocolActor.cs:734-735` — delete the two lines `if (_mailbox.TryPeek(out var older) && older.PostedAt < entry.DueTimestamp)` / `break;` | **red**: `ProtocolActorTimerTests.A_Due_Timer_Does_Not_Overtake_Work_Posted_Before_It_Fell_Due` (pins #240 / PR #280; PR reports `{"timer","frame"}`) |
| M2 | `src/CanKit.Pro.Actor/TickMath.cs:31` — replace `Math.Ceiling((decimal)span.Ticks * frequency / TimeSpan.TicksPerSecond)` with `(decimal)Math.Ceiling(span.TotalSeconds * frequency)` (the pre-#260 double path, direction 1) | **red**: `ProtocolActorTimerTests.A_Timer_Is_Armed_Exactly_As_Far_Away_As_Asked` (at least one of the two frequencies; 35/70/85/101 ms per the A1 analysis) and `TickMathTests.A_Span_No_Counter_Reaches_Saturates…` (:87, `DueAt(5, 35 ms, 10^7)`) |
| M3 | `src/CanKit.Pro.Actor/TickMath.cs:38` — replace the body with `=> TimeSpan.FromSeconds((double)sourceTicks / frequency);` (pre-#260 direction 2) | **red**: `TickMathTests.Converting_There_And_Back_Returns_The_Span`, `TickMathTests.A_Remaining_Time_Rounds_Up_And_An_Elapsed_Time_Rounds_Down` (:76, :78) and `ProtocolActorTimerTests.A_Timer_Is_Armed_Exactly_As_Far_Away_As_Asked` |
| M4 | `src/CanKit.Pro.Reliability/BusStateMonitor.cs:218` — change `private void OnFaultHint(object? sender, Exception e) => PostRecheck();` to `=> { }` | **expected green = gap** (re-run of reliability-M3; the `FaultOccurred` hint has no test) |
| M5 | `src/CanKit.Pro.Reliability/BusStateMonitor.cs:201-208` — delete both `if (_errorHintSubscribed) { … -= … }` and `if (_faultHintSubscribed) { … -= … }` blocks | **expected green = gap** (re-run of reliability-M5; no hint is raised after `Dispose` in `BusStateMonitorTests`) |
| M6 | `src/CanKit.Pro.Reliability/BusStateMonitor.cs:529-536` — replace the `try { RecheckOnLoop(); } finally { RearmPoll(); }` with `RecheckOnLoop(); RearmPoll();` | **expected green = gap** (re-run of reliability-M4; no test makes `BusState` throw) |

For A-1 no source mutation distinguishes the gap; the proposed test in A-1 is the pin. A seventh, if there is budget: `ProtocolActor.cs:487` — remove the `lock (_disposeGate)` around the flag flip → expected green (`Concurrent_Dispose_Never_Accepts_Work_That_Then_Fails_To_Run` is a 25-iteration race and the window is a few instructions), which would show that the race-closure test is probabilistic.

## 6. Doc drift

- `src/CanKit.Pro.Actor/README.md:78-82` and `ProtocolActor.cs:480-527` — "Dispose waits up to five seconds for the loop to actually finish": only the first caller does (A-1).
- `src/CanKit.Pro.Actor/IProtocolActor.cs:65-73` and README :80-82 — the dispose-timeout report is raised on the disposing thread (A-4).
- `src/CanKit.Pro.Actor/ProtocolActor.cs:536-541` — `WaitForLoopTask` comment describes a path that does not exist (A-5).
- `src/CanKit.Pro.Actor/ClockTimeout.cs` — no word on actor disposal (A-2).
- `src/CanKit.Pro.Actor/ProtocolActor.cs:88-115` — `TimeSource` summary attached to `RunningLoopCount` (A-8).
- `tests/CanKit.Pro.Tests/TestCases/ProtocolActorTests.cs:154-155` — stale `WaitAsync` reason (A-7).
- `src/CanKit.Pro.Actor/README.md:15` ("What is validated") says the synchronization-context mode is validated for "marshaling through the supplied context, failure surfacing, timers and dispose — not its ordering or concurrent serialization": accurate.
- `src/CanKit.Pro.Reliability/README.md` and the `BusStateMonitor` / `IDeadline` XML docs: accurate against the code (coalescing, gate-before-sample, lifetime, the zombie paragraph, `Rearm` forcing `Cancelled`, `IsDegraded(Unknown)`); `ICanBus` event signatures match upstream v0.5.6 (`ICanBus.cs:200, :220`).
- `VirtualClock.WaitUntilTimerArmedAsync(…, lateBy)` (`VirtualClock.cs:180-214`) is the tolerance the previous review warned a future "repair" would reach for; its remarks (:185-190) give a concrete mechanical reason (two clock readings per schedule, a send costing one virtual step) and the default overload keeps the exact comparison. Acceptable as documented, but it is a tolerance and every new call site that passes a non-zero `lateBy` deserves the same justification.

## 7. What is done well (brief)

The #240 fix is the right abstraction (a timer is an event at its due instant) and costs one `TryPeek`; its test forces the interleaving instead of timing it. `TickMath` removes a whole class of one-tick surprises and the tests sweep the entire millisecond range at both real frequencies. The three-state `TimerEntry` plus `Retire()` makes the cancel/fire/dispose races linearizable without a lock on the timer list; `WithdrawableCall` does the same for `PostAsync` cancellation with a single CAS; `_disposeGate` closes the dispose/post race completely rather than narrowing it, and the test hammers it. `BusStateMonitor`'s gate-released-before-sample argument is correct and the `MailboxActor` test makes the mailbox depth the asserted quantity. `VirtualClock`'s two-round-trip barrier is reasoned from the loop's phase order and still holds after #280. The remarks throughout name the failure they prevent and the review that found it, which made this re-check cheap.

## 8. Open questions for the maintainer

1. **A-1**: should every `Dispose` caller join (README semantics), or should the README say "the first caller"? The former is a ten-line change and I would make it; the layers' own `_disposeDone` plumbing suggests they wanted the former and built around its absence.
2. `FinalDrain` fires due timers after the last mailbox item (:630). Every deadline that expires in that pass and tries to re-arm or post produces an `ObjectDisposedException` on `BackgroundExceptionOccurred` during an orderly shutdown. Is that noise acceptable, or should due timers be discarded at `FinalDrain` like not-yet-due ones (README :78 "Not-yet-due Schedule callbacks are discarded" would then read "pending")? A layer that needs its idle deadline to fire at shutdown would have to say so.
3. A-3's gate latch is unreachable with `ProtocolActor`. Fix it anyway (two lines) or leave the foreign-actor case undocumented?
4. `IsOnCurrentActor` on the interface remains a 2.0 item; is there a 2.0 list it is on? I did not find an issue.
5. `DeadlineTests.Rearm_After_The_Deadline_Already_Resolved_Returns_False` is the one wall-clock-margin test here (500 ms); move it onto `VirtualClock` in the next test-side PR that touches the file, or now under #92?
