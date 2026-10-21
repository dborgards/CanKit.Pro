# CanKit.Pro.CANopen — LSS (CiA 305), CiA 801 detection, flying master, discovery

Deep review of `main` @ 36c97ca, 2026-10-21. Finding prefix **L**. Read-only; nothing was built or run.
Standard citations (CiA 305 v3.0.0, CiA 302-2 v4.1.0, CiA 801) are **from memory** throughout and are
marked as such where a claim depends on them.

## 1. Scope read

Source (all read completely):

| File | Lines |
| --- | ---: |
| `src/CanKit.Pro.CANopen/Lss/LssMaster.cs` | 660 |
| `src/CanKit.Pro.CANopen/Lss/LssMaster.Fastscan.cs` | 175 |
| `src/CanKit.Pro.CANopen/Lss/LssCommissioning.cs` | 756 |
| `src/CanKit.Pro.CANopen/Lss/LssBitRateDetection.cs` | 378 |
| `src/CanKit.Pro.CANopen/Lss/ILssMaster.cs`, `LssAddress.cs`, `LssBitTiming.cs`, `LssCommand.cs`, `LssFrames.cs`, `LssServiceResult.cs`, `LssSlaveOptions.cs`, `LssMasterOptions.cs`, `LssState.cs`, `LssTimeoutException.cs`, `CanOpenLssNodeExtensions.cs` | 212, 160, 85, 125, 103, 106, 96, 72, 16, 28, 31 |
| `src/CanKit.Pro.CANopen/CanOpenNode.Lss.cs` | 989 |
| `src/CanKit.Pro.CANopen/CanOpenNode.FlyingMaster.cs` | 644 |
| `src/CanKit.Pro.CANopen/CanOpenDiscovery.cs` | 343 |
| `CanOpenNode.cs` (constructor :195-335, dispatch :1380-1420, `SendControlFrame`/`SendReservedFrame` :2590-2700, SDO client cancel/begin/timeout :2160-2330, NMT :1570-1625), `CanOpenNode.CommunicationProfile.cs` (:440-470, :655-690, `PerformNmtReset` :825-960), `CanOpenNode.PeerSdo.cs` (:1-100) | excerpts |
| `README.md` §§ Discovery (:417-460), Not built (:109-172), LSS/slave/commissioning/CiA 801 (:1152-1365), Flying master (:1366-1487) | excerpts |
| `samples/CanKit.Pro.Sample.CanOpenLss/Program.cs` + README, `samples/CanKit.Pro.Sample.CanOpenBusScan/Program.cs` (:1-200) | 569, 534 |
| Upstream v0.5.6: `CanKit.Adapter.Kvaser/KvaserBus.cs`, `KvaserProvider.cs`, `SocketCanBus.cs`, `PcanBus.cs`, `VectorBus.cs`, `ControlCanBus.cs`, `ZlgCanBus.cs`, `Virtual/VirtualBus.cs`, `VirtualBusHub.cs`, `CanEnums.cs` (listen-only, FrameObserved, error-info paths) | excerpts |

Tests (read completely): `Lss/LssSlaveTests.cs` 3335, `LssMasterTests.cs` 1113, `LssMasterBoundaryTests.cs` 783,
`LssCommissioningTests.cs` 1154, `LssCommissioningConcurrencyTests.cs` 211, `LssBitRateDetectionTests.cs` 380,
`LssMasterActivationRecoveryTests.cs` 93, `LssMasterIndeterminateSendTests.cs` 95, `LssMasterLifetimeTests.cs` 120,
`LssMasterResponseRecoveryTests.cs` 122, `LssMasterUnsentTests.cs` 74, `LssCodecTests.cs` 144,
`ClockedLssRig.cs` 131, `ClockedLssNodeRig.cs` 132, `ClockedLssNodeRigTests.cs` 77, `ScriptedLssSlave.cs` 465,
`DetectionProbeBus.cs` 181, `LssTestService.cs` 155, `LssTestTiming.cs` 10, `FlyingMasterRig.cs` 210,
`CanOpenDiscoveryTests.cs` 487. Flying-master tests (1492 + 1754 lines) were read by outline plus the
passages the previous review named (`CanOpenFlyingMasterGapTests.cs:753-790`, `:1321-1346`); the source
they test is unchanged since 8cec2ee (`git log 8cec2ee..HEAD -- CanOpenNode.FlyingMaster.cs` lists only
the LSS node-id retarget commits bed1c34/1568f47 and d9c4eae; the test files only 6bfa8a7, rig sharing).

Issues/PRs read: #315, #320, PRs #304, #305, #306, #309, #316, #331.

## 2. Status of previous findings

| Previous item | Status today | Evidence |
| --- | --- | --- |
| §3 minor: "scan cancel lets up to 125 SDO probes run on; a following `ScanAsync` reports IDs as absent" (`CanOpenDiscovery.cs:176-179`, `CanOpenNode.cs:1900-1905`) | **Not reproducible from the code on `main`; not ticketed.** Cancellation reaches every probe: `SdoUploadCoreAsync` registers the token (`CanOpenNode.cs:2186-2219`), the callback posts `CancelSdoClient` to the actor, which removes the session, sends abort `0800 0000h` and cancels the transfer (`:2221-2240`); `ScanCoreAsync` awaits `Task.WhenAll` (`CanOpenDiscovery.cs:176`), so it returns only after every probe ended; and the peer channel introduced by 2c1c931 is released synchronously when the transfer's TCS completes (`CanOpenNode.PeerSdo.cs:35-47, :73-97`), so an immediately following `ScanAsync` is not refused as "already in flight". The `CancelSdoClient`/`RegisterSdoCancellation` text at 8cec2ee is identical to today's, so I cannot reconstruct the mechanism the earlier note had in mind. **Gap remains:** no test cancels a bus-level `ScanAsync` (`CanOpenDiscoveryTests.cs` cancels only the listen, `:157-192`). See §4 and mutation note. |
| canopenA-M4 — discovery timeout classification (`CanOpenDiscovery.cs:321`, formerly :321) | **Still red (covered).** `The_Scan_Asks_Every_Node_Id_Except_The_Client_And_The_Skipped_Ones_For_1000h` (`CanOpenDiscoveryTests.cs:242-255`, "a timeout is absence") and `Any_Answer_Is_Presence_And_A_Timeout_A_Refusal_Or_A_Busy_Client_Is_Not` (`:257-284`) both fail if the `Origin == Local && SdoProtocolTimedOut` arm is widened or removed. |
| canopenA-M5 — equal-claim guard (`CanOpenNode.FlyingMaster.cs:340`, formerly :333-334) | **Still red (covered).** `An_Equal_Claim_Does_Not_Depose_The_Active_Master` (`CanOpenFlyingMasterTests.cs:228-250`) and `An_Equal_Claim_During_The_Detect_Cycle_Yields_To_The_Lower_Node_Id` (`:678-702`) pin both halves of the condition. |
| Timing row "`QuiesceAsync` with `Task.Delay(40)` (six positive tests)" | **Unchanged.** `FlyingMasterRig.cs:133-140` still sleeps 40 ms between two settle rounds. Not ticketed as such (#92 is closed). |
| "Tests that cannot fail": `A_Force_Reset_Completion_After_Dispose_Is_Swallowed` (`CanOpenFlyingMasterGapTests.cs:1321-1345`) and the second half of `A_Frame_During_The_Cold_Reset_Is_Dropped_And_Dispose_Swallows_The_Completion` (`:753-780`) | **Unchanged**: both still end in `await Task.Delay(200);` with no assertion; an unobserved task exception keeps xunit green. No issue found by `gh issue list --search "flying master"`. |

The other previous IDs (K1, Q7–Q9, A1, R1, I6–I8, U1–U3, J9–J12, T1–T3, C9–C16, B11–B14) are outside this slice.

## 3. New findings

### L-1 · Important — CiA 801 detection runs in *normal* mode on Kvaser while `RequireListenOnly` passes

- **Where:** `LssBitRateDetection.cs:343-364` (`CheckOpenedAsRequested` compares `bus.Options.WorkMode`, i.e. the *requested* mode), `samples/CanKit.Pro.Sample.CanOpenLss/README.md` last paragraph ("Adapters whose `Baud(...)` takes effect on open (PCAN, Kvaser, Vector) need no such suffix"). Upstream v0.5.6: `CanKit.Adapter.Kvaser/KvaserProvider.cs:82-83` advertises `CanFeature.ListenOnly` when the channel has `canCHANNEL_CAP_SILENT_MODE`; `KvaserBus.cs` opens, configures bitrate, `canBusOn` (:87-101) and `ApplyConfig` (:123-134) handles only `ChannelWorkMode.Echo` — there is no `canSetBusOutputControl(canDRIVER_SILENT)` anywhere in the adapter (grep over the whole adapter: no `BusOutputControl`/`SILENT` outside the capability constant).
- **Mechanism:** `cfg.SetWorkMode(ChannelWorkMode.ListenOnly)` is accepted (the feature is advertised), `Options.WorkMode` reports `ListenOnly`, the detection's check is satisfied, but CANlib's default output control is normal mode. At a wrong candidate the controller transmits active error flags; at the right one it acknowledges frames — exactly what CiA 801 §5.2.1 (from memory) and this type's own remarks (`:178-191`) promise not to do. `LssBitRateProbe.ErrorFrames`/`BusState` still work, so the *result* is usually right; the *guarantee* ("transmits nothing", README :1328-1330, sample README) is false on this adapter.
- **Reachability:** every Kvaser user of `DetectAsync`; the maintainer's own bench has a Kvaser Leaf Light v2 (`docs/reviews/2026-10-06-echo-hardware-measurements.md:13`).
- **Confidence:** high that the adapter never sets silent mode (read); medium that CANlib defaults to normal output (from memory of CANlib's `canOpenChannel` semantics).
- **Test:** none possible on the virtual adapter (its `WorkMode` only switches echo, `VirtualBusHub.cs:73`).
- **Fix:** upstream, apply `canSetBusOutputControl(handle, canDRIVER_SILENT)` before `canBusOn` when `WorkMode == ListenOnly` (and `canDRIVER_NORMAL` otherwise). Here: strike Kvaser from the "need no such suffix" sentence and say in the README that `RequireListenOnly` can only verify what the factory *asked* for, naming the adapters whose listen-only path was read (SocketCAN `#netlink`, PCAN, Vector, ZLG, ControlCAN) and the one that ignores it.

### L-2 · Minor — the LSS master drops a slave reply flagged as a host echo

- **Where:** `LssMaster.cs:96-101` (`service.Subscribe(frame => …)`, `includeEcho` left at its default `false`); contrast `CanOpenNode.cs:294-300` and `CanOpenDiscovery.cs:224-228`, both `includeEcho: true` for the stated reason that a node in the same process on the same adapter is flagged as "this host's echo".
- **Mechanism:** `CanFrameEvent.IsEcho` is host-scoped (`CanFrameEvent.cs:65-90`), and a subscription that did not ask never sees an echo (`:76-80`). An LSS master and a `CanOpenNode` LSS slave sharing one adapter that flags echoes (SocketCAN loopback, Kvaser/PCAN in Echo mode) — the multiplexing `CanOpen.OpenNode` documents as supported, and the overload `OpenMaster(ICanBusService …)` invites — never see each other: the slave's `7E4h` reply is discarded before the master's predicate runs and every confirmed service ends in `LssTimeoutException`. The master itself never transmits on `7E4h`, so an echo on that id can only be another node's reply; the same reasoning `ListenAsync` applies.
- **Reachability:** supported configuration, narrow (one host, one adapter, echo-flagging driver). Invisible on the virtual adapter, where master and slaves sit on separate channels in every test.
- **Confidence:** medium-high (follows from the two documented contracts; not measured on hardware).
- **Test:** none (`LssMasterTests`, `LssSlaveTests` always open the master on channel 0 and slaves on others).
- **Fix:** `service.Subscribe(predicate, includeEcho: true)` in the master's constructor, with the same comment `ListenAsync` carries.

### L-3 · Minor — a second activate during the silence lifts the silence before its own delays start

- **Where:** `CanOpenNode.Lss.cs:422-431` (`CancelLssActivation`: "The node may transmit again" → `CompleteLssTransmitHold()` whenever the previous switch task ran to completion), called from `ActivateLssBitTiming` (:565) before the new delay is scheduled (:573-575).
- **Mechanism:** activation A, delay d: silence from t=d to 2d. A second activate with delay d′>0 arriving at t∈(d,2d) ends A's silence at once (`EndLssSilence` → held NMT/heartbeat/LSS replies are flushed, SDO/PDO/EMCY resume) and the slave transmits freely until t+d′, when the new silence begins. CiA 305 §6.4.4 (from memory) asks the slave to stay quiet for the second period of the activation it is executing; a replacement should restart the timing, not open a transmit window. The comment says this is deliberate; the README (:1236-1246) describes supersession of the *callback* only.
- **Reachability:** only a master that transmits during the switch delays (this master cannot: `LssMaster.cs:176` reserves `resumeAt` for every later command), or a second master. Hostile/buggy peer only.
- **Confidence:** high on the code path; low on the spec text.
- **Test:** none. `A_Second_Activate_During_The_Silence_Drops_The_First` (`LssSlaveTests.cs:1218-1240`) does not reach this path, see L-8.
- **Fix:** in `CancelLssActivation`, release the hold only when the node is disposed; let the replacement's `BeginLssSilence` take over an existing silence (it already tolerates `_lssSilent == 1` through `WaitForTransmitsToDrain`).

### L-4 · Minor — the commissioning cleanup's 2 s token can abandon the release or poison the master

- **Where:** `LssCommissioning.cs:647-666` (`ReleaseToWaitingAsync`, `new CancellationTokenSource(TimeSpan.FromSeconds(2))`), against `LssMaster.cs:389` (`Execute` first waits out `_nextCommandAt`) and `:418-449` (`SendAsync`: a cancellation that lands after entering `SendConfirmedAsync` sets `_indeterminateSend`, after which every command throws).
- **Mechanism:** after a cancelled or failed `ActivateBitTimingAsync` the master holds `resumeAt` = 2 × switch delay (up to 2 × 65 535 ms). `ReleaseToWaitingAsync` runs from `finally` with its own 2 s token: (a) if the remaining reservation exceeds 2 s the release is cancelled while waiting for the gate and silently swallowed (`:658-660`), so every slave stays in configuration and the next `ScanUnconfiguredAsync` or `ConfigureSlaveAsync` starts from a state the tool believes it cleaned; (b) if the reservation ends between 1 s and 2 s after the cleanup began, the 2 s timer fires *inside* `SendConfirmedAsync` (up to `CanBusService.DefaultConfirmTimeout` = 1 s on a bus with echo confirmation, `CanBusService.cs:31`) and the tool's own housekeeping renders the master permanently unusable ("indeterminate transmission").
- **Reachability:** API use — a switch delay above ~1 s together with cancellation or a failed `BitRateSwitch`; (b) additionally needs an echo-confirming adapter. Not triggered by the virtual bus.
- **Confidence:** high for (a), medium for (b) (window exists; width depends on the driver).
- **Test:** none (`A_Cleanup_Failure_Does_Not_Hide_A_Successful_Configure` scripts the master to throw; `A_Cancelled_Listing_Scan_Releases_The_Slaves_It_Parked` uses Fastscan windows of 120 ms).
- **Fix:** give the cleanup no wall-clock budget of its own — the master's wait is already finite (reservation ≤ 2 × 65 535 ms, send ≤ confirm timeout) — or expose the pending quiet period on `ILssMaster` and size the budget from it; never let a cleanup token reach `SendAsync`.

### L-5 · Docs / open question — the master transmits during the first switch-delay period

- **Where:** `LssMaster.cs:177-194` (`WaitingPhase.BeforeDelay`: switch-state-global(waiting) is sent right after the activation, i.e. inside the first period); README :1183-1186, `LssSlaveConfiguration.ReleaseToWaiting` doc (`LssCommissioning.cs:129-134`).
- **Mechanism:** CiA 305 §6.4.4 (from memory) defines the switch delay as the time until every node has switched and has the LSS master wait both periods before it sends again; this implementation deliberately uses the first period to send one more LSS frame so the slaves end in waiting on the old rate. A slave that stops *receiving* at the activation (several stacks put the controller into configuration mode for both periods) never hears it; the fallback (resend after the second period on the new rate) exists only when a host `BitRateSwitch` is configured, otherwise the call fails after both periods (`:185-187`).
- **Severity/confidence:** Docs; low confidence on the spec wording. The behaviour is documented in the API, but not as a deviation.
- **Fix:** state in the README that the old-rate waiting frame is a deliberate departure from the two-silent-periods rule and name the alternative (no `returnToWaiting`, new master on the new rate).

### L-6 · Minor / open question — Fastscan completion rule differs between the node, the scripted slave and (from memory) the norm

- **Where:** node `CanOpenNode.Lss.cs:857-863` (`bitCheck == 0 && (lssNext < lssSub || (lssNext == 4 && lssSub == 3))`), scripted slave `ScriptedLssSlave.cs:444-448` (`lssNext == 0 || lssNext < lssSub || …`), master `LssMaster.Fastscan.cs:60-66` (last *scanned* field gets `LSSNext 0`).
- **Mechanism:** the master relies on "LSSNext 0 at BitCheck 0 switches whatever LSSSub is" so that a scan that skips the serial number (allowed by validation, `:44-45`) still parks the slave. CiA 305 §6.7 as I remember it ties the switch to the serial number (LSSSub 3, LSSNext 0, or 4 per the corrigendum); a third-party slave with that rule finishes a `Skip`-serial scan without switching, and `ScanUnconfiguredAsync` then configures nobody (`LssCommissioning.cs:275-278` assumes the slave is in configuration). The node's rule is also *more* permissive than the scripted slave's for `LSSSub 0` (vendor-only probes with LSSNext 0 stay in the scan — right) but it switches on any `LSSNext < LSSSub`, which the scripted slave does too; the two agree with each other and with the master, not necessarily with other vendors. The README admits "not against a third-party LSS slave" (:10).
- **Confidence:** low (spec from memory). **Test:** `An_Assigning_Scan_Assigns_When_Fastscan_Skips_The_Serial_Number` passes against this repo's slave only.
- **Fix:** document that skipping the serial number depends on the slave treating LSSNext 0 as completion on any LSS number, or restrict `Skip` to the product code and revision when interoperability matters.

### L-7 · Docs — README / XML drift

1. Sample README (`samples/CanKit.Pro.Sample.CanOpenLss/README.md`, last paragraph) lists Kvaser among adapters that "need no such suffix" — see L-1.
2. README :1217-1218 "The node-id passed to `OpenNode` is the persistent and pending node-id" — only when no store loads one; `ResolveLayerSettings` (`CanOpenNode.Lss.cs:113-122`) lets a stored node-id replace the argument, which is then only validated.
3. `LssSlaveOptions.Manufacturer` (`LssSlaveOptions.cs:61-69`) does not say the handler runs on the node's actor (`HandleLss` :486-494): a blocking handler stalls every CANopen service of the node. `BitRateSwitch` says "runs off the node's actor" (:73); the asymmetry is worth one sentence.
4. `ILssMaster` remarks (`ILssMaster.cs:25-27`) "Cancellation returns promptly, but later commands wait out an abandoned response window" — true, but a cancelled `ActivateBitTimingAsync` reserves up to 2 × 65 535 ms, which the commissioning cleanup cannot wait for (L-4); the README commissioning section (:1297-1299, "best-effort") should name the reservation as the reason the release can be skipped.

### L-8 · Test quality — `A_Second_Activate_During_The_Silence_Drops_The_First` does not reach the silence

- **Where:** `LssSlaveTests.cs:1218-1240`.
- **Mechanism:** the second `ActivateBitTimingAsync(0)` goes through the same master, and `Execute` (`LssMaster.cs:389`) waits out the first activation's `resumeAt` (`:176`) before sending, so the frame arrives after the slave's second delay has ended (both ends measure 2 × 300 ms from the same frame). The assertions (`switches == 2`, state) hold for "a second activate *after* the silence". The in-silence path (`CancelLssActivation` ending a live hold, L-3) is untested. The tests that do inject during a silence use a separate `poke` bus (`:561-562`, `:592-596`).

## 4. Test quality and gaps

**What is strong.** The master is driven on a virtual clock for every window (`ClockedLssRig`, `ClockedLssNodeRig`), with stamps on the same clock as production; the recovery classes (`LssMasterActivationRecoveryTests`, `IndeterminateSendTests`, `ResponseRecoveryTests`, `UnsentTests`) each pin one reservation rule and are mutation-sensitive by construction (they count frames on `LssTestService.Commands`). `LssMasterBoundaryTests` covers arrival-stamp filtering at the exact deadline tick, saturation near `long.MaxValue`, parked readers and fatal exceptions. The #331 ordering tests assert their premise (`IsLssSilentForTests`, `HeldTransmitCountForTests`) before the claim.

**Gaps (name, what is missing).**
- Master + slave on one shared host service (L-2): no test; a `ControllableBus.EchoCapable` run with `OpenMaster(service)` and a node on the same service would show it.
- A second activate while the slave is silent (L-3/L-8): no test reaches `CancelLssActivation` with `_lssSilent == 1`.
- Commissioning cleanup against a long reservation (L-4): no test; `ReleaseToWaitingAsync` is only exercised with scripted masters that return at once.
- Discovery: a cancelled bus-level `ScanAsync` and a following scan (§2 row 1) — no test; the scan tests cancel nothing.
- `InquireNodeIdAsync` pending fallback (`CanOpenNode.Lss.cs:483`) is unobservable: when `_nodeId` is `FFh` the pending id is always `FFh` too (`ConfigureLssNodeId` :529-536 applies a valid id at once; `PerformNmtReset` :886-890 clears the active id only for pending `FFh`), so `IsActiveNodeId(_nodeId) ? _nodeId : _pendingNodeId` reduces to `_nodeId`. Not a defect; the XML doc on `ILssMaster.InquireNodeIdAsync` describes a state this slave cannot be in.
- `LssBitRateDetection` on hardware: the only non-double test (`Detects_The_Bus_On_The_Loopback_Adapter_With_Live_Heartbeat_Traffic`) cannot see work-mode fidelity (L-1).

**Wall-clock-gated tests (per the #92 rule: quantity the host can perturb → margin).**

| Test | Perturbable quantity | Margin | Outcome if exceeded |
| --- | --- | --- | --- |
| Every `Bench`-based test in `LssSlaveTests` (`Quick()` :32-39: `ResponseTimeout` 300 ms, `IdentificationTimeout` 80 ms) — e.g. `Confirmed_Services_Run_Only_In_Configuration…` :101, `Node_Id_Is_Applied_On_Reset_Communication…` :153, `Store_Persists…` :348, `A_Driver_Fault_Completes_A_Held_Nmt…` :449 | reader→actor→reply→reader latency on the virtual bus against one confirmed service | 300 ms (80 ms for identify) | false red (`LssTimeoutException`); recorded once on macOS in #331's CI, #92 row |
| `LssSlaveTests.The_Second_Delay_Suppresses_A_Node_Guarding_Rtr` :873-902 | `WaitUntilAsync(switches == 1)` + `Task.Delay(400)` + `Task.Delay(200)` against an 800 ms second delay measured on the slave's actor | 200 ms | false red (RTR heard before `await activate`) |
| `LssSlaveTests.The_Switch_Silence_Sends_A_Heartbeat_After_The_Delay` :1314-1340 (80 + 160 ms inside an 800 ms silence) | same | 560 ms | false red |
| `LssSlaveTests.Disposing_The_Slave_Drops_A_Held_Nmt` :442 (`WhenAny(held, Delay(1000))`) | disposal latency | 1 s | false red |
| `LssSlaveTests` negative windows `Task.Delay(40/50/80/200/250)` at :192, :412, :439, :487, :518, :563, :597, :1103, :1148, :1191, :1212, :1237, :1304, :1366, :1448, :1454, :1508, :1527, :1536, :1559, :1741, :1767, :1867, :2030, :2036, :2880; `Repeated_Activations…` :2470 (2 s) | "nothing must arrive" | — | false green only |
| `LssCommissioningTests` on `Bench` (`Quick()` :28-35: `ResponseTimeout` 300 ms, `FastscanTimeout` 120 ms wall clock): `Configure_And_Inquire_One_Slave_By_Its_Address` :116, `ReadIdentities_Skips…` :162 (SdoTimeout 200 ms), `A_Cancelled_Listing_Scan_Releases…` :206, `An_Assigning_Scan_Does_Not_Rewrite…` :259, `Inquire_Can_Miss…` :573, `A_Disposed_Master_Lets_The_Scan_End` :759 | per-step Fastscan window / per-service response window against scheduler latency (#315's mechanism) | 120 ms / 300 ms | false red (`NoMatch`, `Selected == false`, timeout) — #315 moved ten tests but these six stayed on the wall clock |
| `LssCommissioningTests.Configure_Can_Leave_The_Slave…` :515 (`Task.Delay(40)`) | "no switch" | — | false green only |
| `LssMasterTests.Cancelling_And_Disposing_Unblock_A_Wait` :283-303 (`CancellationTokenSource(40 ms)`, `Task.Delay(20)`) | if the 40 ms timer fires while `SendConfirmedAsync` is still running (pool stall), the master is poisoned and the second command throws `CanOpenTransportException` instead of `ObjectDisposedException` | 40 ms | false red |
| `LssMasterTests.Activate_With_No_Delay…` :361, `A_Zero_Delay_Returns_To_Waiting…` :702, `Commands_Run_One_At_A_Time` :777 (`Rig`, `ResponseTimeout` 5 s) | virtual-bus latency | 5 s | false red (large margin) |
| `LssBitRateDetectionTests.Detects_The_Bus_On_The_Loopback_Adapter…` :295-317 | a 20 ms heartbeat inside a 2 s window | 1.98 s | false red (large margin) |
| `FlyingMasterRig.QuiesceAsync` :133-140 (`Task.Delay(40)`) — every flying-master test | pool latency of `SendControlFrame` + hub delivery | 40 ms | false red (unchanged from the previous review) |
| `CanOpenFlyingMasterGapTests` :779, :877, :1344, :1421, :1584 (`Task.Delay(200)`) | — | — | false green only; :779 and :1344 have no assertion at all |
| `CanOpenDiscoveryTests.Listening_Transmits_Nothing…` :101 (`ReplyWindow` 500 ms) | — | — | false green only |

## 5. Mutations proposed

| # | Edit (exact) | Expectation |
| --- | --- | --- |
| L-M1 | `LssMaster.cs:96` — `_subscription = service.Subscribe(frame =>` → `_subscription = service.Subscribe(includeEcho: true, predicate: frame =>` (closing `});` at :101 unchanged) | **expected green = gap** (L-2): no test shares one echo-flagging host between master and slave. |
| L-M2 | `CanOpenNode.Lss.cs:429` — `if (Volatile.Read(ref _disposed) != 0 \|\| _lssBitRateSwitch.Status == TaskStatus.RanToCompletion)` → `if (Volatile.Read(ref _disposed) != 0)` | **expected green = gap** (L-3/L-8): `A_Second_Activate_During_The_Silence_Drops_The_First` sends its second activate after the reservation, so the hold is already released; `Repeated_Activations_Serialize_Adapter_Switches_And_Keep_Traffic_Held` supersedes while the hook is still running (status not `RanToCompletion`), so the branch is not taken there either. |
| L-M3 | `LssMaster.cs:437` — `mayHaveTransmitted = confirmation.FailureReason != TxConfirmFailureReason.Rejected;` → `mayHaveTransmitted = false;` | red: `LssMasterIndeterminateSendTests.An_Indeterminate_Send_Rejects_Queued_And_Future_Commands_Even_After_A_Late_Transmission` (failure 0 and 3: queued command no longer throws "indeterminate transmission"). Pins the poisoning rule L-4 depends on. |
| L-M4 | `CanOpenNode.Lss.cs:842` — `if (_lssState != LssState.Waiting \|\| !IsLssUnconfigured()) return null;` → `if (_lssState != LssState.Waiting) return null;` | red: `LssSlaveTests.Fastscan_Finds_Unconfigured_Slaves_In_Numeric_Order` (the fourth scan expects `NoUnconfiguredSlave`; configured slaves now answer the reset step). |
| L-M5 | `LssBitRateDetection.cs:346` — `if (options.RequireListenOnly && busOptions.WorkMode != ChannelWorkMode.ListenOnly)` → `if (false)` | red: `LssBitRateDetectionTests.A_Bus_Opened_Outside_Listen_Only_Fails_When_It_Is_Required`. Shows the check is live — and that it can only ever see the requested mode (L-1). |
| L-M6 | `LssMaster.Fastscan.cs:76` — `if (!scanned.Bit0Acknowledged` → `if (false` | red: `LssMasterTests.Fastscan_Finds_The_Smallest_Unconfigured_Slave_Then_The_Next` (Match fields are never confirmed, the slave never advances, the serial scan reads all ones and `first.Address` is not `SlaveA`); also `LssSlaveTests.Full_Fastscan_Finds_An_Address_Whose_Every_Bit_Is_Zero`. Pins the §6.7.3 re-confirmation step. |

Candidate beyond the six (for the §2 discovery row): `CanOpenNode.cs:634-636` region — delete `RegisterSdoCancellation(tcs, cancellationToken, serverNodeId);` in `SdoUploadCoreAsync` → red in `LssSlaveTests.The_Switch_Silence_Drops_An_Sdo_Upload_From_The_Slave` (upload ends by SDO timeout instead of `OperationCanceledException`), but **green for every discovery test** — the scan-cancel gap.

## 6. Doc drift (README / XML vs behaviour)

- Sample README: Kvaser listed as applying listen-only on open (L-1).
- README :1217-1218 vs `ResolveLayerSettings` :113-122 (stored node-id overrides the `OpenNode` argument) — L-7.2.
- `LssSlaveOptions.Manufacturer` silent on the actor thread — L-7.3.
- README :1297-1299 "best-effort" release without naming the reservation/2 s budget interplay — L-7.4 / L-4.
- README :1183-1186 and `ILssMaster.ActivateBitTimingAsync` doc describe the old-rate waiting frame as a feature, not as a departure from the two silent periods — L-5.
- `ILssMaster.InquireNodeIdAsync` doc ("reports its pending node-id, which may be FFh") describes a state the node-hosted slave cannot reach (§4, last gap bullet). Accurate for the scripted slave only.
- Otherwise the README's LSS, commissioning, CiA 801 and flying-master sections match the code line for line (switch-delay semantics, held frames and their order after #331, `Limit == 1` activation rule, timeout result classes, `1F90h` defaults and the `04h > 127 × 05h` rule, `1F80h` bit polarity).

## 7. What is done well (brief)

- Every master wait is anchored to the transmit stamp, not to "after the confirmation returned", and abandoned windows are reserved for the next command (`Reserve`, `_nextFastscanAt`); the indeterminate-send poisoning is a defensible answer to drivers that may still transmit later, and it is documented at the interface, in the README and in the SRS.
- Fastscan: bit-0 priority, prefix probing, the explicit `LSSNext` handling for the vendor-id and the re-confirmation when bit 0 was not acknowledged are all commented against the annex and pinned by tests on a virtual clock.
- The slave's held-transmit queue (#331) is a clean single-consumer design; the reservation/drain gate (`_lssTxInFlight`, `WaitForTransmitsToDrain`) keeps identity changes and bit-rate switches from racing driver calls, and the tests for it assert their premises.
- CiA 801 detection decides a probe through one `TaskCompletionSource` (window-edge race closed), ignores echoes and RX-stream error frames, tolerates adapters that refuse the error subscription, and disposes every probe bus.
- Discovery's listen is honest about a pump that ended early (#197) and includes echoes for the local-node case.

## 8. Open questions for the maintainer

1. L-1: is the Kvaser silent-mode gap to be fixed upstream (PR to pkuyo/CanKit) or documented here as "Kvaser: detection is reactive"? Until then the sample README sentence is wrong for the bench adapter you measured with.
2. L-2: should `OpenMaster(ICanBusService …)` on a service shared with a local `CanOpenNode` be a supported setup (then `includeEcho: true`), or should the README say an LSS master needs its own adapter?
3. L-3/L-5/L-6 depend on CiA 305 wording I only have from memory (the silent periods on both sides; the Fastscan completion condition). Worth one look at §6.4.4 and §6.7.3 of the normative text before deciding whether any of the three is a defect or a documented choice.
4. L-4: is the 2 s cleanup budget in `LssCommissioning` a deliberate upper bound (then the reservation after a long switch delay needs documenting), or may the cleanup inherit the master's own bounded waits?
5. The six `LssCommissioningTests` and the whole `Bench` family of `LssSlaveTests` still run confirmed services against 300 ms / 120 ms wall-clock windows — the #315 mechanism. #315 is closed; should the remaining wall-clock tests get a row in a successor to #92, or is the plan to move `CanOpenNode` onto `VirtualClock` (the step PR #316 named as out of scope)?
