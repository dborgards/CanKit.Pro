# CanKit.Pro.CANopen — PDO engine, MPDO, foreign PDO observation, device descriptions

Deep review 2026-10-21, `main` @ 36c97ca. Finding prefix **P**. Read-only; nothing was built or run.
All CiA 301 / CiA 306 statements below are from memory of CiA 301 v4.2.0 and are marked "(from
memory)" where a claim rests on them.

## 1. Scope read

Source (every file read completely):

| File | Lines |
| --- | --- |
| `src/CanKit.Pro.CANopen/CanOpenNode.Pdo.cs` | 969 |
| `src/CanKit.Pro.CANopen/CanOpenNode.PdoTiming.cs` | 190 |
| `src/CanKit.Pro.CANopen/CanOpenNode.Mpdo.cs` | 253 |
| `src/CanKit.Pro.CANopen/CanOpenNode.ForeignPdo.cs` | 559 |
| `src/CanKit.Pro.CANopen/ForeignPdo.cs` | 173 |
| `src/CanKit.Pro.CANopen/Pdo/PdoMapping.cs` | 297 |
| `src/CanKit.Pro.CANopen/Pdo/ICanOpenPdo.cs`, `ICanOpenPdoMonitoring.cs`, `PdoBits.cs` | 37 / 46 / 25 |
| `src/CanKit.Pro.CANopen/CanOpenCobId.cs` | 219 |
| `src/CanKit.Pro.CANopen/CanOpenDeviceDescription.cs` | 268 |
| `src/CanKit.Pro.CANopen/CanOpenNode.DeviceDescription.cs` | 1140 |
| `src/CanKit.Pro.CANopen/README.md` (§ The object dictionary is the configuration, § Device descriptions, § Observing a peer PDO, § PDO engine) | 1692 total |

Read in part, where the PDO code depends on them: `CanOpenNode.CommunicationProfile.cs` (`Co`,
`PopulateCommunicationProfile`, the validator dispatch, `OnOdEntryWrittenForCommunicationProfile`,
`RefreshExtendedPdoFilter`, `IsCommunicationPdoTarget`, `PdoFrameKey`, `IsUsablePdoCobIdWord`,
`ApplyCommunicationObject`), `CanOpenNode.cs` (constructor, subscription filter, `HandleIncomingFrame`,
`SendControlFrame`, `SdoUploadAsync`/`SdoUploadCoreAsync`, `RaiseRpdoReceived`),
`ObjectDictionary.cs` (`TryWriteRawLocked`, `Transaction`, `WriteUnsigned`, `Declare`, `Remove`,
`Require`, `DecodeU32`), `OdEntry.cs` (`OdEntryLayout.FixedSize`), `CanOpenNode.PeerSdo.cs`
(`EnsurePeerSdoAccess`), `ICanOpenNode.cs` (the `ConfigureTpdo`/`ConfigureRpdo`/`TriggerTpdoAsync`/
`ObserveForeignPdoAsync` contracts).

Tests (read completely): `CanOpenPdoEngineTests.cs` (1495), `CanOpenPdoCoverageTests.cs` (645),
`CanOpenPdoExtensionsTests.cs` (195), `CanOpenPdoReviewTests.cs` (530), `CanOpenPdoTimingTests.cs`
(351), `CanOpenDynamicMappingTests.cs` (450), `CanOpenForeignPdoObserveTests.cs` (1402),
`CanOpenForeignPdoCoverageTests.cs` (67), `CanOpenForeignPdoExtensionsTests.cs` (124),
`CanOpenDeviceDescriptionTests.cs` (567), `CanOpenDeviceDescriptionPropertyTests.cs` (151),
`Fixtures/device.eds`, `device.dcf`, `pdo-timing.dcf`, `quirky.eds`.

Not read: the EdsDcfNet library (NuGet, no source in the tree). Claims about `$NODEID`
evaluation, `CompactSubObj` expansion and the CLR types `CanOpenValueConverter.Parse` returns are
therefore inferred from how this code consumes them, not verified.

## 2. Status of previous findings

| Finding | Status today | Evidence |
| --- | --- | --- |
| **C13** — EMCY 8210h for every short RPDO frame, no debounce | **Fixed** (once per run of short frames). The second half of the proposed fix — setting/clearing bit 4 of 1001h — was not adopted; see P-8 / open question 2. | PR #263 (`61a5a82`, merge `350df5b`): `CanOpenNode.Pdo.cs:635-649` (`rp.ShortFrameReported`), `CanOpenNode.Mpdo.cs:149-158` for MPDOs. Tests `Rpdo_Shorter_Than_Its_Mapping_Raises_Emcy_Once_Per_Run_Of_Short_Frames`, `Rpdo_Shorter_Than_Its_Mapping_Raises_No_Emcy_While_The_Emcy_Is_Switched_Off`, `Mpdo_Emergencies_Are_Latched_And_Rearmed_By_Recovery`. |
| **#292 / PR #302** — `PdoMapping.Add(PdoMappingEntry)` accepts a zero-width entry; `ConfigureTpdo` then destroys the PDO before failing | **Fixed.** | `8b2aaf5` (merge `b06e71d`): `Pdo/PdoMapping.cs:231-236` (`CheckWidth`) applied in `Add` and the collection constructor. Tests `Mapping_Add_Refuses_An_Entry_Of_A_Width_The_Constructor_Would_Refuse` (4 rows incl. `default`), `A_Configured_Tpdo_Survives_An_Attempt_With_A_Zero_Width_Entry`, `Pdo_Public_Bounds_And_Bit_Mapping_Bounds_Leave_The_Previous_Value_Intact`. |
| **#295 / PR #298** — `ObserveForeignPdoAsync` mixed a live COB-ID with the file's mapping when another SDO call cut in | **Fixed.** | `157e7ad` (merge `4e45f3c`): `CanOpenNode.ForeignPdo.cs:65-104` takes the per-peer observe gate, then the peer SDO channel with `Wait(0)` and holds it to the end; busy at start → every upload is the in-flight refusal → whole snapshot from the file. Test `A_call_cutting_in_mid_snapshot_is_refused_and_the_snapshot_stays_live`, `An_SDO_already_in_flight_falls_back_to_the_file_instead_of_throwing`, `Overlapping_observations_of_one_peer_both_read_the_live_mapping`. |
| **#296 / PR #300** — bound description read without synchronisation against in-place edits | **Closed by documentation (option c), not by code.** The race is still there and still unsynchronised; #299 (OPEN) carries the 2.0 fix (immutable description). | `bf1816b`, `b017d76` (merge `83c25f4`): `CanOpenDeviceDescription.cs:47-57` (XML doc on `Objects`). The readers are unchanged: `TryDescribedObject` at `ForeignPdo.cs:508-517` does single lookups around awaits; `DescribedObjects.Capture` (`DeviceDescription.cs:252-276`) is used by the safety transaction only, not by the PDO observer or the loader. |
| **§3 minor** — `ObserveForeignPdoAsync` costs 9+N uploads and blocks ≥ 9 s on a silent peer | **Resolved.** (a) A local SDO timeout or transport failure marks the whole snapshot unavailable after one failed request (`ForeignPdo.cs:76-93`, `IsPeerUnavailableForPdoSnapshot` :431-433); (b) the peer-SDO gate refuses pairs the bound file does not declare *before* a frame is sent, so undeclared records cost nothing on the bus (and with no bound file every PDO record is refused: `PeerSdoAccessException.IsAllowedWithoutPeerDescription` admits only 1000h/1001h/1018h); (c) the README now names the cost. Residual cost: an online peer that aborts each upload costs up to 2 × max(4, declared) fast round trips — acceptable. | `8956cfb` (PR #313). Tests `Offline_Peer_With_512_Pdos_Uses_One_Failure_Per_Snapshot` (both the timeout and the transport-failure arm), `A_mapping_record_the_bound_description_omits_is_not_read_over_SDO`, `Observe_Extended_Bit_Mapping_And_Mpdo_From_The_File` ("no bound peer description: … without bus traffic"). |
| **C3 leftover** — dummy mapping 0001h–0007h, width of 0001h open ("Frage 10") | **Resolved as a deliberate choice**: `DummyBitLength(0x0001) = 1` bit, 0002h/0005h = 8, 0003h/0006h = 16, 0004h/0007h = 32 (`PdoMapping.cs:56-63`); README "Dummy `0001h:00` consumes one bit". CiA 301 Table 44 defines BOOLEAN as a 1-bit type (from memory), so this is consistent with bit-granular mapping. | PR #313 (`da9c2b9`). Tests `Bit_Mapping_Packs_And_Unpacks_Across_Byte_Boundaries` (maps `0x0001:00` as 1 bit on the wire), `Pdo_Public_Bounds_…` (the `DummyBitLength` table), `Tpdo_Mapping_Entries_Take_Effect_By_Sub_Index_Not_By_Write_Order` and `Rpdo_Dummy_Entry_Skips_Its_Bytes_…` (0005h, with a probe object at the dummy's index). |

## 3. New findings

### P-1 · `ConfigureTpdo` on a node opened from a description whose TPDO record omits sub-index 03h or 05h throws `KeyNotFoundException` after it has destroyed the PDO — Important

- **Where:** `CanOpenNode.Pdo.cs:142-146` (`WritePdoRecords` writes `comm:03` and `comm:05` unconditionally) together with `CanOpenNode.DeviceDescription.cs:697-703` (`ApplyPdoCommunicationRecord` *removes* every implemented sub-index the file does not declare) and `ObjectDictionary.cs:408-436` / `:512-517` (`WriteUnsigned` → `Require` throws `KeyNotFoundException` for a missing entry).
- **Mechanism:** CiA 301 Table 71 makes the inhibit time (03h) and the event timer (05h) optional sub-indices of a TPDO communication record (from memory), and real EDS files leave them out (`Fixtures/quirky.eds` `[1801]` declares `SubNumber=3`, sub-indices 0–2). The loader deletes `1801h:03/05/06` from the dictionary. A later `ConfigureTpdo(2, mapping)` runs the §7.5.2.38 sequence inside `_od.Transaction`: step 1 sets bit 31 (PDO destroyed), the mapping record is rewritten, `comm:02` is written, then `_od.WriteUnsigned(comm, 0x03, inhibit)` throws `KeyNotFoundException`. `WritePdoRecords` catches only `ArgumentException`, `RunOnActorAndWait` rethrows, so the caller sees an undocumented exception type and the TPDO is left destroyed with the new mapping and type already in the record. `ConfigureMpdo` (`CanOpenNode.Mpdo.cs:44-46`) guards exactly these writes with `TryGet`; `ConfigureTpdo` does not.
- **Reachability:** an application calling a public API on a node opened from a conforming EDS/DCF. No hostile peer involved.
- **Severity:** Important (public API, data loss of the previous configuration, wrong exception type). **Confidence:** high — the three code paths are explicit; the fixture that reproduces it is already in the tree.
- **Test coverage:** none. `Pdo_Configuration_Tolerates_Absent_Optional_Communication_Entries` removes 1800h:03/05/06 and then exercises `ConfigureMultiplexedTpdo`, `ConfigureMultiplexedRpdo` and `ConfigureRpdo(2)` — never `ConfigureTpdo`. Every test that calls `ConfigureTpdo` on a described node uses a file whose TPDO record carries 03h and 05h (`device.eds`, `pdo-timing.dcf`, the `Comm(…, tpdo: true)` helper).
- **Fix:** in `WritePdoRecords`, before step 1, refuse with `ArgumentException` a non-zero `inhibit`/`eventTimerMs` for a record that has no 03h/05h, and skip the writes when the sub-index is absent and the value is 0 (the `TryGet` pattern of `ConfigureMpdo`). Same guard for `comm:02` for robustness. Related (minor): `ConfigureTpdo(n)` for a PDO the file does not declare throws `KeyNotFoundException` from `_od.ReadUnsigned(comm, 0x01)` at `:133` before any write — harmless, but the documented exception is `ArgumentException`.

### P-2 · The loader keeps mapping sub-indices 2..64 that the file does not declare — Minor

- **Where:** `CanOpenNode.DeviceDescription.cs:899-912` (`ApplyPdoMappingRecord` re-declares only the sub-indices the file lists and never removes the others), contrast `:697-703` (the communication record *does* remove undeclared implemented sub-indices) and `CommunicationProfile.cs` `PopulateCommunicationProfile` (every mapping record is created with 64 slots).
- **Mechanism:** an EDS declaring `[1A00]` with `SubNumber=2` promises a device with one mapping slot. After loading, `1A00h:02`–`:40h` still exist, upload as 0 and accept writes, and `1A00h:00` accepts a count up to 64. A conformance tool comparing device and EDS flags the device; a master may configure more entries than the EDS says the device supports.
- **Reachability:** every description-loaded node; visible to any master that walks the record.
- **Severity:** Minor. **Confidence:** medium that it is unintended — the comment on the communication record ("the description says which of them exist on this device") shows the intent, the mapping record has no such comment, and removing the slots would break `WritePdoRecords`' loop over all 64 slots (`Pdo.cs:136-139`) unless that loop also tolerates absent slots — which may be why they were kept.
- **Test coverage:** none asserts absence or presence of undeclared mapping slots (`A_Description_Shapes_The_Dictionary_And_The_Runtime` checks `1A00h:00/:01` only).
- **Fix:** remove undeclared slots ≥ 1 in `ApplyPdoMappingRecord` and make `WritePdoRecords` write only slots that exist (refusing a mapping with more entries than slots with `ArgumentException` before step 1). Or document that the node always offers 64 slots.

### P-3 · `TriggerTpdoAsync` transmits a cyclic-synchronous (01h–F0h) or RTR-only (FCh/FDh) TPDO immediately — Minor (behaviour) / Docs

- **Where:** `CanOpenNode.Pdo.cs:562-564` (`TriggerTpdoOnActor`: `else EmitTpdo(rt)` for every type that is neither 00h nor FEh/FFh), `EmitTpdo` `:457-459` (sends unless the SYNC window is closed).
- **Mechanism:** CiA 301 Table 72 (from memory): 01h–F0h "transmitted after the SYNC", FCh/FDh "transmitted only on RTR". The README table says the same ("Transmitted at every SYNC", "nothing answers before the first SYNC", test comment "an RTR-only PDO is never transmitted unrequested"). `ICanOpenNode.TriggerTpdoAsync`'s XML doc, however, promises "the other modes transmit immediately", and the code does: a cyclic TPDO goes out off-cycle (its SYNC counter untouched), an FCh TPDO goes out with a *fresh* sample instead of the SYNC-sampled buffer, an FDh TPDO without an RTR. A consumer that expects SYNC-aligned data sees an unsolicited frame.
- **Reachability:** application call only; the bus side never triggers it.
- **Severity:** Minor — documented in the interface, undocumented in the README (which says the opposite). **Confidence:** high on the mechanism; the "defect" judgement depends on whether the maintainer wants the interface's wording (open question 3).
- **Test coverage:** none in either direction (`Rtr_And_Event_Requests_Keep_Their_Transmission_Semantics_With_A_Window` calls `EmitTpdo` by reflection, not `TriggerTpdoAsync`; `Sync_Window_Discards_Late_Rpdos_…` triggers a 01h TPDO only while the window is closed). Mutation M5 shows the gap.
- **Fix:** treat the trigger as an event: 01h–F0h → ignore (or latch like 00h), FCh/FDh → ignore; align the XML doc and README. If the current behaviour is wanted, say so in the README's transmission-type table.

### P-4 · The per-slot length check counts stale preceding slots — Minor

- **Where:** `CanOpenNode.Pdo.cs:264-274` (`ValidatePdoMappingWrite`, sub-index ≥ 1: `precedingBits` sums every slot `1..subindex-1` as it stands).
- **Mechanism:** the record keeps previous entries while the mapping is disabled (sub0 = 0; `WritePdoRecords` clears them but a master does not have to). A master remapping from {slot1 = 64 bits} to {slot1 = 8, slot2 = 8} that writes slot 2 before slot 1 — the order is free, and `Tpdo_Mapping_Entries_Take_Effect_By_Sub_Index_Not_By_Write_Order` relies on that freedom — is refused with 0604 0042h although the final mapping fits. The final check at sub0 (`:243-254`) already covers "wrong length for the PDO at all".
- **Reachability:** a conforming master, unusual write order over a previously wider mapping.
- **Severity:** Minor. **Confidence:** medium (whether step 3 may check the PDO total at all is a reading of §7.5.2.38 from memory).
- **Test coverage:** none for the stale-slot case.
- **Fix:** drop the preceding-slots check (keep the sub0 check) or count only the slots the *new* count will name (unknown at that point — hence drop).

### P-5 · `HandleMpdo` writes the DAM/SAM target with an unguarded `WriteRaw` — Minor

- **Where:** `CanOpenNode.Mpdo.cs:194-201` (`WriteMpdoTarget`), compare `CanOpenNode.Pdo.cs:676-681` (`ActuateRpdo` catches and reports).
- **Mechanism:** `IsMpdoTarget` reads the entry, then `_od.WriteRaw` re-resolves it. If the application re-declared the object meanwhile with another width, or the target is a managed object with a validator (none of the current `IsCommunicationPdoTarget` entries has one, so today only the re-declaration race), `WriteRaw` throws out of `HandleMpdo` into `HandleIncomingFrame`'s catch → `BackgroundExceptionOccurred`; the frame is dropped, no 8230h, `MpdoTargetReported` not latched, `RpdoReceived` not raised.
- **Reachability:** API misuse (re-declaration while Operational) only.
- **Severity:** Minor. **Confidence:** high on the path, low on practical impact.
- **Test coverage:** none. **Fix:** `TryWriteRaw` and treat a rejection as "target not available" (8230h) or report as `ActuateRpdo` does.

### P-6 · `ConfigureTpdo` / `ConfigureRpdo` leave `1800h:06`, `1400h:03` and `1400h:05` as they were — Minor / Docs

- **Where:** `CanOpenNode.Pdo.cs:126-148` (`WritePdoRecords` writes comm 01/02/03/05 for TPDO, 01/02 for RPDO).
- **Mechanism:** `ICanOpenNode.ConfigureTpdo` says "Values not provided use CiA 301 defaults" and lists COB-ID, inhibit time, event timer. A SYNC start value a master wrote earlier (`1800h:06`) survives a `ConfigureTpdo(…, Synchronous)` and gates the new PDO on a SYNC counter the application never asked for; an RPDO deadline (`1400h:05`) survives a `ConfigureRpdo` and reports timeouts for the new configuration. Both are writable while the PDO is destroyed, so the sequence could reset them.
- **Reachability:** application after a master configured the record over SDO (needs `WritableCommunicationParameters` or a file declaring the record `rw`).
- **Severity:** Minor. **Confidence:** high (code), medium that it is unintended.
- **Test coverage:** `Sync_Start_Waits_Through_Overflow_And_Restarts_On_Operational` writes sub 6 by hand and does not reconfigure; none covers the survival.
- **Fix:** write 0 to `comm:06` (TPDO) and `comm:05` (RPDO) when the sub-index exists, or document that these are left alone.

### P-7 · RPDO: a synchronous RPDO received outside the SYNC window is dropped without restarting the deadline — Docs (no defect)

`CanOpenNode.Pdo.cs:650-651`. Documented ("synchronous RPDOs outside it are discarded, including before the first SYNC"), and the deadline then expires as for a lost frame. CiA 301 §7.5.2.7 defines the window for synchronous PDOs without saying "transmit only" (from memory), so the choice is defensible. Noted only because FR-CO-053 calls it "gewählte optionale Verwerfungspolitik" — the README should say the same so a reader does not take it for a norm requirement.

### P-8 · PDO-related EMCYs never set 1001h bit 4 and never send the 0000h "error reset" — Minor (open question)

`CanOpenNode.Pdo.cs:643-644`, `Mpdo.cs:154/172`, `PdoTiming.cs:142/185`: 8210h, 8230h, 8240h, 8250h are emitted with the error register *as it is*. CiA 301 §7.5.2.2 bit 4 is "communication error" and §7.2.7.3.1 has the device send error code 0000h once the error is gone (from memory; whether a PDO length error is a "communication error" is interpretive). C13's proposal included this half; the fix took the debounce only. Not a defect against the README, which does not claim it; listed as open question 2.

### P-9 · Doc drift in XML comments after #313 — Docs

- `CanOpenNode.Pdo.cs:277-281` (`ValidateMappingEntry` summary): "the width itself must be a byte multiple of at most 64 bits" — the code accepts any 1..64 bits for variable-length types and 1 bit for BOOLEAN (`:287-304`).
- `Pdo/PdoMapping.cs:13-16` and `:49-50`, `CanOpenNode.ForeignPdo.cs:16-17`, `CanOpenNode.CommunicationProfile.cs` class doc: still name `1600h–1603h / 1A00h–1A03h` and "occupies its bytes"; the records now go to `17FFh / 1BFFh` and dummies occupy bits (README is right).
- `ICanOpenNode.ConfigureTpdo` `mapping` parameter: "have the mapped bit length (dummy entries 0001h–0007h excepted)" — a dummy must match its static width too (`:291-292`, 0604 0041h otherwise; README says so).
- README § Observing a peer PDO: "The COB-ID is read live from 1400h:01 / 1800h:01" — only when a peer description is bound that declares the record; without a binding every PDO record is refused by the gate and the whole observation is a file decode (`Observe_Extended_Bit_Mapping_And_Mpdo_From_The_File` relies on it). Worth one sentence.
- README § PDO engine: "The start gate applies to cyclic TPDO types 1..240" — fine, but the XML doc on `HandleSync` cites nothing for why 00h/FCh are exempt (open question 1).

### Checked and found sound (no finding)

- Mapping bit rules: 1..64 bits per entry, ≤ 64 bits total, dummy widths, BOOLEAN 1 or 8 bits, fixed-width types at their full width, communication-area targets only where CiA 301 allows (`IsCommunicationPdoTarget`), `sub0 = 0` while remapping, `41h–FDh` reserved, PDO must be destroyed for any mapping write (stricter than the norm, documented, tests `Mapping_Records_Change_Only_While_The_Pdo_Is_Destroyed`).
- COB-ID bits: bit 31 valid/invalid (idempotent destroy of an unassigned higher PDO accepted), bits 0–29 frozen while valid, bit 30 RTR on TPDO (`RtrAllowed`), ignored on RPDO (`PdoFrameKey` masks it), bit 29 extended frames routed by a separate filter, restricted base CAN-IDs refused, SYNC CAN-ID mutually exclusive with PDOs.
- Transmission types: 00h latch, 01h–F0h counter with SYNC start value, FCh buffer sampled at every SYNC and answered inside the window, FDh fresh sample on RTR, FEh/FFh events + inhibit (100 µs units: `:346`) + event timer (restarts on every transmission, re-armed on each Start), reserved values refused with 0609 0030h, inhibit/SYNC-start immutable while valid, event timer mutable while valid and an inhibited pending event survives the rebuild.
- RPDO deadline (#313): armed on a valid reception only, refreshed per reception, not by a short frame, one-shot, cancelled by reconfiguration and leaving Operational, 8250h gated on `_emcyValid`, callback checks disposal/state/validity.
- MPDO: SAM/DAM address byte, scanner (1FA0h–1FCFh, UNSIGNED32 `block | index | sub`) and dispatcher (1FD0h–1FFFh, UNSIGNED64 `block | local index | local sub | sender index | sender sub | sender node`) layouts match §7.5.2.39/40 (from memory); DAM to node 0 or self only; SAM from node 0 ignored; one SAM producer per node; event-driven types only, no event timer; per-object coalescing queue honouring the inhibit time; 8-byte length rule with latched 8210h/8230h.
- Foreign PDO: TPDO-then-RPDO match order, live-before-file per record, exact widths for live mapping values, cancellation propagates and is never replaced by the file, payload copied before the first await, channel held for the snapshot, one timeout per snapshot.
- Device descriptions: ordering (application objects → PDO comm destroyed → mapping → create), `ParameterValue` over `DefaultValue` (empty `ParameterValue` falls back), `$NODEID` resolved with the opened node-id (a DCF's own `NodeID`), 24/40/48/56-bit types native, unrepresentable types/values become findings instead of silence, a mapping record without its communication record is dropped with a finding.

## 4. Test quality and gaps

Strong: ordering witnesses instead of delays for negatives; `ManualTimeSource` for every inhibit/event-timer/deadline/window assertion; the foreign-PDO suite scripts the peer's SDO server and asserts upload *order* on the peer's own bus; the EDS tests load real fixture files; seeded mutation of the fixtures proves the loader's exception contract.

Gaps:

1. `ConfigureTpdo` on a description-loaded node without 1800h:03/05 (P-1) — add to `CanOpenDeviceDescriptionTests` with `quirky.eds`: `device.ConfigureTpdo(2, new PdoMapping().Add(0x2000, 0, 16))` must succeed and leave `1801h:01` valid.
2. Mapping slots the file does not declare (P-2) — assert `od.TryGet(0x1A00, 2, out _)` after loading `device.eds` once the intended behaviour is decided.
3. `TriggerTpdoAsync` on 01h–F0h/FCh/FDh (P-3) — untested in either direction.
4. Stale-slot length check (P-4): write slot 2 before slot 1 over a previous 64-bit slot 1.
5. `WriteMpdoTarget` exception path (P-5).
6. No test writes an RPDO communication record over SDO with bit 30 set, nor an RPDO comm `1400h:03` (retained "implementation-specific" inhibit) — both accepted silently; a one-row theory would pin it.
7. No test covers a description whose comm record sub-index 00h disagrees with the sub-indices it lists (`:709` overwrites it silently).

Timing-gated tests in this area (per the #92 rule — quantity the host can perturb in brackets):

- `CanOpenDynamicMappingTests.Tpdo_ChangeOfState_DoesNotEcho_On_RpdoUnpack`: `await Task.Delay(300)` proves a negative by waiting [thread-pool and virtual-bus delivery latency; a slow host can only turn a real echo into a false *pass*, not a false fail].
- `CanOpenForeignPdoObserveTests.Overlapping_observations_of_one_peer_both_read_the_live_mapping`: `await Task.Delay(50)` to let the second call queue behind the first [scheduling latency; the assertion holds in either interleaving because the per-peer gate serialises, so the delay only decides *what* is exercised].
- `CanOpenForeignPdoObserveTests.An_SDO_timeout_falls_back_to_the_mapping_in_the_EDS`: wall-clock `SdoTimeout = 200 ms` plus `Stopwatch.Elapsed > 150 ms` [a lower bound on a wall-clock timeout: a loaded host makes it longer, never shorter — safe as written, but the 200 ms timeout itself is the kind of short wall-clock wait #92 lists].
- `CanOpenDynamicMappingTests.Tpdo_EventTimer_Fires_Periodically_Without_Sync`: real 50 ms event timer, waits for ≥ 3 frames [wait, not an assertion about time — safe].
- `CanOpenDynamicMappingTests.Sdo_ServerSession_Expires_With_Abort_When_Peer_Goes_Silent`: `sdoServerTimeout = 150 ms`, read loop with 2 s receive timeout [wait, not an assertion — safe].
- Everything else in `CanOpenPdoEngineTests`, `CanOpenPdoTimingTests`, `CanOpenPdoCoverageTests` runs on `ManualTimeSource` with 5 s upper-bound waits only.

## 5. Mutations proposed

| # | Source change (exact) | Expectation |
| --- | --- | --- |
| M1 | `CanOpenNode.Pdo.cs:640` — `if (_emcyValid && !rp.ShortFrameReported)` → `if (_emcyValid)` | `Rpdo_Shorter_Than_Its_Mapping_Raises_Emcy_Once_Per_Run_Of_Short_Frames` red (pins C13). |
| M2 | `CanOpenNode.DeviceDescription.cs:701` — `_od.Remove(index, sub);` → `_ = sub;` (keep the undeclared comm sub-index) | **expected green = gap**: no test asserts that an undeclared `1800h:03/05/06` is absent after loading (P-1's precondition is not pinned either way). |
| M3 | `CanOpenNode.Pdo.cs:585` — delete `&& _syncCounterOverflow > 1` | `Sync_Start_Is_Ignored_Without_A_Counter_And_Zero_Does_Not_Wait` red (a DLC-0 SYNC never matches start 5). |
| M4 | `CanOpenNode.ForeignPdo.cs:88` — delete `unavailable = ex;` | `Offline_Peer_With_512_Pdos_Uses_One_Failure_Per_Snapshot` red (requests > 1 per snapshot; pins the §3-minor fix). |
| M5 | `CanOpenNode.Pdo.cs:564` — `else EmitTpdo(rt);` → `else return;` | **expected green = gap** (P-3): nothing asserts that `TriggerTpdoAsync` transmits a 01h–F0h/FCh/FDh TPDO. |
| M6 | `CanOpenNode.Mpdo.cs:44` — `if (_od.TryGet(comm, 3, out _)) _od.WriteUnsigned(comm, 3, 0);` → `_od.WriteUnsigned(comm, 3, 0);` | `Pdo_Configuration_Tolerates_Absent_Optional_Communication_Entries` red (`KeyNotFoundException`) — shows the guard that `ConfigureTpdo` lacks (P-1) is what that test pins for MPDOs only. |

## 6. Doc drift (README / XML docs vs behaviour)

- `ICanOpenNode.TriggerTpdoAsync` ("the other modes transmit immediately") vs README § PDO engine table (synchronous: "at every SYNC"; FCh: "the buffered sample answers an RTR") — P-3.
- `ICanOpenNode.ConfigureTpdo` exception contract (`ArgumentException`) vs `KeyNotFoundException` on description-shaped records — P-1.
- `ICanOpenNode.ConfigureTpdo` "Values not provided use CiA 301 defaults" vs `1800h:06`/`1400h:05` kept — P-6.
- XML docs still describing four PDOs, byte-only widths and "bytes" for dummies — P-9.
- README § Observing a peer PDO: live reads happen only with a bound description that declares the record — P-9.
- README § Device descriptions: does not say that undeclared mapping slots stay (P-2) or that the file's comm-record sub-index 00h value is replaced by the node's own highest (`DeviceDescription.cs:709`).
- README § PDO engine, synchronous window: "synchronous RPDOs outside it are discarded" could add that this is a chosen policy (FR-CO-053 does) — P-7.

## 7. What is done well

The dictionary-is-the-configuration design holds end to end: every API call, SDO download and file value goes through one validator with the norm's abort codes, and the runtime is rebuilt from the records, so the tests can configure a PDO three ways and observe one wire. The #313 work is careful about the hard cases — SYNC counter commit after confirmation, window re-check at the driver hand-off, start-value gate, inhibited events surviving a rebuild, deadline retirement on every exit path — and each has a virtual-clock test with an ordering witness. The foreign-PDO observer's snapshot semantics (#295/#313) are now precise and the README states them. The loader degrades loudly: every value it cannot take becomes a finding with the abort code a master would have seen.

## 8. Open questions for the maintainer

1. **SYNC start value for 00h and FCh** (`Pdo.cs:584-589`, commit `e35edd7`): §7.5.2.37 sub-index 06h defines the start value "for this PDO" without limiting it to cyclic types (from memory). Was exempting acyclic and RTR-only TPDOs a reading of the norm text or a convenience? If the latter, the README should say it is a choice.
2. **Error register / error reset** (P-8): should 8210h/8230h/8240h/8250h set bit 4 of 1001h, and should an 0000h EMCY follow when the run ends (frame fits, deadline met)? C13 proposed it; the fix took the debounce only.
3. **`TriggerTpdoAsync` on synchronous and RTR-only TPDOs** (P-3): keep "transmit immediately" (then the README table and the Table-72 wording in the code comments need a caveat) or make the trigger an event only (latch for 00h, ignore for 01h–F0h/FCh/FDh)?
4. **Mapping slots vs the EDS** (P-2): should a description-loaded node expose only the mapping sub-indices the file declares, with `ConfigureTpdo`/`ConfigureRpdo` refusing a mapping with more entries than slots? That pairs with the P-1 fix.
5. **RPDO bit 30**: CiA 301 Table 66 carries the RTR bit for RPDOs as well (from memory); the node ignores it on RPDOs. Fine to leave, but should the validator reject a set bit 30 on an RPDO record (it has no meaning for a consumer), or is silent acceptance the intended tolerance?
6. **Synchronous window on reception** (P-7): is the RPDO discard a norm reading or a chosen policy? FR-CO-053 says the latter; the README should match.
