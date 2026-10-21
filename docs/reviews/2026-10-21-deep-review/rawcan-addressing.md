# Deep review 2026-10-21 — CanKit.Pro.RawCan and CanKit.Pro.Addressing

Repository `main` @ 36c97ca. Upstream CanKit read at the pinned tag v0.5.6 in a clone of pkuyo/CanKit.
Finding prefix: **R**. Everything below was read in full, not grepped, unless a line says otherwise.
Line numbers are those of `main` @ 36c97ca.

## 1. Scope read

| File | Lines |
| --- | ---: |
| `src/CanKit.Pro.RawCan/CanBusService.cs` | 727 |
| `src/CanKit.Pro.RawCan/Subscription.cs` | 232 |
| `src/CanKit.Pro.RawCan/PendingSend.cs` | 118 |
| `src/CanKit.Pro.RawCan/TxConfirmation.cs` | 105 |
| `src/CanKit.Pro.RawCan/CanIdFilter.cs` | 306 |
| `src/CanKit.Pro.RawCan/CanFrameEvent.cs` | 169 |
| `src/CanKit.Pro.RawCan/ICanBusService.cs` | 194 |
| `src/CanKit.Pro.RawCan/ISubscription.cs` | 102 |
| `src/CanKit.Pro.RawCan/FilterOverlap.cs` | 48 |
| `src/CanKit.Pro.RawCan/CanBusServiceExtensions.cs` | 168 |
| `src/CanKit.Pro.RawCan/ProtocolErrorCodes.cs` | 59 |
| `src/CanKit.Pro.RawCan/Utils/IsExternalInit.cs`, `CanKit.Pro.RawCan.csproj`, `README.md` | 9 / 27 / 183 |
| `src/CanKit.Pro.Addressing/{CanIdRange,J1939Fields,J1939Id,J1939Name,J1939Pgn}.cs`, `.csproj`, `README.md` | 51 / 62 / 115 / 250 / 143 / 8 / 93 |
| `tests/.../TestCases/TxConfirmTests.cs` | 830 |
| `tests/.../TestCases/RawCanSubscriptionTests.cs` | 1152 |
| `tests/.../TestCases/RawCanConcurrencyTests.cs` | 111 |
| `tests/.../TestCases/CanIdFilterOverlapTests.cs` | 342 |
| `tests/.../TestCases/CanIdFilterPropertyTests.cs` | 196 |
| `tests/.../TestCases/PendingKeyTests.cs` | 162 |
| `tests/.../TestCases/AddressingTests.cs` | 280 |
| `tests/.../Infrastructure/{ControllableBus,DeferredEchoQueue,EchoWorlds,EchoCapableOptions}.cs` | 307 / 180 / 115 / 60 |
| `tests/.../ApiApprovals/CanKit.Pro.RawCan.approved.txt` | 91 |
| `tools/CanKit.Pro.EchoProbe/Program.cs` | 644 |
| `docs/reviews/2026-10-06-echo-hardware-measurements.md`, `docs/upstream-candidates.md` | 42 / 101 |
| Previous review `docs/reviews/2026-09-30-deep-review.md`: K1, Q7, Q8, R1, §3 RawCan/Addressing, §4.2 mutation table, Appendix A | excerpts |
| SRS FR-RAW-010..015, 030..034, 040..041; arc42 ADR-7 | excerpts |

Upstream files read for specific claims (v0.5.6): `CanFrame.cs` (factories :202-262, `LenToDlc`/`Validate` :339-365), `CanStructs.cs` (`CanReceiveDataView` :213-234), `CanBusExceptionDispatcher.cs` (:35-85), `CanExceptionPolicy.cs` (whole), `VirtualProvider.cs:15`, `VirtualBus.cs` (:40-70, :110-160, :180-310), `VirtualBusHub.cs:57-76`, `PcanBus.cs` (:50-80, :167-172, :271, :383-397, :440-560), `KvaserBus.cs` (:50-75, :110, :368-385, :547, :657), `VectorBus.cs:362,:637`, `VectorClassicTransceiver.cs:140-150,:232-248`, `VectorFdTransceiver.cs:160-175,:285-295`, `SocketCanBus.cs:321-337,:862`, `ZlgCanBus.cs:717-727`, `ControlCanBus.cs:277,:464`, `QueuedTxCanBus.cs:1-130`.

Not done, per the brief: no build, no test run, no mutation applied. Every "red/green" below is a prediction for the coordinator's run.

## 2. Status of previous findings

### K1 — echo confirmation assumes flagged echoes (issue #249, still OPEN)

**What changed since 8cec2ee.** PR #287 (merged 2026-10-06, `c1dbb26` reachable from `main`) changed the J1939 node and the *texts*: `ICanBusService.cs:159-166` now names the measured limits, the root `README.md:96-97` says "use Normal mode there", arc42 ADR-7 carries an "Einschränkung (gemessen, #249)" paragraph, `docs/reviews/2026-10-06-echo-hardware-measurements.md` records PCAN/Kvaser, and the third echo world `EchoWorld.FlaggedNormal` exists (`EchoWorlds.cs:37-43`, `ControllableBus.FlaggedEchoInNormalMode` :89-91). **RawCan's code is unchanged by decision** (#287 body: "a content-based guess for unflagged echoes would swallow a peer's identical frame").

**What RawCan does today, precisely.**
- Path choice: `CanBusService.cs:321-322` — echo path iff `Features.HasFlag(CanFeature.Echo) && WorkMode == ChannelWorkMode.Echo`; otherwise approximated.
- Matching: `:189-190` — only a `CanReceiveDataView` with `IsEcho == true` is offered to `TryMatchEcho`; no content-based fallback. An unflagged own frame goes to subscriptions as peer traffic and never confirms anything.
- Per adapter (v0.5.6, verified in the clone): **SocketCAN** flags (`MSG_CONFIRM`; by source, unmeasured) → echo path works. **PCAN** Echo mode: `AllowEchoFrames` on (`PcanBus.cs:167-172`), transceivers never set `IsEcho` → every confirmed send ends `Timeout` after 1 s (measured, 3/3 on classic and FD). **Kvaser** Echo mode: `canIOCTL_SET_TXACK` on (`KvaserBus.cs:132`) but notify mask `canNOTIFY_RX | canNOTIFY_ERROR` only (`:110`) → nothing arrives on a quiet bus → `Timeout` (measured). **Vector Classic** Echo mode: own TX event dropped (`VectorClassicTransceiver.cs:236-238`) → `Timeout`; Normal mode: delivered flagged (`:246`) but RawCan is on the approximated path there and `_pendingCount == 0` short-circuits the echo lookup (`:189`), so the flagged frame is harmless. **Vector FD**: TX event never delivered (`VectorFdTransceiver.cs:142-147`, previous review) → `Timeout`. **Virtual**: does **not** declare `CanFeature.Echo` (`VirtualProvider.cs:15`) → always approximated; its unflagged synchronous echo (`VirtualBusHub.cs:72-76`) only reaches subscriptions.
- Net: on 0.5.6 the echo path is usable on exactly one adapter (SocketCAN), and that one is unmeasured.

**Does the contract match the measurements?** `ICanBusService.cs:159-166`: yes. `src/CanKit.Pro.RawCan/README.md`: **no** — see R-1. `EchoWorlds.cs:24-28` ("what SocketCAN, Kvaser and Vector do"): no. Vector remains unmeasured (#249 stays open for that).

### Q7 (RawCan half) — non-canonical CAN-FD lengths never match their echo

**Status: open by decision, unticketed.** PR #273 (`fix(isotp)`, closed #250) fixed the in-repo producer and wrote: "Not done here: rejecting a non-canonical FD length in RawCan's `SendConfirmedAsync` ... is its own decision." No issue carries that half. Mechanism unchanged: `PendingKey.Equals` is byte-wise (`PendingSend.cs:80-84`); upstream `CanFrame.Fd` accepts any 0..64 (`CanFrame.cs:358-365`, `LenToDlc` rounds up); Vector FD transmits `msg.Dlc = frame.Dlc` (`VectorFdTransceiver.cs:290-291`) and rebuilds the echo with `DlcToLen(msg.Dlc)` bytes (`:170`), so `Fd(id, new byte[13])` comes back as 16 bytes and the echo path returns `Timeout`. Reachable only by an API caller that builds such a frame (no in-repo producer remains after #273). Severity Minor today; the decision is the maintainer's (§8).

### Q8 — `TxConfirmFailureReason.BusOff` unreachable on every 0.5.6 adapter

**Status: open, unticketed (no issue found by search), docs unchanged.** Re-verified against the clone:
- `OnFaultOccurred` (`CanBusService.cs:679-710`) fires only from `ICanBus.FaultOccurred` and only when `_bus.BusState == BusOff` (`:685`).
- Upstream raises `FaultOccurred` through `CanBusExceptionDispatcher.Report` only when the effective severity is ≥ `FaultThreshold` (= `Fault`, `CanExceptionPolicy.cs:30`), latched once (`CanBusExceptionDispatcher.cs:75-79`). `Classify` never returns `Fault` (returns `Info`/`Error` only). The only `severity: CanExceptionSeverity.Fault` reports are the six poll-loop deaths: `SocketCanBus.cs:862`, `KvaserBus.cs:547`, `VectorBus.cs:637`, `PcanBus.cs:474`, `ZlgCanBus.cs:717`, `ControlCanBus.cs:464`. A controller going bus-off surfaces as `BusState` (getters: `PcanBus.cs:383-397`, `KvaserBus.cs:368-385`, `VectorBus.cs:585`, `SocketCanBus.cs:478`, `ZlgCanBus.cs:365`) and as error frames, never as a fault.
- So an outstanding send during a real bus-off ends as `Timeout`, and `TxConfirmation.cs:17-21`, `README.md:112`, ADR-7 ("`BusState.BusOff` löst über `ICanBus.FaultOccurred` ein proaktives Fast-Fail aus") describe a path only `ControllableBus.RaiseFault` takes.
- Test: `Outstanding_SendConfirmed_Resolves_As_BusOff_Immediately_On_Fault` sets `BusState = BusOff` *and* raises the fault, so it passes with the guard at `:685` deleted (rawcan-M1 was green on 8cec2ee; nothing was added since — `git log 8cec2ee..HEAD -- TxConfirmTests.cs` shows only `d19412a`). Mutation R-M1 re-pins this.
- Side note on the same handler: `_bus.BusState` on PCAN/Kvaser is a driver call with `ThrowIfDisposed()` (`PcanBus.cs:386-388`, `KvaserBus.cs:372-374`); it runs on the thread whose poll loop just died. An exception there is swallowed by the adapter's `raiseFault` lambda (`PcanBus.cs:64` `catch { }`), so the sweep silently does not happen — harmless today because the sweep is unreachable anyway.

### R1 — a late echo after the timeout confirms the next byte-identical send

**Status: open, unticketed, undocumented in RawCan.** Mechanism unchanged: matching is content-only (`TryMatchEcho` `:609-677`); a send that timed out is completed at `:564-570` without unlinking, and the next `TryMatchEcho` unlinks it (`:649-651`) and hands the echo to the next live identical entry (`:668`). An echo of the *expired* frame arriving after that confirms the newer send. `README.md:116-117` and `ICanBusService.cs:152-153` still say "never cross-matched". Mitigated only in J1939 (`J1939NodeImpl.cs:131-135`, `ClaimEchoGrace`, #180). ISO-TP's repeated Flow-Control frames are byte-identical across blocks, so the window exists there too, bounded by N_As. No test pins either reading (a test cannot distinguish "late echo of send 1" from "echo of send 2" on the wire, which is the finding). Decision in §8.

### #294 / PR #301 — token consulted before the driver call

**Status: fixed.** Commits `d19412a`, `177ccf3`, `c5dd5eb` (all on `main`). Approximated path: `CanBusService.cs:370-374` (`ThrowIfCancellationRequested` immediately before `TransmitAsync`). Echo path: `:477-494` inside `_pendingGate`, with the entry unlinked under the gate before the throw (`:492`). Tests: `TxConfirmTests.A_Token_Cancelled_Before_The_Call_Abandons_The_Approximated_Send` (:252-264), `A_Token_Cancelled_While_The_Echo_Path_Waits_On_The_Gate_Abandons_The_Send` (:270-297). SRS FR-RAW-033 and the README (:120-125) carry the new wording. Mutation R-M3 pins the in-gate check.

### §3 minor list "RawCan / Addressing"

| Item | Status |
| --- | --- |
| Cancelled token honoured only after sending; not at all on the approximated path | **fixed** (#301, above) |
| Stamps in `TxConfirmation` path-dependent, XML doc wrong (`TxConfirmation.cs:73-75`) | **open** — now `:72-86` and `:88-103`; see R-3 |
| "Dispose cancels all outstanding sends" true only on the echo path (`README.md:117-118`) | **open** — see R-2 |
| `default(CanIdFilter)` is a valid filter matching standard ID 0 | **open**, unticketed — see R-6 |
| Addressing package description promises filter-overlap detection that lives in RawCan | **open** — `CanKit.Pro.Addressing.csproj:4` unchanged; see R-7 |
| Two test comments cite FR-RAW-010 for FR-RAW-015 content | **open** — `RawCanSubscriptionTests.cs:882` and `:974` |
| `ControllableBus.EchoCapable` calls synchronous echo "exactly as a real echo-mode adapter does"; `DeferredEchoCapable` used by one test | **half open** — wording unchanged (`ControllableBus.cs:76-79`, `:224-225`, `DeferredEchoQueue.cs:16-17`); the usage count is no longer true: `DeferredEchoCapable` is used at 31 sites across UDS, J1939, J1939-TP and CANopen tests (still exactly one in `TxConfirmTests.cs:83`). See R-8 |

### Appendix-A items that touch this package (previous review's status re-checked)

`Transmit under _pendingGate` — still deliberately open, reasoning at `CanBusService.cs:463-470` (#53, #102), pinned by `Frame_Arrival_Is_Stamped_Before_The_Pending_Send_Lock` and `A_Blocking_Transmit_Does_Not_Hold_Up_A_Plain_Frame`. `ProtocolErrorCodes` 6002..6005 — unchanged, upstream `CanKitErrorCode` (`CanKitExceptions.cs:11`) still ends at 6001; the "tripwire" claim is from the previous review, not re-run here.

## 3. New findings

Ordered by severity. "Reach" says who can trigger it.

### R-1 · RawCan README contradicts the measured echo behaviour it ships with — Docs (the NuGet-facing page), confidence high

- `src/CanKit.Pro.RawCan/README.md:16`: "TX echo as SocketCAN, Kvaser and Vector deliver it is modelled by a test double ... Nothing in this package has run against real CAN hardware". Both halves are now false: Kvaser 0.5.6 delivers no echo on a quiet bus and Vector Classic drops it in Echo mode (measurements doc :20-30, `VectorClassicTransceiver.cs:236-238`), and `CanBusService.SendConfirmedAsync` *has* run on PCAN and Kvaser hardware — that is rows 2, 4 and 6 of the measurements table, produced by `tools/CanKit.Pro.EchoProbe/Program.cs:322-343`.
- `README.md:96-118` (TX-Confirm section) says nothing about which adapters can take the echo path, while `ICanBusService.cs:159-166` and the root README do. A reader of the package page configures PCAN in Echo mode and gets a 1 s timeout per send.
- Same drift in `tests/.../Infrastructure/EchoWorlds.cs:24-28` ("what SocketCAN, Kvaser and Vector do") and `ControllableBus.cs:76-79`.
- Reach: every user reading the package README. Test: n/a. Fix: rewrite "What is validated" to name the measured rows and the unmeasured ones (SocketCAN, Vector), and copy the adapter paragraph from `ICanBusService.cs:159-166` into the TX-Confirm section (or link it) — one place, the other pointing at it, per CLAUDE.md's "a figure belongs in one place".

### R-2 · `Dispose` cannot cancel an in-flight approximated send; README says it does — Minor, confidence high

- `SendApproximatedAsync` (`CanBusService.cs:335-435`) registers nothing the service can reach; `Dispose` (`:253-304`) sweeps `_pending` only. A send waiting at `:413` on a stalled `TransmitAsync` keeps running until its own timeout or the caller's token, after the service — and typically the bus — is gone. `README.md:117-118` ("disposing the service cancels any outstanding `SendConfirmedAsync` calls rather than leaving them to time out") is true only on the echo path.
- Reach: only a custom `ICanBus` whose `TransmitAsync` is genuinely asynchronous (all seven 0.5.6 adapters return `Task.FromResult(Transmit(frame))`: `VirtualBus.cs:155`, `SocketCanBus.cs:336`, `PcanBus.cs:271`, `KvaserBus.cs:657`, `VectorBus.cs:362`, `ZlgCanBus.cs:726`, `ControlCanBus.cs:277`), i.e. the same population #202 was written for.
- Test: none (`NonEcho_Bus_Times_Out_Observably_When_TransmitAsync_Never_Completes` never disposes; `Disposing_Service_Cancels_Outstanding_SendConfirmed_Calls` uses the echo path).
- Fix: either keep a `HashSet<CancellationTokenSource>` of approximated in-flight `waitCts` under `_pendingGate` and cancel them in `Dispose` (then the call ends with `OperationCanceledException`, matching the echo path), or change the README sentence to "on the echo path".

### R-3 · `TxConfirmation` stamp documentation is path-dependent and partly false — Docs, confidence high

- `TxConfirmation.cs:72-77` (`HostTransmitTimestamp`): "taken immediately after the driver accepted the frame, or zero when nothing was handed to the driver (an outright rejection)". On the approximated timeout path (`CanBusService.cs:425`) the frame *was* handed to the driver and both stamps are zero. On the echo path a `Timeout`/`BusOff` result carries both stamps (`:522`), a `Rejected` one does not (`:515`).
- `TxConfirmation.cs:88-103` (`HostHandoffTimestamp`): "taken immediately before the driver call, inside the service's send lock" — the approximated path has no send lock (`:349`).
- `README.md:127-128`: "two host-monotonic readings that bracket the driver call, taken inside the service's send lock" — same.
- `CanBusService.cs:345-347` claims that when the runtime declines to inline the `ExecuteSynchronously` continuation "the reading is what it would have been anyway"; it is later by one scheduling hop. Minor, but it is the kind of sentence a reader trusts.
- Reach: any consumer that starts a deadline from the stamps (ISO-TP `IsoTpChannel.cs:1157-1164`, LSS `LssMaster.cs:441`, UDS functional client) — they already guard `> 0`, so no behavioural bug, only a contract a reader cannot rely on.
- Test: none in RawCan asserts either stamp on either path (`grep HostHandoffTimestamp tests/.../TestCases/{TxConfirmTests,RawCanSubscriptionTests}.cs` is empty; only UDS/LSS tests consume them).
- Fix: state per path which results carry stamps, drop "inside the send lock" for the approximated path, and add one RawCan test per path asserting `0 < HostHandoffTimestamp <= HostTransmitTimestamp` on success and the documented zeros on timeout/rejection.

### R-4 · A timeout above ~24.8 days throws after the frame is already on the wire — Minor, confidence high (misuse)

- `SendConfirmedAsync` validates `timeout > TimeSpan.Zero` only (`CanBusService.cs:312-314`). `WaitForPendingAsync` calls `timeoutCts.CancelAfter(timeout)` (`:574`) **after** the frame has been transmitted (`:495`) and the entry registered; `CancelAfter` throws `ArgumentOutOfRangeException` for a delay above `int.MaxValue` ms (netstandard2.0/net48; `uint.MaxValue - 1` ms on .NET 6+), e.g. `TimeSpan.MaxValue`. The exception propagates through the `finally` at `:524-529` (entry unlinked, fine) to a caller whose frame went out and who is told "bad argument". Same on the approximated path at `:409`, but only when the transmit is still pending.
- Reach: API misuse (`TimeSpan.MaxValue` as "wait forever"; `Timeout.InfiniteTimeSpan` is already rejected as non-positive).
- Test: none. Fix: validate `timeout <= TimeSpan.FromMilliseconds(int.MaxValue)` next to the positivity check at `:313` so the argument error precedes the driver call on both paths.

### R-5 · The approximated path rebuilds the frame and can reject what the echo path would transmit — Minor, confidence high on mechanism, medium on reach

- `CanBusService.cs:356-361` re-creates the caller's frame as `CanFrame.Classic(frame.ID, copy, isRemoteFrame: frame.IsRemoteFrame, ...)`. Upstream `CanFrame.Classic(int, ReadOnlyMemory<byte>, ...)` throws `ArgumentOutOfRangeException(nameof(dataInit))` when `isRemoteFrame && !dataInit.IsEmpty` (`CanFrame.cs:207-208`). A remote frame with a non-empty payload is constructible through the `IMemoryOwner<byte>` overload, which does not check (`CanFrame.cs:228-240`) — the natural shape for "RTR with DLC = 8 over a pooled 8-byte buffer". Such a frame transmits on the echo path (`:495`, no copy) and throws on the approximated path before the driver is called: two answers to one call, keyed on the bus mode.
- Second case, same lines: `frame.FrameKind` is mapped to `CanFd` or `Classic` only; a `CanFrameType.CanXl` (upstream enum `CanEnums.cs:138-144`; Vector FD transceiver handles it, `VectorFdTransceiver.cs:294`) or `Invalid` frame is silently re-created as Classic and then rejected by `Validate` for > 8 bytes or sent as a classic frame.
- Reach: API misuse / exotic frames; no in-repo caller does either.
- Test: `The_Drivers_Copy_Is_The_Same_Frame_For_Every_Kind` covers the five normal shapes (`:441-465`), not RTR-with-data or XL.
- Fix: build the copy with `CanFrame.Create(frame.ID, frame.Flags, copy)` is **not** an option (it hard-codes `CanFd`, `CanFrame.cs:305-311`); instead pass `frame.IsRemoteFrame ? ReadOnlyMemory<byte>.Empty : copy` and throw `NotSupportedException` for `FrameKind` other than `Can20`/`CanFd` on both paths, so the two paths agree.

### R-6 · `default(CanIdFilter)` is accepted and silently matches standard ID 0 only — Minor, confidence high (misuse)

- `CanIdFilter.cs:27-31` (`Kind.Range = 0`), `:44-50`, `:140-151`: an uninitialised struct is a range `[0, 0]` on the standard space. `CanBusService.Subscribe(CanIdFilter)` (`:116`) and `Subscription.Reconfigure(CanIdFilter)` (`Subscription.cs:99-103`) accept it; only `ISubscription.cs:82-84` warns. A field left unassigned in caller code becomes a subscription that receives ID 0 and nothing else, with nothing to report. Carried over from the previous §3; still unticketed.
- Reach: API misuse. Test: none (`grep "default(CanIdFilter)" tests/` empty).
- Fix: add `Kind.Unset = 0` (shift `Range`/`Mask` to 1/2), make `Matches` return false and `Overlaps` false for it, and throw `ArgumentException` from `Subscribe`/`Reconfigure`. Note for §8: turning "matches ID 0" into "throws" is a behavioural change under SemVer.

### R-7 · Addressing package description promises a feature that lives in RawCan — Docs, confidence high

- `src/CanKit.Pro.Addressing/CanKit.Pro.Addressing.csproj:4`: "... and ID-range/mask filter overlap detection". `CanIdFilter.Overlaps` and `FindOverlappingFilterSubscriptions` are in `CanKit.Pro.RawCan` (`CanIdFilter.cs:160`, `CanBusService.cs:120`); the Addressing README (:70-74) says so correctly. The csproj text is what nuget.org shows. Fix: delete the clause.

### R-8 · Test infrastructure still describes the synchronous echo as hardware-faithful — Docs (tests), confidence high

- `ControllableBus.cs:76-79` ("exactly as a real echo-mode adapter does"), `:224-225`, `DeferredEchoQueue.cs:16-17` ("a real echo-mode adapter delivers the echo synchronously from inside Transmit"). Only `CanKit.Adapter.Virtual` does (`VirtualBusHub.cs:72-76`); every hardware adapter delivers on its RX poll thread (`PcanBus.cs:440-560`, Kvaser notify callback). The hardware-faithful model is `DeferredEchoCapable`, now used at 31 sites in other packages' tests but at one in `TxConfirmTests.cs:83`. The echo-path tests that run on the synchronous double (`Echo_Bus_Confirms_Via_Real_Echo_Match`, `Echo_Bus_Matches_Concurrent_Identical_Frames_Individually_Without_Crashing`, `Echo_Bus_Does_Not_Let_A_Resolved_Send_Consume_A_Later_Identical_Echo`) therefore exercise the Virtual shape of reentrancy; the `Concurrent_Identical` stress on a deferred bus (where the FIFO really has N entries under contention) does not exist. Fix: reword both comments to name Virtual, and add a deferred-mode variant of the 16-thread stress test (release echoes from a second thread while sends race).

### R-9 · A bus fault that is not a bus-off is invisible to subscribers and confirmations — Minor, confidence high

- The only fault a 0.5.6 adapter ever raises is "my poll loop died" (Q8 above). After it, no frame will ever arrive. `OnFaultOccurred` returns at `CanBusService.cs:685`; `Frames` streams stay open (`ISubscription.cs:36-41` promises completion only on dispose), pending sends end as `Timeout`, and `BackgroundExceptionOccurred` (`ICanBusService.cs:84-91`) is documented for predicate faults only. A consumer that only holds an `ISubscription` has no way to learn the bus is dead; `BusStateMonitor` in Reliability subscribes to `FaultOccurred` itself for exactly this reason.
- Reach: a conforming adapter whose driver fails (unplugged USB, driver error); nothing hostile.
- Test: none. Fix (minimal, additive): relay `ICanBus.FaultOccurred` through `BackgroundExceptionOccurred` and document it; a larger step (completing all subscriptions on fault) changes the `Frames` contract and belongs in §8.

### R-10 · `BackgroundExceptionOccurred` is not always raised on the dispatch thread — Docs, confidence high

- `ICanBusService.cs:88-89`: "Invoked synchronously on the bus's dispatch thread, so handlers must return quickly and must not call back into the service." `CallbackSubscription.Report` raises it from the pump task (`CanBusServiceExtensions.cs:115`, `:127`, `:142-143`), i.e. a thread-pool thread, where calling back into the service is harmless. Fix: "on the bus's dispatch thread for predicate faults and on the delivery pump for callback faults".

### R-11 · "Accepted by the driver" overstates what the approximated path knows on a queued bus — Docs, confidence medium

- `ICanBusService.cs:154-155`, `TxConfirmation.cs:55-57`: approximated = "the driver accepting the frame". Upstream ships `CanKit.Core.Utils.QueuedCanBus` (`QueuedTxCanBus.cs:118-125`), whose `Transmit`/`TransmitAsync` return "enqueued", not "accepted" (its own comment). Wrapped in it, `Confirmed = true, IsApproximated = true` means "queued in process". Not a defect in RawCan; a sentence in the contract ("what `ICanBus.Transmit` returning > 0 means for the bus in use") would close it. Reach: anyone wrapping their bus in the upstream queue.

### R-12 · EchoProbe serialises the NAME by hand — nit, confidence high

- `tools/CanKit.Pro.EchoProbe/Program.cs:351-352` uses `BitConverter.GetBytes` + conditional `Array.Reverse`; `J1939Name.ToBytes()` (`J1939Name.cs:157-162`) is the fixed-endian version the package added for #55. Equivalent on every host; one place to get wrong instead of two. No behavioural effect.

### Things checked and found correct (so the next reviewer need not)

- **Lock ordering.** Two locks: `_gate` (registry) and `_pendingGate` (pending sends). `_gate` is never held while calling out (`:149-156`, `:166-173`, `:264-269`). `_pendingGate` is held across `_bus.Transmit` (`:471-504`, deliberate, #53/#102) and across `TryMatchEcho` (`:615-676`); on a synchronous-echo bus the RX dispatch — including caller-supplied predicates — therefore runs inside `_pendingGate` on the transmitting thread, which is reentrant and the Tcs is completed with `RunContinuationsAsynchronously` (`PendingSend.cs:108-109`), so no caller continuation runs under the lock. `Dispose` takes the two sequentially, never nested. `DeferredEchoQueue._gate` is released before `_deliver` (`:112-127`), so test infra does not invert the order. No deadlock path found.
- **Dispose vs in-flight echo send.** `_pendingDisposed` is set under `_pendingGate` (`:291`) and checked under it (`:473`); a send that registered before the sweep is cancelled (`:302-303`) and its `finally` no-ops on `Node == null` (`:596-597`). A synchronous echo that arrives after `FrameObserved -=` (`:260`) is lost and the send is swept anyway. Correct, but the register-after-sweep race is untested (R-M2).
- **Backpressure/drop policy.** Bounded `DropOldest`, `SingleReader = false`, `SingleWriter = false` (`Subscription.cs:82-88`) — correct, since `ControllableBus` tests raise from several threads. Pinned by `Full_Subscription_Buffer_Drops_The_Oldest_Frames_Not_The_Newest`.
- **Reader failure propagation.** There is no reader task in RawCan; the stream ends only through `TryComplete()` without error, so `Frames` never throws. The callback pump's own failure is reported (`CanBusServiceExtensions.cs:121-128`). The gap is the bus fault (R-9), not a reader.
- **CanIdFilter overlap semantics.** Range/Range, Mask/Mask, Range/Mask reduce to one bit-witness search (`CanIdFilter.cs:197-304`); the 32-bit walk honours masks above bit 28; both factories reject out-of-space inputs. Differential tests against brute force over `Matches` (`CanIdFilterPropertyTests.cs`, seeded) and the exhaustive 13×13 sweep (`CanIdFilterOverlapTests.cs:232-289`) are the right kind of evidence. No defect found.
- **PendingKey identity.** `Ext | Rtr | Error` in, `Brs | Esi` out (`PendingSend.cs:33`), reasoning at `:20-32` is sound (ESI reflects the controller, not the request). Hash and `Equals` agree; `ForEchoLookup` aliasing is safe because the key never escapes `TryMatchEcho`.
- **netstandard2.0 polyfill divergence.** RawCan ships only `IsExternalInit` under `#if !NET5_0_OR_GREATER` (needed by the two `record struct`s) and takes `System.Memory`, `System.Threading.Channels`, `Microsoft.Bcl.AsyncInterfaces` on netstandard2.0 (`.csproj:12-16`). Every BCL call in the package exists with the same semantics on net48 (`Task.ContinueWith(…, state, token, options, scheduler)`, `CancellationTokenSource.CreateLinkedTokenSource`, `BoundedChannelFullMode.DropOldest`, `MemoryExtensions.SequenceEqual`). The `Polyfill` package is test-only (`Directory.Packages.props:48-53`). The only runtime difference found is the `CancelAfter` upper bound in R-4 (int vs uint range), which is a misuse either way. No divergence.
- **Addressing.** NAME bit layout, J1939-21 PGN composition (PDU1 PS excluded from the PGN), `Normalize` keeping DP/R bits, Cannot-Claim recognition (SA 0xFE to DA 0xFF), LSB-first NAME bytes, `ComposePgn` refusing a PDU1 PGN with a non-zero low byte — all consistent with SAE J1939-21/-81 as I remember them (from memory, standard text not at hand). No defect.

## 4. Test quality and gaps

**Strong.** `Echo_Bus_Matches_Identical_Pending_Sends_In_Fifo_Order` is the one test in the suite that can observe FIFO order, and it says so; `Echo_Bus_Does_Not_Let_A_Resolved_Send_Consume_A_Later_Identical_Echo` makes the #24 race deterministic by resolving the first send from inside the second's `Transmit`; `Frame_Arrival_Is_Stamped_Before_The_Pending_Send_Lock` asserts an ordering against an instant the test itself creates (no tolerance to widen); `PendingKeyTests` explains why direct `Equals` tests exist (the dictionary never reaches `Equals` for keys in different buckets — R-M5 demonstrates it); `Echo_Lookup_Does_Not_Copy_The_Payload_Of_Every_Echo_Frame` measures allocation rather than arguing it; the seeded differential tests for overlap are exactly what #210 asked for.

**Gaps (no test today):**
1. Q8 — a fault with `BusState != BusOff` leaves sends pending (`:685` unpinned; R-M1).
2. `Dispose` racing a send between the eager check (`:309`) and the gate (`:473`) — R-M2.
3. `Dispose` during a stalled approximated send (R-2).
4. R1 — a late echo after a timeout: neither "confirms the next send" nor the opposite is pinned.
5. Q7 RawCan half — non-canonical FD length on the echo path.
6. `Subscribe(default(CanIdFilter))` (R-6).
7. `Subscribe`/`SendConfirmedAsync` after `CanBusService.Dispose` throw `ObjectDisposedException` (`:151-152`, `:309-310`): untested (only `Reconfigure` after subscription dispose is).
8. `ISubscription.WaitToReadAsync` returning false after dispose: untested in RawCan (consumed by ISO-TP only).
9. `FindOverlappingFilterSubscriptions` excluding a concurrently disposed subscription (`:130`, `:133`): untested.
10. Stamps on both paths (R-3).
11. A deferred-echo variant of the 16-thread identical-frame stress (R-8).
12. `J1939Name.Compose` range checks (`J1939Name.cs:118-131`) and `J1939Id.Compose` `dataPage > 1` (`J1939Id.cs:69`): no test exercises any of the eight throws.

**Comment drift in tests:** `RawCanSubscriptionTests.cs:882` and `:974` cite FR-RAW-010 for FR-RAW-015 content (carried over).

**Timing-gated tests (per #92: the quantity the host can perturb, and the margin):**

| Test | Wall-clock dependence | Perturbable quantity / margin | Risk |
| --- | --- | --- | --- |
| `TxConfirmTests.Echo_Bus_Timeout_Is_Configurable_Per_Call` (:535-552) | asserts `elapsed(500 ms timeout) > elapsed(100 ms timeout)` | a scheduling/GC stall ≥ 400 ms during the first call inverts it; margin 400 ms | **real** (#92 class) |
| `TxConfirmTests.Cancelling_One_Send_Does_Not_Block_On_An_Unrelated_Send_Inside_Transmit` (:235-237) | `Task.Run(Cancel)` must start and return within `Task.Delay(2 s)` | thread-pool injection latency while one pool thread is parked in `OnTransmitting` and another runs the test; margin 2 s | moderate |
| `TxConfirmTests.Echo_Bus_Reports_Rejected_Immediately_Without_Waiting_For_Timeout` (:583) | `< 1 s` for a synchronous path | host stall ≥ 1 s; margin 1 s | low |
| `TxConfirmTests.Echo_Bus_Times_Out_Observably_When_No_Echo_Arrives` (:372-373) | `≥ 250 ms` and `< 5 s` for a 300 ms timeout | lower bound cannot fail (a timer never fires early); upper margin 4.7 s | low |
| `TxConfirmTests.Outstanding_SendConfirmed_Resolves_As_BusOff_Immediately_On_Fault` (:640) | `< 5 s` vs a 30 s timeout | margin 5 s | low |
| `TxConfirmTests.A_Token_Cancelled_While_The_Echo_Path_Waits_On_The_Gate_Abandons_The_Send` (:290) | `Task.Delay(50)` before `Cancel()` | none — both orderings (cancelled before or while waiting at the gate) pass; the sleep is dead weight, not a risk | none |
| `RawCanSubscriptionTests.Callback_Subscribe_Dispose_From_Inside_Handler_Does_Not_Hang` (:259) | `disposed` must complete within 500 ms (vs the 2 s join) | pump start + `Transmit` + handler on a pool thread; margin 500 ms | **real** on a loaded runner |
| `RawCanSubscriptionTests.Disjoint_Id_Filters_…` (:111-112), `Disposing_Service_Stops_Delivery_And_Detaches` (:763) | 200/500 ms "nothing more arrives" windows | delivery on Virtual is synchronous in `Transmit`, so a late frame is impossible; the window only costs time | none |
| `RawCanConcurrencyTests` (:86) | 20 ms drain windows, 60 s bound | bound only | none |
| `ControllableBus`/`DeferredEchoQueue` 5–10 s waits | hang bounds on events the test causes | — | none |

## 5. Mutations proposed

| ID | Edit (exact) | Expected |
| --- | --- | --- |
| R-M1 | `src/CanKit.Pro.RawCan/CanBusService.cs:685` — delete the line `if (_bus.BusState != BusState.BusOff) return;` | **green = gap** (Q8): no test raises a fault with `BusState != BusOff` and asserts the send stays pending |
| R-M2 | `src/CanKit.Pro.RawCan/CanBusService.cs:473-474` — delete `if (_pendingDisposed) throw new ObjectDisposedException(nameof(CanBusService));` | **green = gap**: the register-after-sweep race is untested (`Disposing_Service_Cancels_Outstanding_SendConfirmed_Calls` registers before `Dispose`) |
| R-M3 | `src/CanKit.Pro.RawCan/CanBusService.cs:490-494` — delete the `if (cancellationToken.IsCancellationRequested) { RemovePending(pending); cancellationToken.ThrowIfCancellationRequested(); }` block | **red**: `TxConfirmTests.A_Token_Cancelled_While_The_Echo_Path_Waits_On_The_Gate_Abandons_The_Send` (TransmitCount 2, no OCE) |
| R-M4 | `src/CanKit.Pro.RawCan/Subscription.cs:166` — delete `if (isEcho && !_includeEcho) return;` | **red**: `RawCanSubscriptionTests.Subscription_Does_Not_Deliver_Echoes_By_Default`, `Echo_Is_Never_Offered_To_A_Predicate_That_Did_Not_Opt_In`, `IncludeEcho_Is_Decided_Per_Subscription`, `Reconfigure_Does_Not_Change_The_Echo_Choice`, `Withholding_Echoes_From_Subscribers_Does_Not_Break_SendConfirmed` |
| R-M5 | `src/CanKit.Pro.RawCan/PendingSend.cs:83` — delete `&& _frameKind == other._frameKind` (leave the hash at :73 as is) | **red only** in `PendingKeyTests.Keys_Differing_In_One_Component_Are_Not_Equal("frame kind")`; `TxConfirmTests.Echo_Bus_Does_Not_Confirm_A_Classic_Send_From_A_Can_Fd_Echo_With_The_Same_Id` stays **green** because the hash still separates the buckets — this pins the claim in `PendingKeyTests.cs:15-21` that the bus tests cannot reach `Equals` |
| R-M6 | `src/CanKit.Pro.Addressing/J1939Fields.cs:60` — replace `(IsPdu1 ? 0u : PduSpecific)` with `PduSpecific` | **red**: `AddressingTests.J1939_Compose_Decompose_Round_Trips_For_A_Pdu1_Message` (Pgn 0xC80B ≠ 0xC800), `ComposePgn_Round_Trips_For_A_Pdu1_Pgn_With_A_Destination_Address`, `J1939Pgn_Classifies_Well_Known_J1939_Pgns` (IsCannotClaim) |

## 6. Doc drift (README / XML docs vs behaviour)

| Where | Says | Is |
| --- | --- | --- |
| `RawCan/README.md:16` | echo "as SocketCAN, Kvaser and Vector deliver it"; "nothing ... has run against real CAN hardware" | Kvaser 0.5.6 delivers none on a quiet bus, Vector Classic drops it; `SendConfirmedAsync` ran on PCAN and Kvaser (R-1) |
| `RawCan/README.md:96-118` | TX-Confirm section without adapter limits | `ICanBusService.cs:159-166` has them (R-1) |
| `RawCan/README.md:112`, `TxConfirmation.cs:17-21`, ADR-7 | `BusOff` is a reachable outcome | unreachable on every 0.5.6 adapter (Q8) |
| `RawCan/README.md:116-117`, `ICanBusService.cs:152-153` | "never cross-matched" | a late echo after a timeout is matched to the next identical send (R1) |
| `RawCan/README.md:117-118` | Dispose cancels any outstanding call | echo path only (R-2) |
| `RawCan/README.md:127-128`, `TxConfirmation.cs:72-77,:88-103` | stamps bracket the driver call inside the send lock; zero only on rejection | path-dependent (R-3) |
| `ICanBusService.cs:88-89` | fault event raised on the dispatch thread | also from the callback pump (R-10) |
| `ICanBusService.cs:154-155`, `TxConfirmation.cs:55-57` | approximated = driver accepted | "enqueued" on `QueuedCanBus` (R-11) |
| `CanKit.Pro.Addressing.csproj:4` | overlap detection in Addressing | in RawCan (R-7) |
| `ControllableBus.cs:76-79,:224-225`, `DeferredEchoQueue.cs:16-17`, `EchoWorlds.cs:24-28` | synchronous echo is what real adapters do; Kvaser/Vector flag | only Virtual; see K1 (R-8) |
| `CanBusService.cs:391-392`, `ICanBusService.cs:177-179` | every 0.5.6 adapter completes `TransmitAsync` synchronously | **verified true** for all seven adapters |
| `CanBusService.cs:371-373`, `ICanBusService.cs:189-190` | 0.5.6 adapters ignore the token in `TransmitAsync` | **verified true** (all `Task.FromResult(Transmit(frame))`) |
| `CanFrameEvent.cs:82-86`, `ICanBusService.cs:38-41` | Virtual in Echo mode echoes unflagged | **verified** (`VirtualBusHub.cs:72-76`, `CanReceiveData.IsEcho` left default) |
| `docs/upstream-candidates.md:48-49` | Virtual does not declare `CanFeature.Echo` | **verified** (`VirtualProvider.cs:15`) |

## 7. What is done well (brief)

- The pending-send design is explained where it lives: why the lock spans `Transmit` (`:445-470`), why the timeout path must not take it (`:539-557`), why claiming is by completing (`:653-667`). Each paragraph names the race it closes and the issue that found it.
- Dispatch is lock-free and allocation-free for unmatched frames; one payload copy per frame shared by all matching subscriptions; the echo gate runs before any user code.
- `CanIdFilter` factories fail loudly on the mistake that used to be invisible (29-bit ID without `Extend`), and the overlap search is verified against brute force rather than against itself.
- `PendingKey` is the right identity (`Ext`/`Rtr`/`Error` in, `Brs`/`Esi` out) and its reasoning is written down.
- The test doubles are honest about what they model and why (`ControllableBus`, `DeferredEchoQueue` header comments), apart from the one sentence in R-8.
- EchoProbe refuses to call a silent peer or a rejected transmit a measurement (`Program.cs:511-534`), which is what made the 2026-10-06 table trustworthy.

## 8. Open questions for the maintainer

1. **R1 (late echo).** Document it as a known limit of content-based matching, or add a per-key tombstone with a bounded lifetime (the J1939 node's `ClaimEchoGrace` already does this one layer up)? A tombstone re-opens the #24 cascade for genuinely lost frames; a sentence in the contract does not.
2. **Q8 (BusOff).** Make `BusOff` reachable by watching `ErrorFrameReceived` + `BusState` the way `BusStateMonitor` does, or correct `TxConfirmation.cs`, the README and ADR-7 to "only when the adapter raises `FaultOccurred` while `BusState` is `BusOff`, which no 0.5.6 adapter does"? Either way an issue should carry it; none does today.
3. **Q7 RawCan half.** Reject a CAN-FD payload whose length is not 0–8, 12, 16, 20, 24, 32, 48 or 64 in `SendConfirmedAsync` (`ArgumentException`, both paths), or leave it to the adapter? Rejecting is a behavioural change for a frame that today "works" on SocketCAN/Virtual and times out on Vector.
4. **R-2.** Cancel approximated in-flight sends on `Dispose` (one more set under `_pendingGate`) or change the README sentence?
5. **R-6.** Rejecting `default(CanIdFilter)` turns a silent "matches ID 0" into a throw — bug fix or SemVer-relevant change?
6. **R-9.** Should a bus fault complete every `Frames` stream (contract change) or only be relayed through `BackgroundExceptionOccurred` (additive)?
7. **R-1.** The RawCan README's "What is validated" paragraph: rewrite with the measured rows, or point at `docs/reviews/2026-10-06-echo-hardware-measurements.md` and keep one sentence?
8. **#249.** Vector is still unmeasured; the contract text is "read from its source". Is a Vector run with `EchoProbe` planned, or should the contract say "unmeasured" more loudly?
