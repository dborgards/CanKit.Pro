# CanKit.Pro.Uds — deep review, 2026-10-21 (main @ 36c97ca)

Read-only review. Standard citations (ISO 14229-1, ISO 14229-2, ISO 15765-2) are from memory
unless a file in the repository is quoted; each such place says so.

## 1. Scope read

Source, all read in full (line counts from `wc -l`):

| File | Lines |
|---|---|
| `src/CanKit.Pro.Uds/UdsClientImpl.cs` | 1717 |
| `src/CanKit.Pro.Uds/UdsFunctionalClient.cs` | 656 |
| `src/CanKit.Pro.Uds/IUdsClient.cs` | 309 |
| `src/CanKit.Pro.Uds/UdsClientOptions.cs` | 110 |
| `src/CanKit.Pro.Uds/SuppressedResponseWindows.cs` | 102 |
| `src/CanKit.Pro.Uds/UdsException.cs` | 127 |
| `src/CanKit.Pro.Uds/UdsServiceId.cs` | 101 |
| `src/CanKit.Pro.Uds/UdsTransferResponse.cs`, `UdsFunctionalResponse.cs`, `UdsNegativeResponseCode.cs`, `UdsClient.cs`, `UdsClientExtensions.cs` | 56 / 30 / 71 / 56 / 33 |
| `src/CanKit.Pro.Uds/README.md` | 155 |

Tests, all read in full:

| File | Lines |
|---|---|
| `tests/CanKit.Pro.Tests/TestCases/Uds/UdsClientTests.cs` | 2605 |
| `tests/CanKit.Pro.Tests/TestCases/Uds/UdsFunctionalClientTests.cs` | 1383 |
| `tests/CanKit.Pro.Tests/TestCases/Uds/UdsExpiredDeadlineTests.cs` | 875 |
| `tests/CanKit.Pro.Tests/TestCases/Uds/UdsTransferTests.cs` | 789 |
| `tests/CanKit.Pro.Tests/TestCases/Uds/SimulatedUdsEcu.cs` | 337 |

Read for the claims that depend on them: `src/CanKit.Pro.IsoTp/IIsoTpChannel.cs`,
`IsoTpReceivedPdu.cs`, `IsoTpReceptionInProgress.cs`, `IsoTpFunctionalListener.cs`,
`IsoTpFunctionalResponse.cs`, `IsoTpFunctionalCollection.cs`, `IsoTpFunctionalClient.cs:130-260`,
`IsoTpChannel.cs:290-345, 895-925`, `src/CanKit.Pro.Actor/ClockTimeout.cs`.

History since the previous review (`git log 8cec2ee..HEAD -- src/CanKit.Pro.Uds tests/.../Uds`):
`551261a`/`d0902e8` (tick conversion), `cc2d688`…`93a91d5` (PR #271, DisposeAsync and disposal
hardening), `66f4255` (PR #274, multi-DID), `1ae2c12` (PR #334, test R on the virtual clock).
`README.md` has no commit since `8cec2ee`.

## 2. Status of previous findings

| ID | Status | Evidence |
|---|---|---|
| **U1** multi-DID partial answer rejected | **Fixed** | PR #274 (`66f4255`, issue #252 closed). `UdsClientImpl.cs:301-308` keeps the answered DIDs; a positive response with no record is the only protocol error left. Tests `ReadDataByIdentifier_Multi_Did_Returns_The_Answered_Dids_When_Others_Are_Left_Out`, `…_Rejects_A_Positive_Response_Without_Records`, `…_Surfaces_RequestOutOfRange_When_None_Is_Supported` (`UdsClientTests.cs:2073-2134`). |
| **U2** P2Server / P2\*Server from the session response neither decoded nor applied | **Open, never ticketed** | `UdsClientImpl.cs:170-181` returns the raw `sessionParameterRecord`; `UdsClientOptions` is immutable (`:5-8`); nothing reads the record. `gh issue list --search "P2Server OR sessionParameterRecord"` finds nothing. An ECU whose programming session declares P2\*Server_max = 10 s (record bytes 2..3 in 10 ms units, ISO 14229-1 §9.2.2.4 from memory) and sends its next 0x78 after 8 s is conformant; the client with default P2\*ClientMax 5 s throws `UdsTimeoutException(P2Star)`. See U-1 below for what the same gap does to stale responses. |
| **U3** response validation untested; `UploadAsync` width check; README example | **Open, never ticketed** | No test on the physical client sends a positive response with a wrong sub-function, DID, routine id or BSC echo — the `SimulatedUdsEcu` handlers always echo `req[1]`/`req[1..2]`; mutations M1/M2 below re-check this. `UploadAsync` still decodes `memorySize` without the >8-byte guard (`UdsClientImpl.cs:986-992` vs `:897-910`), see U-4. `README.md:65-72` still pairs `memorySize` 512 with an arbitrary `firmwareChunk`, which `:916-919` rejects unless the chunk is exactly 512 bytes. |
| **Q9** `Dispose` blocks on work that needs the channel's actor | **Addressed with a documented residual** | PR #271 (`cc2d688`…`93a91d5`, issue #251 closed): `DisposeAsync` on `UdsClientImpl.cs:1541-1563` reached through `UdsClientExtensions.DisposeAsync` (`:26-32`); the keep-alive join is bounded by `DisposeLockTimeout` on both paths (`:1685`, `:1691`). Residual: from a channel callback, sync `Dispose` can still block 5 s (keep-alive join, `:1685`) **plus** 5 s (request lock, `:1527`), because the keep-alive's request is parked in `SettleAsync()` which is awaited without any token (`:1276`) and needs the actor the caller is holding. `UdsClientExtensions.cs:15-24` says so; `IUdsClient.cs:36-37` and `README.md:121-123` ("up to five seconds") do not. Tests: `Dispose_Joins_A_Running_KeepAlive…`, `A_Cancellation_Callback_That_Throws_*` (`UdsClientTests.cs:2295-2399`) cover the bounded joins with a stub whose send never ends; none reproduces the from-the-actor call site. |
| **#312 / PR #334** cancelled-after-window test | **Done** | `1ae2c12`: `R_A_Suppressed_Send_Cancelled_After_Its_Provisional_Window_Still_Opens_One` runs on the virtual clock (`UdsExpiredDeadlineTests.cs:489-534`); no source change. The window re-note it pins is `UdsClientImpl.cs:527-534`. |
| §3 `SimulatedUdsEcu` masks the SID with 0x7F | Open | `SimulatedUdsEcu.cs:122`. |
| §3 `SimulatedUdsEcu` answers suppressed requests ≠ 0x3E positively | Open | `SimulatedUdsEcu.cs:123` only treats 0x3E. `Suppressed_Send_Windows_Are_Kept_Per_Service` (`UdsClientTests.cs:847`) sends `11 81` and the double answers `51` — the test passes only because the wait-out drops it. |
| §3 channel disposed during a request → `InvalidOperationException` | Open | `IsoTpChannel.cs:335`; `UdsException.cs:12-13` and `README.md:127-129` still promise `IsoTpException` subclasses for transport failures. The client even classifies `InvalidOperationException` as "refused before transmission" (`UdsClientImpl.cs:547-548`), which is right for an oversize PDU and wrong for a disposal mid-send. |
| §3 functional client: no `MaxResponsePendingCount` counterpart | Open | `UdsFunctionalClient.cs:460-473` extends on every 0x78 without a count; so does the physical client's wait-out, see U-5. |
| §3 functional client keeps a faulted listener task | Open | `UdsFunctionalClient.cs:417-486` catches only `OperationCanceledException` and `ObjectDisposedException`; any other exception leaves the `_listeners[sid]` entry in place, `EnsureListener` (`:392`) then never starts another, and the service's window is neither waited out nor forgotten again. |
| §3 NRC 0x21 doc "repeat, not wait" | Open | `UdsClientOptions.cs:82-86`. |
| §3 `IUdsClient` summary names seven services | Open | `IUdsClient.cs:16-17`; the interface has eleven. |
| §3 `requestSeedLevel` bound 0x7F in doc vs 0x7D in code | Open | `IUdsClient.cs:142, 147-148` vs `UdsClientImpl.cs:368-370` (`>= 0x7F` rejects 0x7F, so 0x7D is the largest accepted, 0x7E its sendKey). |
| §3 duplicate `<summary>` in `SuppressedResponseWindows` | Open | `SuppressedResponseWindows.cs:48-53`; also a stray `///.` line at `:72-73`. |

## 3. New findings

### U-1 · A late answer to the previous same-service request is taken as this request's — stale data on same-DID polling, a spurious `UdsProtocolException` otherwise
**Important · confidence medium** (mechanism certain; reachability needs a server later than the client's P2/P2\*, which U2 leaves to manual configuration).

`UdsClientImpl.cs:1175-1179` rejects a response only when its first frame predates this request's last-frame handoff; `:1233-1240` then accepts any PDU whose first byte is the positive SID. A request A that timed out (`:1182`, `:1414`) or was cancelled leaves no window for its service — the catch at `:1243-1251` discards what is queued *now* and nothing more. Sequence with a slow server: A (`22 F1 90`) times out at P2; B (`22 F1 90`) is sent; A's late `7F 22 78` arrives after B's handoff → B restarts P2\* on it (`:1208-1228`); A's final `62 F1 90 <old>` arrives → B returns it (`:1240`). B's own answer then arrives for nobody and is dropped by C's pre-send discard, or returned to C. In a polling loop this is a permanent one-request skew with no error. When B asks for a *different* DID, the per-service echo check (`:223-225`, likewise `:173`, `:192`, `:330`, `:351-357`, `:394`, `:415`, `:824`) throws `UdsProtocolException` and aborts B, whose own answer is still due — the README's "mismatched responses … are silently discarded; the wait continues inside the same budget" (`README.md:81-82`) is true only at SID level.

Reachability: a conformant server never answers later than P2Server_max / P2\*Server_max, so this needs either a nonconformant server or a client whose P2/P2\* is shorter than the session declares — the second is exactly what U2 leaves unapplied, and the README example sets `P2ClientMax = 50 ms` (`README.md:53`).

Test: none. `TimedOut_Request_Does_Not_Poison_Next_Same_Service_Transaction` (`UdsClientTests.cs:1685`) covers only the response that is *already queued* when the next request is sent.

Fix: (a) move the per-service echo check into the receive loop — pass an `isMine(byte[])` predicate into `ExecuteCoreAsync` (the sub-function/DID/routine-id/BSC byte table the functional client already has in `EchoedRequestBytes`, `UdsFunctionalClient.cs:571-582`) and treat a non-matching positive response as a stray that keeps the wait going; (b) for the same-echo case, decide between documenting the limitation and an opt-in post-timeout quarantine window for the service (the `SuppressedResponseWindows` machinery exists; P2\* from the timeout would be the natural length) — see open question 2.

### U-2 · Keep-alive failures are invisible, and the comment names an observability path that does not carry them
**Minor · confidence high.**

`TesterPresentKeepAlive.LoopAsync` (`UdsClientImpl.cs:1654-1666`) swallows every exception but cancellation and disposal — `UdsTimeoutException`, `UdsNegativeResponseException` (0x7F serviceNotSupportedInActiveSession after an ECU reset, say) and `IsoTpException` alike — and the comment at `:1663-1665` sends callers to the channel's `BackgroundExceptionOccurred`. That event is the channel's (`IIsoTpChannel.cs:198-206`) and never sees a UDS-level outcome: a timeout or NRC is thrown into the loop and dropped there. A session that died (ECU reset, S3 elapsed while a long `computeKey` held the lock) is noticed only by the next request's NRC 0x7E/0x7F, with nothing that says why. The keep-alive also ends silently when the (not owned) channel is disposed underneath it (`:1659`).

Test: none asserts anything about a failing keep-alive.

Fix: raise a `KeepAliveFailed` event (handle or client) with the exception, or expose a `Task` on the handle that faults after N consecutive failures; at least correct the comment and the `StartTesterPresentKeepAlive` XML doc.

### U-3 · Every `SettleAsync` is awaited without the request's token
**Minor · confidence high** (mechanism); reachability only when the channel's actor is blocked — the Q9 call site, which no contract forbids (`IUdsClient.cs:36-37`).

`UdsClientImpl.cs:1272-1282` calls `_channel.SettleAsync()` with no token; it is reached from the wait-out (`:571`, `:584`), the pre-send and abort discards (`:1268`) and the deadline probe (`:1400`). `IIsoTpChannel.SettleAsync`'s own contract (`IIsoTpChannel.cs:140-144`) says a token ends the wait while the settling goes on, which is exactly what a disposal needs. As written, `Dispose`'s `CancelInFlight` (`:1582-1586`) cannot reach a request parked there, so the bounded 5 s + 5 s waits of Q9's residual are the only exit, and `DisposeAsync` cannot do better than `Dispose` here.

Fix: pass `linkedToken` through (`SettleAsync(linkedToken)`), catching `OperationCanceledException` where the caller already handles cancellation.

### U-4 · `UploadAsync` decodes `memorySize` without the width guard `DownloadAsync` has
**Minor · confidence high** (misuse of the API only).

`UdsClientImpl.cs:986-992` shifts every byte into a `ulong`; a 9-byte size with a non-zero leading byte (`01 00 00 00 00 00 00 00 01`) decodes to 1 while the wire (`:751-756`) carries the full nine bytes. The client then reads one block, sends `RequestTransferExit` mid-transfer and trims to 1 byte — no error. `DownloadAsync` rejects this at `:900-908` (`DownloadAsync_Rejects_Oversized_MemorySize_Before_Decode`).

Test: none for the upload side.

Fix: lift the leading-byte scan of `:899-910` into a shared helper and use it in both.

### U-5 · No bound on NRC 0x78 extensions of a suppressed-send window
**Minor · confidence high**; only a buggy or hostile peer triggers it.

`WaitOutSuppressedResponseWindowAsync` extends `until` on every `7F sid 78` (`UdsClientImpl.cs:630-647`), and the functional listener does the same (`UdsFunctionalClient.cs:460-473`); `MaxResponsePendingCount` (`:1211`) applies only to the request's own loop. A server that keeps answering 0x78 to a suppressed `3E 80` holds every later `3E` request for as long as the caller's token allows.

Fix: count extensions per window and forget the window after `MaxResponsePendingCount` of them (physical) / a matching option on `UdsFunctionalClient.Create`.

### U-6 · A suppressed `10 8x` / `11 8x` through `SendRawAsync` leaves `CurrentSession` stale
**Docs · confidence high.**

`SendRawAsync` sends `10 83` without a response (`UdsClientImpl.cs:480-484`); the ECU changes session, `_currentSession` (`:177`) does not. Same for `11 81` (`:198`). `IUdsClient.cs:47-54` describes `CurrentSession` as tracking the session without mentioning the escape hatch. Either update it on a confirmed suppressed send of 0x10/0x11 (the frame is on the bus; the ECU answers a suppressed session change silently) or say that `SendRawAsync` does not track it.

### U-7 · The functional result list reports `7F sid 78` as a negative response
**Docs · confidence high.**

`UdsFunctionalClient.cs:289, 296` adds every `7F sid nrc` to the result, 0x78 included; `UdsFunctionalResponse.IsNegative` is then true for an ECU whose final answer is still coming (after the window, where only the listener hears it). A caller counting refusals counts "pending" as one. Either filter 0x78 out of the result or document that the caller must (`SendRawAsync` summary, `:131-146`).

### U-8 · Trivia (no separate IDs)
- `StartTesterPresentKeepAlive` refuses a period above `int.MaxValue` ms with `ArgumentOutOfRangeException(nameof(period))` (`UdsClientImpl.cs:452-453`) although the constructor accepted a `TesterPresentPeriod` up to ~49 days (`:137-138`); with `period == null` the blamed argument was never passed.
- `SuppressedResponseWindows.Note(byte, long, TimeSpan)` and `Remaining(long)` (`:24-25`, `:90-91`) hard-code `Stopwatch.Frequency` and are unused by the clients, which always pass `_time.Frequency`; on an injected clock they would be wrong if anyone picked them up.

## 4. Test quality and gaps

What is strong: the P2/P2\* arithmetic is pinned from both ends by `UdsExpiredDeadlineTests` A–M (stub stamps, not the host's clock), the suppressed-window machinery by ~20 virtual-clock tests in `UdsClientTests` (`:790-1491`) and `UdsFunctionalClientTests`, the BSC wrap by `DownloadAsync_Multi_Block_Uses_BSC_Wrap`, the lock discipline by contention-event tests (`SecurityAccess_Holds_Lock_Across_Seed_And_Key`, `DownloadAsync_Holds_Exclusive_Lock_Against_Concurrent_TesterPresent`), the disposal by `:2218-2481`.

Gaps:
1. No positive response with a wrong echo on the physical client (U3): sub-function (0x10, 0x11, 0x27, 0x31, 0x3E), DID (0x22, 0x2E), routine id, BSC — mutations M1/M2.
2. `MaxResponsePendingCount` boundary is not pinned (M3).
3. U-1's stale-answer sequence is not reproduced; a `StubChannel` variant that delivers `7F 22 78` + `62 F1 90 old` after the second request's handoff would do it.
4. `UploadAsync`: no test for the >8-byte size, the final-block trim (`:1023-1026`) or the empty-chunk protocol error (`:1014-1016`); `RequestTransferSetupCoreAsync` protocol errors (`:762-776`, zero width, width > 8, truncated) untested.
5. Empty response and NRC shorter than three bytes (`:1186-1195`) untested.
6. Keep-alive: nothing asserts behaviour on failures (U-2) or that the loop stops when the borrowed channel is disposed.
7. Functional: faulted listener task (§3) and unbounded 0x78 extension (U-5) untested.
8. `SimulatedUdsEcu` answers suppressed non-0x3E requests (`:123`), so every test using `11 81` exercises the wait-out against a nonconformant double; a conformant one would leave the inbox empty and the tests would still have to pass — worth fixing the double before relying on it for more.

Timing-gated tests (wall clock, per the #92 rule — "quantity the host can perturb" named):
- `UdsExpiredDeadlineTests.I_A_Reception_That_Began_Before_The_Request_Went_Out_Is_Not_Waited_For` (`:293-302`): asserts `sw.Elapsed < 400 ms`; the host perturbs the 80 ms deadline callback, the 50 ms `InProgressRecheck` timer and thread-pool latency; margin ≈ 270 ms. **At risk.**
- `UdsFunctionalClientTests.A_Pending_Answer_Extends_The_Window_From_Its_Arrival_Not_From_The_Collections_End` (`:677-687`): `sw.Elapsed < 2400 ms` against ≈1650 ms expected; perturbed: listener retire latency and two real collections; its own comment records macOS 300 ms late. **At risk.**
- `UdsFunctionalClientTests.A_Request_The_Client_Refuses_Leaves_No_Window` (`:935-939`): `< 1 s` vs ≈100 ms; margin 900 ms. Moderate.
- `UdsClientTests.ResponsePending_Loop_Aborts_When_Exceeding_MaxResponsePendingCount` (`:1998-2000`): real P2 500 ms / P2\* 1 s against the ECU's unpaced 0x78s over the virtual bus; a 0x78 landing late turns the asserted `UdsProtocolException` into `UdsTimeoutException`. Moderate (the gap that caused #115/#116 is gone; the 500 ms P2 remains).
- One-sided (the host can only make them pass): `UdsExpiredDeadlineTests` A/C/F/M (real delays order events, decisions are on stub stamps), N (100 ms `DisposeLockTimeout`), `UdsClientTests.Client_Times_Out_When_Ecu_Silent_Within_P2` (150 ms, silent ECU), `TesterPresent_KeepAlive_Fires_Periodically` (80 ms period, ≥3 within 10 s), `A_Busy_Repeat_With_A_Delay_Repeats_On_The_Wall_Clock` (no timing assertion), `UdsFunctionalClientTests:453, :799, :850` (30/100 ms wall-clock cancellations of calls parked on a virtual window), `:1060` (`Task.Delay(50)` negative check, acknowledged), the 200 ms `DisposeLockTimeout` tests (`UdsClientTests.cs:2363, :2387`).
- Gated by construction rather than by time: `TimedOut_Request_Does_Not_Poison_Next_Same_Service_Transaction` (actor hold), `SecurityAccess_Holds_Lock_Across_Seed_And_Key` and `DownloadAsync_Holds_Exclusive_Lock_Against_Concurrent_TesterPresent` (contention events), `P2_Ends_With_The_First_Frame_Of_A_MultiFrame_Response` (STmin 127 ms × 12 real inside a 10 s bound, N_Cr on the virtual clock).

## 5. Mutations proposed

| # | Edit (exact) | Expectation |
|---|---|---|
| M1 | `UdsClientImpl.cs:173`: `if (response.Length < 2 \|\| response[1] != sub)` → `if (response.Length < 2)` | **green = gap** (U3 still open; re-check of the previous uds-M5) |
| M2 | `UdsClientImpl.cs:824`: `if (response[1] != blockSequenceCounter)` → `if (false)` | **green = gap** (BSC echo untested; U3) |
| M3 | `UdsClientImpl.cs:1211`: `if (pendingCount > _options.MaxResponsePendingCount)` → `>=` | **green = gap** (`ResponsePending_Loop_Aborts_When_Exceeding_MaxResponsePendingCount` asserts only the exception and message, not the count) |
| M4 | `UdsClientImpl.cs:963`: `unchecked { bsc++; }` → `bsc = (byte)(bsc == 0xFF ? 0x01 : bsc + 1);` | **red**: `UdsTransferTests.DownloadAsync_Multi_Block_Uses_BSC_Wrap` (`seenBsc[0xFF] == 0x00`) |
| M5 | `UdsClientImpl.cs:1082`: `repeats < _options.MaxBusyRepeatRequests` → `repeats <= _options.MaxBusyRepeatRequests` | **red**: `UdsClientTests.BusyRepeatRequest_Is_Surfaced_Once_The_Repeats_Are_Used_Up` (`RequestsHandled == 2`) |
| M6 | `UdsFunctionalClient.cs:294`: `if (IsResponsePending(data, sid) && r.HostArrivalTimestamp >= cutoff)` → `if (IsResponsePending(data, sid))` | **red**: `UdsFunctionalClientTests.A_Pending_Answer_From_Before_The_Handoff_Is_Not_This_Requests(100)` |

## 6. Doc drift

- `README.md:81-82` "Stray or mismatched responses … silently discarded; the wait continues" — a mismatched echo throws `UdsProtocolException` (U-1).
- `README.md:121-123` "Dispose waits up to five seconds" — up to ten with a keep-alive running (`:1685` + `:1527`); `DisposeAsync` (PR #271) is not mentioned anywhere in the README.
- `README.md:65-72` quick start throws unless `firmwareChunk.Length == 512` (U3).
- `README.md:127-129` / `UdsException.cs:12-13`: transport failures "re-thrown as `IsoTpException` subclasses" — a channel disposed mid-request surfaces `InvalidOperationException` (§3).
- `IUdsClient.cs:16-17` seven services vs eleven; `:142, 147-148` `requestSeedLevel` "0x01..0x7F" vs code 0x7D; `:142-145` only a *zero-length* seed short-circuits, while `UdsClientImpl.cs:398-399` and `README.md:124-126` treat any all-zero seed so.
- `IUdsClient.cs:36-37` Dispose contract says nothing about the call-site restriction that `UdsClientExtensions.cs:15-24` describes (Q9 residual).
- `UdsClientOptions.cs:82-86` NRC 0x21 "repeat, not wait" (§3).
- `UdsClientImpl.cs:1663-1665` keep-alive comment names an observability path that does not exist for UDS-level failures (U-2).
- `SuppressedResponseWindows.cs:48-53` doubled `<summary>`, `:72-73` stray `///.`.
- `SimulatedUdsEcu.cs:287-289` the `EcuResponsePendingThenSilent` summary sits above `EcuResponsePendingThenNegative`.
- `IsoTpReceivedPdu.cs:16-20` and friends say "Stopwatch.GetTimestamp" while an injected clock may be the source; the UDS client's own comments (`UdsClientImpl.cs:57-62`) are already correct about this.

## 7. What is done well

- The P2/P2\* budget is stamp arithmetic on both ends (last-frame transmit → first-frame arrival, `UdsClientImpl.cs:1115-1182`), with the handoff cutoff for strays and the in-progress reception rule for multi-frame answers; every piece is pinned by a stub-channel test that cannot be perturbed by the host.
- The suppressed-response windows (`:46-49`, `:494-657`, `SuppressedResponseWindows.cs`) solve a problem most UDS clients ignore, per service, with 0x78 routing from every path that reads the inbox, and the functional client mirrors it with a standing listener per service.
- The disposal after PR #271: one flag, cancellation before the lock, bounded joins, the semaphore left intact for a holder that outlasts the wait, `_disposeDone` for a concurrent second call.
- Request-echo correlation in the functional client (`UdsFunctionalClient.cs:571-605`) is the right model and the table is correct against ISO 14229-1's response layouts (checked from memory for every listed SID, including the optional DDDI of `2C 03` via the `Math.Min`).
- The test infrastructure: `ClockPair`, `EcuSteps`/`OrderedGates`, `RequestLockContended`/`RequestLockAcquired` as observables instead of sleeps.

## 8. Open questions for the maintainer

1. **U2**: apply the session's P2Server_max / P2\*Server_max automatically (the client re-derives its budgets on a successful 0x10, with a documented margin), or only ship a decoder plus a documented way to re-create the client per session? The former changes behaviour for every caller who set `P2ClientMax` deliberately; the latter leaves U-1 reachable by default.
2. **U-1**: document the stale-answer limitation, or add an opt-in quarantine window after a timeout/cancellation (P2\* from the abort, per service) that the next same-service request waits out as suppressed sends already do?
3. The lock is held across `computeKey` (`UdsClientImpl.cs:379-382, 403`) to keep the keep-alive out of the seed/key pair. From memory ISO 14229-1 §9.4 invalidates a seed on a new requestSeed, a session change or a timeout, not on a TesterPresent in between; if that holds, releasing the lock during a slow (HSM-backed) `computeKey` would protect S3 instead of starving it. Worth checking against the text before changing.
4. **U-7**: should `7F sid 78` entries stay in the functional result list?
5. `DownloadAsync` has no recovery on NRC 0x73 / a transport fault for one block (re-sending the same BSC is allowed by ISO 14229-1 §14.3 from memory). Intentional scope limit? If so, the XML doc of `DownloadAsync` could say so.
