# CanKit.Pro – Deep Review (whole repository)

**Date:** 2026-10-21 · **State:** `main` @ `36c97ca` (after PR #336) ·
**Scope:** 9 packages, ~40,500 lines of library code, ~64,900 lines of tests, CI/release pipeline,
engineering scripts, documentation, samples, tools.

> This is the third review of **CanKit.Pro itself**. The predecessor is
> `2026-09-30-deep-review.md` (state `8cec2ee`, v1.3.0 released); its finding IDs (K1, Q7–Q9, A1, R1,
> I6–I8, U1–U3, J9–J12, T1–T3, C9–C16, B11–B14 and the §3 lists) are reused here. Between the two
> states lie 56 merged pull requests and 508 commits (`git log --first-parent 8cec2ee..36c97ca`),
> adding 12,100 lines to `src/` and 23,200 lines to `tests/` — CANopen Safety (CiA 304, CiA 319 tool
> and device side), LSS master/slave/commissioning and CiA 801 bit-rate detection, PDO SYNC start and
> monitoring, `DisposeAsync` in three packages, ISO-TP FD padding, SBOM release assets.
>
> **How the review was made.** Eleven parallel sub-reviews each read one package (CANopen in four
> slices) or the build/CI/docs area completely — source and tests, not grep — and recorded today's
> status of every previous finding with evidence. Their full reports, with every file:line, are
> kept beside this document in [`2026-10-21-deep-review/`](2026-10-21-deep-review/) so the next
> review can check them instead of reconstructing them (this one could not reconstruct one of the
> previous review's §3 notes, see Appendix A). A coordinating thread ran the gate, read every
> Important finding at its cited lines, verified the two premises that rest on something outside
> this repository (a five-line `SemaphoreSlim` repro for I-1; a grep of `pkuyo/CanKit` @ `v0.5.6`
> for L-1), and ran the mutations the sub-reviews proposed (section 4.2). Standard citations
> (ISO 15765-2, ISO 14229-1, SAE J1939-21/-81, CiA 301/302/304/305/319/801) are from memory
> throughout and say so; the standard texts were not available.

---

## Overall assessment

The repository closed almost the whole finding set of the previous review within three weeks, and
closed it the right way: every fix that was re-mutated in this review is still detected by the test
that was written for it (section 4.2). Appendix A has the line-by-line status. What is left open
from 2026-09-30 is of two kinds — decisions the maintainer took and documented (C14, C15, the
`Transmit`-under-lock design), and minor items that were never turned into tickets and therefore
never worked (Q8, R1, J9, J10, J12, T3, U2, U3, the eight ISO-TP norm questions). This review
tickets all of them (Appendix B), so the next one can count instead of re-finding.

**Verified in this environment (macOS, .NET SDK 10.0.401):**

| Step | Result |
| --- | --- |
| `dotnet build -c Release -p:CI=true` (netstandard2.0 + net10.0, warnings = errors) | success, 0 warnings |
| `dotnet test --framework net10.0` | 2423 / 2423 passed, 3 min 54 s |
| `dotnet format --verify-no-changes` | no changes |
| `dotnet pack` + `eng/verify-packages.py` into a fresh directory | 9 nupkg + 9 snupkg, all 9 "ok" |
| `eng/verify-requirements-traceability.py` | 142 requirements, 123 Must, 107 traced, 5 waived, 11 recorded gaps, 0 new |
| `eng/verify-release-config.mjs`, `eng/next-release-version.mjs` | pass; next release resolves to 1.4.0 (26 feat, 199 fix, no breaking commit) |
| CI, Docs, CodeQL on `main` @ `36c97ca` | green (read via the API) |
| Action SHA pins vs version comments (13), cited requirement ids vs SRS/ADR (129 distinct) | all match |

**No critical finding.** The eight Important findings share one shape, which is the lesson of this
review: *a fix landed on the path the finding named and not on its mirror.* The block-download
client got its abort token (C9, PR #259); the block-upload server did not (C-1). The BAM sender got
session identity (T1, PR #258); the CM sender did not (T-1). `ConfigureMpdo` guards the optional
communication sub-indices with `TryGet`; `ConfigureTpdo` does not (P-1). The reader's cancellation
got a filter for the channel's own token in one place and not in the other (I-2). None of these is a
regression; each is the sibling of a closed finding, found by asking "where else does this pattern
occur" — a question worth adding to the fix routine.

**The new code is good.** CANopen Safety is the most carefully specified feature in the repository:
every FR-CO-035..046/051/058/059 row has a test that asserts the normative behaviour, with two
exceptions that are now tickets (S-3, S-4), and the one Important code finding there (S-2) is a
pre-check that stops one frame too late. The LSS stack's reservation model (every wait anchored to
the transmit stamp, abandoned windows reserved for the next command) is sound; its one Important
finding (L-1) is an upstream adapter that advertises a capability it does not apply. The PDO work in
#313 handles the hard cases (SYNC counter commit after confirmation, window re-check at the driver
handoff) with ordering witnesses rather than sleeps.

**The test suite** has grown from 1385 to 2423 tests and kept its discipline: timers on a virtual
clock with exact arming, races forced on the actor instead of sampled, regression tests that cite
the cause. The remaining wall-clock exposure is inventoried in #362; its two largest rows are the
`CanOpenNode` LSS slave (no `VirtualClock` seam yet, the step PR #316 named as out of scope) and
the real-clock CM round trips in `J1939TpTests` (#279 lifted the timers for the BAM round trips
only). Two sub-review predictions of "this test goes red" were wrong and are recorded as such in
section 4.2; the claim-needs-a-mutation rule in `CLAUDE.md` earned its keep twice today.

**One sentence of recommendation:** dry-run and cut 1.4.0 (#346) before the 225 releasing commits
drift further from anyone's memory; then close the four mirror-path findings (#341, #350, #342,
#339) and the two Safety gaps (#343, #344), each a small change with one new negative test.

---

## 1. Critical findings

None.

## 2. Important findings

Each entry names the issue that carries it. Mechanism, reachability, confidence and the proposed
fix are in the issue and in the sub-report; here is what the maintainer needs to rank them.

### 2.1 Cross-cutting

**A-1 · A second `ProtocolActor.Dispose()` returns before the loop has stopped** — #338.
`ProtocolActor.cs:485-488` returns at the idempotency guard for every caller but the first; the
join belongs to the first caller only. After a reentrant on-loop `Dispose`, the owner's `using`
block returns while the callback is still running and `FinalDrain` has not run. The README promises
the wait without qualification. Every in-repo layer shields itself with its own guard; the published
package does not. Ten-line fix, one new test. Confidence: high (mechanism), medium (impact).

**I-1 · `IsoTpChannel.Dispose` disposes the send gate after a 2 s wait; a queued `SendAsync` then
hangs for ever** — #339. `IsoTpChannel.cs:669-676`. `SemaphoreSlim.Dispose` does not complete
pending `WaitAsync` waiters (verified by repro on .NET 10). The 2 s branch is reached whenever
`FailInFlightSend` cannot run in time — most plausibly a `Dispose` from an actor-side
`BackgroundExceptionOccurred` handler, which also stalls the actor loop for the 2 s (I-6). Fix: stop
disposing the gate. Confidence: medium-high.

### 2.2 UDS

**U-1 · A late answer to the previous same-service request is taken as this request's; session
P2Server/P2\*Server are not applied (U2)** — #340. `UdsClientImpl.cs:1175-1240`: a request that
timed out leaves no window for its service; its late `7F xx 78` restarts the next request's P2\*
and its late positive response is returned as the next request's answer (same DID: stale data,
silently; different DID: `UdsProtocolException` aborts the new request). Reachable with a server
later than the client's P2 — which the unapplied session timing (U2, carried over, never ticketed)
leaves as the default situation. Confidence: medium (mechanism certain, frequency depends on U2).

### 2.3 J1939

**J9 (carried over) · The multi-frame fallback channel sends a whole TP session under an address
already lost** — #347. Unchanged since 2026-09-30, never ticketed, still no test (mutation
j1939-N1 green). One-line fix. The companion J10 (Dispose vs `RebindTransportOnLoop` leaks a
channel and its actor thread) is in the same issue.

### 2.4 CANopen

**C-1 · The block-upload server keeps streaming a sub-block after its session ended** — #341.
`CanOpenNode.SdoBlock.cs:1114` hands up to 127 frames to `SendOrderedControlFrames` with no
`shouldStop`; a client abort, the idle timeout, a supersede, NMT Stop and disposal all leave the
batch running, and a conforming client that restarts fast gets the old segments interleaved with
its next transfer. The server-side mirror of C9. Confidence: high. Three neighbours in the same
function (C-3 empty object, C-9 duplicate `0xA3`, C-6 abort overtaking the last segment) ride along.

**P-1 · `ConfigureTpdo` on a node opened from a description whose TPDO record omits 03h/05h throws
`KeyNotFoundException` after destroying the PDO** — #342. `CanOpenNode.Pdo.cs:142-146` writes the
optional sub-indices unconditionally; the loader (`DeviceDescription.cs:697-703`) removed them;
the transaction fails after step 1. The fixture that reproduces it (`quirky.eds` `[1801]`) is in the
tree; no test calls `ConfigureTpdo` on it. Confidence: high.

**S-2 · `ConfigurePeerSafetyAsync` refuses a peer with more SRDOs than the bound file implies
mid-transaction, after deleting the peer's SRDOs** — #343. The pre-check covers the SRDOs named,
the delete-first pass walks `1..peerCount`, the peer-SDO gate refuses the first record above the
file's highest; the README's "nothing sent, not mid-transaction" guarantee is false for that case.
Safe outcome (13FEh = 0) holds; the contract does not. Confidence: high.

**S-3 · No test holds the consumer half of FR-CO-038** (a consumer with an invalid configuration
must ignore a correct pair) — #344, together with S-4 (the A5h readback mismatch path, the only way
a CRC disagreement reaches the tool, is untested). Both gaps measured: 403 of 403 Safety tests pass
with either check removed (section 4.2). A safety property without a test is rated Important here
even though the code is right.

**S-1 · 13FFh checksum byte order** — a comment on #289, not a new issue: the fields are fed
MSB-first; CANopenNode's `CO_SRDOcalcCRC` (from memory) feeds the CANopen little-endian image in the
same order with the same primitive. If EN 50325-5 prescribes the latter, no conforming peer ever
acknowledges and no conforming tool's checksum ever matches. One reference vector settles it; the
layout exists twice (S-6, #360) and should become one serializer before it changes.

**L-1 · CiA 801 bit-rate detection runs in normal mode on Kvaser** — #345. CanKit 0.5.6's Kvaser
adapter advertises `CanFeature.ListenOnly` (`KvaserProvider.cs:82-83`) and never calls
`canSetBusOutputControl(…, canDRIVER_SILENT)` (grep over the adapter; PCAN does apply its parameter).
`RequireListenOnly` checks the *requested* mode, so the guarantee "transmits nothing" is false on the
bench adapter the echo measurements were made with, and the sample README lists Kvaser as needing
no special handling. Upstream fix plus a doc correction here. Confidence: high (adapter), medium
(CANlib default).

### 2.5 Build, CI, release

**B-1 · The post-#314 release chain has never run; 1.4.0 is waiting on it** — #346. `release.yml`
and `.releaserc.json` gained the SBOM hand-over, `verify-sboms.py` in `prepareCmd`, the asset
upload and the `attest` job on 2026-10-20; the last release run is from 2026-09-30. A failure in the
asset upload would land after `dotnet nuget push`. Dry run first — it covers `verify` and the
artifact download but not `prepareCmd`, which semantic-release skips in dry-run mode, so the SBOM
hand-over check belongs in `verifyReleaseCmd` if it is to be rehearsed — then cut.

## 3. Minor findings

The complete lists, with file:line and a proposed fix each, are in the sub-reports and in one issue
per package (Appendix B). The items with a behavioural consequence, in one line each:

**Actor / Reliability** (#351) — `ClockTimeout` is orphaned by actor disposal and nothing says the
caller must link a lifetime token; the dispose-timeout report is raised off-loop and the contract is
silent; `WaitForLoopTask` has a dead `catch` with a false comment; `BusStateMonitor.PostRecheck`
latches its gate on a non-ODE `Post` failure (foreign actor only); the three untested
`BusStateMonitor` paths of the previous review are still untested (actor-M4/M5 green).

**RawCan / Addressing** (#352, #348, #349) — the package README still describes echo semantics the
measurements disproved and says nothing ran on hardware; `Dispose` cannot cancel an approximated
in-flight send; `TxConfirmation` stamp docs are path-dependent and partly false; a timeout above
`int.MaxValue` ms throws after the frame is on the wire; the approximated path rebuilds the frame and
rejects what the echo path transmits (RTR with data, XL); `default(CanIdFilter)` matches ID 0;
`BusOff` unreachable and a poll-loop fault invisible to subscribers (Q8/R-9, #348); late echo
cross-match (R1, #349); the Q7 RawCan half (non-canonical FD lengths) is open by decision in PR #273
and unticketed until now.

**ISO-TP** (#353) — a foreign service ending by `OperationCanceledException` re-enters the I6 hang;
`TryReceiveWithArrival` can report empty during a discard; `CollectResponsesAsync` lacks the window
check its sibling has; the eight norm questions from 2026-09-30 §3 (N_Ar, N_Cr start, reserved FS,
FF_DL > `int.MaxValue`, `EncodeStMin` truncating in the sub-ms band, TX_DL/BRS, Mixed/NormalFixed
untested, mixed section numbers) are all still open; NFR-003 in the SRS still claims the measurement
#246 removed from the README.

**UDS** (#354) — keep-alive failures are swallowed and the comment points at an event that never
carries them; `SettleAsync` is awaited without the request's token everywhere; `UploadAsync` lacks
the `memorySize` width guard `DownloadAsync` got in #182 (U-4); no bound on 0x78 extensions of a
suppressed window; the README quick start still throws (U3); the test double answers suppressed
non-0x3E requests, so every `11 81` test runs against a non-conformant ECU.

**J1939** (#355) — the application-frame ledger never expires on a non-echo bus and keeps rejected
sends (a peer on the vacated address loses identical frames once each); a directed Request for
Address Claimed is unanswered while Claiming; `Dispose` during an automatic re-claim reports
`ObjectDisposedException` as a background fault; the arbitrary scan re-claims the lost address
(121 rounds, "240" in two comments); `ReceiveBufferCapacity` still has no consumer (J12); four
previous mutation gaps (overlap ban, tick coalescing, directed request, AAC bit) remain gaps.

**J1939-TP** (#356, #350) — `BackgroundExceptionOccurred` raised on the loop and, in `AbortRx`,
before the inbox fault is written; `ApplyPendingCts` does not clear `HoldPending`; the mid-block
hold test cannot tell "ignored" from "remembered"; SN 0 untested; `ArmT2` after a follow-up CTS
still untested (j1939tp-M1 green, a conforming slow originator is aborted 500 ms early without it);
`ReceiveAllAsync` ends on the first abort while its doc says "same semantics"; an abort can overtake
its own unconfirmed RTS; send to own SA accepted.

**CANopen core / SDO** (#357) — unknown specifiers abort an open classic session with a bogus
multiplexer; `e = 1, s = 0` refused for 1..3-byte objects (no test sends it, canopen-M3 green);
block client treats `s = 1, size 0` as unbounded; non-alternating guarding toggle dropped silently
(canopen-M6 green); `RespondToNodeGuardingRtr = false` also disables life guarding; constructor
leaks the actor on a throwing description; short EMCY dropped without trace; SDO client not gated
in Stopped; client-side toggle checks untested (canopen-M2 green, the previous canopenB-M5);
`ProducerNodeId` wrong for a moved 1014h; README layout/dependencies/not-built drift.

**CANopen PDO / device description** (#358) — `TriggerTpdoAsync` transmits synchronous and RTR-only
TPDOs immediately (interface says so, README says the opposite, nothing tests either way, pdo-M5
green); the per-slot length check counts stale slots; `HandleMpdo` writes unguarded; `ConfigureTpdo`
leaves `1800h:06`/`1400h:05` as a master set them; the loader keeps undeclared mapping slots
(pdo-M2 green); XML docs still say four PDOs and byte widths.

**CANopen LSS / flying master** (#359) — the LSS master subscribes without `includeEcho` and cannot
see a local slave on an echo-flagging adapter (lss-M1 green); a second activate during the silence
opens a transmit window (lss-M2 green); the commissioning cleanup's 2 s token can skip the release
or poison the master against a reservation of up to 131 s; three CiA 305 wording questions
(first-period waiting frame, Fastscan completion rule, silence on replacement); the `Bench` family
and six commissioning tests still run on 300 ms / 120 ms wall-clock windows (#315's mechanism).

**CANopen Safety** (#360) — step-D probing rebuilds a dictionary per assigned slave per boot event
on the actor; the 13FFh layout exists twice; a lone inverted frame before the first pair raises a
spurious `OutOfOrder`; three docs still say "tool side only" / "device side not implemented";
`docs/packages/canopen.md` does not mention safety at all.

**Build / CI / docs** (#361) — the ruleset bypass is *Maintain*, docs say "repo-owner";
`cancel-in-progress` also cancels `main` runs; GitVersion output interpolated into `run:`; no
`timeout-minutes` anywhere; CONTRIBUTING claims releases on merge and a weaker gate than CI; nine
checkouts persist the token; a Copilot autofix commit changed `src/` without a conventional subject;
inert review flags in the ruleset; stale pin comment, incomplete third-party table, SBOM versions
not cross-checked, Dependabot group spelling; `tools.zip` (94 MB of build output, a stale snapshot
of `tools/` at `57141d2`) is untracked in the root — delete it; `docs/superpowers/plans/` is
published to the website.

## 4. Test quality

### 4.1 Overall

The suite's shape is the one the previous review asked for, extended to the new code: `VirtualClock`
with `WaitUntilTimerArmedAsync` for every timer-is-the-subject test in LSS, Safety, PDO timing,
J1939-TP and UDS; `ManualTimeSource` through internal constructors; ordering witnesses (a later
frame on the same path, a contention event, a held server) instead of sleeps for negatives; raw-frame
peers that assert on the wire. The new infrastructure — `ScriptedLssSlave`, `DetectionProbeBus`,
`FakeExpeditedServer`, `ClockedLssNodeRig`, the `DisposeOnFirstPostActor` double — is documented as
seams and used as such.

Three things to keep an eye on:

1. **Mirror coverage.** Where a fix got a test, its sibling path often has none: the block-upload
   server (C-1), the CM sender (T-1, j1939tp-M2 green), `ConfigureTpdo` on a quirky file (P-1),
   the consumer half of FR-CO-038 (S-3). The mutation table below shows each.
2. **Tests that cannot tell two behaviours apart.** `A_Cts_Hold_In_The_Middle_Of_A_Block_Is_Ignored`
   (block = whole message), `A_Second_Activate_During_The_Silence_Drops_The_First` (never reaches
   the silence), `AssertEqualNameContest` (accepts both outcomes), the two flying-master gap tests
   that end in `await Task.Delay(200)` with no assertion.
3. **Wall-clock margins.** #362 lists the 19 rows that can go red on a loaded runner; the previous
   review's rule (name the perturbable quantity, check the margin against it) was applied per test
   in every sub-report §4.

### 4.2 Mutation checks

Every "test X catches Y" or "Y is untested" claim from the sub-reviews was measured by a one-line
mutation in a detached worktree at `36c97ca`: apply, rebuild the test project, run the named filter,
restore (`git checkout -- .`, tree verified clean after each). "red" means the filter failed; "green"
means nothing in the filter detected the mutation. A mutation whose target string was not unique was
re-run with more context; a mutation whose filter was narrower than the test the sub-review named
was re-run with the package filter. The table lists the final run per mutation; the result files
and per-run logs were not kept in the repository.

<!-- MUTATION-TABLE-START -->
| Mutation | Edit | Prediction | Measured |
| --- | --- | --- | --- |
| actor-M1 | #240 `TryPeek` hold-back deleted (`ProtocolActor.cs:734-735`) | red | red (1 of 12 failed) |
| actor-M2 | `TickMath.DueAt` back to the double path (`TickMath.cs:31`) | red | red (5 of 22 failed) |
| actor-M4 | `FaultOccurred` hint removed (`BusStateMonitor.cs:218`) | green (gap, ex reliability-M3) | green (0 of 21 failed) |
| actor-M5 | handler detach in `Dispose` removed (`:201-208`) | green (gap, ex reliability-M5) | green (0 of 21 failed) |
| rawcan-M1 | Bus-Off guard removed (`CanBusService.cs:685`) | green (gap Q8, #348) | green (0 of 68 failed) |
| rawcan-M2 | `_pendingDisposed` check in the gate removed (`:473-474`) | green (gap, #352) | green (0 of 68 failed) |
| rawcan-M3 | in-gate token check removed (`:490-494`) | red (pins #301) | red (1 of 27 failed) |
| rawcan-M4 | echo filter per subscription removed (`Subscription.cs:166`) | red | red (5 of 39 failed) |
| rawcan-M6 | PDU1 PS kept in the PGN (`J1939Fields.cs:60`) | red | red (2 of 24 failed) |
| isotp-M1 | WFT reset on CTS removed (`IsoTpChannel.cs:1595`) | green (gap, #353) | green (0 of 248 failed) |
| isotp-M2 | N_Bs re-arm on Wait removed (`:1609`) | green (gap, #353) | green (0 of 248 failed) |
| isotp-M3 | FD unpadded rounding reverted (`IsoTpFrameCodec.cs:129-131`) | red (pins #273; was green on 8cec2ee) | red (4 of 111 failed) |
| isotp-M4 | FC address-extension byte from `RxAddressExtension` (`:434`) | green (gap: no Extended multi-frame test, #353) | green (0 of 248 failed) |
| isotp-M5 | `BeginSendOnLoop` disposed guard removed (`:906-910`) | red (pins I7) | red (1 of 95 failed) |
| uds-M1 | sub-function echo check removed (`UdsClientImpl.cs:173`) | green (gap U3, #354; ex uds-M5) | green (0 of 150 failed) |
| uds-M2 | BSC echo check removed (`:824`) | green (gap U3, #354) | green (0 of 150 failed) |
| uds-M3 | `MaxResponsePendingCount` `>` → `>=` (`:1211`) | green (gap, #354) | green (0 of 150 failed) |
| uds-M4 | BSC skips 0x00 after 0xFF (`:963`) | red | red (1 of 15 failed) |
| uds-M6 | functional 0x78 handoff cutoff removed (`UdsFunctionalClient.cs:294`) | **green — sub-review predicted red**; the listener's own anchoring covers the frame, the cutoff is not pinned by any UDS test | green (0 of 150 failed) |
| j1939-N1 | fallback channel replaced by a throw (`J1939NodeImpl.cs:1250-1251`) | green (gap J9, #347; ex j1939-M3) | green (0 of 116 failed) |
| j1939-N2 | periodic overlap ban removed (`:1922`) | green (gap, #355; ex j1939-M1) | green (0 of 116 failed) |
| j1939-N3 | Request for Address Claimed answered only when global (`:1635`) | green (gap J-2, #355; ex j1939-M4) | green (0 of 116 failed) |
| j1939-N4 | NAME AAC bit ignored (`:579`) | green (gap, #355; ex j1939-M5) | green (0 of 116 failed) |
| j1939-N5 | `FlaggedOnly` marker rule removed (`:1058`) | red (pins #287) | red (1 of 116 failed) |
| j1939-N6 | `ReleaseClaimEchoGrace` on fire removed (`:1129`) | red (pins J11) | red (1 of 116 failed) |
| j1939tp-M1 | `ArmT2` after a follow-up CTS removed (`J1939TpChannel.cs:784`) | green (gap, #356; ex j1939tp-M3) | green (0 of 112 failed) |
| j1939tp-M2 | stale CM confirmation check removed (`:1162`) | green (gap T-1, #350) | green (0 of 112 failed) |
| j1939tp-M3 | retransmit frontier `<=` → `<` (`:1016`) | red | red (4 of 112 failed) |
| j1939tp-M4 | `AllPacketsSent` end rule removed (`:1083`) | red | red (1 of 112 failed) |
| j1939tp-M5 | mid-block hold remembered for every block (`:993`) | green (gap T-4, #356) | green (0 of 112 failed) |
| j1939tp-M6 | SN 0 special case removed (`:727`) | green (gap T-5, #356) | green (0 of 112 failed) |
| canopen-M1 | block-download `shouldStop` disabled (`CanOpenNode.SdoBlock.cs:600`) | red (pins C9 / #259) | red (2 of 26 failed) |
| canopen-M2 | client-side upload toggle check removed (`CanOpenNode.cs:2467`) | green (gap C-13, #357; ex canopenB-M5) | green (0 of 1418 failed) |
| canopen-M3 | `e = 1, s = 0` taken as empty payload (`:1976`) | green (gap C-4, #357) | green (0 of 1418 failed) |
| canopen-M4 | inline cleanup on the actor removed (`:886`) | red (pins Q9 / #269) | red (2 of 26 failed) |
| canopen-M6 | repeated guarding toggle accepted (`NodeGuarding.cs:221-222`) | green (gap C-7, #357) | green (0 of 1418 failed) |
| pdo-M1 | once-per-run latch for 8210h removed (`CanOpenNode.Pdo.cs:640`) | **green — sub-review predicted red**; see the note below | green (0 of 1418 failed) |
| pdo-M2 | undeclared comm sub-indices kept by the loader (`DeviceDescription.cs:701`) | green (gap, #342) | green (0 of 1418 failed) |
| pdo-M5 | `TriggerTpdoAsync` ignores 01h–F0h/FCh/FDh (`Pdo.cs:564`) | green (gap P-3, #358) | green (0 of 1418 failed) |
| pdo-M6 | `ConfigureMpdo` `TryGet` guard removed (`Mpdo.cs:44`) | red (the guard `ConfigureTpdo` lacks, P-1) | red (1 of 177 failed) |
| lss-M1 | master subscribes with `includeEcho: true` (`LssMaster.cs:96-101`) | green (gap L-2, #359) | green (0 of 339 failed) |
| lss-M2 | second activate no longer lifts the hold (`CanOpenNode.Lss.cs:429`) | green (gap L-3, #359) | green (0 of 339 failed) |
| lss-M4 | configured slaves answer Fastscan (`:842`) | red (`LssCommissioningTests.Assign_Gives_The_Next_Node_Id_And_Stops_When_The_Slave_Rejects_The_Store`; the `LssSlaveTests` filter alone stayed green) | red (1 of 339 failed) |
| lss-M6 | Fastscan bit-0 re-confirmation skipped (`LssMaster.Fastscan.cs:76`) | red | red (5 of 36 failed) |
| safety-M1 | consumer takes a pair with an invalid configuration (`SrdoEngine.Consumer.cs:30`) | green (gap S-3, #344) | green (0 of 403 failed) |
| safety-M2 | A5h readback mismatch ignored (`CanOpenNode.PeerSafety.cs:105`) | green (gap S-4, #344) | green (0 of 403 failed) |
| safety-M3 | short-pair bound `<` → `<=` (`SrdoEngine.Consumer.cs:119`) | red | red (13 of 23 failed) |
| safety-M4 | `SrdoCrc.AddU32` little-endian, `SrdoRecords` unchanged (`SrdoCrc.cs:92-95`) | red (one-sided change caught by integration tests only, S-6) | red (111 of 403 failed) |
| safety-M6 | first-cycle delay 1 ms × id instead of 0.5 ms (`SrdoEngine.cs:174`) | red | red (3 of 18 failed) |
| build-M5 | `CanKit.Pro.Uds` removed from `PublicApiSurfaceTests.Tracked` | green (gap, #361) | green (0 of 8 failed) |
| build-M1 | `.releaserc.json` subject glob without `repo-tooling` | red (`verify-release-config.mjs`: two probes would publish a patch) | red |
| build-M2 | new `Must` row FR-RAW-099 in the SRS | red (traceability: 1 new untraced Must) | red |
| build-M6 | `EXPECTED_PACKAGE_COUNT = 8` | red (`verify-packages.py` exit 1) | red |
<!-- MUTATION-TABLE-END -->

<!-- MUTATION-SUMMARY-START -->
Tally: 53 mutations measured (23 red, 30 green). 51 results match the sub-review's prediction; 2 do not (uds-M6, pdo-M1), both in the direction "predicted red, measured green" — a claimed detector that does not detect. Four mutations needed a second run with more context because their target line was not unique; two (pdo-M1, lss-M4) were first run with a filter narrower than the test the sub-review named and re-run with the package filter; the table shows the final run. Every mutation at a fix site of a 2026-09-30 finding (actor-M1/M2, rawcan-M3, isotp-M3/M5, j1939-N5/N6, j1939tp-M3/M4, canopen-M1/M4) was detected; the other red rows pin claims this review's sub-reports make about the new code. The green rows are the coverage gaps of section 4.3, each carried by the issue named in the row.

**Note on pdo-M1.** With the once-per-run latch removed, `Rpdo_Shorter_Than_Its_Mapping_Raises_Emcy_Once_Per_Run_Of_Short_Frames` still sees two EMCYs for its four frames (short, short, fits, short). The test takes one arrival from its blocking collection after the first frame and one after the fourth, then asserts the count *at that instant*: the second take returns the EMCY the second frame produced, the count is two, and the fourth frame's EMCY is still in flight. The test therefore cannot tell "the second short frame raised nothing" from "the fourth frame's EMCY has not arrived yet", and passes with the latch removed by timing. `EmitEmcy` (`CanOpenNode.cs:542`) does no folding of identical codes, so the latch is the only debounce and is unpinned. Fix the test by waiting for the fourth frame's EMCY (an ordering witness) before asserting the count; added to #358.
<!-- MUTATION-SUMMARY-END -->

### 4.3 Missing negative tests (ranked by value)

1. S-3 / S-4: consumer with an invalid configuration takes no pair; A5h readback mismatch (#344).
2. C-1: server abort mid-sub-block, then count what else appears on `0x580 + id`; empty-object
   block upload; duplicate `0xA3` after a partial ack (#341).
3. P-1: `ConfigureTpdo` on `quirky.eds` (#342); S-2: peer count above the file's records (#343).
4. I-1: two queued senders plus `Dispose` with a held actor (#339); A-1: second `Dispose` while a
   callback blocks (#338).
5. U-1: `7F 22 78` + `62 F1 90 old` delivered after the second request's handoff (#340); U3: positive
   responses with wrong sub-function / DID / BSC echo (uds-M1/M2 green, #354).
6. J9 forced interleaving (#347); directed Request for Address Claimed; NAME AAC bit; periodic
   overlap and coalescing (#355).
7. T-1 stale CM confirmation with `DeferredEchoes` (#350); `ArmT2` after a follow-up CTS; SN 0;
   a real mid-block hold (#356).
8. CANopen: `e = 1, s = 0`; repeated guarding toggle; client-side toggle; `TriggerTpdoAsync` on
   01h–F0h/FCh/FDh; undeclared mapping slots (#357, #358).
9. LSS: master and slave on one echo-flagging host service; second activate inside the silence;
   cleanup against a long reservation; cancelled bus-level scan (#359).
10. RawCan: fault with `BusState != BusOff` leaves sends pending; register-after-sweep race; stamps
    on both paths; `FaultOccurred` hint and handler detach in `BusStateMonitor` (#348, #352, #351).

## 5. What is done well

1. **The previous findings were worked, not managed.** Appendix A: of 31 numbered findings, 19 are
   fixed on `main` with a named test, 4 were fixed in part or decided and documented, and every fix that was
   re-mutated here is still pinned (actor-M1, rawcan-M3, isotp-M3/M5, j1939-N5/N6, j1939tp-M3/M4,
   canopen-M1/M4, pdo-M6).
2. **CANopen Safety as a specification exercise.** The scope document records the maintainer's
   decisions, the SRS rows name their tests, the same loader/validator/checksum code serves device,
   tool and step-D expectation, and the 19-row agreement theory makes "one rule set" measurable.
   The PR descriptions carry their own mutation lists.
3. **LSS timing anchored to transmit stamps**, abandoned windows reserved, indeterminate sends
   poisoning the master rather than guessing — and each rule has a one-test class that counts
   frames on `LssTestService.Commands`.
4. **PDO engine (#313)**: SYNC counter committed after confirmation, window re-check at the driver
   handoff, inhibited events surviving a rebuild, deadline retired on every exit path, each with a
   virtual-clock test and an ordering witness.
5. **Disposal, three times over**: `DisposeAsync` in UDS, CANopen and J1939 with on-actor/off-actor
   split, thread markers instead of timeouts in CANopen, one completion for concurrent callers,
   failure reporting — 20 CANopen dispose tests alone.
6. **ISO-TP inbox end-state design after #275**: flag and signal beside an always-open writer,
   producers and disposal under one gate, discard write-back exempt, double-read before believing
   the end.
7. **Release chain**: OIDC only in the last job, SHA-pinned actions with matching comments, package
   validation baseline moved by the release that proves the version exists, the release config
   executable-tested on every PR, SBOMs verified in the dry-run job.
8. **EchoProbe and the measurement record**: a tool that refuses to call a silent peer a
   measurement, and a dated record (`2026-10-06-echo-hardware-measurements.md`) that the contract
   text now cites instead of restating.
9. **Honesty in the test doubles and the READMEs** about what is modelled and what never ran on
   hardware — with the one RawCan README paragraph (R-1) as the exception that proves the rule.

## 6. Recommended order

No finding of this review is a regression of an open branch; per § *Stay inside the task* all go
through issues. The order below is a priority, not a bundling.

1. **Release (B-1, #346):** dry run, then 1.4.0.
2. **Mirror-path fixes, one small PR each with a negative test:** C-1 (#341), P-1 (#342), S-2
   (#343), I-1 (#339), A-1 (#338), T-1 (#350), J9 (#347). All are under thirty lines.
3. **Safety test gaps (#344)** and the #289 reference vector; S-6 one serializer before the byte
   order changes.
4. **Before 1.5:** U-1/U2 (#340, a decision), L-1 docs + upstream candidate (#345), the LSS
   `VirtualClock` seam that retires the `Bench` rows of #362, the per-package minor issues in the
   order their severity labels say.
5. **Docs honesty:** the RawCan README (R-1), the three stale CiA 319 statements (S-8), CONTRIBUTING's
   gate and release claims (B-7), `tools.zip` out of the root.

## 7. Decisions that belong to the maintainer

1. **U2 / U-1:** apply the session's P2Server/P2\*Server automatically (behaviour change for every
   caller who set `P2ClientMax` deliberately) or ship a decoder plus a per-session re-create path?
   And after a timeout: document the stale-answer limit or add an opt-in quarantine window?
2. **S-1:** can a reference 13FFh vector be obtained before EN 50325-5 is? It decides the byte
   order and the GFC-in-Pre-Operational question together.
3. **L-1:** Kvaser silent mode — PR upstream, or document "Kvaser: detection is reactive" here?
4. **L-2:** is `OpenMaster(ICanBusService)` shared with a local `CanOpenNode` a supported setup
   (`includeEcho: true`) or does an LSS master need its own adapter?
5. **C-4, C-7, C-12, P-3, P-8:** accept `e = 1, s = 0`; indicate a non-alternating guarding toggle
   at once; gate the SDO client in Stopped; make `TriggerTpdoAsync` event-only; set 1001h bit 4 for
   PDO EMCYs — each is "norm (from memory) vs documented choice", and the maintainer has the texts.
6. **T3:** which J1939-21 revision is the reference, and does it define a Connection Abort Role byte?
7. **Q8 / R1 / R-2 / R-6 / Q7-RawCan:** implement `BusOff` via `BusState` or correct the docs; late
   echo tombstone or contract sentence; cancel approximated sends on `Dispose` or narrow the README;
   reject `default(CanIdFilter)` (SemVer); reject non-canonical FD lengths.
8. **A-1:** every `Dispose` caller joins (README semantics; recommended) or the README says "the
   first caller"? And should `FinalDrain` still fire due timers given the ODE noise at shutdown?
9. **B-2 / B-10:** bypass for *Maintain* or *Repository admin*; keep or drop the inert review flags
   (two Write collaborators exist).
10. **Norm text (the maintainer has CiA 301/305 and ISO 15765-2):** the eight ISO-TP questions in
    #353, the three CiA 305 wording questions in #359, SYNC start value for 00h/FCh in #358.
11. **Timing (#362):** move `CanOpenNode` onto `VirtualClock` now, or carry the `Bench` rows as
    known canaries?

---

## Appendix A · Status of the previous review's findings (2026-09-30, `main` @ `8cec2ee`)

Evidence (file:line, PR/commit reachable from `main`, test name) is in the sub-reports; one line per
finding here. "decided" means documented as a choice with the reasoning in code, README or PR.
Count over the 31 numbered rows: 19 fixed, 4 fixed in part or decided (K1, Q7, C14, C15), 8 open
(Q8, R1, U2, U3, J9, J10, J12, T3 — all ticketed now). The §3 lists are summarised per package below the table.

| ID | Short | Status |
| --- | --- | --- |
| K1 | Echo assumptions vs PCAN/Vector/Kvaser | J1939 half fixed (PR #287, marker lifecycle verified, third echo world in all four theories); RawCan half open by decision, #249 stays open; measurements recorded; RawCan README drift R-1 |
| Q7 | FD without padding | fixed in the codec (PR #273, isotp-M3 now red); no channel-level test; RawCan half open by decision, unticketed → #352 |
| Q8 | `BusOff` unreachable | open, unticketed → #348 (rawcan-M1 green again) |
| Q9 | `Dispose` blocks on own actor | fixed (PRs #269, #271, #272, `DisposeAsync` in all three; UDS residual documented in `UdsClientExtensions`, not in `IUdsClient` → #354) |
| A1 | Tick arithmetic | fixed both directions (PR #260, `TickMath`, swept over every ms at two frequencies; actor-M2 red) |
| R1 | Late echo cross-match | open, unticketed → #349 |
| I6 | Receive hangs on service dispose | fixed (PR #261, nine tests); residual for a foreign service ending by OCE (I-2, #353) |
| I7 | `BeginSendOnLoop` guard | fixed (PR #261; isotp-M5 red) |
| I8 | Negative timers accepted | fixed in ISO-TP (PR #261) and J1939-TP (PR #258) |
| U1 | Multi-DID partial answer | fixed (PR #274) |
| U2 | Session P2Server not applied | open, unticketed → #340 |
| U3 | Response validation untested; `UploadAsync` width; README example | open, unticketed → #354 (uds-M1/M2 green again) |
| J9 | Fallback channel under lost SA | open, unticketed → #347 (j1939-N1 green) |
| J10 | Dispose vs rebind leak | open, unticketed → #347 |
| J11 | `_claimEchoGraces` growth | fixed (`3199063`, j1939-N6 red) |
| J12 | `ReceiveBufferCapacity` without consumer | open, unticketed → #355 |
| T1 | BAM cancel double-serve | fixed (PR #258, five virtual-clock tests); same gap on the CM path → T-1, #350 |
| T2 | CTS(0) in `WaitEom` | fixed (PR #258) |
| T3 | Bidirectional abort | open, unticketed → #350 |
| C9 | Block-download batch after abort | fixed (PR #259, canopen-M1 red); server mirror → C-1, #341 |
| C10 | Classic upload size mismatch | fixed (#253, PR #265; shortfall reported, overrun aborts) |
| C11 | Linear buffer growth | fixed (PR #263, geometric with a 40 MB allocation bound test) |
| C12 | Block-download timer over the sub-block | fixed (PR #267, per-segment re-arm) |
| C13 | EMCY 8210h per short RPDO | fixed (PR #263, once per run); 1001h bit 4 half not adopted → P-8 |
| C14 | `SendSyncAsync` ungated | decided (#254, PR #265: documented raw send) |
| C15 | Own NMT broadcast not self-applied | decided (PR #288: documented) |
| C16 | Reserved heartbeat bytes | fixed (#255, #266; PRs #265, #267) |
| B11 | Root README snippet | fixed (`0983869`, checked against the approvals and upstream) |
| B12 | Required checks, merge queue | fixed (#256, PR #276; ruleset contexts = job names, strict) |
| B13 | Release chain | fixed (#257, PR #281; #328/#330 via PR #336) |
| B14 | "Four packages" leftovers | fixed (two historical, dated remnants remain) |

**§3 lists.** Actor/Reliability: 2 of 7 fixed (`A_Clean_Dispose` 500 ms, actor-M1 early fire), 5
open → #351. RawCan: 1 of 6 fixed (token, #301), 5 open → #352. ISO-TP: 0 of 8 fixed (all norm
questions) → #353; #246 fixed in the README, SRS row still stale. UDS: 0 of 7 fixed → #354. J1939: 3
of 7 resolved (deadline after dispose, period 0, pseudo-random scan), 4 open → #355. J1939-TP: 0 of
7 fixed (one deliberate and pinned) → #356. CANopen: 0 of 9 fixed → #357; "scan cancel lets 125
probes run on" could not be reproduced from the code on `main` and stays as an untested path (#359).
Repo/Docs/CI: 3 of 9 fixed (`ci.yml` comments, `.releaserc` assets, `format` required), 6 open →
#361. Previous §4.2 "green = gap" mutations re-run: reliability-M3/M4/M5, uds-M5/M5b, j1939-M1/M2/
M4/M5, canopenB-M5, j1939tp-M3 — all still green (section 4.2 names them under their new IDs);
isotp-M5 and rawcan-M1's sibling canopenB-M1 now have detectors.

## Appendix B · Issues filed from this review

| Issue | Carries |
| --- | --- |
| #338 | A-1 second `Dispose` |
| #339 | I-1 / I-6 send gate |
| #340 | U-1 + U2 |
| #341 | C-1 + C-3 + C-6 + C-9 |
| #342 | P-1 + P-2 + P-6 |
| #343 | S-2 |
| #344 | S-3 + S-4 (tests) |
| #345 | L-1 (+ upstream candidate) |
| #346 | B-1 release |
| #347 | J9 + J10 |
| #348 | Q8 + R-9 |
| #349 | R1 |
| #350 | T-1 + T3 |
| #351 | Actor/Reliability minors A-2..A-8 |
| #352 | RawCan/Addressing minors R-1..R-12, Q7 RawCan half |
| #353 | ISO-TP minors I-2..I-5, §3 carry-overs |
| #354 | UDS minors U-2..U-8, U3, §3 carry-overs |
| #355 | J1939 minors J-1..J-8, J12 |
| #356 | J1939-TP minors T-2..T-8, j1939tp-M3 gap |
| #357 | CANopen core minors C-2, C-4, C-5, C-7, C-8, C-10..C-14 |
| #358 | CANopen PDO minors P-3..P-9 |
| #359 | LSS/flying master minors L-2..L-8, wall-clock rows |
| #360 | Safety minors S-5..S-8 |
| #361 | Build/CI/docs minors B-2..B-16 |
| #362 | Wall-clock-gated test inventory |
| #289 (comment) | S-1 byte order |
