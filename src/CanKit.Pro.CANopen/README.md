# CanKit.Pro.CANopen

Status: **1.3.0 is the first stable release**: from 1.3.0 on the public API follows SemVer, so a breaking
change costs a major version. 1.0.0 – 1.2.3 were published as stable before the API had been reviewed
against the specifications; they are unlisted and deprecated on nuget.org and should not be used. See
[Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

## What is validated, and what is not

**Validated:** The communication profile as this repository reads CiA 301 — SDO server and client, PDO, NMT, heartbeat, node and life guarding, SYNC, EMCY, EDS/DCF loading — CANopen Safety as it reads CiA DSP 304 V1.0 and, for the configuration entity, CiA 319 v2.0.0, and LSS master, slave and commissioning as it reads CiA 305 v3.0.0, by the test suite in `tests/CanKit.Pro.Tests`, between instances of this implementation and against raw frames the tests send, over `CanKit.Adapter.Virtual`. The LSS master is also validated against a scripted slave in that suite, not against a third-party LSS slave.

**Not validated:** CiA 301 conformance as a tester would judge it, and any real CANopen device or third-party master. Device descriptions are tested against files and descriptions written for the tests, not against ones shipped by real devices. Nothing in this package has run against real CAN hardware, a conformance tester or a third-party implementation: the test project references `CanKit.Adapter.Virtual` and no hardware adapter.

**CANopen (CiA 301)** node implementation for CanKit.Pro. Provides an in-process
`ICanOpenNode` whose object dictionary carries the CiA 301 communication profile and drives the
node's behaviour: an SDO server and client, a PDO engine with every transmission type of
Table 72, an NMT slave state machine, heartbeat producer/consumer, node guarding and life
guarding, a SYNC producer/consumer and structured EMCY — all composed on the CanKit.Pro L2
pipeline (`ICanBusService` / `IProtocolActor` / `DeadlineScheduler`), exactly like
`CanKit.Pro.IsoTp`, `CanKit.Pro.J1939Tp` and `CanKit.Pro.Uds`.
CANopen Safety (CiA DSP 304 V1.0) is available opt-in through `node.Safety()` — see
[CANopen Safety](#canopen-safety-cia-304).

One `ICanOpenNode` serves both roles a CANopen application takes: the **device** (its own object
dictionary, the PDOs it produces and consumes, configurable by a master over SDO) and the **tool
or master** (SDO client, NMT master, heartbeat and node-guarding consumers, SYNC producer). Two
or more nodes may share one `ICanBusService`, so a master and simulated slaves can coexist on a
virtual bus in one process.

## Coverage

This is a **subset of CiA 301**, not a complete implementation of it. What the subset covers is
every requirement of SRS §4.3.2 — the requirements this repository set itself. FR-CO-013 to
FR-CO-024 are the device-role scope the maintainer decided on 16 September 2026
(`docs/reviews/2026-09-15-canopen-scope.md`), FR-CO-025 to FR-CO-028 the EDS/DCF path of that
scope (#132); FR-CO-029 to FR-CO-034 are the master and tool role decided for #131
(`docs/reviews/2026-09-26-canopen-master-tool-scope.md`); FR-CO-035 to FR-CO-046 are CANopen
Safety after CiA DSP 304 V1.0, decided on 9 October 2026
(`docs/reviews/2026-10-09-canopen-safety-scope.md`). FR-CO-047 is the LSS master after CiA 305
v3.0.0. FR-CO-048 is the LSS slave in the node, including the pending node-id and
`LSS_Supported` in a device description. FR-CO-049 is commissioning on the master: Fastscan,
node-id assignment, bit timing, store and inquire. FR-CO-050 is CiA 801 automatic bit-rate
detection, the procedure behind table 0 index 9. FR-CO-051 is the configuration entity after CiA 319 v2.0.0 (17 October 2026). FR-CO-052 to FR-CO-057 add the PDO extensions. FR-CO-058 and FR-CO-059 are the device side of CiA 319 (20 October 2026). The SRS's "Quelle"
column says for each of them whether a standard requires it, the architecture does, or a
maintainer decision does.

| SRS id | Feature |
| --- | --- |
| FR-CO-001 | Local object dictionary with typed read/write, data-type enforcement and per-entry PDO mappability |
| FR-CO-002 | SDO expedited transfer (payloads ≤ 4 bytes) |
| FR-CO-003 | SDO segmented transfer (payloads > 4 bytes, toggle-bit protocol) |
| FR-CO-004 | SDO block transfer (CiA 301 §7.2.4.3.15) — client + server, download + upload, blksize negotiation, optional CRC-16/XMODEM |
| FR-CO-005 | TPDO/RPDO mapping held in the mapping records `1600h`–`17FFh` / `1A00h`–`1BFFh`: bit-granular, up to 64 bits, written through `ConfigureTpdo`/`ConfigureRpdo` or over SDO by the CiA 301 §7.5.2.36/§7.5.2.38 re-mapping procedure |
| FR-CO-006 | Event-driven, timer-driven and SYNC-triggered TPDO transmission; change-of-state emission on application OD writes (`CanOpenNodeOptions.EnableChangeOfStateTpdo`, default on; bus-originated writes never re-trigger, so no echo loops) |
| FR-CO-007 | NMT master + slave state machine (Start / Stop / Pre-Op / Reset Node / Reset Communication) |
| FR-CO-008 | Heartbeat producer + consumer timeout event |
| FR-CO-009 | Node guarding (CiA 301 §7.2.8.3.2.1) — RTR-based consumer + producer, life-time timeout event |
| FR-CO-010 | SYNC producer and consumer |
| FR-CO-011 | EMCY encode/decode + structured receive event |
| FR-CO-012 | Uses the L2 `ICanBusService` demux (subscription with COB-ID filter) |
| FR-CO-013 | Communication-profile objects created at their CiA 301 defaults (`1000h`, `1001h`, `1018h`, `1005h`, `1006h`, `1007h`, `1019h`, `100Ch`, `100Dh`, `1010h`, `1011h`, `1014h`, `1016h`, `1017h`, `1200h`, four RPDO and four TPDO records by default); PDO records read-only on the bus unless `WritableCommunicationParameters`; sub-index `04h` of the PDO communication records absent (`0609 0011h`) |
| FR-CO-014 | The object dictionary is the single source of truth: an accepted SDO download and a local write reach the service alike, the configuration methods are OD writes, managed objects cannot be re-declared |
| FR-CO-015 | TPDO transmission types `00h`, `01h`–`F0h`, `FCh`, `FDh`, `FEh`/`FFh`; reserved values rejected with `0609 0030h` |
| FR-CO-016 | Inhibit time `1800h:03` (100 µs units, minimum interval) and event timer `1800h:05` (ms, maximum interval) for event-driven TPDOs |
| FR-CO-017 | Synchronous RPDOs (`1400h:02` = `00h`–`F0h`): received data actuated with the next SYNC |
| FR-CO-018 | COB-ID control bits valid/RTR/frame in `1400h:01`, `1800h:01`, `1005h` and `1014h`; restricted CAN-IDs of Table 40 rejected; PDOs support base and extended frames |
| FR-CO-019 | Reset Node / Reset Communication restore the power-on values; `1010h:01` "save" and `1011h:01` "load" (in-memory); wrong signature `0800 0020h` |
| FR-CO-020 | Mapping validation: existence, mappability, direction, width, bit lengths, 64-bit limit, sub-index position, `sub0` as UNSIGNED8, dummy mapping `0001h`–`0007h` |
| FR-CO-021 | Producer-side life guarding from `100Ch`/`100Dh` with `LifeGuardingEvent` (occurred / resolved); heartbeat takes precedence |
| FR-CO-022 | NMT state semantics of Table 37: no SDO, SYNC or EMCY in Stopped, PDO only in Operational, state-change heartbeat only with the producer on, guarding toggle reset on reset |
| FR-CO-023 | Heartbeat consumer and producer from `1016h`/`1017h`; `1016h` grows per consumer; duplicate node-id `0604 0043h` |
| FR-CO-024 | RPDO length handling: too short → not processed and EMCY `8210h`; too long → the first bytes are used |
| FR-CO-025 | A node opened from an EDS or DCF (CiA 306, read with `EdsDcfNet`): application objects with type, access, `PDOMapping` and value from the file, the managed communication objects through the validated write path, PDO records in the §7.5.2.38 order, undeclared PDOs absent, `$NODEID` evaluated, a DCF's `ParameterValue` and `NodeID` honoured |
| FR-CO-026 | Whatever the node cannot take as written is degraded, corrected in the dictionary and reported per entry in `DeviceDescription` (omitted / corrected / PDO disabled / not implemented / default supplied, with the SDO abort code that decided) |
| FR-CO-027 | The file's access rights govern the bus: `rw` PDO records are writable without `WritableCommunicationParameters`, `ro` stays `ro`; its `PDOMapping` attribute is the object's mappability |
| FR-CO-028 | The described values are the power-on values: "load" and Reset Communication return `1000h`–`1FFFh` to them (or to the last "save"), Reset Node the application objects too |
| FR-CO-029 | The SDO client transfers only what a peer EDS/DCF bound for the server declares, plus the SRDO records up to the file's highest one (CiA DSP 304 §8.4.2.2); without one, only `1000h:00`, `1001h:00` and `1018h:00`–`04h` ([SDO client and a peer's device description](#sdo-client-and-a-peers-device-description)) |
| FR-CO-030 | `ObserveForeignPdoAsync` splits a peer's PDO with its live COB-ID and mapping, falling back to the peer file ([Observing a peer PDO](#observing-a-peer-pdo)) |
| FR-CO-031 | NMT flying master election, CiA 302-2 v4.1.0 ([Flying master](#flying-master)) |
| FR-CO-032 | Boot-up of the slaves in `1F81h` by the active master, `1F80h`, `1F82h`, `1F89h` ([Boot-up](#boot-up)) |
| FR-CO-033 | Listen-only discovery: heartbeat or boot-up within a caller-chosen window, nothing transmitted ([Discovery](#discovery)) |
| FR-CO-034 | Scan on request: one SDO upload of `1000h:00` per node-id not already found ([Discovery](#discovery)) |
| FR-CO-035 | Safety objects `1300h`, `1301h`–`1340h`, `1381h`–`13C0h`, `13FEh`, `13FFh` as managed objects at the DSP 304 §8.4.2.2 defaults, only with `SrdoCount` > 0 or SRDO records in a description; reset, store and load like the communication objects ([The objects](#the-objects)) |
| FR-CO-036 | Every write to them validated: value ranges, `0800 0022h` in Operational, delete before change, plain/inverted pairs, at most 8 bytes, COB-ID collisions; `13FEh` back to 0 on every parameter write ([The objects](#the-objects)) |
| FR-CO-037 | The `13FFh` checksum of §8.4.2.2 as CRC-16/XMODEM over the fields MSB-first; `SrdoCrc` is public ([Configuring and committing](#configuring-and-committing)) |
| FR-CO-038 | At the transition to Operational, per SRDO: `13FEh` = `A5h` and a matching checksum, else `ConfigurationInvalid` and nothing sent ([Configuring and committing](#configuring-and-committing)) |
| FR-CO-039 | Producer: cycle = refresh time, first cycle after 0.5 ms × node-id, plain and inverted frame as one unit, `TriggerSrdoAsync` and change of state send at once and restart the cycle ([Producer](#producer)) |
| FR-CO-040 | Consumer: order, inversion, SRVT, SCT, length (EMCY `8210h` once per run), writing the mapped objects, `SrdoReceived` / `SrdoStateChanged`, re-validation by the next valid pair ([Consumer](#consumer)) |
| FR-CO-041 | Global failsafe command: sent and reported only with `1300h` = 1 in Operational, DLC 0 ([GFC](#gfc)) |
| FR-CO-042 | `ICanOpenSafety` through `node.Safety()`, without widening `ICanOpenNode`; configuration, delete, commit, state, options ([Opening a safety node](#opening-a-safety-node)) |
| FR-CO-043 | EDS/DCF: the safety objects loaded like the PDO records, with findings; the file's highest SRDO record raises `SrdoCount` ([Opening a safety node](#opening-a-safety-node)) |
| FR-CO-044 | `ConfigurePeerSafetyAsync` after §9.2 (write, read back, compare, `A5h` only without a difference) and `VerifyPeerSafetyConfigurationAsync` after §8.3.1 step D, both through the peer-SDO gate ([Configuring a peer](#configuring-a-peer)) |
| FR-CO-045 | Step D in the boot-up: a DCF-bound safety slave is verified before NMT Start, and no Start goes to node 0 while one is assigned; a failure signals `SlaveSafetyConfigurationInvalid` and the slave is not started; a mandatory slave or a simultaneous start halts the boot ([Verifying, and the boot-up](#verifying-and-the-boot-up)) |
| FR-CO-046 | `ObserveForeignSrdoAsync`: live record before file, pair check, split into an `IForeignPdoSink` as `ForeignPdoKind.Srdo`, no timing judged ([Observing a peer SRDO](#observing-a-peer-srdo)) |
| FR-CO-047 | LSS master (CiA 305): switch state global and selective, configure node-id and bit timing, activate with both switch delays, store, inquire identity and node-id, identify remote and non-configured slaves, Fastscan ([Layer setting services](#layer-setting-services-cia-305)) |
| FR-CO-048 | LSS slave in the node: waiting and configuration, identity from `1018h`, pending node-id applied on NMT reset communication, unconfigured `FFh` does not boot, store through `ILssConfigurationStore`, `LSS_Supported` in an EDS/DCF ([Layer setting services](#layer-setting-services-cia-305)) |
| FR-CO-049 | LSS commissioning on the master: Fastscan of unconfigured slaves, batch and single node-id assignment, bit timing, store, inquire, and `1018h` of configured nodes ([Layer setting services](#layer-setting-services-cia-305)) |
| FR-CO-050 | CiA 801 automatic bit-rate detection: listen at each candidate in listen-only mode, a received frame confirms the rate, errors or a degraded bus state rule it out, the table is walked over the passes allowed ([Automatic bit-rate detection](#automatic-bit-rate-detection-cia-801)) |
| FR-CO-051 | `ConfigurePeerSafetyAsync` as the configuration entity of CiA 319 v2.0.0: the identity of §5.2 first, the steps of §5.3 with the storage of step 5 (`PeerSafetyConfiguration.Storage`, `PeerSafetyResult.Stored`), and the §6.2 default for the SRVT of a tx SRDO the peer does not implement, in the verification too ([Configuring a peer](#configuring-a-peer)) |
| FR-CO-052 | SYNC counter (`1019h`), producer wrap/reset and one-time TPDO start gate (`1800h:06`) |
| FR-CO-053 | Synchronous window (`1007h`) for RPDO acceptance and TPDO driver hand-off |
| FR-CO-054 | RPDO deadline (`1400h:05`), one-shot application indication and EMCY `8250h` |
| FR-CO-055 | Bit mapping, one-bit BOOLEAN/dummy and native 24/40/48/56-bit integer widths |
| FR-CO-056 | Configurable PDO count up to 512 per direction and 29-bit PDO identifiers |
| FR-CO-058 | The device side of CiA 319: at the write of `A5h` the checksums of the existing SRDOs are compared and a mismatch sets `13FEh` back to `00h` at once (§5.3 step 4); the same after a reset, before the boot-up (§5.4) ([Configuring and committing](#configuring-and-committing)) |
| FR-CO-059 | `SrdoConfigurationCheckInterval`: the same check repeated periodically (§5.3, opt-in); a mismatch in Operational makes every SRDO `ConfigurationInvalid` ([Configuring and committing](#configuring-and-committing)) |
| FR-CO-057 | SAM/DAM MPDO, scanner/dispatcher blocks and inhibit queue |

## Not built, and why

Deliberate omissions, each checked against the norm text:

* **TIME (`1012h`)** — `Category: Optional`; no clock service is implemented behind it.
* **SRDO mapping of objects without a fixed width** — a `VISIBLE_STRING`, `OCTET_STRING`,
  `UNICODE_STRING` or `DOMAIN` cannot be mapped into an SRDO (`0604 0041h`; a finding for a
  device description; refused before the first frame of a peer configuration). DSP 304 has no
  such clause; it is this implementation's rule: safety process data has a fixed width, and the
  width is what the mapping entry and the checksum pin. A PDO may still map them.
* **Non-volatile storage behind `1010h`** — "save" takes the current communication parameters
  as the power-on values a reset restores, for the lifetime of the process. A device whose
  configuration must survive a restart persists it itself and replays it after `OpenNode`.
* **29-bit SYNC, EMCY and SRDO COB-IDs** — these services remain base-frame-only;
  PDO communication records support extended identifiers through bit 29.
* **The DLC-1 guarding RTR** — CiA 301 Figure 44 draws the node-guarding request with DLC 1.
  CanKit derives a frame's DLC from its data and refuses data on a remote frame, so the RTR this
  node sends carries DLC 0 (#59). Producers answer a guarding RTR by CAN-ID; what the consumer
  evaluates is the reply.
* **Reset Node vs. Reset Communication without a device description** — both restore the same
  set, the communication-profile objects, because the node then has no source for the power-on
  values of the application objects; the application restores those itself from
  `ApplicationReset`, which runs before the boot-up goes out. With a description the two differ
  as CiA 301 says (see below).
* **The pst > 0 fallback from block to segmented transfer** — `pst = 0` is forced, which CiA 301
  §7.2.4.3.13 defines as "change of transfer protocol not allowed".
* **Diverse redundancy and the safe state (CiA DSP 304 §9.5, §8.3.2)** — this library is not
  developed to IEC 61508 / DIN V VDE 0801 and claims no safety integrity level. It builds the
  inverted frame from the plain one and compares bit by bit on reception; building the pair
  "by two different ways" and entering the safe state are the device's.
* **SRDO details V1.0 leaves open** — the checksum's initial value and byte order (CRC-16/XMODEM,
  MSB-first here) and the `ro`/`rw` conflict of `13FFh` (`rw` here, as the records are: always
  locally, on the bus with `WritableCommunicationParameters` or a file that declares it `rw`;
  §9.2 needs the tool to write it). To be reconciled against EN 50325-5:2010 in #289.
* **Bit-granular SRDO mapping and more than 8 objects per SRDO** — the safety mapping retains its own narrower limits; the PDO extensions do not widen them.
* **The safety application layer of CiA 319 clauses 7 and 8** — the *Safety application
  configuration valid* and *… signature* objects a CiA profile or a manufacturer-specific profile
  places in its own area, and the data set they sign. Which objects, in which order, is the
  profile's to say, and no profile with safety provisions is implemented here. What they share
  with the SRDO signature is the polynomial: `SrdoCrc.Crc16Xmodem`, suitable for up to 2048 bytes,
  with the choices V1.0 leaves open (#289).
* **Timing in `ObserveForeignSrdoAsync`** — the caller holds the timestamps and judges SRVT and
  SCT.
* **Automatic bit-rate detection as an LSS command** — table 0 index 9 names CiA 801 detection,
  but configure bit timing cannot make a device run it: the master only sends the index, and the
  slave here rejects index 9 (`BitTimingNotSupported`), because it cannot reopen its own channel
  at another rate on command. The CiA 801 procedure itself is built as a separate call,
  [`LssBitRateDetection`](#automatic-bit-rate-detection-cia-801), which a device runs at startup
  before it has a node-id.
* **CiA 801 single-dominant-bit timing (§5.2.2), the external AND/OR-gate variant (§5.2.4), and
  the default fallback bit rate (§5.1)** — the first needs a timer on the receive line that no
  adapter exposes, the second is hardware, and the third is the approach the report rejects.
  `LssBitRateDetection` is the listen-only method of §5.2.3 only.
* **Switching the open channel's bit rate** — `ActivateBitTimingAsync` waits both switch-delay
  periods and, when the caller names a rate and sets `LssMasterOptions.BitRateSwitch`, calls
  that callback between them. The callback must retune the channel this master is already
  reading; it must not dispose that bus. The second delay, and a waiting frame after a zero
  delay, still go out on it. An adapter that cannot change rate while open is left alone:
  omit `returnToWaiting`, dispose this master after the call returns, and send switch state
  global from a new master on the new bus.

What a device description declares beyond this — a TIME service behind `1012h` —
is not built either; [Device descriptions](#device-descriptions-eds-dcf) says what the node does
with such an entry instead of ignoring it.

## The object dictionary is the configuration

`OpenNode` creates the CiA 301 communication profile in the node's object dictionary, and the
flying-master objects, at their defaults: `1000h` (device type 0), `1001h` (error register), `1018h` (sub0 = `01h`,
vendor-id 0 = "no vendor-ID assigned"), `1005h`/`1006h` (SYNC on `080h`, not generated),
`100Ch`/`100Dh` (life guarding off), `1010h`/`1011h` (store / restore), `1014h` (EMCY on
`080h` + node-id, valid), `1016h`/`1017h` (heartbeat off), `1F80h`/`1F81h`/`1F82h`/`1F89h`/`1F90h`
(flying master off, no slave assigned, boot timeout 0; see [Flying master](#flying-master)),
`1200h` (the default SDO server,
constant) and four RPDO plus four TPDO records (`1400h`–`1403h`, `1600h`–`1603h`,
`1800h`–`1803h`, `1A00h`–`1A03h`) at their pre-defined connection set CAN-IDs, "not valid" until
configured. `1000h` and `1018h` are placeholders the application replaces with `AddU32` — they
stay outside the PDOs: those objects are not mapping targets, whatever an entry's flag says.
Other communication objects are mappable where CiA 301 explicitly permits it, as described below. The other objects are managed by the node and take values, not
re-declarations — an `Add*` on one
of them throws `InvalidOperationException`, whether it would replace a sub-index or add one the
node does not implement (`1016h` grows through `AddHeartbeatConsumer`, not by hand).

**API calls are OD writes.** `StartFlyingMaster` writes `1F90h:03` and bits 0 and 5 of `1F80h`;
`StartHeartbeatProducer` writes `1017h`; `AddHeartbeatConsumer`
writes a sub-index of `1016h`, growing the array when every slot is taken; `StartSyncProducer`
writes `1006h` and bit 30 of `1005h`; `SendEmcyAsync` writes `1001h`;
`ConfigureTpdo`/`ConfigureRpdo` run the §7.5.2.38 re-mapping procedure over the PDO records.
What a master reads over SDO is therefore what the node does, and a value the node accepts from
a master takes effect the same way.

**Both write paths are validated alike.** Every write to a managed object — an SDO download or a
local `ObjectDictionary.WriteRaw`/`WriteUnsigned` — is checked against the norm's value rules
before it is stored, and the runtime is rebuilt from the dictionary afterwards. A rejected SDO
download is answered with the CiA 301 abort code; a rejected local write throws
`ArgumentException` naming the same code. The codes in use:

| Abort code | Raised for |
| --- | --- |
| `0601 0000h` | Writing a mapping entry while the mapping is enabled (`sub0` ≠ 0) |
| `0601 0002h` | Download to a read-only object: the PDO records without `WritableCommunicationParameters`, `1200h`, `1000h`, `1001h`, `1018h`, `1F81h:00`, `1F82h:00`, `1F90h:00` |
| `0602 0000h` | Mapping entry whose target object does not exist; `sub0` enabling a mapping with an empty slot |
| `0604 0041h` | Target not PDO-mappable, not accessible in the PDO's direction, or of another width than mapped; dummy entry with the wrong width |
| `0604 0042h` | Mapping longer than 8 bytes |
| `0604 0043h` | Second `1016h` entry for a node-id already monitored |
| `0607 0010h` | Mapping bit length 0 or above 64 |
| `0607 0012h` / `0607 0013h` | Download of the wrong width for the object — `sub0` of a mapping record is UNSIGNED8 |
| `0609 0011h` | Sub-index `04h` of a PDO communication record, or any other absent sub-index |
| `0609 0030h` | Reserved transmission type; bit 29 set on SYNC/EMCY; bit 30 of `1014h` set; restricted CAN-ID; CAN-ID changed while the object is valid; inhibit time or SYNC start changed while the PDO is valid; reserved mapping count `41h`–`FDh`; flying-master priority above 2; device time slot 0; a priority time slot that is not greater than 127 times the device time slot; a `1F82h` request that is not a known NMT state, or that names a node which is not assigned |
| `0800 0020h` | Wrong signature written to `1010h:01` / `1011h:01` |
| `0800 0022h` | SDO server session open when the node entered Stopped or was reset; `1F82h` written while this node is not the active NMT master |

**Read-only by default.** `CanOpenNodeOptions.WritableCommunicationParameters` (default
`false`) decides whether a master may write the PDO communication and mapping records over SDO.
Off, the records are `ro` on the bus: the application configures its PDOs through
`ConfigureTpdo`/`ConfigureRpdo`, and the records describe that configuration truthfully without
offering to change it. CiA 301 permits exactly this for PDO parameter records — footnote `*` of
its objects overview: "These may be ro" — and for nothing else, which is why the SYNC, EMCY,
heartbeat and guarding objects are `rw` in either case, as their definitions prescribe, and
`1200h` is constant. On, every accepted write takes effect in the PDO engine.

**Power-on values.** An NMT Reset Node or Reset Communication returns the managed objects to
their power-on values: the values last stored, or the defaults the node was created with if
nothing was stored. `StoreParameters()` is the local equivalent of a master writing "save" to
`1010h:01`; `RestoreDefaultParameters()` of "load" to `1011h:01` — per §7.5.2.14 the defaults
become valid with the next reset, not before. A device configured through
`ConfigureTpdo`/`ConfigureRpdo` calls `StoreParameters()` once its configuration is complete;
otherwise a Reset Communication from the master undoes it.

**Units** are the norm's: `1006h` in µs; `1016h`, `1017h` and `100Ch` in ms; `1800h:03` in
multiples of 100 µs; `1800h:05` in ms. The `TimeSpan` arguments of the API are converted and
range-checked against the object's UNSIGNED width.

## Device descriptions (EDS/DCF)

A node can be opened from a CiA 306 device description — an **EDS**, or a **DCF** for a
commissioned device — read with [`EdsDcfNet`](https://github.com/dborgards/eds-dcf-net):

```csharp
var description = CanOpenDeviceDescription.Load("device.eds");
using var device = CanOpen.OpenNode(bus, nodeId: 0x11, description);

// A DCF carries its NodeID, so the node-id may be left to the file:
using var commissioned = CanOpen.OpenNode(bus, CanOpenDeviceDescription.Load("device.dcf"));

foreach (var finding in device.DeviceDescription!.Findings)
    Console.WriteLine(finding);            // what the node could not take as written
```

The description shapes the object dictionary, and the dictionary drives the node as it always
does, so a master sees the described device from its first frame:

* **Application objects** are created with the file's data type, access, `PDOMapping` attribute
  and value — a DCF's `ParameterValue` over the `DefaultValue` of the EDS it was made from.
  `$NODEID+…` expressions are evaluated against the node-id the node is opened with.
* **The managed communication objects** (`1000h`, `1001h`, `1005h`, `1006h`, `100Ch`, `100Dh`,
  `1014h`, `1016h`, `1017h`, `1018h`, `1F80h`, `1F81h`, `1F89h`, `1F90h`) take the file's access and value through the same
  validated write path an SDO download uses, so a value the norm would reject on the bus is
  rejected here too. A mandatory object the file omits keeps its placeholder. `1F82h` is the
  exception: a described value is stored as the initial tracked NMT state and is not sent as a
  command.
* **PDO records** are applied in the order of §7.5.2.38 — communication parameters with the PDO
  destroyed, then the mapping, then "create PDO" — and a PDO the file does not declare does not
  exist (`0602 0000h`). The file's access rights on the records hold on the bus: a record it
  declares `rw` is writable by a master without `WritableCommunicationParameters`, one it
  declares `ro` is not.
* **The described values are the power-on values.** "Load" (`1011h`) and Reset Communication
  return `1000h`–`1FFFh` to them, or to the last "save"; Reset Node restores the application
  objects as well, which without a description the node cannot do.

What the node cannot implement as written is **degraded, corrected in the dictionary and
reported** — never ignored. `ICanOpenNode.DeviceDescription` is a `DeviceDescriptionReport`:
`IsExact` is true when every entry was taken as written, and each `DeviceDescriptionFinding`
names the entry, the outcome, the reason, the described value and, where an SDO rule decided,
its abort code:

| Outcome | Meaning |
| --- | --- |
| `Omitted` | Not created: a data type the dictionary does not represent, an unparsable value, a sub-index of a fixed record the node does not implement |
| `Corrected` | Created with the CiA 301 default instead of the described value, which the rule an SDO download hits rejected (a reserved transmission type, a second `1016h` entry for one producer) |
| `PdoDisabled` | The PDO stays destroyed (bit 31 set) because its communication record or its mapping could not be applied — an invalid COB-ID, a mapping onto an object the file does not declare |
| `NotImplemented` | Created with the described value as data, with no behaviour behind it: `1012h` (TIME), a `1200h` that differs from the default SDO server |
| `SuppliedDefault` | A mandatory object (`1000h`, `1001h`, `1018h`) the file does not declare; the node keeps its placeholder |

The parsed model is available as `CanOpenDeviceDescription.Objects` (the `EdsDcfNet` object
dictionary) and `DeviceInfo`, and `ParseDiagnostics` lists what the parser repaired while reading
a lenient file. This loader shapes the node it is given to. A *foreign* device's file is bound
with `BindPeerDeviceDescription` instead, as the list of objects the SDO client may transfer
([below](#sdo-client-and-a-peers-device-description)). Writing a whole DCF into a foreign device
is not built.

## SDO client and a peer's device description

One transfer per server is in flight at a time: a call to a server with a transfer in flight
fails with `InvalidOperationException` ("already in flight") before anything is sent. A safety
transaction with that server (`ConfigurePeerSafetyAsync`, `VerifyPeerSafetyConfigurationAsync`)
counts as a transfer in flight for its whole length, and so does the live snapshot of
`ObserveForeignPdoAsync` ([Observing a peer PDO](#observing-a-peer-pdo)) and of
`ObserveForeignSrdoAsync` ([Observing a peer SRDO](#observing-a-peer-srdo)). Calls to different
servers are independent.

`SdoUploadAsync` and `SdoDownloadAsync` do not accept an arbitrary index. Before any frame is
sent they check a peer EDS or DCF bound for that server:

```csharp
var peer = CanOpenDeviceDescription.Load("remote.eds");
master.BindPeerDeviceDescription(nodeId: 0x11, peer);

await master.SdoUploadAsync(0x11, 0x2000, 0x00);   // only if 2000h:00 is in remote.eds
```

A DCF is commissioned for one node-id. Binding it to a different node throws
`ArgumentException` and leaves any description already bound for that node in place. An EDS
has no commissioned node-id and may be bound to any server.

`CanOpenDeviceDescription.Contains` is that check. A pair the file does not declare throws
`PeerSdoAccessException` (`PeerDescriptionLoaded` is true), including `1000h`, `1001h` and
`1018h` when the file leaves them out. `UnbindPeerDeviceDescription` drops the binding.

A bound description may be edited in place — `Objects` is the EdsDcfNet model, mutable — and
the edit is seen from the next call that reads it. But nothing synchronizes those readers with
the edit: the gate reads the description on every SDO call to that peer, the loader while a node
is being opened from the same instance, `ObserveForeignPdoAsync` and `ObserveForeignSrdoAsync`
for their file fallback, and a safety transaction for its one reading at the start. So edit a bound description
only while no call that reads it is running — in practice, while this node is idle towards that
peer. An edit during such a call is not supported, and what the call then sees is not specified:
the EdsDcfNet dictionaries are not safe for a read concurrent with a write. A reader that
enumerates — the loader, a safety transaction's reading — may fail with the dictionary's
`InvalidOperationException`, reported as that call's own failure (a step-D verification not
verified, with what that means for the boot — the slave is not started; a mandatory slave, or any
under a simultaneous start, halts the boot, an optional one is skipped —
[Verifying, and the boot-up](#verifying-and-the-boot-up)); a reader that looks objects up one at
a time around its own awaits —
the observers' file fallback, the gate's `Contains` — may answer from a description that is part
old and part new, or report no match, with no error at all. None of this is detected (#296). An
immutable description — edit, then bind again — is the planned shape for the next major (#299).

One exception follows from CiA DSP 304 §8.4.2.2: `13FFh:00` is the number of SRDOs, so a file
whose highest SRDO record is N implies the records of SRDOs 1..N, and a device that loads it
provides one the file leaves out at its defaults, deleted. The gate lets through what the device
provides there — sub-indices `00h`–`06h` of `1301h`–(`1300h` + N) and `00h`–`10h` of
`1381h`–(`1380h` + N) — so that a peer's safety configuration can be written and verified
([Configuring a peer](#configuring-a-peer)). Nothing else the file leaves out passes, and a file
without an SRDO record implies none. The implication is the file's, not the device's: a device
opened with `SrdoCount` above the file's highest SRDO record has records the file does not imply,
and a transfer to them is refused — the bound file has to describe the device.

With **no** description bound for the server, only the three CiA 301 mandatory base objects are
transferred:

| Object | Sub-indices allowed without a peer file |
| --- | --- |
| `1000h` Device type | `00h` |
| `1001h` Error register | `00h` |
| `1018h` Identity | `00h`–`04h` (vendor-id, product code, revision, serial number) |

`1018h:05` and above are not part of that exemption, and neither is any optional object,
including `1003h`. `PeerSdoAccessException.IsAllowedWithoutPeerDescription` is that list.
Anything else throws `PeerSdoAccessException` with `PeerDescriptionLoaded` false, again before
a frame is sent.

A request the bus rejects or does not confirm fails its transfer immediately with
`CanOpenTransportException`, instead of waiting for `SdoTimeout`. With the request not on the
wire, no answer can come, and a timeout would look like a silent server. The same failure is also
raised on `BackgroundExceptionOccurred`. If `SdoTimeout` elapses while a request is still waiting for its
confirmation, the confirmation decides. A failed send ends the transfer with
`CanOpenTransportException`. A confirmed one lets the timeout stand, which then completes up to
the confirmation window (`CanBusService.DefaultConfirmTimeout`) later than `SdoTimeout`. Only the latest request of a transfer can decide it this way. The client sends again only
after the server has answered, and that answer proves the earlier request reached the server.
A later failed confirmation of the earlier request means a lost echo, not a lost frame, and is
only raised on `BackgroundExceptionOccurred`. A send that is cancelled because the service was
disposed counts as failed.

## Observing a peer PDO

`ObserveForeignPdoAsync` splits a PDO of another node into a caller-supplied sink. Nothing is
written to this node's object dictionary, and a frame that is not one of this node's own RPDOs
is still not applied when it arrives.

The COB-ID is read live from `1400h:01` / `1800h:01`. A word that comes back is used ahead of the
peer EDS or DCF, including a PDO the device marks invalid (bit 31) and a CAN-ID the file does
not name. The same entry in the file is used only when that upload aborts, times out, is refused
by the peer-SDO gate, or does not return a word.

The mapping is read live from `1600h`–`17FFh` / `1A00h`–`1BFFh` through the same SDO client, so
the peer description bound for that node applies. A read that aborts, times out, is refused by
that gate, or is not a mapping of at most 64 bits uses the mapping in the
file passed to the call — and so does a value of another width than its type: sub-index `00h` must
be exactly one byte (UNSIGNED8) and each entry exactly four (UNSIGNED32); a longer one is not cut
to its low bytes. The SRDO observer reads its live mapping the same way. Observations of one peer run one after another. A base-frame CAN-ID CiA 301
restricts (`CanOpenCobId.IsRestricted`) is not taken as a PDO, from the device or from the file.
A payload shorter than the mapping writes nothing; a dummy entry `0001h`–`0007h` consumes its
bits and writes no signal.

A local SDO timeout or transport failure switches the remaining reads of that observation to
file fallback. An offline peer therefore costs one failed live request, even when the file
declares 512 PDOs. The next observation retries live reads. A peer-sent SDO abort or a gate
refusal applies only to the requested entry; cancellation still propagates.

The live reads of one observation — COB-IDs, mapping — are one snapshot of the peer: the observer
takes the peer's SDO channel, without waiting, and holds it to the end, so a safety transaction
that starts meanwhile waits for it (as it waits for any call in flight) and another SDO call of
this node to that peer is refused as in flight ([SDO client and a peer's device
description](#sdo-client-and-a-peers-device-description)). A channel busy at the start — a call
in flight, a transaction — makes every live read of that observation "already in flight", so the
whole snapshot is the file's; a live COB-ID is never combined with the file's mapping because a
caller came in between (#295).

## Discovery

`CanOpenDiscovery` finds the nodes on a bus. Listening is the default, and scanning is a separate
call.

```csharp
// Opens no node and transmits nothing. The window defaults to 2 s.
var heard = await CanOpenDiscovery.ListenAsync(bus);

// Only when asked: one SDO upload of 1000h:00 to each node-id not heard.
using var client = CanOpen.OpenNode(bus, nodeId: 0x7F);
var answered = await CanOpenDiscovery.ScanAsync(client, heard.Select(n => n.NodeId));
```

`ListenAsync` subscribes to `701h`–`77Fh` for the window the caller passes, any positive duration.
Without one it uses `DefaultListenWindow`, 2 seconds. A node is reported when either of these is
seen:

- a heartbeat: state byte `04h`, `05h` or `7Fh`
- a boot-up: `00h`

`Evidence` says which of the two were heard. `HeartbeatState` is the state in the last heartbeat.
Bit 7, the guarding toggle, is masked, as the node's own heartbeat consumer does. Other state
bytes, remote frames and 29-bit frames are ignored. There is an overload that takes
an `ICanBusService` shared with other protocols, and that service is left open.

`ScanAsync` reads `1000h:00` through the client's SDO client, and that is the only request it
sends. A node that stays silent also gets the client's usual timeout abort (`0504 0000h`), so a
node that answers late can drop its half-open transfer. It asks every node-id from 1 to 127
except the client's own and the ones passed in, and the requests run concurrently. A node is
reported when it answers:

- with the value, which lands in `DeviceType`
- with an SDO abort
- with a response the client aborts as malformed

The client's own timeout (`SdoTimeout`) means the node is absent. The peer-SDO gate applies, so a
node-id whose bound file does not list `1000h:00` is not asked. Neither is a node-id the client
already has a transfer with, and neither kind is reported. Any other failure ends the scan. That
includes a request the bus rejects or does not confirm, which fails with
`CanOpenTransportException`, so a dead bus does not look like an empty one.
Opening the client transmits its boot-up, which is one reason the scan is not the default.

## PDO engine

```csharp
using CanKit.Pro.CANopen.Pdo;

node.ObjectDictionary.WriteUnsigned(0x1019, 0, 12); // set before a non-zero 1006h
node.ConfigureTpdo(1, mapping, TpdoTransmission.Synchronous);
var od = node.ObjectDictionary;
uint cob = od.ReadUnsigned(0x1800, 1);
od.WriteUnsigned(0x1800, 1, cob | CanOpenCobId.InvalidBit);
od.WriteUnsigned(0x1800, 2, CanOpenTransmissionType.SynchronousEveryNthSync(3));
od.WriteUnsigned(0x1800, 6, 5); // count first SYNC at 5, transmit at 7, 10, 1, ...
od.WriteUnsigned(0x1800, 1, cob);

node.Pdo().RpdoTimeout += (_, e) => Console.WriteLine($"RPDO{e.PdoIndex} timed out");
```

The default is four PDOs per direction. `CanOpenNodeOptions.PdoCount` selects 1..512
(or clone existing options with `options.With(pdoCount: 8)`);
an EDS/DCF raises the allocation to its highest declared PDO. Only PDOs 1..4 have a
pre-defined COB-ID: higher PDOs need an explicit identifier. Bit 29 of `cobId` selects
an extended frame; base and extended frames with the same numeric ID route independently.
SYNC, EMCY and the safety services keep their existing frame-format limits.

Mappings contain up to 64 bits in sub-index order, LSB first across byte boundaries.
`BOOLEAN` may use one bit (the existing eight-bit form is also accepted); fixed-width
integers must use their full declared width. Dummy `0001h:00` consumes one bit.
Communication targets are allowed where CiA 301 marks them optionally mappable
(`1001h`/`1002h`/`1013h:00`, `1026h:01`–`03`, `1200h`–`12FFh:01`–`02`), subject to
their own mappability and access flags. The final byte is zero-padded. Native 24/40/48/56-bit integers are available through
`ObjectDictionary.AddRaw` and EDS/DCF loading; `ReadRaw`/`WriteRaw` retain their exact widths.
The existing `ReadUnsigned`/`WriteUnsigned` convenience methods remain U8/U16/U32.

**SYNC counter and start value.** The start gate applies to cyclic TPDO types 1..240;
acyclic TPDOs (0) and synchronous RTR sampling (252) use each accepted SYNC. `1019h:00` = 0 uses DLC-0 SYNC; 2..240 enables a
one-byte counter that starts at 1 and wraps to 1. Boot-up, leaving NMT Stopped, enabling
the producer and resuming a zero communication period reset it to 1. Only confirmed sends
advance it; cancellation, rejection and stale queued ticks retain the value. A late
confirmation cannot override a configuration reset. Values 1 and 241..255 are rejected. A change while `1006h` is non-zero
is rejected with `0800 0022h`, even with the periodic producer disabled. The consumer
uses `1019h` to select DLC but ignores its numeric overflow value; an unexpected length
is ignored and reports EMCY `8240h`. `SyncReceivedEventArgs.Counter` carries the counter.
TPDO `1800h:06` = 1..240 waits for that counter after entering Operational; the matching
SYNC is the **first counted SYNC**, so type 3 with start 5 first transmits on counter 7.
Start 0, or a disabled counter, preserves ordinary counting. The start value cannot
change while the PDO is valid. RPDO sub-index 6 remains absent as the specification requires.

**Synchronous window.** `1007h:00` is the window length in microseconds; 0 disables it.
With a window enabled, synchronous RPDOs outside it are discarded, including before the
first SYNC. Data accepted inside the window is applied at the next SYNC. Synchronous
TPDOs are checked both when scheduled and at the send hand-off: a queued sample that
outlived its window or was superseded by another SYNC is discarded. This is software
scheduling; it cannot withdraw a frame already handed to the CAN driver.

**RPDO deadline.** `1400h:05` is a timeout in milliseconds; 0 disables it. The next valid
reception arms it, each later reception refreshes it, and expiry raises
`node.Pdo().RpdoTimeout` once and emits EMCY `8250h` when its EMCY producer is enabled. A short frame does not refresh it. Reconfiguration and
leaving Operational cancel it; re-entry waits for a reception. `1400h:03` is retained as
an implementation-specific RPDO inhibit parameter, without suppressing reception.

**MPDO.** `node.Pdo()` exposes `ConfigureMultiplexedTpdo`/`ConfigureMultiplexedRpdo`
with `MpdoAddressMode.Source` (`FEh` in the mapping count) or `Destination` (`FFh`).
These are event-driven only: no SYNC, RTR or TPDO event timer, and at most one valid
SAM producer per node. `SendSourceMpdoAsync` reads a local object; `SendDestinationMpdoAsync`
takes the remote node-id (0 = broadcast), multiplexer and 1..4 data bytes. Values are
padded to the four-byte MPDO data field. Both follow NMT Operational and the inhibit time.
DAM consumers write the addressed local object, or report EMCY `8230h` when it is unavailable.
SAM consumers use the object dispatching arrays `1FD0h`–`1FFFh`, including consecutive
sub-index blocks. SAM producers use the scanner arrays `1FA0h`–`1FCFh`: an application
write emits that scanned object; `TriggerTpdoAsync` scans the whole configured list.
Declare these optional arrays before enabling the MPDO, either in the EDS/DCF or with
`AddRaw`/`AddU32` and a UNSIGNED8 sub0 announcing the highest supported slot (1..254).
Scanner slots are UNSIGNED32, dispatcher slots UNSIGNED64, in the §7.5.2.39/40 layout;
writing existing scanner slots refreshes the producer. Mapping/communication records
are writable over SDO under the same access rules as ordinary PDOs.

`ObserveForeignPdoAsync` also accepts extended PDO keys (bit 29 set) and bit mappings;
`ForeignPdoSignal.BitLength` reports the exact width. For MPDO it returns the transmitted
multiplexer and four data bytes, rather than applying the observed peer's dispatcher.

Every TPDO and RPDO is rebuilt from its communication and mapping records whenever one of them
changes. The transmission type byte in `1800h:02` decides how a TPDO fires:

| `TpdoTransmission` | `1800h:02` | Behaviour |
| --- | --- | --- |
| `SynchronousAcyclic` | `00h` | A change of state of a mapped object or `TriggerTpdoAsync` latches one transmission for the next SYNC |
| `Synchronous` | `01h` | Transmitted at every SYNC |
| *(write the byte)* | `02h`–`F0h` | Transmitted at every n-th SYNC. `CanOpenTransmissionType.SynchronousEveryNthSync(n)` gives the byte; write it to `1800h:02` through the object dictionary |
| `RtrOnlySynchronous` | `FCh` | Sampled at every SYNC into a buffer; the buffered sample answers an RTR — nothing answers before the first SYNC |
| `RtrOnlyEventDriven` | `FDh` | Sampled and transmitted when an RTR arrives |
| `EventDriven`, `EventTimer` | `FEh` | Transmitted on change of state and on `TriggerTpdoAsync`, rate-limited by the inhibit time; `EventTimer` additionally sets `1800h:05` |
| *(write the byte)* | `FFh` | As `FEh` |

`F1h`–`FBh` are reserved and rejected with `0609 0030h`. A TPDO fires only in Operational, and
every SYNC counter, latch and sample starts afresh on each transition into Operational.

**Inhibit time and event timer** apply to `FEh`/`FFh` only, as §7.5.2.37 says. The inhibit time
(`1800h:03`) is the minimum interval between two transmissions: an event inside it schedules
exactly one transmission for when it has elapsed, and further events until then coalesce into
that one. The event timer (`1800h:05`) is the maximum interval: it restarts with every
transmission and fires only in Operational. `1800h:03` can be changed only while the PDO is
invalid (bit 31 set). Both are measured on the actor's `ITimeSource`, so a test can drive them
with a virtual clock.

**RTR.** A remote frame on a valid TPDO's COB-ID is a PDO read (§7.2.2.5.2) and is answered
unless bit 30 of `1800h:01` ("no RTR allowed") is set — by the event-driven types and the
RTR-only types (see the table). A synchronous TPDO (`00h`–`F0h`) is transmitted after the SYNC
and by nothing else, so its RTR is not answered: §7.2.2.3 defines the remote request for
event-driven PDOs. The record's power-on default carries bit 30; `ConfigureTpdo` without an
explicit `cobId` clears it.

**PDOs sharing a COB-ID.** Two valid RPDO records may name the same COB-ID, and a frame on it
actuates each of them; two valid TPDO records may as well, and an RTR on it reads each of them.
SYNC is the exception: a frame on the CAN-ID in `1005h` is a SYNC and nothing else, so `1005h`
cannot be moved onto the CAN-ID of a PDO that exists or of a valid EMCY, and neither a PDO nor
a valid EMCY can be put on the SYNC CAN-ID (`0609 0030h` either way).

**Synchronous RPDOs.** `ConfigureRpdo(…, transmission: RpdoTransmission.Synchronous)` writes
`00h` to `1400h:02`: received data is held and written to the object dictionary — and reported
through `RpdoReceived` — with the next SYNC, the most recent frame winning. `EventDriven`
(`FEh`) actuates immediately. An RPDO shorter than its mapping is not processed and produces
EMCY `8210h` (if `1014h` is valid); a longer one is read up to the mapped length.

**Mapping validation** runs on every entry, whether it arrives over SDO or through
`ConfigureTpdo`/`ConfigureRpdo`: the target must exist, be PDO-mappable (`pdoMappable` on the
`Add*` methods — default `true` for application objects, `false` for the communication
profile, except entries CiA 301 marks optionally mappable), be readable for a TPDO or writable for an RPDO, and have the mapped width; the total
must fit 64 bits; entries occupy the payload in sub-index order. A **dummy entry** — index
`0001h`–`0007h`, sub-index 0, the bit length of that static data type — occupies its bytes
without touching the dictionary, which pads an RPDO to a peer's layout. `ConfigureTpdo` and
`ConfigureRpdo` copy the `PdoMapping` into the record; mutating the instance afterwards changes
nothing. A rejection throws `ArgumentException` naming the abort code and leaves the PDO
invalid.

**Re-mapping from a master** follows §7.5.2.36/§7.5.2.38 and needs
`WritableCommunicationParameters`: destroy the PDO (bit 31 of `1800h:01` to 1) → disable the
mapping (`1A00h:00` = 0) → write the entries (`1A00h:01`…) → enable the mapping (`1A00h:00` = N)
→ set transmission type, inhibit time, event timer → create the PDO (bit 31 to 0). The engine
picks the record up at the last step.

## Error control

**Heartbeat.** The producer and the consumer are separate modules. `HeartbeatProducer` sends
this node's heartbeat; `HeartbeatConsumer` watches other nodes. Neither module references the
other. `1017h` ≠ 0 runs the producer. Each sub-index of `1016h` with a node-id in 1..127 and a
non-zero time is a consumer whose timeout is armed immediately, so a producer that never
appears is reported too. `HeartbeatReceived` reports every heartbeat and boot-up on
`0x700 + id`.

For every node, periodic producer ticks are coalesced while a heartbeat send is outstanding;
transmission recovery sends no catch-up burst, including when LSS is disabled.

`HeartbeatTimeout`, `NodeGuardingTimeout` and `EmcyReceived` share the node's event queue
with heartbeats, SYNC, PDOs and NMT, and keep their place in that order. They are the events
the queue will not drop when a subscriber falls behind `EventQueueCapacity` (default 64). A
slow handler can lose a heartbeat or an RPDO; it cannot lose a timeout or an emergency and
then treat the peer as healthy. Their backlog is still bounded: a timeout for a producer whose
timeout is already waiting is folded into it, an EMCY identical to one already waiting likewise
(only while nothing else about that producer was queued in between: error, reset, error stays
three events), and a producer with `EventQueueCapacity` distinct emergencies waiting has its further ones
discarded, with one `BackgroundExceptionOccurred` report per burst. `BackgroundExceptionOccurred`
is not queued.

**Node guarding (consumer side).** `StartNodeGuardingConsumer(nodeId, guardTime, lifeTimeFactor)`
polls `0x700 + nodeId` with an RTR every `guardTime` and raises `NodeGuardingTimeout` after
`guardTime × lifeTimeFactor` without a reply whose toggle bit alternated. A boot-up from the
producer resets the toggle baseline rather than counting as a reply.

**Life guarding (producer side).** With `100Ch` (guard time) and `100Dh` (life time factor)
both non-zero, the node supervises the master that guards it: guarding starts with the first
RTR received, and when the next RTR stays away longer than guard time × life time factor,
`LifeGuardingEvent` reports `Occurred` — once per lapse — and the next RTR reports `Resolved`.

**Precedence.** CiA 301 §7.2.8.3.2.2 forbids running both protocols on one NMT slave: while
`1017h` ≠ 0 the heartbeat protocol is in use, guarding RTRs are not answered and no life
guarding runs (`RespondToNodeGuardingRtr` additionally gates the reply). The same rule decides
the **state-change heartbeat**: a heartbeat announcing a new NMT state goes out only while the
producer is on, because with it off an unsolicited data frame on `0x700 + id` is
indistinguishable from a toggle-0 guarding reply (#43).

## NMT state and resets

Table 37 of CiA 301, as the node applies it:

| | Pre-operational | Operational | Stopped |
| --- | --- | --- | --- |
| PDO | — | transmitted and received | — |
| SDO | served | served | requests ignored; an open server session is aborted with `0800 0022h` |
| SYNC | consumed and produced | consumed and produced | neither; the producer keeps its cycle and resumes afterwards (`SendSyncAsync` is a raw send and transmits regardless of the state and of bit 30 of `1005h`) |
| EMCY | transmitted | transmitted | held; the most recent one is transmitted when the node leaves Stopped |
| Heartbeat, guarding | active | active | active |

**Reset Node and Reset Communication** abort SDO server sessions, discard a held EMCY, reset the
guarding toggle to 0 (§7.2.8.3.2.1), restore the power-on values of the communication profile
(see above), raise `ApplicationReset`, send the boot-up and settle in Pre-operational. With the
heartbeat producer on, a heartbeat with the new state follows the boot-up.

`ApplicationReset` is the application's turn in the reset: it is raised synchronously on the
actor loop, still in Initialisation, and the boot-up goes out only when the handler has
returned — so a master that reacts to the boot-up never reads an application object the reset
had not reached. `NmtCommandReceived` is the notification of the command, delivered on the event
queue, and can arrive after the boot-up. A handler of `ApplicationReset` writes the dictionary
and returns; it must not wait for the node.

### SDO block transfer

`SdoDownloadAsync` auto-selects the block codec when the payload reaches
`CanOpenNodeOptions.SdoBlockThresholdBytes` (default 128 bytes). Below that threshold the
payload length picks the codec, per CiA 301: 1..4 bytes go expedited, 5 bytes and up go
segmented. `SdoUploadAsync` keeps the classic client under `SdoTransferMode.Auto` (the size is
unknown up front, so the server's initiate response decides expedited vs. segmented); pass
`SdoTransferMode.Block` to force block upload. `Block` is the only transport a caller can
force — the expedited/segmented split is not selectable. The block size advertised by this node is
`CanOpenNodeOptions.SdoBlockSize` (default 127; peers with a smaller window renegotiate
downward). CRC-16/XMODEM is exchanged when both endpoints set the "cc" / "sc" bit
(`SdoBlockCrcSupported`, default `true`). Classic segmented transfers carry a server-side idle
timeout (`CanOpenNodeOptions.SdoServerTimeout`, default 5 s) matching the block transfer guard,
and block transfer retransmits from the first unconfirmed segment on a partial sub-block ACK
(bounded by `CanOpenNodeOptions.SdoBlockMaxRetransmissions`, default 3).

## CANopen Safety (CiA 304)

The binding is **CiA DSP 304 version 1.0** (1 January 2001), the safety-relevant communication
on top of CiA 301: SRDOs, the global failsafe command (GFC) and the objects `1300h`–`13FFh`.
Version 1.1 moved into EN 50325-5:2010, which is not in this repository; what it changes is to
be reconciled in #289. Sections cited here are those of DSP 304 V1.0. Everything lives in the
namespace `CanKit.Pro.CANopen.Safety` and is reached through `node.Safety()`, an extension on
`ICanOpenNode` (the interface is not widened); on a node this library did not create it throws
`NotSupportedException`.

The tool side — configuring a device as its *configuration entity* — follows **CiA 319 version
2.0.0** (draft specification proposal, 2 December 2025), the implementation and configuration
specification for safety-related devices: the identity verification of its §5.2, the sequence of
§5.3 with the storage of step 5, and the rule of §6.2 for the checksum of an SRVT the device does
not implement. What it says the device does — step 4 of §5.3, the download indication, the
periodic recalculation and the start-up check of §5.4 — is built as described under
[Configuring and committing](#configuring-and-committing). Its clauses 7 and 8, the safety
application layer of CiA profiles and of manufacturer-specific profiles, are not — see
[Not built](#not-built-and-why). "CiA 319" in this section means that version.

### Opening a safety node

`CanOpenNodeOptions.SrdoCount` (0..64, default 0) is the number of SRDOs the node implements.
With 0 the node is the plain CiA 301 node it was before: none of `1300h`–`13FFh` is created and
`001h` is not handled, so an application may declare those objects itself (§9.4: "The
implementation of CANopen Safety shall be allowed only in safety devices"). Records above the
count are not reserved either.

A device description raises the count to the highest SRDO record it declares
(`1301h`–`1340h`, `1381h`–`13C0h`); the option is a floor. The safety objects of the file are
loaded like the PDO records: through the validated write path, the mapping before the SRDO is
created, the file's access type honoured, every value the node refuses reported as a finding with
its abort code — a refused mapping entry also with the rule it broke, since `0604 0041h` stands for
several (not mappable, not accessible, another width, no fixed width); an inverted slot that does
not repeat its plain slot and a mapping beyond 8 bytes keep the generic text with their own abort
code. A record below the highest one that the file omits exists all the same —
`13FFh:00` is the number of SRDOs (§8.4.2.2), so records 1..n exist and a master reaches every
one — at its defaults, deleted, with the node's default access (read-only on the bus without
`WritableCommunicationParameters`), and is reported as `SuppliedDefault`. A mapping
record without its communication record is applied with that SRDO deleted, and a mapping the
node refuses leaves that SRDO deleted. `13FEh` is applied last, so a file whose checksums match its records loads as a
valid configuration; a file whose checksum does not match loads without a finding, and the node
sets `13FEh` back to `00h` at that write (CiA 319 §5.3 step 4, below). EdsDcfNet evaluates `$NODEID` in the form `$NODEID+<constant>`: the pre-defined
COB-ID FFh + 2 × node-id is not of that form, so a file states it as a number or as
`$NODEID+<constant>` for its own node-id.

```csharp
using CanKit.Pro.CANopen.Safety;

using var node = CanOpen.OpenNode(bus, nodeId: 0x05, new CanOpenNodeOptions { SrdoCount = 2 });
node.ObjectDictionary.AddU8(0x2001, 0x00, 0);   // the safety input it transmits

var safety = node.Safety();
safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8),
    TimeSpan.FromMilliseconds(30));
safety.CommitSafetyConfiguration();             // checksums, then 13FEh = A5h
safety.SrdoStateChanged += (s, e) =>
    Console.WriteLine($"SRDO{e.SrdoNumber} valid={e.IsValid} {e.Reason}");
```

`EnableChangeOfStateSrdo` (default on) makes an application write to an object mapped in a
transmit SRDO transmit that SRDO at once, as `EnableChangeOfStateTpdo` does for TPDOs; a write
from the bus never triggers one.

### The objects

DSP 304 §8.4.2.2 Table 6, created at these defaults:

| Object | Sub-index | Default | Each write is held to |
| --- | --- | --- | --- |
| `1300h` GFC parameter | `00h` | 0 | 0 or 1 (`0609 0030h`). Writable in Operational: it is not covered by the checksum. |
| `1301h`–`1340h` communication record of SRDO n | `00h` | 6 | Constant (`0601 0002h`). |
| | `01h` direction | 0 | 0 deleted, 1 transmit, 2 receive; 3..255 `0609 0030h`. Creating needs both COB-IDs set, in range, consecutive and not held by another existing SRDO, and every mapped object accessible in that direction (a producer reads, a consumer writes). |
| | `02h` refresh time (tx) / SCT (rx), ms | 25 | 1..65535 (`0609 0030h` for 0). |
| | `03h` SRVT, ms | 20 | 1..255. Part of the checksum for both directions; only a consumer acts on it. |
| | `04h` transmission type | 254 | Constant; every write `0609 0030h`. |
| | `05h` COB-ID 1 | FFh + 2 × node-id for SRDO 1 of a node-id 1..64, else 0 | 0 or an odd CAN-ID 101h..17Fh, bits 11..31 clear; not changeable while the SRDO exists; not one of another existing SRDO (`0609 0030h`). |
| | `06h` COB-ID 2 | 100h + 2 × node-id, likewise | 0 or COB-ID 1 + 1 (102h..180h), same rules. |
| `1381h`–`13C0h` mapping of SRDO n | `00h` | 0 | 0 or an even count 2..16 (`0609 0030h`); every slot up to it filled (`0602 0000h`), each odd slot passing the slot rules below again (its object may have been re-declared since the slot was written), each even slot equal to the odd one before it — plain, then inverted (`0604 0041h`), at most 8 bytes in total (`0604 0042h`). |
| | `01h`–`10h` | 0 | Byte-aligned 8..64 bits, an existing mappable object outside `1000h`–`1FFFh` of a fixed width equal to that width — no string or domain, this implementation's rule — no dummy (`0607 0010h`, `0602 0000h`, `0604 0041h`). Mapping writes while the SRDO exists, and slot writes while the count is not 0, `0601 0000h`. |
| `13FEh` configuration valid | `00h` | 0 | Any value; only `A5h` means valid. |
| `13FFh` safety configuration checksum | `00h` | `SrdoCount` | Constant (`0601 0002h`). |
| | `01h`–n | 0 | UNSIGNED16, the checksum of SRDO n. |

The records, `13FEh` and `13FFh` refuse every write in Operational with `0800 0022h` (§8.3.2.4,
note 1); reading stays allowed. The transition into Operational takes the dictionary's write gate,
under which a direct write checks the state and stores its value, so a write racing it is either
stored before it — and seen by its configuration check — or refused. Every accepted write to a
record or to `13FFh` sets `13FEh` back to 0 (§8.4.2.2, "automatically 0"), so a changed parameter can never sit beside a stale `A5h`. A
PDO cannot take a CAN-ID in 101h..180h (CiA 301 Table 40), so SRDO COB-IDs are only checked
against each other.

The objects are communication-profile objects like the PDO records: read-only on the bus unless
the node is opened with `WritableCommunicationParameters` or the file declares them `rw`, always
writable locally. Reset Node and Reset Communication return them to their power-on values, `1010h`
"save" stores them and `1011h` "load" brings them back. Putting a stored configuration back does
not clear `13FEh`, so a configuration that was valid when it was saved is valid again after the
reset.

### Configuring and committing

`ConfigureSrdoProducer(n, mapping, refreshTime)` and `ConfigureSrdoConsumer(n, mapping, sct,
srvt)` write record n in the order §8.4.2.2 requires for a mapping change: the SRDO deleted
(sub-index `01h` = 0), the mapping disabled, its slots (each object twice, plain and inverted),
its count, the times, the COB-IDs, and the direction last. Without COB-IDs SRDO 1 of a node-id
1..64 takes its pre-defined pair (§8.3.3 Table 4); any other SRDO needs them
(`ArgumentException`). A producer's sub-index `03h` keeps its value (default 20 ms).
`DeleteSrdo(n)` sets the direction to 0. All three throw `InvalidOperationException` in
Operational and on a node without SRDOs, and `ArgumentException` naming the abort code for a value
the dictionary refuses. A configuration is checked whole before its first write — the same writes
through the same validator against a copy of the dictionary — so one the dictionary would refuse
part-way (a mapped object the direction cannot use, COB-IDs another SRDO holds) changes nothing:
the SRDO, its checksum and `13FEh` stay as they were.

An object an existing SRDO maps (sub-index `01h` ≠ 0), producer or consumer, cannot be
re-declared: `Add*` throws `InvalidOperationException` naming the SRDO. The checksum covers the
mapping entry, not the declaration behind it, so a new width or access would otherwise reach the
bus or the application with the configuration still valid. Writing the object's value is not
affected. To re-declare it, delete the SRDO first (§8.4.2.2, object 1381h–13C0h: "For
changing the SRDO mapping first the SRDO shall be deleted"); creating it again checks every
mapped object anew. The PDOs have no such rule — a re-declared RPDO object only surfaces when a
PDO is written into it.

`CommitSafetyConfiguration()` is §9.2 for a node configured locally: it writes `13FFh:n` for
every SRDO, computed by `SrdoCrc.Compute`, and then `13FEh` = `A5h`. `A5h` has to come last,
because every checksum write clears it. The checksum (§8.4.2.2) runs over the direction (1 byte),
the refresh time or SCT (2), the SRVT (1), COB-ID 1 (4), COB-ID 2 (4), the mapping count (1) and,
per mapping sub-index, that sub-index (1) and its value (4). V1.0 gives the polynomial
x¹⁶ + x¹² + x⁵ + 1 and nothing else: the initial value and byte order chosen here are those of
CRC-16/XMODEM (`SrdoCrc.Crc16Xmodem`, also used by the SDO block transfer), multi-byte fields
MSB-first. `SrdoCrc.Compute` takes the times as whole milliseconds, rounded half to even
(`Math.Round`), and refuses with `ArgumentOutOfRangeException` what does not fit the field after
rounding — a negative time, a refresh time or SCT above 65535 ms, an SRVT above 255 ms — and a
direction above 2, rather than checksum a value cut to the field. 0 is accepted: the device's own
records are checksummed as they are, and the dictionary refuses 0 itself.

At every transition to Operational each SRDO is checked (§9.5, last rule; §8.3.1 step D):
`13FEh` = `A5h` and `13FFh:n` equal to the checksum of the record. Without both the SRDO is
`ConfigurationInvalid`: a producer does not transmit, a consumer reports invalid at once. The
node's NMT state is not affected.

The device side of CiA 319 adds three more moments at which the node compares the checksums of
its existing SRDOs (sub-index `01h` ≠ 0 — a deleted SRDO has no configuration to protect, and
§9.5 does not check it either) with `13FFh` and, on a mismatch, sets `13FEh` to `00h` itself.
The checksum is recomputed over the record as the dictionary holds it, every mapping slot raw,
the inverted ones included (`SrdoRecords.RecordChecksum`; the transition to Operational uses it
too), so a record changed behind the dictionary's back in any slot is a mismatch:

* **At the write of `A5h`** (§5.3 step 4), from any writer — the configuration entity's SDO, a
  local write, a device description applying `13FEh` last. The readback of step 4 therefore
  sees `00h` on a device whose checksums do not fit, instead of the `A5h` just written.
* **After a reset** (§5.4), over what the reset put back, before the boot-up goes out. The
  stored values come back as stored; only `13FEh` is cleared.
* **Periodically**, when `CanOpenNodeOptions.SrdoConfigurationCheckInterval` is set (§5.3 says the
  SRD "should" do this and names no interval, so it is opt-in; `With` keeps the interval,
  `WithSrdoConfigurationCheckInterval` replaces it). Besides the checksums, the check compares the
  checksummed objects of the SRDOs that existed then — each one's record, mapping and `13FFh:n`
  — byte for byte with the image taken when `A5h` was written or restored: every write to one of
  them clears `13FEh`, so while `A5h` stands any difference was made behind the dictionary's
  back, a direction that now reads as 0 or reserved included. A deleted SRDO is not in the image;
  it has no configuration to protect. A mismatch found in Operational makes every SRDO invalid as the transition
  would — `ConfigurationInvalid`, or no longer existing (reason `null`) when its record reads as
  deleted — and ends the send period as leaving Operational does: a pair waiting, a pair already
  queued on the chain and the inverted half of a held pair go nowhere; the consumer reports
  invalid, and `SrdoStateChanged` says so. CiA 319 specifies no notification of the
  configuration entity.

And the clearing rule of §5.3 after step 5 holds as the dictionary works: every accepted write
to a safety parameter clears `13FEh`, whether or not the value changed; a write the node refuses
— read-only, out of range, in Operational — leaves it.

### Producer

A valid transmit SRDO sends its pair every refresh time while the node is Operational. The first
transmission is delayed by 0.5 ms × node-id (§9.5), so the producers of a network do not start in
step. Each transmission is the plain frame on COB-ID 1 followed by its bitwise inverse on COB-ID
2 (§8.1), handed on as one unit; the pairs of all SRDOs of the node go out on one ordered send
chain, so an inverted frame can never overtake its plain frame. `TriggerSrdoAsync(n)` and a change
of state transmit at once and restart the refresh cycle from there; the refresh time is the
longest interval between two transmissions the engine schedules. On the bus a pending pair waits
behind a stalled one (below).

At most one pair per SRDO is in flight. A pair that comes due while the previous one is not yet
confirmed becomes that SRDO's pending pair, replacing an older pending one — the latest data wins —
and is sent as soon as the previous pair completes. Leaving Operational, or disposing the node,
drops the pending pair, and a pair already on the send chain starts no further send — neither another
SRDO's pair queued behind the one in flight nor the inverted half of the one in flight (§8.3.2.2):
a consumer that times out on its SRVT is safer than one that refreshes its SCT on a stale pair.
Each frame is handed to the bus service with a cancellation token of its Operational period,
which leaving Operational or disposing cancels; the token is honoured up to the bus service's
driver call, including a send task already waiting there behind another send (#294). A frame the
driver has been handed goes out — as the driver queue would on any controller. A send that fails or throws is reported on `BackgroundExceptionOccurred`; the cycle goes on and the
SRDO stays valid. A mapped object that is gone, has no fixed width, or no longer has the width of
its mapping entry stops the producer instead (§8.1.3.1): no pair padded or cut to fit goes out,
the failure is reported on `BackgroundExceptionOccurred`, and the SRDO is
`MappedObjectUnavailable` — no further cycle and no `TriggerSrdoAsync` transmission — until it is
rebuilt (a record written, or Operational entered again). With the rule on re-declaring above this
takes a path around `Add*`; it is the guard behind that rule.
`GetSrdoState(n)` of a producer is valid while the node is Operational with a
valid configuration. The state is a snapshot of the actor's view: right after `OpenNode` the
records are taken on the actor's first turn, so a read before any call that round-trips the actor
(`State`, say) has returned may still show direction `None`.

### Consumer

A receive SRDO starts each Operational period invalid with `NotReceived` and its SCT running.
Then (§8.1.1, §8.1.3.1, §9.5):

* the plain frame starts the SRVT; a second plain frame replaces the first and restarts it;
* the inverted frame must follow within the SRVT (`ValidationTimeExpired` otherwise), must have
  a plain frame before it (`OutOfOrder`), and must be its bitwise inverse with the same length
  (`Mismatch`);
* both frames are classic CAN frames (§8.1): a CAN FD frame, or a payload over 8 bytes, on either
  id is no SRDO frame and is rejected as a wrong inverse is — `Mismatch`, a pending plain frame
  dropped, nothing written, no `SrdoReceived`, the SCT not restarted, no EMCY;
* a complete pair writes the mapped objects, raises `SrdoReceived` and restarts the SCT; when the
  SCT elapses without one the SRDO is `SafeguardCycleExpired`;
* a mapped object that refuses its bytes — a write validator, or a declaration that no longer
  takes them — ends the writing there: the objects before it keep this pair's bytes, the refused
  one and those after it keep their previous values, and the refusal is reported on
  `BackgroundExceptionOccurred`. The SRDO is `ActuationRefused`, and the pair counts as not
  received, as §8.1.3.1 leaves a pair too short for the mapping unprocessed: no `SrdoReceived`,
  and the SCT is not restarted. The reason is the most recent cause: an SCT still running keeps
  running from the last pair taken and its expiry reports `SafeguardCycleExpired`; one that had
  already expired stays expired. On a node this is a safeguard, like `MappedObjectUnavailable`:
  no public path produces a refused mapped write today — the node installs no write validator on
  application objects and offers no public one, and it refuses to re-declare a mapped object;
* a pair shorter than the mapping is not processed and produces EMCY `8210h` (when EMCY is
  enabled) once, until a pair of the right length arrives or the SRDO is rebuilt — as for an
  RPDO; a longer one, within the 8 bytes of a classic frame, uses its first bytes (§8.1.3.1).

The next valid pair makes an invalid SRDO valid again — there is nothing to reset — except
`ConfigurationInvalid`, which needs a new commit and a transition to Operational.
`SrdoStateChanged` reports the transitions, not every pair, and is a critical event like a
heartbeat timeout: it is never discarded to make room in the event queue, and a second identical
one still waiting behind the first is folded into it. A remote frame, a frame outside Operational
and the node's own producer frames (on a bus that echoes) are ignored. Leaving Operational makes
every SRDO `NotOperational` and stops SCT and SRVT.

### GFC

The global failsafe command is COB-ID `001h` with no data (§8.2). `SendGlobalFailsafeCommandAsync`
sends it at once — it does not wait behind SRDO pairs — and only with `1300h` = 1 in Operational;
otherwise it throws `InvalidOperationException`. The returned task is the send itself, not the
node's actor turn that started it: it completes when the bus service confirmed the send — on its
echo path once the echo arrived, otherwise once the driver accepted the frame — or reported its
failure (on `BackgroundExceptionOccurred`, as for every frame), or when the send was cancelled.
Like an SRDO frame it is handed to the bus service with the token of the
Operational period, here linked with the caller's token, so leaving Operational, disposing the
node or cancelling the caller's token cancels a send that has not reached the bus service's
driver call — including one already waiting there behind another send (#294); a frame the driver
has been handed goes out. A send cancelled by the period ends
quietly; one the caller cancelled — before the call ran or while the send waited — ends with
`OperationCanceledException`, and so does a send under which the bus service is disposed, which
is also reported on `BackgroundExceptionOccurred` as a transport failure. A GFC is reported by
`GlobalFailsafeCommandReceived` under the same two conditions, and only as a classic CAN frame with
DLC 0 — a CAN FD frame on `001h` is no GFC and is ignored. On a bus that
echoes, the node's own GFC is reported too. Like `SrdoStateChanged` the event is critical, and
identical ones still waiting are folded into one.

### Configuring a peer

`ConfigurePeerSafetyAsync(peer, configuration)` is the tool side of §9.2, Figure 9. A
`PeerSafetyConfiguration` holds `1300h` and, per SRDO number, the communication parameter and the
mapping. `Add` refuses what the peer's validator would refuse whatever its state, before a
configuration can delete the peer's SRDOs (§8.4.2.2): a direction other than tx or rx, COB-IDs
that are not an odd id of 101h..17Fh and the next one, a refresh time or SCT outside 1..65535 ms or
an SRVT outside 1..255 ms, and a COB-ID another SRDO of the same configuration uses;
`PeerSafetyConfiguration.FromDeviceDescription(dcf, nodeId)` builds it from a DCF's
parameter values (default value where none, `$NODEID` resolved) as what a device with that
node-id holds after loading the file, so that step D against a slave's own DCF expects what the
slave holds. It is not a second reading of the file: the node's own loader — the same code, over
a dictionary of its own — applies the file, and the expectation is every SRDO that exists
afterwards, with the records it holds. A value the file leaves out, does not parse or the device
refuses keeps the device's default (25 ms refresh time or SCT, 20 ms SRVT, the pre-defined
COB-IDs of SRDO 1 for a node-id 1..64, §8.3.3); an SRDO whose creation the device refuses —
COB-IDs that are not a consecutive pair or that another existing SRDO holds, no mapping record,
a mapping that does not apply (§8.4.2.2) — is left out, as the device leaves it deleted.

The call is the *configuration entity* of CiA 319 clause 5. Before its first frame it runs the
peer-SDO gate over every transfer it will make whatever the peer answers — `1018h:01`–`03`,
`13FFh:00`, `1300h`, `13FFh:n` of each SRDO named, `13FEh`, the `1010h` sub-index asked for — so a
file that does not declare one of them is refused with nothing sent (`PeerSdoAccessException`),
not mid-transaction with the peer part-way through its configuration; an SRDO named above the
file's highest SRDO record, which the gate could never let through, is refused likewise
(`ArgumentException`). Its first frames verify the peer's identity (§5.2): `1018h:01`
(vendor-ID), `:02` (product code) and `:03` (revision number) are uploaded and compared with the
bound EDS/DCF's own `1018h:01`–`03` — "the entries in the EDS", as declared: the ParameterValue,
the DefaultValue where none. All three are read; a peer that is not the device the file describes
ends the call with those entries as its `Mismatches` — the four bytes the file's entry encodes
to, the bytes the peer returned, whatever their number — and nothing written. §5.2 requires all
three of an SRD, where CiA 301 makes `:02` and `:03` optional: the file must declare each with a
value, and the peer must implement them; one that aborts an upload of them is not an SRD this
procedure can configure, and the abort propagates — unless an earlier entry already differed, when
the peer is another device and that answer is reported as a mismatch with no bytes read. The
file's `[DeviceInfo]` numbers are a second source: where set, they must agree with the `1018h`
entries, and a file that contradicts itself is refused before the first frame
(`InvalidOperationException`), as is one whose three entries are all 0, which carries no
identity, or one declaring an entry without a value this implementation reads (a plain number; a
`$NODEID` formula is no identity); an absent `[DeviceInfo]` key, which EdsDcfNet reads as 0, is
none of these. Without a bound
description the call is refused before any frame, naming `1018h:01` — the first transfer, which
would have nothing to be compared with. The further checks the note of §5.2 names (firmware and
hardware version, serial number, physical location) are the application's.

Then the call reads the peer's SRDO count from `13FFh:00` and refuses a configuration naming an SRDO
above it (`ArgumentException`) before writing anything; a peer whose `13FFh:00` is not exactly one
byte — it is UNSIGNED8, so none, two or four bytes are refused rather than read by their low byte —
or a count above 64 — the most `13FFh:00` can be (§8.4.2.2) — is an `InvalidOperationException`, before any
further frame and for a verification alike: SRDOs claimed beyond 64 could never be configured or
verified, so the count is not taken as 64. Times outside 1..65535 ms (cycle) and 1..255 ms (SRVT, for every
direction, because the checksum covers it) are refused before the first frame. So is a mapping
the peer's bound EDS/DCF rules out, because §9.2 deletes every SRDO of the peer first and a
refusal at the mapping would leave it without its configuration: each mapped object must be
declared there, flagged mappable (`PDOMapping`; an absent key reads as not mappable),
of a data type with a fixed width (no string or domain, as locally; an entry without
`DataType`, or with a type code this implementation does not know, is refused too, since its
width cannot be known) and as wide as it, and readable for a producer or writable for a consumer
(`AccessType`). What the bound file lets the tool see is refused locally; what only the peer can
judge — a rule of its own beyond its file — is the peer's (`ArgumentException`, nothing sent).
Then it writes
sub-index `01h` = 0 for every SRDO of the peer — so that two SRDOs can exchange their COB-IDs —
then per SRDO the record in delete-first order (an SRDO the configuration does not name stays
deleted), then `1300h`, then every checksum. The mapping is its count and the slots its objects
use; a slot above the count is no part of the SRDO and is neither written nor read, so the peer's
file need not declare it. It reads all of it back and compares byte for byte.
Only without a difference does it write `13FEh` = `A5h` and read that back too.

That is §5.3 of CiA 319, steps 1 to 3: the configuration data, then the checksums, then the
readback, then `A5h`. Step 4 is the peer's — it recalculates the checksums itself and, where one
does not match, sets `13FEh` back to 0 — and the readback of `A5h` shows it, so a peer whose
checksum convention differs from this implementation's (#289) fails the result instead of running
on a configuration it did not accept. Step 5 is optional and asked for with
`PeerSafetyConfiguration.Storage`: `AllParameters` downloads the "save" signature to `1010h:01`,
`CommunicationParameters` to `1010h:02`, after the acknowledgement, so that what the peer stores
includes `13FEh` = `A5h` and `13FFh`; `PeerSafetyResult.Stored` says the peer confirmed it, and
nothing is stored when the readback differed. `Storage` is settable, so a configuration from
`FromDeviceDescription` can ask for it too. A sub-index the bound file does not declare is
refused before the first frame (`PeerSdoAccessException`), as the gate would refuse the transfer —
but then after the acknowledgement; a store the peer aborts comes after the acknowledgement,
which stands, and the result says so: `Succeeded`, not `Stored`, the abort in
`PeerSafetyResult.StorageAbort` rather than thrown away with the acknowledged state.

An SRD need not implement the SRVT of a tx SRDO, and its EDS "should include the SRVT parameter
for all supported SRDOs" (CiA 319 §6.2): a tx SRDO whose communication record the bound file
declares *without* sub-index `03h` is one the peer is taken not to hold an SRVT for, and one whose
`03h` the file declares read-only (or `const`) is one the peer holds and will not take. The
configuration must say 20 ms for such an SRDO — the value §6.2 prescribes for the calculation,
and what such a peer computes; another value is refused before the first frame
(`ArgumentException`), because it cannot be put on the peer, and a checksum over it would not be
the peer's. The file is not trusted alone where it says nothing: an undeclared sub-index is
written all the same (the gate lets it through as an implied record entry), so that a peer which
does hold it writable ends with 20 ms rather than with a value from an earlier configuration — a
delete writes sub-index `01h` only. A peer that aborts that write with `0609 0011h` (sub-index
does not exist) does not implement it and is not asked to read it back; one that aborts it with
`0601 0002h` (read-only) holds it, and is read back and compared like every other value — so a
peer holding another value than 20 ms is a mismatch. A peer that *took* the write answers for it:
an abort on its readback propagates. A sub-index the file declares read-only is not written, only
read back. A consumer's SRVT is not optional, and neither is one the file declares writable: their
aborts propagate. A record the file does not declare at all, implied by a higher one, counts as
whole, as a device provides it. The verification applies the same rule, so a DCF without the
sub-index verifies against what the device holds.

`PeerSafetyResult.Succeeded` means acknowledged. Otherwise `Mismatches` lists each
(index, sub-index) with the bytes written and the bytes read — or, for `1018h`, the bytes the
file's identity encodes to and the bytes the peer returned, with nothing written — and the result is not `Succeeded`:
`A5h` is written only after a clean readback, and a readback of `A5h` that differs fails the
result too. An SDO abort, a timeout or a refusal by the peer-SDO gate propagates as from
`SdoDownloadAsync`. `13FEh` is 0 once at least one parameter write has been accepted, because
every such write clears it; an abort before that — for example `0800 0022h` from an Operational
peer, which refuses the first write — leaves it unchanged. Every transfer
passes the peer-SDO gate, so the peer's EDS or DCF must be bound (`BindPeerDeviceDescription`).
One configuration or verification per peer runs at a time, and it reserves the peer's SDO channel for the whole transaction: it waits for a call to that peer already in flight (its cancellation token ends the wait), and while it runs another SDO call of this node to that peer is refused as already in flight, so nothing interleaves between two of its transfers (a write after the readback could otherwise stale the checksum it acknowledges). It also works from the peer description as it was when it started — the moment it owns the peer's channel, after any call it waited for: its pre-check, the identity and the gate of each of its transfers read a snapshot taken then — the declared pairs of the safety area 1300h–13FFh, of 1018h and of 1010h, the file's identity, the records declared without an SRVT, the highest SRDO record, and the described entries of the mapped objects — not the bound instance, which is publicly mutable. Rebinding, unbinding or editing the bound description in place during it takes effect for the next call — an edit landing while the snapshot is being taken is the one case the snapshot does not cover ([SDO client and a peer's device description](#sdo-client-and-a-peers-device-description)). The same holds for the configuration or expectation passed in: a copy taken at that moment is what is checked, written and compared, so an SRDO added to the caller's instance meanwhile is not part of the transaction.

### Verifying, and the boot-up

`VerifyPeerSafetyConfigurationAsync(peer, expected)` is §8.3.1 step D without writing anything:
it uploads `13FEh` (must be `A5h`), `13FFh:n` of every expected SRDO (must equal the checksum
of the expected record) and the records, and compares them with what a configuration with
`expected` would have written (`1300h` aside, which step D does not list). An SRDO the
expectation does not name must be deleted (sub-index `01h` = 0); its checksum is not compared,
because the device checks the checksums of existing SRDOs only (§9.5). A tx SRDO whose record the
bound file declares without a writable sub-index `03h` is expected at the checksum over 20 ms, and
a peer that aborts the upload of a sub-index `03h` the file does not declare with `0609 0011h` does
not implement it — no difference (CiA 319 §6.2, as for a configuration); every other abort
propagates. `13FFh:00`, the checksums of the SRDOs expected and `13FEh` pass the gate before the
first frame. The identity of CiA 319 §5.2 is not read: it
belongs to the configuration, and the boot-up binds a DCF for a node-id.

The active flying master runs step D itself. An assigned slave whose bound **DCF** declares at
least one SRDO is a safety slave; an EDS carries no parameter values and never makes one. Before
NMT Start the master verifies the slave against its DCF — the expectation is taken from the
description bound at the moment the verification owns the slave's SDO channel, the moment its own
snapshot is taken ([Configuring a peer](#configuring-a-peer)), so a rebind or an edit while it
waits for a call in flight is compared, not a stale reading; the expectation and the gate of the
verification's transfers come from one reading of that description. No safety DCF bound at that
moment means nothing to verify — no verification is recorded, and the boot looks again: a DCF
bound since is verified, none means the slave is started as any other:

* verified — the slave is started as any other;
* not verified (a difference, or an upload that failed; an exception is also reported on
  `BackgroundExceptionOccurred`, except when the result arrives during a held cold reset, where it
  is acted on after the hold) — `FlyingMasterChanged` signals
  `SlaveSafetyConfigurationInvalid` for that slave and it is not started. A mandatory slave, or
  any safety slave under a simultaneous start (`1F80h` bit 1 set and bit 2 clear), halts the boot
  with the error reaction of `1F80h` bits 4 and 6, as a boot timeout does; an optional one is
  skipped and the boot goes on.

This node's own self-start and the moment of a simultaneous start wait for verifications still
running. A verification that ends while a forced Reset Communication is held is acted on only
after the hold — a success is kept, a failure is verified again — and a result from a boot that
has since been cancelled is discarded. A safety slave that is already Operational when first
seen — running before this master took over, or keep-alive — is not started by this master and
therefore not verified: step D is "before NMT Start".

While any assigned slave is a safety slave, the master sends no NMT Start to node 0: a broadcast
would also reach a safety slave that has not announced yet — keep-alive, running before this
master took over, or with its boot-up still in flight — before step D. A simultaneous start
(`1F80h` bit 1) keeps its moment — every mandatory slave seen, no verification running — but at
that moment every slave seen so far is started with an NMT Start of its own, a safety slave only
once verified, and a slave that announces later is started on its own as it announces. The cost:
one frame per slave instead of one broadcast, and a keep-alive slave that never announces (no
heartbeat, no boot-up) is not started by this master — as with bit 1 clear.
Only the boot-up is gated: an NMT Start to node 0 the application requests itself — through
`SendNmtCommandAsync` with node 0 or `1F82h` sub-index `80h` — goes out as requested.

### Observing a peer SRDO

`ObserveForeignSrdoAsync(peer, cobId1, frame1, frame2, description, sink)` splits another node's
SRDO pair into the sink, as `ObserveForeignPdoAsync` does for a PDO, and writes nothing to this
node's dictionary. The record is the one whose live `1301h`–`1340h:05` equals `cobId1`; a word
that was read decides, matching or not, and the file's value is used only for a record whose
upload failed or returned no word — a word is exactly four bytes (UNSIGNED32), and a direction
(sub-index `01h`) exactly one (UNSIGNED8); a value of another length counts as not read. A COB-ID
word with a bit above bit 10 does not match. Only an
existing record matches: one whose direction (sub-index `01h`, live, or the file's when that upload
fails) is not 1 or 2 — 0, or a reserved value 3..255 — is skipped, because a deleted record keeps
its COB-IDs and another SRDO may have taken them over (the device refuses only the ids of an
existing SRDO, §8.4.2.2); a direction neither source yields is not taken for a deletion. The two
frames must have the same length and be bitwise inverse (§8.1). The mapping is read live from
`1381h`–`13C0h` — every slot up to the count, the inverted ones included, so a full mapping takes
16 slot uploads rather than 8 — or from the file when that read fails: a slot that cannot be read,
even or odd, or a value of another width than its type (sub-index `00h` exactly one byte, a slot
exactly four), means the live mapping is not read. Either is used only when it is one the device's
own loader takes: each even sub-index up to the count is declared and repeats the odd one before it
(§8.4.2.2, `1381h`–`13C0h`: the inverted object), the same rule the dictionary's mapping validator
applies; otherwise the pair is not decoded and the reason names the slot. A live even slot that was
read and differs from its odd slot is not decoded either, and the file is no fallback for it: live
wins. Unlike for a PDO
(FR-CO-030), a live mapping is read in full whenever its count is, even beyond the slots the file
declares: the peer-SDO gate lets through every slot of an SRDO record the bound file implies
([SDO client and a peer's device description](#sdo-client-and-a-peers-device-description)), so
safety data is never split by a file's mapping while the device's own is readable. A mapping
with a dummy entry and a frame shorter than the mapping are reported not decoded. Signals carry `ForeignPdoKind.Srdo` and the SRDO number. SRVT and SCT are
not judged: the caller holds the timestamps.

The live reads of one observation — COB-ID, direction, mapping — are one snapshot of the peer:
the observer takes the peer's SDO channel, without waiting, and holds it to the end, so a safety
transaction that starts meanwhile waits for it (as it waits for any call in flight) and another
SDO call of this node to that peer is refused as in flight. A channel busy at the start — a
transaction, a call in flight — makes every live read of that observation "already in flight",
so the whole snapshot is the file's; the live COB-ID of one configuration is never combined with
the file's mapping of another.

### What this is not

This library is not developed to IEC 61508 / DIN V VDE 0801 and claims no safety integrity level.
It implements the data transport of DSP 304 V1.0 — the frame pair, the timing checks, the objects,
the checksum — and has not been run against a certified safety device or tester. The diverse
redundancy of §9.5 (the pair "built by two different ways", the data "compared … in the
application") and the safe state a safety controller enters are the device's to provide.

## Layer setting services (CiA 305)

`CanOpenLss.OpenMaster` is an LSS master (FR-CO-047). It transmits on `7E5h` and receives on
`7E4h`. Exactly one LSS master may be active in a CANopen network; this type does not police
other processes. Commands run one at a time. A confirmed service that draws no response within
`LssMasterOptions.ResponseTimeout` throws `LssTimeoutException`. Switch state selective, the
identify services and the Fastscan reset step treat that silence as "no slave matched".

Switch state global with `LssState.Configuration` puts every LSS slave into configuration. Use
it only when the network has a single LSS slave. Otherwise select one slave with
`SwitchStateSelectiveAsync`, or let Fastscan do it: a successful Fastscan leaves that one
unconfigured slave in configuration, and switch state selective is not required afterwards.

```csharp
await using var master = CanOpenLss.OpenMaster(bus, new LssMasterOptions
{
    ResponseTimeout = TimeSpan.FromSeconds(1),
    FastscanTimeout = TimeSpan.FromMilliseconds(50),
});

var found = await master.FastscanAsync();
if (found.Succeeded)
{
    var nodeId = await master.ConfigureNodeIdAsync(0x10);
    if (nodeId.Succeeded)
        await master.StoreConfigurationAsync();
    await master.SwitchStateGlobalAsync(LssState.Waiting);
}

// Table 0 index 2 is 500 kbit/s. Both switch-delay periods elapse here.
// No host-switch callback is configured in this example, so omit the per-call bit rate.
await master.SwitchStateGlobalAsync(LssState.Configuration);
await master.ConfigureBitTimingAsync(LssBitTiming.Cia301Table, tableIndex: 2);
await master.ActivateBitTimingAsync(switchDelayMilliseconds: 100);
```

Before sending further commands, dispose this master and reopen the adapter at 500 kbit/s.
To retune the already-open channel between the two periods instead, configure `BitRateSwitch`
and pass `bitsPerSecond` to `ActivateBitTimingAsync`. A supplied rate requires that callback
on every activation path; a missing callback is rejected before any frame is sent.

`ConfigureNodeIdAsync` sends the value it is given, including values the protocol rejects, so
the slave's error code (`LssCommand.NodeIdOutOfRange`, or `LssCommand.ManufacturerSpecific`
with `LssServiceResult.SpecError`) can be observed. `FFh` is a legal pending node-id: the
slave is then not configured. Inquire node-id reports the active node-id, or the pending one
when the slave has no active node-id yet.

Fastscan walks vendor-id, product code, revision and serial. Bit 0 has priority over bit 1, so
the slave found is the numerically smallest address among the unconfigured slaves that take
part. A field can be scanned, matched (the number is already known) or skipped. The vendor-id
cannot be skipped, and at most two numbers can; a partial scan must still select one slave.
`KnownHighBitCount` checks a known prefix in one step and scans only the remaining bits.
Each Fastscan step occupies `FastscanTimeout` from the request, and `FastscanCycleTime` is an
extra gap before the next Fastscan request, including the reset of a new scan. A completed scan
returns without waiting for that gap. Identify waits out `IdentificationTimeout` the same way, so a
later slave is not left queued for the next command. Give the slave a node-id in `1..127`
before the next Fastscan, or the same slave is found again.

### The slave

`CanOpenNodeOptions.Lss` turns layer setting services on in the node (FR-CO-048). A device
description with `LSS_Supported=1` does the same when `Lss` is left unset; an explicit
`LssSlaveOptions.Enabled = false` wins over the file. `node.LssState()` is waiting or
configuration. It is an extension, so `ICanOpenNode` itself did not grow a member.

The node-id passed to `OpenNode` is the persistent and pending node-id. `0xFF` is legal only
while LSS is on: the node then stays in NMT Initializing, sends no boot-up and serves no SDO,
until configure node-id gives it a value in 1..127. That value becomes active at once and the
node boots, and it stays in LSS configuration. A node that already has an active node-id keeps
it until the next NMT reset communication, which copies the pending node-id into the active one
and retargets pre-defined COB-IDs that still name the old one. Reset node copies the persistent
node-id back into the pending one first. Neither reset reloads the bit timing.
An identity change closes the transmit gate and drains old-ID reservations before publishing
the new node-id. Old queued SDO batches and heartbeat states are discarded; queued EMCYs
resolve their COB-ID after the change. A bit-rate switch already holding the gate stays silent.

`ILssConfigurationStore` is the only persistence. Without one, store answers "not supported".
`1010h` does not store the layer settings, and an LSS store does not rewrite the object
dictionary. Table 0 indexes follow `BaudRate_*` in the device description when the file names
any of them; otherwise every named CiA 301 rate is accepted. `BitRateSwitch` is called when the
pending timing becomes active; the callback can reconfigure the channel. The node stays silent
until both the second switch delay and the callback complete successfully, including for a zero
delay. While the node is active, failed or cancelled callbacks are reported on
`BackgroundExceptionOccurred` and keep the node silent until a later successful activation.
Repeated activations serialize the callbacks. An LSS command that arrives during the silence is
performed at once; its reply is held with the other frames that may not go out yet and sent in
order with them once the slave may transmit — every reply, not only the last one.

A new activation requests cancellation of the previous callback as soon as it is received,
including during the first switch delay. Disposal also requests cancellation. Neither waits for
application cancellation handlers. The replacement callback gets a fresh token and starts only
after the previous callback finishes. Callbacks must honor their token or implement their own
timeout: a callback that ignores cancellation still delays later callbacks and can continue after
disposal. Queued callbacks are not started after their cancellation is requested. The node never
resumes transmission on an assumed switch completion or a timeout alone.

NMT, boot-up and heartbeat that come due in that window are held; cancelled NMT commands are
not sent, and heartbeat frames use the node-id active when they are sent.
The transmit gate closes before waiting for existing driver reservations to finish, so new
traffic cannot prolong the drain or reach the driver before the switch completes.
Periodic heartbeat ticks are coalesced while a send is outstanding, so recovery does not replay
a backlog. Disposal discards queued heartbeats.
A flying-master Reset Communication is applied only once its NMT frame has been sent. An SRDO
pair already reserved finishes before switching starts; pairs due during silence are dropped
as a unit. A GFC command that cannot be transmitted fails with `CanOpenTransportException`.
Other frames are not sent.

```csharp
var store = new MyLssStore();
using var node = CanOpen.OpenNode(bus, nodeId: 0xFF, new CanOpenNodeOptions
{
    Lss = new LssSlaveOptions { Store = store },
});
// node.State is Initializing and node.NodeId is 0xFF until a master configures 1..127.
```

### Commissioning

`LssCommissioning` (FR-CO-049) runs the master as a commissioning sequence. A scan without a
node-id parks each unconfigured slave in configuration so Fastscan does not return it twice,
then switches every slave back to waiting. A scan that assigns node-ids switches every slave
to waiting first, then configures the slave Fastscan left in configuration. Switch state
selective is not repeated after Fastscan. The scan switches back to waiting after each slave,
unless activation already sent that frame before the switch delay. Bit timing is activated
during a scan only when `Limit` is 1. A scan that can select more than one slave configures
the table and leaves the rate unchanged, so a slave that already has a node-id stays on the
bitrate the next Fastscan still uses. Activating one selectively switched slave is valid when
it is the only LSS slave, or every other node already runs the new rate. A slave that rejects
a service stops the batch; its result carries the code. Slaves configured earlier keep the
pending bit rate, and the stored rate when store already succeeded. This tool does not roll
them back to the rate the network is still using; the caller does that before a power cycle.
A confirmed service that times out stops the scan and returns the slaves that already received
a node-id, including the slave whose configure node-id succeeded before a later service timed out. Configure node-id writes the pending node-id. On a slave that already has an active
node-id it takes effect at the next NMT reset communication, which this tool does not send.
`ConfigureSlaveAsync` throws on a timeout; a previously confirmed pending node-id can still
be written. The exception identifies the service that timed out and preserves its stack trace.
Supplying `ActivateBitRate` requires the master's `BitRateSwitch` callback. Commissioning
checks activation arguments before selecting or writing any slave.
Configure, inquire and scan sequences sharing a master are serialized through their cleanup,
including calls from different `LssCommissioning` instances. Direct calls to that master must
not overlap a commissioning sequence.

Configured slaves do not take part in Fastscan. `ReadIdentitiesAsync` reads `1018h:01`–`04`
from a node that has already booted. A missing later sub-index is 0. A timeout or any other
abort is not reported as a zero LSS number. Switching the slaves back to waiting after a
service is best-effort: if that frame fails, the configuration result is still returned.

```csharp
var tool = new LssCommissioning(master);
var scan = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
{
    AssignFromNodeId = 0x10,
    Store = true,
});
// scan.Slaves lists each address and the node-id it was given. scan.Stop says why it ended.

var one = await tool.ConfigureSlaveAsync(scan.Slaves[0].Address, new LssSlaveConfiguration
{
    BitTimingSelector = 0,
    BitTimingIndex = 2,
    ActivateDelayMilliseconds = 100,
    ActivateBitRate = 500_000,
});
```

The sample `samples/CanKit.Pro.Sample.CanOpenLss` is that sequence as a command-line tool.
`--demo` needs no hardware. Activating bit timing still does not reconfigure the open channel.

### Automatic bit-rate detection (CiA 801)

`LssBitRateDetection` (FR-CO-050) is the method behind table 0 index 9: a device that does not
know the bit rate of the bus finds it before it is given a node-id, by listening at each
candidate in turn. It is the listen-only approach of CiA 801 §5.2.3, Figure 1.

For each candidate the caller's factory opens the controller **in listen-only mode**, so the
search acknowledges nothing and transmits no error frame (§5.2.1). The detection then waits for
the first of: a received frame — the controller delivers one only at the right rate, so the
candidate is detected; enough error frames to rule the rate out (two by default, so a single
corrupted frame at the right rate does not move the search on, §5.2.3/§7); or the end of the
window. A window that ends with the controller no longer error-active rejects the candidate too,
for adapters that move `BusState` without raising an error frame.

```csharp
// On CanKit, the factory opens each candidate listen-only, accepting every id.
var result = await LssBitRateDetection.DetectAsync(rate => CanBus.Open(endpoint, cfg =>
    cfg.Baud(rate).SetWorkMode(ChannelWorkMode.ListenOnly).EnableErrorInfo()));

if (result.Detected)
{
    // result.BitsPerSecond is the rate; result.TableIndex is its CiA 301 table index when
    // the table lists it. Reopen the bus at that rate for normal operation.
}
```

The method needs traffic (§6): at least one frame has to be on the bus while the correct
candidate is open. A CANopen manager produces it with its heartbeat, with SYNC, or with SDO
reads of `1000h`, `1001h` or `1018h`. The factory opens each candidate afresh because adapters
generally cannot change the bit rate of an open channel; the number of frames the search
consumes depends on how many candidates precede the right one and on the adapter's close/reopen
time (Figure 2). `LssBitRateDetectionOptions` sets the candidates and their order (500/250/125
kbit/s first by default), the per-candidate window, the error threshold and the number of
passes. `RequireListenOnly` only relaxes the detection's own check that each probe was opened
listen-only; it does not change how your factory opens the bus. For an adapter that accepts
listen-only but does not report the mode back, turning it off is enough and the factory keeps
opening listen-only. For an adapter that rejects listen-only but is silent in normal mode, turn
it off **and** have your factory open each probe in normal mode — which is reactive, so a wrong
candidate may emit error frames, against §5.2.1. The sample's `detect` verb does exactly this
under `--allow-non-listen-only`, and is otherwise this call on the command line, with `--demo`.

The single-dominant-bit timing of §5.2.2, the external AND/OR-gate variant of §5.2.4 for
controllers without a listen-only mode, and the default fallback bit rate of §5.1 are not built;
see [Not built](#not-built-and-why).

## Flying master

A master application joins the NMT flying-master election with `StartFlyingMaster`. The binding
is **CiA 302-2 version 4.1.0**, network management and NMT flying master, object `1F90h`.
[Lely's standards index](https://opensource.lely.com/canopen/docs/standards/) lists that part,
and its NMT master cites the same edition. DSP 302 clause 5.5 is the historical ancestor of
those services, not the binding. CiA 302 is members-only and is not in this repository; the
behaviour below follows the public descriptions of that edition. The maintainer decisions
for the points those descriptions do not settle are on the pull request.

`1F80h` bit 0 marks an NMT-master-capable device and bit 5 selects the flying-master process.
Both are required. `StartFlyingMaster` sets them and writes the priority level (0 highest, 2
lowest) to `1F90h:03`. The other bits of `1F80h` belong to the boot-up below. Clearing bit 0
or bit 5, or calling `StopFlyingMaster`, leaves the election and stops the boot-up.

`1F90h` is an array of six UNSIGNED16 values, in milliseconds:

| Sub-index | Meaning | Default |
| --- | --- | --- |
| `01h` | How long to wait for an active master to answer | 100 |
| `02h` | Delay before that question is asked | 500 |
| `03h` | Priority level, 0..2 | 2 |
| `04h` | Priority time slot | 1500 |
| `05h` | Device time slot | 10 |
| `06h` | While active, repeat the negotiation trigger after this long; 0 disables the repeat | 4000 + 10 × node-id |

`04h` must be greater than 127 times `05h`, so a better priority level always finishes before a
worse one, whatever the node-ids are. The defaults satisfy that (1500 > 127 × 10). A priority
above 2, a device time slot of 0, or a pair that breaks the rule is rejected with `0609 0030h`.
Sub-index `00h` is constant 6 (`0601 0002h`).

The services are fixed CAN-IDs, which CiA 301 reserves and which this node therefore subscribes
to separately from the `080h`–`77Fh` range:

| CAN-ID | DLC | Meaning |
| --- | --- | --- |
| `0x073` | 0 | "Is an NMT master already active?" |
| `0x071` | 2 | The answer, and the claim: priority, then node-id |
| `0x072` | 0 | Start (or restart) the timeslot race |
| `0x076` | 0 | Force a new election |

After the delay the node asks with `0x073`. A reply whose priority number is lower than or
equal to ours — a better master, or an equal one — puts this node on standby. An equal claim
does not depose a master that is already active; during the race the first claim of equal
priority wins, and the timeslot formula makes that the lower node-id
(`priority × 04h + node-id × 05h`). A reply from a worse master is a reason to send `0x076` and
start again. A claim from a worse node during the race is treated as a network configuration
error: `0x076` goes out and `FlyingMasterChanged` reports `ConfigurationError`.

The first election after `StartFlyingMaster` is a cold boot. If nobody answers, the node
broadcasts NMT Reset Communication (`0x82`, node 0) and runs the election again as a warm boot,
so the timeslot race is not racing traffic from before the reset. That reset restores power-on
values, which is why `StartFlyingMaster` records `1F80h`, `1F81h`, `1F89h` and `1F90h` as
power-on values before it starts. Call `StoreParameters` first if the rest of the configuration
must survive the same reset. A later loss of the active master starts a warm election and does
not broadcast Reset Communication again. The active master is who sends Reset Communication when
it receives `0x076`. Winning then runs the boot-up below.

The flying master composes those two modules and does not keep a heartbeat timer of its own.
While this node stands by, `StartFlyingMaster`'s heartbeat timeout is installed on the consumer
as a `1016h` entry for the winner, unless the application already monitors that node. A timeout
from that module raises `ActiveMasterLost` and starts a new election. When this node becomes
the active master and `1017h` is 0, the producer module is started at half that timeout so
peers can see the loss. Writing the bits of `1F80h` without calling `StartFlyingMaster` runs
the same election and does not invent a heartbeat time; a standby node then reclaims only if
some consumer for the winner times out. `FlyingMasterRole` is `Inactive`, `Delaying`, `Detecting`, `Negotiating`, `Active`
or `Standby`. `ActiveFlyingMasterNodeId` names the winner once one is known.

### Boot-up

Once `FlyingMasterRole` is `Active`, this node initialises the slaves named in `1F81h`.
Sub-index 0 is constant 127. Each other sub-index is the node-id, an UNSIGNED32:

| Bit | Set means |
| --- | --- |
| 0 | This node-id is a slave. The sub-index equal to the master's own id is ignored. |
| 2 | The master may boot it: after it is seen, NMT Start is sent. |
| 3 | Mandatory. The master does not finish boot-up until this slave has been seen. |
| 4 | Keep-alive. NMT Reset Communication is not sent to this slave. |

Bits 8–31 hold the guard time and life time. They are stored. Heartbeat is preferred, and
node guarding is not started from them. `1F84h`–`1F88h` (the identity check) and the concise
DCF are not implemented.

`1F80h`, read the way the open stack that cites 302-2 v4.1.0 reads it:

| Bit | Clear | Set |
| --- | --- | --- |
| 1 | Start each slave on its own. | One NMT Start, target 0, after every mandatory slave has been seen. Sent only when bit 2 is also clear, so the master enters Operational together with the slaves. The master does not apply that broadcast to itself. With a CiA 304 safety slave assigned there is no broadcast: at the same moment each seen slave gets an NMT Start of its own, a safety slave only after step D, and a keep-alive slave that never announces is not started ([Verifying, and the boot-up](#verifying-and-the-boot-up)). |
| 2 | This node enters Operational when the mandatory slaves have been seen, or at once when there are none. | This node stays in its current NMT state. |
| 3 | Boot the assigned slaves. | Do not reset them and do not send NMT Start. |
| 4 | On a mandatory-slave timeout, reset that slave. | On a mandatory-slave timeout, NMT Reset Node to every assigned slave. |
| 6 | — | On a mandatory-slave timeout, NMT Stop to every assigned slave. Takes precedence over bit 4. |

`StartFlyingMaster` leaves bits 1, 2, 3, 4 and 6 as they were. Which value that is depends on
the profile the node was opened with (`CanOpenNodeOptions.Profile`), checked when `1F80h` is
created. A device starts with bits 2 and 3 clear: the winner enters Operational, and it starts
slaves individually if `1F81h` assigns any. A tool starts with both bits set, so self-start and
slave-start stay suppressed until the application clears them.

Each assigned slave that is not keep-alive is sent NMT Reset Communication to its own node-id.
A broadcast is not used there. While this node is the active flying master it ignores NMT
addressed to its own node-id, and a broadcast reset or stop does not take the master down
(including the echo of a reset it sent itself).

The guard time and life time stored in the upper bytes of each `1F81h` entry are kept as
written and are not used to start node guarding. Heartbeat (`1016h` / `1017h`) is the
keep-alive this node runs.

Heartbeats, including the boot-up byte `0x00`, update `1F82h` at that node-id. Reading `1F82h`
returns the last state byte, or 0 when none has been seen. Writing it, while this node is the
active master, requests a command and does not replace that state. The value is the state, not
the command specifier: `4` stop, `5` operational (NMT Start), `6` reset node, `7` reset
communication, `127` pre-operational. Sub-index `80h` addresses every node. Sub-index 0 is
constant 128. A write while this node is not the active master is rejected with `0800 0022h`.

`1F89h` is the boot timeout in milliseconds. 0 disables it. When it elapses, each mandatory
slave that was never seen raises `FlyingMasterChanged` with `SlaveBootTimeout`, and the boot-up
stops before this node enters Operational and before a simultaneous NMT Start. The error
reaction is the `1F80h` bit 6 / bit 4 row above. With both clear, only the missing slave
receives NMT Reset Node.

## Quick start

```csharp
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;

using var bus = CanBus.Open("virtual://demo/0",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

using var node = CanOpen.OpenNode(bus, nodeId: 0x11);

// FR-CO-001 / FR-CO-013: the communication profile is already there. Replace the placeholders
// and add the application objects.
node.ObjectDictionary.AddU32(0x1000, 0x00, 0x00030191, OdAccess.ReadOnly); // device type
node.ObjectDictionary.AddU16(0x2000, 0x00, 0x0000);                        // a process value

// FR-CO-005 / FR-CO-016: TPDO1 carries 2000h, event-driven, not more often than every 10 ms
// and at least every 500 ms. This is a write to 1800h / 1A00h.
node.ConfigureTpdo(1, new PdoMapping().Add(0x2000, 0x00, bitLength: 16),
    transmission: TpdoTransmission.EventTimer,
    eventTimerInterval: TimeSpan.FromMilliseconds(500),
    inhibitTime: TimeSpan.FromMilliseconds(10));

// FR-CO-008: heartbeat producer (1017h) and a consumer for a peer (1016h).
node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(200));
node.AddHeartbeatConsumer(producerNodeId: 0x12, timeout: TimeSpan.FromMilliseconds(500));
node.HeartbeatTimeout += (s, e) => Console.WriteLine($"missed HB from 0x{e.ProducerNodeId:X2}");

// FR-CO-019: make this the configuration a Reset Communication comes back to.
node.StoreParameters();

// FR-CO-002: 1000h:00, 1001h:00 and 1018h:00–04 are readable without a peer file.
var value = await node.SdoUploadAsync(serverNodeId: 0x12, index: 0x1000, subindex: 0x00);

// FR-CO-007: bring the slaves up as an NMT master (PDOs flow in Operational only). Sending does not
// start *this* node by itself: it obeys the frame only if the adapter delivers its own transmits
// back (a loopback, or hardware in an echo mode), and not in Normal mode on a non-echoing adapter,
// so do not rely on it. This node enters Operational through the boot-up of an active flying master
// (bit 2 of 1F80h clear) or as the target of another master.
await node.SendNmtCommandAsync(NmtCommand.Start, targetNodeId: 0);

// Flying master (CiA 302-2 v4.1.0): priority 0 is the highest. The heartbeat
// timeout is how long a standby waits before electing again.
node.StartFlyingMaster(priorityLevel: 0, activeMasterHeartbeatTimeout: TimeSpan.FromMilliseconds(500));
```

See `tests/CanKit.Pro.Tests/TestCases/CANopen` for end-to-end examples that exercise every FR-CO
requirement over the `CanKit.Adapter.Virtual` loopback bus.

## Layout

```
CanKit.Pro.CANopen/
  CanOpen.cs                          // factory
  ICanOpenNode.cs                     // public contract
  CanOpenNode.cs                      // node core: subscription, dispatch, SDO client + server, NMT, heartbeat
  CanOpenNode.CommunicationProfile.cs // partial: the CiA 301 objects, their validation, power-on values, resets
  CanOpenNode.Pdo.cs                  // partial: PDO engine — transmission types, inhibit time, event timer, RTR, mapping
  CanOpenNode.SdoBlock.cs             // partial: SDO block transfer (client + server)
  CanOpenNode.NodeGuarding.cs         // partial: node-guarding consumer + producer, life guarding
  CanOpenNode.FlyingMaster.cs         // partial: NMT flying-master election
  CanOpenNode.BootUp.cs               // partial: boot-up of the slaves in 1F81h once the master is active
  CanOpenNode.PeerSdo.cs              // partial: peer EDS/DCF binding and the client SDO gate
  CanOpenNode.ForeignPdo.cs           // partial: splitting a peer's PDO
  CanOpenNode.Safety.cs               // partial: CiA 304 objects, their validation, ICanOpenSafety, the SRDO send chain
  CanOpenNode.PeerSafety.cs           // partial: configuring (§9.2, as the CiA 319 configuration entity) and verifying (step D) a peer's safety parameters
  CanOpenNode.ForeignSrdo.cs          // partial: splitting a peer's SRDO pair
  CanOpenDiscovery.cs                 // listen-only discovery and the scan on request
  CanOpenNodeOptions.cs
  CanOpenCobId.cs                     // pre-defined connection set, COB-ID control bits, restricted CAN-IDs
  CanOpenEvents.cs                    // event argument types
  ObjectDictionary.cs, OdEntry.cs
  Nmt/NmtState.cs                     // NMT enum + command specifier
  Nmt/FlyingMaster.cs                 // flying-master role and signal
  Sdo/                                // codec, abort codes, exception, block-transfer codec + mode enum
  Pdo/PdoMapping.cs                   // mapping entries, transmission-type enums and byte constants
  Emcy/EmcyMessage.cs                 // 8-byte encode/decode
  Safety/                             // CiA 304: SrdoEngine, SrdoCrc, SrdoFrames, SrdoRecords, ICanOpenSafety, value types
  Lss/                                // CiA 305: LSS master and slave options, frame codec, Fastscan;
                                      //   CiA 801 automatic bit-rate detection (LssBitRateDetection.cs)
```

## Migrating from 1.2.x

`SdoTransferMode.Expedited` and `SdoTransferMode.Segmented` are gone. `SdoTransferMode.Block`
keeps its value `3` — the gap the removed members leave behind is deliberate, see below.

Neither removed member ever reached the wire encoder, so **below
`CanOpenNodeOptions.SdoBlockThresholdBytes`** dropping the argument sends exactly the same frames.
At or above the threshold it does not — see "One behavioural difference" below, which is the only
case in this migration that changes traffic:

```csharp
// before — below the block threshold, both of these produced identical traffic
await node.SdoDownloadAsync(id, index, sub, data, mode: SdoTransferMode.Expedited);
await node.SdoDownloadAsync(id, index, sub, data, mode: SdoTransferMode.Segmented);

// after
await node.SdoDownloadAsync(id, index, sub, data);
```

Below `CanOpenNodeOptions.SdoBlockThresholdBytes` the codec is picked from the payload length,
per CiA 301: 1..4 bytes expedited, 5 and up segmented. The threshold is tested first and so bounds
the expedited range as well — a node configured with a threshold of 1..4 sends even a one-byte
payload by block transfer. On upload it is the server's initiate response that decides. `Block`
remains, because it is the one transport the client genuinely negotiates rather than derives.

**One behavioural difference.** At or above `CanOpenNodeOptions.SdoBlockThresholdBytes` (default
128), a download that used to pass `Expedited` or `Segmented` bypassed block transfer as an
undocumented side effect. It now uses block transfer like any other download of that size. If a
peer cannot handle that, raise `SdoBlockThresholdBytes` on the node options rather than reaching
for a mode argument.

**Nothing to remap.** `Block` keeps its numeric value `3`, so a persisted or transmitted enum
value still means what it meant. That is why the enum is left with a gap where `1` and `2` used
to be: renumbering `Block` to `1` would have made it collide with the value `Expedited` carried
in 1.2.x, and an already-compiled caller passing that literal would have gone from requesting a
no-op hint to forcing block transfer — a silent change on the wire that hangs against a peer with
no block support. Removing the members gives such a caller a compile error instead, which is the
point of the break.

### The object dictionary now drives the node

Each item names what a caller does about it.

* **PDO records are read-only on the bus by default.** A master writing `1400h`, `1600h`,
  `1800h` or `1A00h` over SDO gets `0601 0002h` unless the node was opened with
  `CanOpenNodeOptions.WritableCommunicationParameters = true`. Dynamic mapping over SDO, which
  1.2.x offered unconditionally, needs that option now.
* **`ConfigureTpdo` / `ConfigureRpdo` validate the mapping.** Every mapped object must exist in
  the OD, be PDO-mappable, be accessible in the PDO's direction and have the mapped width; a
  violation throws `ArgumentException` naming the abort code and the PDO stays invalid. 1.2.x
  stored the mapping unchecked and zero-filled a missing object at transmission time. Add the
  objects before configuring the PDO.
* **`EventTimer` TPDOs also transmit on change of state.** The mode is `FEh` with `1800h:05`,
  and CiA 301 makes the event timer the *maximum* interval, not the period; 1.2.x triggered
  change-of-state for `EventDriven` only. A TPDO that must fire on the timer alone maps objects
  the application does not write between transmissions, or is opened with
  `EnableChangeOfStateTpdo = false`.
* **The state-change heartbeat needs the producer on.** 1.2.x sent a heartbeat on every NMT
  transition; now it goes out only while `1017h` ≠ 0. A consumer that relied on it starts the
  producer, or uses node guarding.
* **`ConfigureRpdo` gained a `transmission` parameter** (`RpdoTransmission`, default
  `EventDriven`) after `cobId`, and **`ConfigureTpdo` gained `inhibitTime`** as its last
  parameter (default off). Existing calls compile unchanged.
* **`PdoMapping` is copied at configuration.** Mutating the instance after
  `ConfigureTpdo`/`ConfigureRpdo` no longer changes the PDO (#42). Call the method again instead.
* **`SendEmcyAsync` writes `1001h`** with the error register it carries, and the returned task
  faults with `InvalidOperationException` when EMCY is disabled (bit 31 of `1014h`). In Stopped
  the EMCY is held, not sent.
* **NMT Stopped and resets mean what Table 37 says.** In Stopped, SDO requests are ignored, open
  server sessions are aborted with `0800 0022h`, and SYNC is neither consumed nor produced. Reset
  Node and Reset Communication restore the communication profile to its power-on values: a
  configuration made through `ConfigureTpdo`/`ConfigureRpdo` is gone after a reset unless
  `StoreParameters()` was called.
* **COB-ID arguments are the CiA 301 words.** `cobId` in `ConfigureTpdo`/`ConfigureRpdo`
  carries bit 31 (valid) and bit 30 (RTR); 1.2.x passed the value into the frame ID as it was
  (#41). Bit 29 throws, and so does a restricted CAN-ID of Table 40.
* **Both roles are served by the same node.** There is no separate master or slave type: a
  node's SDO client, NMT master and consumers work alongside its own object dictionary and
  PDOs. That was so in 1.2.x too; what is new is that the device side can be configured by a
  master over the bus.

## Changes since 1.3.0

* **`ConfigurePeerSafetyAsync` is the configuration entity of CiA 319 v2.0.0** (FR-CO-051): it
  reads the peer's `1018h:01`–`03` first and compares them with the bound file's own `1018h`
  entries, so a file that does not declare them with a value, contradicts them in `[DeviceInfo]`
  or declares them all 0 is refused before the first frame, and a peer that is another device ends
  the call with those entries as mismatches; every transfer the call makes whatever the peer
  answers passes the gate before the first frame; `PeerSafetyConfiguration.Storage` (settable)
  asks for the step-5 store through `1010h`, reported in `PeerSafetyResult.Stored`, with a peer's
  abort in `PeerSafetyResult.StorageAbort`; and a tx SRDO whose record the file declares without
  a writable sub-index `03h` must say 20 ms, which is written as optional where the file says
  nothing — a peer's `0609 0011h` or `0601 0002h` is tolerated — and checksummed (§6.2).
* **`CanOpenNodeOptions.With(...)` validates the copy it returns**, as the node does when it is
  opened: an out-of-range value now throws `ArgumentOutOfRangeException` from `With` rather than
  from `OpenNode`. It gained `srdoCount` and `enableChangeOfStateSrdo`; the 1.3.0 signature is
  kept beside it, so compiled callers keep working.
* **Layer setting services (CiA 305) and CiA 801 bit-rate detection** are new: the LSS master,
  the LSS slave in the node, commissioning, and `LssBitRateDetection` (see
  [Layer setting services](#layer-setting-services-cia-305)).

## Install

```bash
dotnet add package CanKit.Pro.CANopen

# plus a CanKit adapter for the hardware you actually talk to, e.g.
dotnet add package CanKit.Adapter.Virtual   # loopback, no hardware
```

Dependencies: `CanKit.Abstractions`, `CanKit.Pro.Actor`, `CanKit.Pro.RawCan`, `CanKit.Pro.Reliability`.

Part of [CanKit.Pro](https://github.com/dborgards/CanKit.Pro) — higher CAN protocol layers
built **on top of** [CanKit](https://github.com/pkuyo/CanKit), which is consumed as a NuGet
package rather than forked.

## License

MIT — see [LICENSE](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE).
CanKit itself is a separate project licensed under Apache-2.0; see
[THIRD-PARTY-NOTICES.md](https://github.com/dborgards/CanKit.Pro/blob/main/THIRD-PARTY-NOTICES.md).
