# Deep review 2026-10-21 — CanKit.Pro.CANopen, slice *CANopen Safety* (CiA 304 / CiA 319)

Repository `main` @ `36c97ca`. Read-only review; nothing was built or run.
Finding-ID prefix **S**. All CiA 304 / CiA 319 / EN 50325-5 statements below are **from memory** unless they quote the
repository's own citation of DSP 304 V1.0; I have none of the three texts. Where I rely on CANopenNode's SRDO
implementation (`CO_SRDO.c`) as a proxy for EN 50325-5, that is memory too and marked as such.

## 1. Scope read

Source (every file read completely):

| File | Lines |
| --- | ---: |
| `src/CanKit.Pro.CANopen/Safety/SrdoEngine.cs` | 322 |
| `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs` | 172 |
| `src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs` | 97 |
| `src/CanKit.Pro.CANopen/Safety/SrdoRecords.cs` | 147 |
| `src/CanKit.Pro.CANopen/Safety/SrdoFrames.cs` | 35 |
| `src/CanKit.Pro.CANopen/Safety/SrdoMapping.cs` | 73 |
| `src/CanKit.Pro.CANopen/Safety/PeerSafety.cs` | 212 |
| `src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs` | 215 |
| `src/CanKit.Pro.CANopen/Safety/{ISrdoEngineHost,SafetyEvents,SrdoCommunicationParameter,SrdoState,SrdoInvalidReason,SrdoDirection,CanOpenSafetyExtensions,PeerSafetyStorage}.cs` | 24+57+20+30+45+13+18+18 |
| `src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs` | 797 |
| `src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs` | 535 |
| `src/CanKit.Pro.CANopen/CanOpenNode.ForeignSrdo.cs` | 194 |
| Wiring read in part (grep + surrounding regions): `CanOpenNode.cs` (ctor 220–320, cleanup 880–1000, dispatch 1380–1430), `CanOpenNode.CommunicationProfile.cs` (225–300, 540–660, 760–900, 985–1045), `CanOpenNode.DeviceDescription.cs` (60–145, 185–340, 745–885), `CanOpenNode.BootUp.cs` (25–60, 90–390), `CanOpenNode.PeerSdo.cs` (125–290), `CanOpenNode.Pdo.cs` (780–915), `CanOpenNode.ForeignPdo.cs` (340–450), `CanOpenNode.Lss.cs` (890–970), `ObjectDictionary.cs` (write path 270–350) | — |

Tests (read completely): `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/` — `CanOpenSafetyNodeTests.cs` 1679,
`CanOpenPeerSafetyTests.cs` 1852, `CanOpenSafetyBootUpTests.cs` 735, `CanOpenForeignSrdoTests.cs` 636,
`CanOpenSafetyCommunicationProfileTests.cs` 509, `CanOpenSafetyDeviceDescriptionTests.cs` 451,
`SrdoEngineProducerTests.cs` 473, `SrdoEngineConsumerTests.cs` 412, `SrdoCrcTests.cs` 103, `SrdoRecordsTests.cs` 83,
`SafetyTypesTests.cs` 138, `SrdoFramesTests.cs` 41; fixture `tests/.../CANopen/Fixtures/safety.dcf` 633;
`FlyingMasterRig.cs` (UntilAsync/QuiesceAsync); `Lss/LssSlaveTests.cs` 960–1015 (SRDO retarget).

Docs: `src/CanKit.Pro.CANopen/README.md` §"CANopen Safety (CiA 304)" (lines 680–1102), `docs/reviews/2026-10-09-canopen-safety-scope.md`
(417), SRS rows FR-CO-035..046, 051, 058, 059, `docs/packages/index.md`, `docs/packages/canopen.md`, arc42 ADR-12 (grep),
issue #289 body, PR #291/#310/#329 mutation lists (via `gh`).

## 2. Status of previous findings

The previous deep review (`docs/reviews/2026-09-30-deep-review.md`, main @ `8cec2ee`) predates CANopen Safety entirely:
`grep -i 'srdo|safety|304'` over it returns nothing. None of K1, Q7–Q9, A1, R1, I6–I8, U1–U3, J9–J12, T1–T3, C9–C16,
B11–B14 or the §3 minor list names a file of this slice. **No previous finding applies to this slice**; their status
belongs to the CANopen core / PDO / SDO reviewers.

## 3. New findings (by severity)

### S-1 — Important (interoperability, #289): 13FFh checksum feeds multi-byte fields MSB-first; the reference implementation (as I remember it) feeds them little-endian

- **Where:** `src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs:58-77` (`CanonicalBytes`: `cycle >> 8` first, `AddU32` 90–96 big-endian) and the second copy of the same layout in `src/CanKit.Pro.CANopen/Safety/SrdoRecords.cs:118-138` (`RecordChecksum`, `AddU32` 140–146).
- **Mechanism:** The order of fields (direction, refresh/SCT, SRVT, COB-ID 1, COB-ID 2, mapping count, then sub-index + value per slot including the inverted slots) and the primitive (CRC-16/XMODEM, init 0000h) match what I remember of CANopenNode's `CO_SRDOcalcCRC` (`CO_SRDO.c`): `crc16_ccitt(..., 0)` over `informationDirection` (1), `safetyCycleTime` via `CO_memcpySwap2` (2), `SRVT` (1), `COB_ID1`/`COB_ID2` via `CO_memcpySwap4` (4+4), `numberOfMappedObjects` (1), then per entry `subindex` (1) + `mappedObjects[i]` via `CO_memcpySwap4` (4). `CO_memcpySwap*` produces the CANopen (little-endian) byte image on every host. So the only difference is the byte order of the 16/32-bit fields — and it is enough to make every checksum differ for any record whose multi-byte fields are not byte-symmetric (every real COB-ID is).
- **Reachability:** a conforming peer (if EN 50325-5 prescribes the CANopen byte order, as CANopenNode's reading suggests). Tool role: `ConfigurePeerSafetyAsync` against such a device always ends `Succeeded == false` with a 13FEh mismatch after step 4 (the device resets A5h). Device role: a conforming tool's 13FFh never matches `RecordChecksum`, every SRDO is `ConfigurationInvalid`, and FR-CO-058 clears every A5h the tool writes.
- **Severity / confidence:** Important; **medium** confidence — from memory of CANopenNode and of EN 50325-5's "data as transmitted" phrasing, not from the text. The repository already lists the byte order as an open decision in #289 and documents it (README 802–806, SRS FR-CO-037), so this is not a defect against the text the code was built to; it is the single most consequential item for #289 and deserves to be settled first.
- **Tests:** `SrdoCrcTests.Canonical_Sequence_Follows_The_Listed_Order` and `Compute_Is_The_Primitive_Over_The_Canonical_Sequence` pin the *current* choice (golden vector) and will go red on purpose when it changes.
- **Fix:** once the text or a reference vector is available, switch both `AddU32` helpers and the two `cycle` bytes to little-endian **in one shared serializer** (see S-6) and update the golden vector; until then, add a README sentence that a device following CANopenNode's convention will not interoperate.

### S-2 — Important: a peer reporting more SRDOs in 13FFh:00 than the bound file implies is refused by the gate *mid-transaction*, after the delete-first pass has started

- **Where:** `src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs:74-76` (count read, `EnsureConfigurationFitsPeer`), `:427-433` (`EnsureConfigurationFitsPeer` checks only the SRDOs *named*), `:459-464` (delete-first loop `for n = 1..peerSrdoCount` writes `130(n)h:01 = 0`), gate `src/CanKit.Pro.CANopen/CanOpenNode.PeerSdo.cs:156-162` with `IsImpliedSrdoRecordEntry` `:270-273` (implies records only up to the file's highest SRDO record).
- **Mechanism:** The pre-checks before the first frame cover the identity, `13FFh:00`, `1300h`, `13FFh:n` of the SRDOs *named*, `13FEh` and `1010h` (`FixedConfigurationTransfers`, `:207-217`). The transfers the §9.2 list makes for *unnamed* SRDOs — `130(n)h:01 = 0` and `13FFh:n = 0` for every `n ≤ peerCount` — are not pre-checked, and `peerCount` can exceed `binding.DescribedSrdoCount`. Sequence with `peerCount = 3`, file highest record 2: identity OK → count 3 → named SRDO 1 ≤ 3 OK → writes `1301h:01 = 0`, `1302h:01 = 0` (peer's SRDOs deleted, 13FEh cleared) → `1303h:01` → gate: `n = 3 > 2` and not declared → `PeerSdoAccessException` propagates with the peer left part-way through. The verification path throws the same exception instead of returning a result (boot-up step D then reports a background exception and signals `SlaveSafetyConfigurationInvalid` — acceptable, but opaque).
- **Reachability:** a conforming peer whose bound EDS/DCF declares fewer SRDO records than the device implements. The README itself acknowledges such devices (`README.md:352-353`: "a device opened with `SrdoCount` above the file's highest SRDO record has records the file does not imply"), and the README's guarantee at `:962-963` ("refused with nothing sent … not mid-transaction with the peer part-way through its configuration") is false for this case.
- **Severity / confidence:** Important (the "safe by construction" outcome — 13FEh = 0 — still holds, but the documented contract does not, and the operator gets an exception naming a record instead of a diagnosis). **High** confidence from code reading.
- **Tests:** none. `An_Srdo_Within_The_Files_Records_But_Above_The_Peers_Count_Is_Refused_After_The_Count` covers the opposite direction (peer count < file), `A_Count_Of_64_Is_Taken` uses a file whose highest record is 64.
- **Fix:** right after `ReadPeerSrdoCountAsync` (configuration and verification alike) refuse `peerCount > binding.DescribedSrdoCount` with `InvalidOperationException` ("the peer reports N SRDOs in 13FFh:00, the bound description implies only M; records M+1..N could not pass the peer-SDO gate; nothing was written") — or run `EnsurePeerSdoAccess` over `(CommIndex(n), 01h)` and `(13FFh, n)` for `n = 1..peerCount` there. Proposed test: `FakeExpeditedServer(session, 2, Device, lieAt: (0, 0), srdoCount: 3)` + `PeerFile()`; assert the exception, `fake.Requests` ends at `13FFh:00`, `fake.Written` has no `1301h:01`.

### S-3 — Important (test gap, FR-CO-038 consumer half): nothing asserts that a *consumer* with an invalid configuration ignores a correct pair

- **Where:** `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs:30` — `if (isRtr || rt.Direction == Transmit || !_operational || !rt.ConfigurationValid) return true;` and `SrdoEngine.cs:163-168` (`Arm` → `ConfigurationInvalid`).
- **Mechanism:** the code is right; the claim "a consumer reports invalid at once" and "its frames are swallowed" (FR-CO-038, README 816–818) is not held by any test. `SrdoEngineConsumerTests` always starts with a valid configuration; `SrdoEngineProducerTests.Nothing_Is_Sent_Outside_Operational_Or_With_An_Invalid_Configuration` covers the producer only; every node/boot-up test with a tampered checksum uses a producer.
- **Severity / confidence:** Important as a gap in the normative coverage of a safety property (a regression here would let a consumer actuate on an unchecked configuration); high confidence.
- **Fix:** add `SrdoEngineConsumerTests.A_Consumer_With_An_Invalid_Configuration_Reports_ConfigurationInvalid_And_Takes_No_Pair`: tamper `13FFh:1`, `EnterOperational`, assert reason `ConfigurationInvalid`, send a correct pair, assert `TryHandleFrame` returned true, `0x2000` unchanged, no `SrdoReceived`, state unchanged. Mutation M1 below shows the gap.

### S-4 — Minor (test gap, CiA 319 §5.3 step 4 as seen by the tool): the A5h readback that differs has no test

- **Where:** `src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs:103-106`.
- **Mechanism:** the only path by which S-1 (or any CRC disagreement) becomes visible to the configuration entity is this readback. No test makes the peer answer the 13FEh readback with anything but A5h (`FakeExpeditedServer` lies at `1301h:02` or nowhere; the real device agrees with the tool by construction).
- **Fix:** a test with `lieAt: (0x13FE, 0x00)`: `Succeeded == false`, one mismatch at `13FEh:00`, `Stored == false`, no `1010h` write. Mutation M2.

### S-5 — Minor (performance on the actor): step-D probing rebuilds a full object dictionary from the DCF per assigned slave on every boot event

- **Where:** `src/CanKit.Pro.CANopen/CanOpenNode.BootUp.cs:282-289` (`SafetyExpectationOf` → `LoadDescribedSafetyObjects` → `PopulateSafetyObjects` + loader over a fresh `ObjectDictionary`), called from `ConsiderStart` `:166` and from `HasAssignedSafetySlave` `:298-304`, which `TryFinishBoot` (`:195`) runs for all 127 ids on every announcement.
- **Mechanism:** correct, but `DescribedObjects.Capture` + a loader run per assigned slave per heartbeat/boot-up, on the actor thread. With many assigned slaves and large DCFs this is noticeable actor latency during a boot storm; it also runs for slaves whose DCF has no SRDO at all.
- **Confidence:** high (code reading); no test needed. **Fix:** memoise "declares any SRDO" per bound description instance (invalidate on bind/unbind), or compute it from `DescribedObjects.SrdoCount > 0` plus a direction ≠ 0 scan without instantiating a dictionary.

### S-6 — Minor (maintainability, "a figure belongs in one place"): the 13FFh byte layout is implemented twice

- **Where:** `SrdoCrc.CanonicalBytes` (`SrdoCrc.cs:54-78`) and `SrdoRecords.RecordChecksum` (`SrdoRecords.cs:118-138`), each with its own `AddU32`.
- **Mechanism:** the tool-side checksum (`Compute`) and the device-side check (`RecordChecksum`, FR-CO-038/058/059) must stay byte-identical; nothing in `SrdoCrcTests`/`SrdoRecordsTests` pins that — a divergence surfaces only in the integration tests (`Configure_Commit_And_Transmit`), and the #289 byte-order change (S-1) has to be made in two places. Mutation M4 shows which tests catch a one-sided change.
- **Fix:** one serializer taking `(direction, cycle, srvt, cob1, cob2, count, IEnumerable<(byte sub, uint value)>)`; `Compute` passes the plain entries doubled, `RecordChecksum` passes the raw slots.

### S-7 — Minor (event noise at start-up): the inverted frame of a pair that began before the consumer entered Operational is reported as `OutOfOrder`

- **Where:** `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs:103-107`.
- **Mechanism:** a consumer that enters Operational between a producer's plain and inverted frame (normal when a network is started with one NMT Start to node 0 and a producer is already running) sees the inverted frame first → `SetInvalid(OutOfOrder)` → `NotReceived → OutOfOrder` is a reason change, so `SrdoStateChanged(OutOfOrder)` is raised (`SrdoEngine.cs:285-294`), then the next pair validates. Harmless for safety; a subscriber logging transitions sees a spurious protocol-violation event at every start. DSP 304 §9.5's chronological-order rule (as the code cites it) is about a pair, not about a frame that belongs to no pair of this consumer.
- **Confidence:** medium that it is unwanted (it is a defensible reading). **Fix (optional):** while `Reason == NotReceived` and no pair has ever been taken in this period, treat a lone inverted frame as "no pair" without a transition.

### S-8 — Docs: three stale statements about the CiA 319 device side

- `docs/packages/index.md:56` says "CiA 319 (v2.0.0, tool side)" — the device side (FR-CO-058/059) shipped in #329.
- `docs/requirements/SRS-CanKit.Pro.md:350` (FR-CO-051) ends with "Clause 7 und 8 (Safety Application Layer) und die Geräteseite von CiA 319 sind nicht umgesetzt (#308)" — #308 is closed by #329; FR-CO-058/059 two rows below say the opposite.
- `docs/packages/canopen.md` does not mention SRDO, GFC, CiA 304 or CiA 319 at all (0 hits), while `index.md` and the root README advertise the feature. A reader of the package page does not learn the feature exists.

## CiA 304 as recalled, checked against the code

| Point (from memory) | Implementation | Verdict |
| --- | --- | --- |
| Two messages, COB-ID 1 odd 101h..17Fh, COB-ID 2 = COB-ID 1 + 1; defaults FFh+2·id / 100h+2·id for SRDO 1, node-id ≤ 64 | `SrdoFrames.cs:27-34`, `CanOpenCobId.SrdoDefaultCobId1/2`, `PopulateSafetyObjects` | matches |
| Second message bitwise inverted, same DLC; 0 ≤ L ≤ 8; L < n → not processed + EMCY 8210h; L > n → first n bytes | `SrdoFrames.Invert/IsInversePair`, `OnSecondFrame` `:112-127`, `Actuate` | matches |
| Record 1301h–1340h: sub1 direction 0/1/2 (3..255 reserved), sub2 refresh/SCT ms U16, sub3 SRVT ms U8, sub4 = 254 const, sub5/6 COB-IDs; defaults 25 / 20 (CANopenNode's example OD uses the same) | `ValidateSrdoCommunicationWrite`, `PopulateSafetyObjects:67-73` | matches |
| Mapping 1381h–13C0h: plain/inverted alternate; count 0, 2, 4 .. 128 (bit-granular) | byte-aligned, ≤ 16 sub-indices | narrower, documented ("Not built") |
| 13FEh: any value, A5h = valid; every write to a safety parameter sets it to 0 | validator + hook `CommunicationProfile.cs:565-566` | matches |
| 13FFh:00 = number of SRDOs; 13FFh:n CRC poly 1021h; field order a)–h) | `SrdoCrc.CanonicalBytes` | order matches; **byte order: S-1** |
| Producer: cyclic every refresh time, event-driven allowed, inverted frame "with minimum delay"; first transmit delayed 0.5 ms × node-id | `Arm:173-174`, `Transmit`, chain in `SendSrdoPairAsync` | matches; the inverted frame waits for the plain frame's *confirmation* (open question 2) |
| Consumer: SRVT between the two messages, SCT between successive valid SRDOs; on violation SRDO invalid, safe state is the application's | `OnFirstFrame`, `ArmSct` on a complete pair | matches (CANopenNode restarts its SCT on the completed pair too, as I remember it) |
| SRDOs only in Operational (§8.3.2.2); no safety-entry writes in Operational (0800 0022h) | `EnterOperational/LeaveOperational`, `IsStateGated` | matches; 1300h writable in Operational by decision |
| GFC: 001h, DLC 0, 1300h gates it | `TryHandleFrame:19-25`, `TrySendGfc` | matches; reception additionally gated on Operational — listed in #289 |
| CiA 319 §5.2 identity over 1018h:01–03; §5.3 steps 1–5; step 4 on the SRD; §5.4 after reset; §6.2 SRVT default 20 for tx SRDOs the SRD does not implement | `ConfigurePeerSafetyAsync`, `RevalidateSafetyConfiguration`, `RestoreValues`, `ValidationTimeKind` | matches the README's account; **S-2** for the pre-check |

Interactions checked: NMT (SRDOs armed only in Operational, under the write gate — `ApplyNmtTransition:784-790`); PDO COB-ID
collisions (PDO/SYNC/EMCY validators keep 101h–180h out, `A_Pdo_Cannot_Take_An_Srdo_CobId`); `DisposeAsync`
(`CleanUpOnActor:906-909` disposes engine, periodic check and send period; links on the chain see the cancelled token);
LSS node-id change (`RetargetDefaultSrdo` writes unchecked, the `EntryWritten` hook still clears 13FEh and retakes the
image — `ObjectDictionary.cs:330-346` raises the hook for unchecked writes, so the periodic check is not fooled);
periodic check re-entrancy (the hook's `VerifyConfigurationValidAndTakeImage` nulls the image when 13FEh is cleared from
inside `RevalidateSafetyConfiguration`; no recursion, no stale image); OD write length checked before the validator
(`ObjectDictionary.cs:284-291`), so `value[1]`/`DecodeU32` in the validator are safe; `Remove` is `internal`, so the
"no public path to a refused mapped write" claim (README 905) holds.

## 4. Test quality and gaps (FR-CO-035..046, 051, 058, 059)

| Requirement | Normative behaviour asserted? | Gap |
| --- | --- | --- |
| FR-CO-035 objects/defaults/reset/store | yes (`Defaults_Of_Section_8_4_2_2`, store/reset trio) | — |
| FR-CO-036 validator | yes, each abort code individually; Operational gate incl. the write-gate race | — |
| FR-CO-037 CRC | golden vector, field sensitivity, rounding, ranges | the two layouts' equality is not unit-pinned (S-6) |
| FR-CO-038 transition check | producer: yes (`Nothing_Is_Sent…`, `Tampered_Checksum…`) | **consumer half untested (S-3)** |
| FR-CO-039 producer timing | yes, to the tick on a virtual clock; chain/pending/period tests are thorough | — |
| FR-CO-040 consumer | yes: SRVT 19/20, SCT 49/50, OutOfOrder, Mismatch, short/long, FD, ActuationRefused | — |
| FR-CO-041 GFC | yes, incl. token semantics and folding | — |
| FR-CO-042 facade | yes | — |
| FR-CO-043 DCF | yes, incl. the device/expectation agreement theory (19 rows) | — |
| FR-CO-044 configure/verify | yes, extensive | **S-2 case missing; A5h readback mismatch (S-4)** |
| FR-CO-045 boot-up step D | yes, incl. held reset, cancelled boot, role loss, channel ownership | — |
| FR-CO-046 foreign SRDO | yes | — |
| FR-CO-051 CiA 319 entity | yes, every clause with a named test | — |
| FR-CO-058 step 4 / §5.4 | yes (`Writing_A5h_Over_A_Wrong_Checksum…`, the §5.4 theory incl. inverted slot and reserved direction) | SDO-originated A5h over a wrong checksum is covered only indirectly (`A_Download_Of_The_Identical_Value…` tests the clearing, not step 4) |
| FR-CO-059 periodic check | yes, incl. send-period end and the deleted-SRDO image rule | — |

Quality notes: negatives are shown with ordering witnesses (heartbeat, chain tail, FIFO NMT queue) rather than sleeps;
every timing assertion is on a `ManualTimeSource`; the peer tests use a scripted expedited server that records requests,
so "nothing sent" is a positive list comparison. The SRS traceability names tests that all exist (spot-checked 20 names).

**Timing-gated tests in this slice (per the #92 rule):**
- `FlyingMasterRig.QuiesceAsync` (`tests/.../CANopen/FlyingMasterRig.cs:133-140`) contains a real `Task.Delay(40)` and is used by every `CanOpenSafetyBootUpTests` test through `UntilAsync`/`AdvanceAsync`/`SettleUntilAsync`. The quantity the host can perturb is the thread-pool start of `SendControlFrame`'s send task plus virtual-bus delivery to the peer node; the margin is 40 ms. The virtual budgets (`800`, `2000`, `5000` virtual ms) are not wall-clock. The assertions that depend on the 40 ms are negatives *after* a settle (`SdoRequests(rig).Should().Be(0/1/requests)`, `NotContain(Start)`): a late arrival after the assertion makes them pass, not fail, so the exposure is to false *passes*, not flakes.
- `WaitUntilAsync` with a 5 s wall deadline and `Task.Delay(5)` polling (`CanOpenSafetyNodeTests.cs:47-55`, `CanOpenSafetyCommunicationProfileTests.cs:43-52`), `WithTimeoutAsync(ShortTimeout = 5 s)` throughout the peer tests, `WithTimeoutAsync(30 s)` in the unanswered-peer foreign-SRDO tests: upper bounds on positive waits only; a host stall above 5 s fails them, nothing is asserted *within* a window.
- `Pairs_Reach_The_Consumer_In_Order`: 200 sequential bus round trips with 5 ms polling — slow (seconds of wall time) but positive.
- No `Stopwatch`, no wall-clock window assertion in the Safety folder.

## 5. Mutations proposed (one-line, exact edit → expected outcome)

| # | File:line | Edit | Expected |
| --- | --- | --- | --- |
| M1 | `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs:30` | delete ` \|\| !rt.ConfigurationValid` from the `return true` condition | **expected green = gap** (S-3): no test sees a consumer with an invalid configuration take a pair |
| M2 | `src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs:105` | `if (!back.AsSpan().SequenceEqual(valid))` → `if (false)` | **expected green = gap** (S-4): the A5h-readback mismatch path has no test |
| M3 | `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs:119` | `if (first.Length < rt.TotalBytes)` → `if (first.Length <= rt.TotalBytes)` | red: `SrdoEngineConsumerTests.Starts_Invalid_And_Becomes_Valid_With_A_Complete_Pair`, `A_Short_Pair_Raises_Emcy_8210h_Once_And_Is_Not_Processed` (the fitting pair), `CanOpenSafetyNodeTests.Producer_And_Consumer_Exchange_An_Srdo` — pins the L = n boundary of §8.1.3.1 |
| M4 | `src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs:92-95` | reverse `AddU32` to little-endian (`value`, `>> 8`, `>> 16`, `>> 24`) **in SrdoCrc only** | red: `SrdoCrcTests.Canonical_Sequence_Follows_The_Listed_Order`; and, because `SrdoRecords.RecordChecksum` keeps MSB-first, `CanOpenSafetyNodeTests.Configure_Commit_And_Transmit` / `Commit_Writes_Checksums_Then_Valid` (13FEh reset to 0 at commit by FR-CO-058) and `CanOpenPeerSafetyTests.Configure_Downloads_Reads_Back_And_Acknowledges` — pins that the two layouts must agree (S-6) and shows how far a one-sided #289 change would be caught |
| M5 | `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs:92` | `_deadlines.Arm(rt.ValidationTime, …)` → `_deadlines.Arm(rt.CycleTime, …)` | red: `SrdoEngineConsumerTests.Srvt_Expiry_Invalidates_And_Drops_The_Pending_Frame` (SRVT 20 ms vs SCT 50 ms), `A_Second_First_Frame_Replaces_The_Pending_One` stays green — pins that the SRVT quantity is sub-index 3 |
| M6 | `src/CanKit.Pro.CANopen/Safety/SrdoEngine.cs:174` | `TimeSpan.TicksPerMillisecond * _nodeId / 2` → `TimeSpan.TicksPerMillisecond * _nodeId` | red: `SrdoEngineProducerTests.First_Cycle_Is_Delayed_Half_A_Millisecond_Per_NodeId`, `Rebuild_Mid_Cycle_Replaces_The_Pending_Cycle`, `Leave_And_Reenter_Mid_Cycle_Starts_Over_With_The_Initial_Delay` — pins the §9.5 0.5 ms × node-id figure |

(S-2 is not pinnable by a mutation — it is missing behaviour; the test in S-2's fix line is the evidence to add.)

## 6. Doc drift (README / XML docs vs behaviour)

1. README `:962-963` — "every transfer it will make whatever the peer answers … refused with nothing sent, not mid-transaction": false for a peer count above the file's highest record (S-2). Either fix the code or narrow the sentence.
2. `docs/packages/index.md:56` "CiA 319 (v2.0.0, tool side)" — device side shipped (#329). `docs/packages/canopen.md` — no safety section at all (S-8).
3. SRS FR-CO-051 (`:350`, last sentence) says the CiA 319 device side is not implemented (#308); FR-CO-058/059 say it is (S-8).
4. `ICanOpenSafety.ConfigurePeerSafetyAsync` XML doc (`ICanOpenSafety.cs:91-95`) repeats the "every transfer … passes the peer-SDO gate before the first frame" claim with the same gap as (1).
5. README `:802-806` / SRS FR-CO-037 state the MSB-first choice correctly and point to #289; nothing to fix there, but S-1 should be added to #289's "known points" as a concrete expected divergence (CANopenNode convention) rather than an unknown.
6. `SrdoState.LastValidAt` XML doc says "UTC" (`SrdoState.cs:27-29`) — correct; the engine comment (`SrdoEngine.cs:34-36`) explains it is wall-clock while everything else runs on the time source. Worth one sentence in the README "Producer/Consumer" sections, since a test driving a `ManualTimeSource` will see `LastValidAt` move with the wall clock.

## 7. What is done well (brief)

- The device/tool split is coherent: the same loader code builds the step-D expectation (`LoadDescribedSafetyObjects`), the same validator runs on the trial copy before a local configuration, and the same `RecordChecksum` serves FR-CO-038/058/059 — the 19-row agreement theory makes the "one rule set" claim measurable.
- The Operational transition under the dictionary's write gate (`ApplyNmtTransition:784-790`) closes the write/arm race precisely and is tested with a positive witness (`WriteGateWaiters`).
- The send chain with a per-period cancellation token, the pending-pair slot and the period end on a periodic-check mismatch are the right shape for "a stale pair is worse than silence", and every one of those claims has a holding-service test.
- CiA 319 §6.2 (optional tx SRVT) is handled in all four combinations (undeclared/read-only × write/readback) with the peer's abort code deciding, and each combination has a test.
- The peer-channel reservation (wait, hold, refuse interleaving) and the one-reading `DescribedObjects` snapshot remove a whole class of mid-transaction races; the tests prove it with held servers rather than sleeps.
- PR descriptions #291/#310/#329 record mutations per claim; the surviving-mutant notes are honest.

## 8. Open questions for the maintainer

1. **#289 priority:** can a reference checksum be obtained (a CANopenNode-based SRD, or a vendor tool's DCF with a filled 13FFh) before EN 50325-5 is? One vector settles S-1 either way.
2. **Plain→inverted gap:** `SendSrdoPairAsync` (`CanOpenNode.Safety.cs:719-723`) hands the inverted frame to the service only after the plain frame's *confirmation* (echo or driver accept). The gap the consumer measures against its SRVT therefore includes the adapter's confirmation latency, not just bus time. Is the README's SRVT guidance meant to say "size the SRVT above your adapter's confirmation latency", or should both frames be queued back-to-back (ordering through the service's own send serialization) with the confirmation awaited afterwards?
3. **Re-verification after a slave reboot:** `_slaveStarted`/`_slaveVerified` are cleared only by `CancelBootUp` (`BootUp.cs:93-108`); a safety slave that reboots on its own (boot-up seen again) is not restarted by this master and hence not re-verified (`ConsiderStart` returns on `_slaveStarted`). That is the boot-up manager's general behaviour, not a safety-specific one — but is it the intended step-D reading, given the slave's §5.4 self-check covers the stored configuration?
4. **GFC in Pre-Operational:** reception is gated on Operational (`TryHandleFrame:23`), listed in #289. If EN 50325-5 has the GFC consumed in every NMT state, the current gate would hide a failsafe command from a node that has left Operational because of an error — the case the GFC exists for. Worth settling together with S-1.
5. **S-7:** is the start-up `OutOfOrder` event wanted as an observable, or should a lone inverted frame before the first pair be silent?
