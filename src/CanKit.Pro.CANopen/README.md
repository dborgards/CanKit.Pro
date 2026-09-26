# CanKit.Pro.CANopen

Status: 1.0.0 – 1.2.3 are **withdrawn from nuget.org** — they were published as stable before
the API had been reviewed. **1.3.0 will be the first release whose API is stable**. Until it is
tagged there is no listed version to install, so the `dotnet add package` line below resolves
nothing and the withdrawn releases come back only on an exact version pin. The
public surface can still change until then. See
[Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

**CANopen (CiA 301)** node implementation for CanKit.Pro. Provides an in-process
`ICanOpenNode` whose object dictionary carries the CiA 301 communication profile and drives the
node's behaviour: an SDO server and client, a PDO engine with every transmission type of
Table 72, an NMT slave state machine, heartbeat producer/consumer, node guarding and life
guarding, a SYNC producer/consumer and structured EMCY — all composed on the CanKit.Pro L2
pipeline (`ICanBusService` / `IProtocolActor` / `DeadlineScheduler`), exactly like
`CanKit.Pro.IsoTp`, `CanKit.Pro.J1939Tp` and `CanKit.Pro.Uds`.

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
scope (#132); the SRS's "Quelle" column says for each of them whether CiA 301 requires it or the
architecture does.

| SRS id | Feature |
| --- | --- |
| FR-CO-001 | Local object dictionary with typed read/write, data-type enforcement and per-entry PDO mappability |
| FR-CO-002 | SDO expedited transfer (payloads ≤ 4 bytes) |
| FR-CO-003 | SDO segmented transfer (payloads > 4 bytes, toggle-bit protocol) |
| FR-CO-004 | SDO block transfer (CiA 301 §7.2.4.3.15) — client + server, download + upload, blksize negotiation, optional CRC-16/XMODEM |
| FR-CO-005 | TPDO/RPDO mapping held in the mapping records `1600h`–`1603h` / `1A00h`–`1A03h`: byte-aligned, up to 8 bytes, written through `ConfigureTpdo`/`ConfigureRpdo` or over SDO by the CiA 301 §7.5.2.36/§7.5.2.38 re-mapping procedure |
| FR-CO-006 | Event-driven, timer-driven and SYNC-triggered TPDO transmission; change-of-state emission on application OD writes (`CanOpenNodeOptions.EnableChangeOfStateTpdo`, default on; bus-originated writes never re-trigger, so no echo loops) |
| FR-CO-007 | NMT master + slave state machine (Start / Stop / Pre-Op / Reset Node / Reset Communication) |
| FR-CO-008 | Heartbeat producer + consumer timeout event |
| FR-CO-009 | Node guarding (CiA 301 §7.2.8.3.2.1) — RTR-based consumer + producer, life-time timeout event |
| FR-CO-010 | SYNC producer and consumer |
| FR-CO-011 | EMCY encode/decode + structured receive event |
| FR-CO-012 | Uses the L2 `ICanBusService` demux (subscription with COB-ID filter) |
| FR-CO-013 | Communication-profile objects created at their CiA 301 defaults (`1000h`, `1001h`, `1018h`, `1005h`, `1006h`, `100Ch`, `100Dh`, `1010h`, `1011h`, `1014h`, `1016h`, `1017h`, `1200h`, the four RPDO and four TPDO records); PDO records read-only on the bus unless `WritableCommunicationParameters`; sub-index `04h` of the PDO communication records absent (`0609 0011h`) |
| FR-CO-014 | The object dictionary is the single source of truth: an accepted SDO download and a local write reach the service alike, the configuration methods are OD writes, managed objects cannot be re-declared |
| FR-CO-015 | TPDO transmission types `00h`, `01h`–`F0h`, `FCh`, `FDh`, `FEh`/`FFh`; reserved values rejected with `0609 0030h` |
| FR-CO-016 | Inhibit time `1800h:03` (100 µs units, minimum interval) and event timer `1800h:05` (ms, maximum interval) for event-driven TPDOs |
| FR-CO-017 | Synchronous RPDOs (`1400h:02` = `00h`–`F0h`): received data actuated with the next SYNC |
| FR-CO-018 | COB-ID control bits valid/RTR/frame in `1400h:01`, `1800h:01`, `1005h` and `1014h`; restricted CAN-IDs of Table 40 and 29-bit frames rejected |
| FR-CO-019 | Reset Node / Reset Communication restore the power-on values; `1010h:01` "save" and `1011h:01` "load" (in-memory); wrong signature `0800 0020h` |
| FR-CO-020 | Mapping validation: existence, mappability, direction, width, byte alignment, 8-byte limit, sub-index position, `sub0` as UNSIGNED8, dummy mapping `0002h`–`0007h` |
| FR-CO-021 | Producer-side life guarding from `100Ch`/`100Dh` with `LifeGuardingEvent` (occurred / resolved); heartbeat takes precedence |
| FR-CO-022 | NMT state semantics of Table 37: no SDO, SYNC or EMCY in Stopped, PDO only in Operational, state-change heartbeat only with the producer on, guarding toggle reset on reset |
| FR-CO-023 | Heartbeat consumer and producer from `1016h`/`1017h`; `1016h` grows per consumer; duplicate node-id `0604 0043h` |
| FR-CO-024 | RPDO length handling: too short → not processed and EMCY `8210h`; too long → the first bytes are used |
| FR-CO-025 | A node opened from an EDS or DCF (CiA 306, read with `EdsDcfNet`): application objects with type, access, `PDOMapping` and value from the file, the managed communication objects through the validated write path, PDO records in the §7.5.2.38 order, undeclared PDOs absent, `$NODEID` evaluated, a DCF's `ParameterValue` and `NodeID` honoured |
| FR-CO-026 | Whatever the node cannot take as written is degraded, corrected in the dictionary and reported per entry in `DeviceDescription` (omitted / corrected / PDO disabled / not implemented / default supplied, with the SDO abort code that decided) |
| FR-CO-027 | The file's access rights govern the bus: `rw` PDO records are writable without `WritableCommunicationParameters`, `ro` stays `ro`; its `PDOMapping` attribute is the object's mappability |
| FR-CO-028 | The described values are the power-on values: "load" and Reset Communication return `1000h`–`1FFFh` to them (or to the last "save"), Reset Node the application objects too |

## Not built, and why

Deliberate omissions, each checked against the norm text:

* **TIME (`1012h`) and synchronous window length (`1007h`)** — both `Category: Optional` (scope
  item 10). A synchronous TPDO is transmitted at the SYNC and is never suppressed for a closed
  window, because there is no window.
* **Bit-granular PDO mapping** — bit lengths must be multiples of 8. Over SDO another length is
  rejected with `0607 0010h`; `PdoMappingEntry` refuses it in its constructor.
* **MPDO** (`sub0` = `FEh`/`FFh` of a mapping record) — rejected with `0609 0030h`, like every
  count above `40h`.
* **Non-volatile storage behind `1010h`** — "save" takes the current communication parameters
  as the power-on values a reset restores, for the lifetime of the process. A device whose
  configuration must survive a restart persists it itself and replays it after `OpenNode`.
* **29-bit COB-IDs** — the node sends and receives CAN base frames only; bit 29 of any COB-ID
  word is rejected with `0609 0030h`, as CiA 301 prescribes for such devices, and
  `ConfigureTpdo`/`ConfigureRpdo` throw for it.
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
* **Flying Master (CiA 302)** — not implemented. As of the maintainer decision on #131 it is a
  required master capability (`docs/reviews/2026-09-26-canopen-master-tool-scope.md`); the
  procedure is not specified here. The boot-up manager used to share this bullet and is not
  covered by that decision.

What a device description declares beyond this — a `1012h`, a 24-bit integer, a fifth PDO —
is not built either; [Device descriptions](#device-descriptions-eds-dcf) says what the node does
with such an entry instead of ignoring it.

## The object dictionary is the configuration

`OpenNode` creates the CiA 301 communication profile in the node's object dictionary, at the
norm's defaults: `1000h` (device type 0), `1001h` (error register), `1018h` (sub0 = `01h`,
vendor-id 0 = "no vendor-ID assigned"), `1005h`/`1006h` (SYNC on `080h`, not generated),
`100Ch`/`100Dh` (life guarding off), `1010h`/`1011h` (store / restore), `1014h` (EMCY on
`080h` + node-id, valid), `1016h`/`1017h` (heartbeat off), `1200h` (the default SDO server,
constant) and four RPDO plus four TPDO records (`1400h`–`1403h`, `1600h`–`1603h`,
`1800h`–`1803h`, `1A00h`–`1A03h`) at their pre-defined connection set CAN-IDs, "not valid" until
configured. `1000h` and `1018h` are placeholders the application replaces with `AddU32` — they
stay outside the PDOs: the communication profile area is never a mapping target, whatever an
entry's mappability flag says. The other objects are managed by the node and take values, not
re-declarations — an `Add*` on one
of them throws `InvalidOperationException`, whether it would replace a sub-index or add one the
node does not implement (`1016h` grows through `AddHeartbeatConsumer`, not by hand).

**API calls are OD writes.** `StartHeartbeatProducer` writes `1017h`; `AddHeartbeatConsumer`
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
| `0601 0002h` | Download to a read-only object: the PDO records without `WritableCommunicationParameters`, `1200h`, `1000h`, `1001h`, `1018h` |
| `0602 0000h` | Mapping entry whose target object does not exist; `sub0` enabling a mapping with an empty slot |
| `0604 0041h` | Target not PDO-mappable, not accessible in the PDO's direction, or of another width than mapped; dummy entry with the wrong width |
| `0604 0042h` | Mapping longer than 8 bytes |
| `0604 0043h` | Second `1016h` entry for a node-id already monitored |
| `0607 0010h` | Mapping bit length 0, above 64 or not a multiple of 8 |
| `0607 0012h` / `0607 0013h` | Download of the wrong width for the object — `sub0` of a mapping record is UNSIGNED8 |
| `0609 0011h` | Sub-index `04h` of a PDO communication record, or any other absent sub-index |
| `0609 0030h` | Reserved transmission type; bit 29 (frame) set; bit 30 of `1014h` set; restricted CAN-ID; CAN-ID changed while the object is valid; inhibit time changed while the PDO is valid; mapping count above `40h` |
| `0800 0020h` | Wrong signature written to `1010h:01` / `1011h:01` |
| `0800 0022h` | SDO server session open when the node entered Stopped or was reset |

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
  `1014h`, `1016h`, `1017h`, `1018h`) take the file's access and value through the same
  validated write path an SDO download uses, so a value the norm would reject on the bus is
  rejected here too. A mandatory object the file omits keeps its placeholder.
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
| `Omitted` | Not created: a data type the dictionary does not represent (24/40/48/56-bit integers), a fifth PDO, an unparsable value, a sub-index of a fixed record the node does not implement |
| `Corrected` | Created with the CiA 301 default instead of the described value, which the rule an SDO download hits rejected (a reserved transmission type, a second `1016h` entry for one producer) |
| `PdoDisabled` | The PDO stays destroyed (bit 31 set) because its communication record or its mapping could not be applied — a 29-bit COB-ID, a mapping onto an object the file does not declare |
| `NotImplemented` | Created with the described value as data, with no behaviour behind it: `1012h` (TIME), a `1200h` that differs from the default SDO server |
| `SuppliedDefault` | A mandatory object (`1000h`, `1001h`, `1018h`) the file does not declare; the node keeps its placeholder |

The parsed model is available as `CanOpenDeviceDescription.Objects` (the `EdsDcfNet` object
dictionary) and `DeviceInfo`, and `ParseDiagnostics` lists what the parser repaired while reading
a lenient file. A tool that configures a *foreign* device from its DCF is the master-role round
(#131), not this loader, which shapes the node it is given to.

## SDO client and a peer's device description

`SdoUploadAsync` and `SdoDownloadAsync` do not accept an arbitrary index. Before any frame is
sent they check a peer EDS or DCF bound for that server:

```csharp
var peer = CanOpenDeviceDescription.Load("remote.eds");
master.BindPeerDeviceDescription(nodeId: 0x11, peer);

await master.SdoUploadAsync(0x11, 0x2000, 0x00);   // only if 2000h:00 is in remote.eds
```

`CanOpenDeviceDescription.Contains` is that check. A pair the file does not declare throws
`PeerSdoAccessException` (`PeerDescriptionLoaded` is true), including `1000h`, `1001h` and
`1018h` when the file leaves them out. `UnbindPeerDeviceDescription` drops the binding.

With **no** description bound for the server, only the three CiA 301 mandatory base objects are
transferred, at the sub-indices this stack implements for them:

| Object | Sub-indices allowed without a peer file |
| --- | --- |
| `1000h` Device type | `00h` |
| `1001h` Error register | `00h` |
| `1018h` Identity | `00h` and `01h` (vendor-id) |

`1018h:02`–`04` (product code, revision, serial) are not part of that minimum, and neither is
any optional object, including `1003h`. `PeerSdoAccessException.IsAllowedWithoutPeerDescription`
is that list. Anything else throws `PeerSdoAccessException` with `PeerDescriptionLoaded` false,
again before a frame is sent.

## PDO engine

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
profile), be readable for a TPDO or writable for an RPDO, and have the mapped width; the total
must fit 8 bytes; entries occupy the payload in sub-index order. A **dummy entry** — index
`0002h`–`0007h`, sub-index 0, the bit length of that static data type — occupies its bytes
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

**Heartbeat.** `1017h` ≠ 0 runs the producer. Each sub-index of `1016h` with a node-id in
1..127 and a non-zero time is a consumer whose timeout is armed immediately, so a producer that
never appears is reported too. `HeartbeatReceived` reports every heartbeat and boot-up on
`0x700 + id`.

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
| SYNC | consumed and produced | consumed and produced | neither; the producer keeps its cycle and resumes afterwards |
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

// FR-CO-002: 1000h:00 is one of the three objects readable without a peer file.
var value = await node.SdoUploadAsync(serverNodeId: 0x12, index: 0x1000, subindex: 0x00);

// FR-CO-007: bring the network up as an NMT master (PDOs flow in Operational only).
await node.SendNmtCommandAsync(NmtCommand.Start, targetNodeId: 0);
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
  CanOpenNodeOptions.cs
  CanOpenCobId.cs                     // pre-defined connection set, COB-ID control bits, restricted CAN-IDs
  CanOpenEvents.cs                    // event argument types
  ObjectDictionary.cs, OdEntry.cs
  Nmt/NmtState.cs                     // NMT enum + command specifier
  Sdo/                                // codec, abort codes, exception, block-transfer codec + mode enum
  Pdo/PdoMapping.cs                   // mapping entries, transmission-type enums and byte constants
  Emcy/EmcyMessage.cs                 // 8-byte encode/decode
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
