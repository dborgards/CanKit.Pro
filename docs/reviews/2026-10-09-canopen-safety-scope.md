# CANopen: Zuschnitt von CANopen Safety (CiA 304)

Stand: 09.10.2026, `main` @ `0041fdd`. Dritte Zuschnitt-Runde — CANopen, Rahmen für
sicherheitsrelevante Kommunikation nach CiA DSP 304. Die Geräterolle (FR-CO-013..028) und die
Master-/Tool-Rolle (FR-CO-029..034) sind entschieden und ausgeliefert
(`docs/reviews/2026-09-15-canopen-scope.md`, `docs/reviews/2026-09-26-canopen-master-tool-scope.md`).
Beide Runden haben CiA 304 ausdrücklich außen vor gelassen („Bit-granulares Mapping, CiA 304 und
CiA 305 bleiben außerhalb beider Runden").

Am 09.10. hat der Maintainer acht Punkte dieser Runde entschieden. Sie stehen unten als
Entscheidungen, nicht als Vorschläge. Das Design danach ist die Form, in der diese Entscheidungen
gebaut werden; die Posten-Tabelle ist die Quelle der `FR-CO`-Zeilen, die erst geschrieben werden,
wenn das Verhalten gebaut ist, das sie prüfen.

## Woher die Belege kommen

| Stufe | Was so belegt ist |
|---|---|
| **gemessen** | Öffentliche API und Quelltext auf `main` @ `0041fdd` |
| **Norm** | CiA DSP 304 Version 1.0 (01.01.2001), Volltext `dsp304.pdf` aus der Materialsammlung. Die Datei `304_v010100001_withdrawn.pdf` (Version 1.1, April 2003) ist eine Seite: *„Content moved to EN 50325-5:2010"*. EN 50325-5 liegt nicht vor |
| **Entscheidung** | Die acht Punkte des Maintainers vom 09.10., wörtlich angewendet |

Wo diese Datei einen Abschnitt zitiert (§8.1, §8.4.2.2, §9.5 …), ist DSP 304 V1.0 gemeint. Was
V1.1 oder EN 50325-5 anders regeln, ist unbekannt und bleibt es, bis der Text vorliegt
(Entscheidungsfrage 1).

## Was schon da ist

Gemessen auf `0041fdd`:

- `CanOpenNode` ist `internal sealed`. Aktor, `DeadlineScheduler`, Bus-Subscription
  (Filter `080h`–`77Fh` plus NMT und Flying-Master-IDs), `ObjectDictionary.WriteValidator`,
  `DeclareGuard`, `EntryWritten`, `OnEnterOperational`/`OnLeaveOperational`,
  `IsManagedCommunicationObject` und der EDS-Pfad für Kommunikationsprofil-Indizes sind intern.
  Öffentlich sind `ICanOpenNode` und `ObjectDictionary` ohne Hooks.
- Die PDO-Engine (`CanOpenNode.Pdo.cs`) ist das Muster, dem jedes aus dem OD abgeleitete
  Kommunikationsobjekt folgt: Records im OD, Validator vor dem Speichern, Runtime aus den
  Records neu gebaut, Timer auf `IProtocolActor.Schedule`, Fristen auf `IDeadlineScheduler`.
- `HeartbeatProducer`/`HeartbeatConsumer` sind eigene Klassen mit injiziertem Aktor bzw.
  Scheduler, vom Node verdrahtet. Das ist das zweite Muster im Paket.
- `c636a17` hat `DisposeAsync` als Extension geliefert statt `ICanOpenNode` zu erweitern:
  *„adding … to a published interface breaks every third-party implementer and would need a
  major release."*
- Die COB-IDs `101h`–`180h` liegen im Subscription-Filter, `001h` (GFC) nicht. Der
  Abort-Code `0800 0022h` existiert (`SdoAbortCode.DataCannotBeTransferredDeviceState`), die
  EMCY `8210h` wird vom RPDO-Pfad bereits erzeugt, CRC-16/XMODEM liegt in `SdoBlockFrames`.
- Keine Zeile in `src/`, `docs/` oder den Issues nennt SRDO; die Root-README führt
  „CANopen Safety" in der Roadmap.

## Entscheidungen

### 1. Ort: im Paket `CanKit.Pro.CANopen`

Kein eigenes Paket. Ein `CanKit.Pro.CANopen.Safety` müsste zuerst eine öffentliche
Erweiterungs-SPI am Node bekommen (Frame-Hook, Aktor-Post, Validator-Kette, NMT-Übergänge,
EDS-Hook) — ein zweites Designprojekt vor dem ersten, das eine Plugin-API einfriert, die sonst
niemand nutzt. `InternalsVisibleTo` zwischen zwei Shipping-Paketen wäre die Abkürzung und
unterläuft die API-Approvals. Präzedenz im Paket: CiA 302-2 (Flying Master, Boot-up) und
CiA 306 (EDS/DCF) liegen auch dort; ADR-4/10/11 trennen nur, was stack-übergreifend
wiederverwendbar oder vom Kern unabhängig ist. SRDO ist keins von beiden.

Namespace `CanKit.Pro.CANopen.Safety`, Ordner `Safety/`. Opt-in: ohne `SrdoCount > 0` existiert
kein Safety-Objekt im OD (§9.4: *„The implementation of CANopen Safety shall be allowed only in
safety devices"*).

### 2. Normbasis: DSP 304 V1.0 jetzt, Abgleich gegen V1.1 / EN 50325-5 als Issue

Gebaut wird gegen den einzigen vollständigen Text. README und SRS nennen V1.0 als Quelle. Ein
Issue hält fest, was beim Vorliegen von EN 50325-5:2010 abzugleichen ist (Entscheidungsfrage 1).

### 3. Rollen: Gerät **und** Master/Tool in dieser Runde

Geräteseite: eigener Node produziert und konsumiert SRDOs, OD `1300h`–`13FFh` per SDO
konfigurierbar, CRC- und Valid-Prüfung beim Übergang nach OPERATIONAL, EMCY `8210h`, GFC.
Master-/Tool-Seite: §9.2-Konfiguration eines Peers mit Zurücklesen, Vergleich und Quittung;
Verifikation nach §8.3.1 Schritt D; Zerlegen fremder SRDOs analog `ObserveForeignPdoAsync`.

### 4. Schritt D im Boot-up-Manager, DCF-gesteuert

Hat ein zugewiesener Slave (`1F81h`) eine gebundene Beschreibung mit mindestens einem
SRDO-Record, dessen Richtung ≠ 0 ist, verifiziert der aktive Master vor NMT Start
`13FEh`/`13FFh` und die Records gegen die aus der Datei berechnete Erwartung. Ohne solche
Beschreibung läuft der Boot-up wie bisher. Eine EDS führt keine Parameterwerte; Safety-Slave
ist nur, wer per DCF gebunden ist.

### 5. Struktur: `SrdoEngine` als eigene interne Klasse

Nach Vorbild `HeartbeatProducer`/`HeartbeatConsumer`: injizierter `IProtocolActor`,
`IDeadlineScheduler`, `ObjectDictionary`, Node-ID und Delegaten für Senden, EMCY und
Ereignisse. Der Node verdrahtet sie (Validator-Zweig, Dispatch, NMT-Übergänge, Rebuild). Die
reinen Teile — CRC, Paar-Codec, Parameter-Typen — sind eigene Klassen in `Safety/`.

### 6. Ein Pull Request

Gerät und Master/Tool landen in einem PR auf `feat/canopen-safety`. Commits je Schritt als
Conventional Commits ohne `!`.

### 7. Keine Erweiterung von `ICanOpenNode`

Dasselbe Muster wie `c636a17`, eine Stufe größer: die Node-Klasse implementiert zusätzlich
`ICanOpenSafety`, `CanOpenSafetyExtensions.Safety(this ICanOpenNode)` liefert sie; ein fremder
Node → `NotSupportedException`. Alle Safety-Mitglieder, auch die Ereignisse, leben dort. Das
Release ist ein Minor.

### 8. Akte hier, nicht unter `docs/superpowers/specs/`

Repo-Konvention: Entscheidungen und Posten stehen in `docs/reviews/`, die SRS-Zeilen zitieren
die Posten-Tabelle.

## Design

### Objektverzeichnis und Validierung (Gerät)

**Anlegen.** `CanOpenNodeOptions.SrdoCount` (0–64, Vorgabe 0). Bei n > 0 legt
`PopulateCommunicationProfile` an: `1300h`, `1301h`–`(1300h + n)`, `1381h`–`(1380h + n)`,
`13FEh`, `13FFh`. Eine EDS/DCF mit SRDO-Records hebt n auf den höchsten beschriebenen Record.
Alle sind `IsManagedCommunicationObject`: Reset stellt Power-on-Werte wieder her, `1010h`
speichert sie, `DeclareGuard` verbietet Neu-Deklaration. Buszugriff `rw` nur mit
`WritableCommunicationParameters`, sonst `ro` (Table 6, Fußnote *„These may be read only"*);
lokal immer schreibbar.

**Defaults** (§8.4.2.2): `1300h` = 0. Record n: sub0 = 6 `ro`; sub1 Richtung = 0; sub2 = 25;
sub3 SRVT = 20; sub4 = 254 `ro`; sub5/sub6 nur für Record 1 und Node-ID ≤ 64 `FFh + 2·ID` /
`100h + 2·ID`, sonst 0. Mapping: sub0 = 0, sub1–10h = 0 (16 Subindizes: 8 Objekte, Klartext und
invertiert im Wechsel). `13FEh` = 0. `13FFh` sub0 = n `ro`, sub1–n = 0.

**Validator-Zweig** in `ValidateCommunicationWrite`:

| Regel | Abort | Quelle |
|---|---|---|
| Write auf `1301h`–`1340h`, `1381h`–`13C0h`, `13FEh`, `13FFh` in OPERATIONAL | `0800 0022h` | §8.3.2.4 Fußnote 1 |
| sub1 ∈ 3–255 | `0609 0030h` | §8.4.2.2 „reserved" |
| sub2 = 0, sub3 = 0 | `0609 0030h` | Wertebereiche 1–65535 / 1–255 |
| sub4 jeder Write | `0609 0030h` | §8.4.2.2 Text |
| sub5 ∉ {257, 259…383 ungerade}, sub6 ∉ {258, 260…384 gerade}, sub6 ≠ sub5 + 1, Bits 31–11 ≠ 0 | `0609 0030h` | §8.4.2.2, Figure 7 |
| sub5/sub6 ändern, während sub1 ≠ 0 | `0609 0030h` | §8.4.2.2 „not allowed … while the SRDO exists" (Code wie SYNC/EMCY „not while valid") |
| COB-ID-Kollision mit gültigem PDO, SYNC, EMCY, anderem SRDO — in beide Richtungen | `0609 0030h` | Regel aus #133: eine CAN-ID trägt ein Kommunikationsobjekt |
| Mapping-Write (sub0 oder Eintrag), während sub1 ≠ 0 | `0601 0000h` | §8.4.2.2 „first the SRDO shall be deleted" (Code wie PDO) |
| Mapping sub0 ∉ {0, 2, 4 … 16} | `0609 0030h` | §8.4.2.2 „0: deactivated, 2, 4–128" — über 16 passt byte-aligned nicht in 8 Byte |
| Paar ungleich (sub 2k ≠ sub 2k−1) | `0604 0041h` | §8.4.2.2 „data not inverted / data inverted" |
| Eintrag existiert nicht, nicht mappbar, Breite kein Byte-Vielfaches | wie `ValidateMappingEntry` | CiA 301 §7.5.2.36 Schritt 3 |
| Summe der ungeraden Einträge > 8 Byte | `0604 0042h` | §8.1.3.1 „0 ≤ L ≤ 8" |
| Subindex 11h+ eines Mapping-Records | `0609 0011h` | existiert nicht |

Jeder akzeptierte Write auf `1301h`–`1340h`, `1381h`–`13C0h` oder `13FFh` setzt `13FEh` = 0
(§8.4.2.2 *„automatically 0"*). `1300h` nicht — nicht CRC-gedeckt. `13FEh` nimmt jeden Wert;
nur `A5h` bedeutet gültig.

**CRC `13FFh:n`** (§8.4.2.2): Reihenfolge Richtung (1 B), Refresh/SCT (2 B), SRVT (1 B),
COB-ID 1 (4 B), COB-ID 2 (4 B), Mapping sub0 (1 B), je Eintrag i = 1..sub0: Subindex i (1 B),
Mapping-Wert (4 B). Polynom x¹⁶ + x¹² + x⁵ + 1 = `1021h`. V1.0 nennt weder Startwert noch
Bytefolge: **Entscheidung** Startwert `0000h`, kein Reflect, kein XOR-out (CRC-16/XMODEM, wie
der SDO-Blocktransfer im Paket; `ComputeCrc16Xmodem` wandert nach `Safety/SrdoCrc`),
Mehrbyte-Felder MSB-first, wie D(x) sie listet (b15…b0). Beides steht im Abgleich-Issue.

**Konflikt in der Norm.** Table 6 führt `13FFh` als `ro`, die Eintragsbeschreibung als `rw`.
`rw` gewinnt: §9.2 verlangt, dass das Tool die Prüfsummen schreibt. sub0 bleibt `ro` und gleich
`SrdoCount`.

**Übergang PRE-OPERATIONAL → OPERATIONAL** (§9.5 letzte Regel, §8.3.1 D): je SRDO mit
Richtung ≠ 0 CRC gegen `13FFh:n` und `13FEh` = `A5h`. Mismatch → SRDO *konfigurationsungültig*:
tx sendet nicht, rx meldet sofort ungültig, Ereignis `SrdoStateChanged(n, ConfigurationInvalid)`.
Der NMT-Zustand bleibt CiA 301: der Node wird OPERATIONAL, nur die Safety-Objekte nicht. Kein
EMCY — die Norm nennt keins.

**EDS/DCF.** `1301h`–`1340h` / `1381h`–`13C0h` wie die PDO-Records (Löschen vor Mapping, jede
Abweichung ein `DeviceDescriptionFinding` mit Abort-Code), `1300h`/`13FEh`/`13FFh` als
verwaltete Variablen.

### `SrdoEngine` (Laufzeit, Gerät)

`Safety/SrdoEngine` (internal sealed, IDisposable). Konstruktor: `IProtocolActor`,
`IDeadlineScheduler`, `ObjectDictionary`, Node-ID, Delegaten `send(cobId, data)`,
`emitEmcy(EmcyMessage)`, `raise(Action, critical)`. Aktor-konfiniert. Methoden `Rebuild(n)`,
`EnterOperational(configValid)`, `LeaveOperational()`, `TryHandleFrame(cobId, data, isRtr)`,
`Trigger(n)`, `SendGfc()`, `State(n)`, `Dispose()`. Ohne Bus testbar.

**Producer** (§8.1, §8.1.3.1, §9.5): erster Zyklus nach 0,5 ms × Node-ID, dann alle
Refresh-Time; Timer auf `actor.Schedule` mit Generationszähler, jede Übertragung startet den
Zyklus neu. Nutzlast über die ungeraden Mapping-Einträge wie `BuildTpdoPayload`; Frame 1 auf
COB-ID 1 Klartext, Frame 2 auf COB-ID 2 bitweise invertiert, beide im selben Aktor-Durchlauf
(*„minimum delay"*). DLC 0–8. `Trigger(n)` sendet sofort und startet den Zyklus neu; außerhalb
OPERATIONAL nichts. Keine Inhibit-Time, kein RTR. Change-of-State: SRDO-gemappte Einträge gehen
in den CoS-Vorfilter, Option `EnableChangeOfStateSrdo` (Vorgabe true).

**Consumer** (§8.1.1, §8.1.3.1, §9.5): Zustand je SRDO `Valid`/`Invalid` + Grund, ausstehender
Frame 1, SCT- und SRVT-Deadline. Start in OPERATIONAL: Invalid (`NotReceived`), SCT scharf.
Frame 1 → ausstehend, SRVT scharf (ein zweiter Frame 1 ersetzt und startet neu). Frame 2 ohne
ausstehenden → Invalid (`OutOfOrder`); mit ausstehendem: gleiche Länge und byteweise `~`, sonst
Invalid (`Mismatch`). Länge < Mapping-Länge → Paar verworfen, EMCY `8210h`, kein Übergang;
Länge > Mapping-Länge → erste n Byte. Gültiges Paar: SRVT abgeschlossen, SCT neu gestellt,
Klartext in die gemappten OD-Einträge (wie `ActuateRpdo`), Übergang → `Valid`, dann
`SrdoReceived`. SRVT abgelaufen → `ValidationTimeExpired`; SCT abgelaufen →
`SafeguardCycleExpired`. Das nächste gültige Paar re-validiert; der sichere Zustand ist
Sache der Anwendung (§8.3.2 *„falls not in this scope"*). `LeaveOperational` → `NotOperational`.
Nur Übergänge werden gemeldet, als kritische Ereignisse.

**GFC** (§8.2): `SendGfc()` sendet `001h`, DLC 0, nur mit `1300h` = 1 und in OPERATIONAL,
sonst `InvalidOperationException`. Datenframe `001h` mit DLC 0 → `GlobalFailsafeCommandReceived`
(kritisch), nur mit `1300h` = 1. GFC trägt keine Absender-ID: auf einem Echo-Bus kommt der
eigene zurück und wird gemeldet — dokumentiert, nicht gefiltert (#249: kein verlässliches
Echo-Flag).

**Verdrahtung.** Subscription-Filter um `id == 001h`; `HandleIncoming` nach NMT/Flying-Master,
vor SYNC: `TryHandleFrame`. `ApplyNmtTransition` ruft `EnterOperational`/`LeaveOperational`;
`EntryWritten` auf `1301h`–`13C0h` → `Rebuild(n)` auf dem Aktor. RTR auf einer SRDO-COB-ID wird
geschluckt. Frames auf den COB-IDs eigener tx-SRDOs werden verworfen wie der eigene EMCY (#95).

### Öffentliche API, Gerät

`CanKit.Pro.CANopen.Safety`:

- `enum SrdoDirection : byte { None = 0, Transmit = 1, Receive = 2 }`
- `sealed class SrdoMapping` — `PdoMappingEntry`-Liste, `MaxEntries = 8`
- `readonly record struct SrdoCommunicationParameter(SrdoDirection Direction, TimeSpan RefreshOrSafeguardCycleTime, TimeSpan ValidationTime, uint CobId1, uint CobId2)`
- `static class SrdoCrc { ushort Compute(in SrdoCommunicationParameter, SrdoMapping) }`
- `enum SrdoInvalidReason { NotReceived, NotOperational, ConfigurationInvalid, SafeguardCycleExpired, ValidationTimeExpired, OutOfOrder, Mismatch }`
- `sealed class SrdoState { int SrdoNumber; SrdoDirection Direction; bool IsValid; SrdoInvalidReason? Reason; DateTime? LastValidAt }`
- `SrdoReceivedEventArgs(SrdoNumber, CobId, Payload, Timestamp)`, `SrdoStateChangedEventArgs(SrdoNumber, IsValid, Reason, Timestamp)`, `GlobalFailsafeCommandReceivedEventArgs(Timestamp)`
- in `CanOpenCobId`: `GlobalFailsafeCommand = 001h`, `SrdoFirstCobId = 101h`, `SrdoLastCobId = 180h`, `SrdoDefaultCobId1(nodeId)`, `SrdoDefaultCobId2(nodeId)` (Node-ID 1–64)

`ICanOpenSafety`, Gerätehälfte:

```csharp
int SrdoCount { get; }
void ConfigureSrdoProducer(int srdoNumber, SrdoMapping mapping, TimeSpan refreshTime,
    uint? cobId1 = null, uint? cobId2 = null);
void ConfigureSrdoConsumer(int srdoNumber, SrdoMapping mapping, TimeSpan safeguardCycleTime,
    TimeSpan validationTime, uint? cobId1 = null, uint? cobId2 = null);
void DeleteSrdo(int srdoNumber);
void CommitSafetyConfiguration();   // 13FFh:n je existierendem SRDO, dann 13FEh = A5h
Task TriggerSrdoAsync(int srdoNumber, CancellationToken cancellationToken = default);
SrdoState GetSrdoState(int srdoNumber);
Task SendGlobalFailsafeCommandAsync(CancellationToken cancellationToken = default);
event EventHandler<SrdoReceivedEventArgs>? SrdoReceived;
event EventHandler<SrdoStateChangedEventArgs>? SrdoStateChanged;
event EventHandler<GlobalFailsafeCommandReceivedEventArgs>? GlobalFailsafeCommandReceived;
```

`Configure*` ist eine OD-`Transaction` auf dem Aktor in Lösch-zuerst-Reihenfolge (sub1 = 0,
Mapping sub0 = 0, Paare, sub0 = 2k, sub2, sub3, sub5, sub6, sub1). COB-ID-Vorgabe nur für
SRDO 1 und Node-ID ≤ 64, sonst Pflichtargument. Ablehnung → `ArgumentException` mit Abort-Code
im Text; Nummer außerhalb 1..`SrdoCount` → `ArgumentOutOfRangeException`; in OPERATIONAL →
`InvalidOperationException`. Zeiten ganze Millisekunden, 1–65535 bzw. 1–255.
`CommitSafetyConfiguration` ist die §9.2-Quittung für die lokale Konfiguration; die Reihenfolge
CRCs → `A5h` ist zwingend, weil jeder `13FFh`-Write `13FEh` löscht. GFC-Freigabe ist der
OD-Write `1300h` = 1. `CanOpenNodeOptions`: `SrdoCount`, `EnableChangeOfStateSrdo`.

### Master/Tool

Typen: `PeerSafetyConfiguration` (`GlobalFailsafeCommandEnabled` + je SRDO-Nummer
`(SrdoCommunicationParameter, SrdoMapping)`; `FromDeviceDescription(description, nodeId)` liest
DCF-`ParameterValue`, Rückfall `DefaultValue`, `$NODEID` wie heute; Richtung 0 = gelöscht),
`PeerSafetyResult { bool Acknowledged; IReadOnlyList<PeerSafetyMismatch> Mismatches }`,
`PeerSafetyMismatch(Index, Subindex, Expected, Actual)`.

`ICanOpenSafety`, Master-Hälfte:

```csharp
Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration,
    CancellationToken cancellationToken = default);
Task<PeerSafetyResult> VerifyPeerSafetyConfigurationAsync(byte peerNodeId, PeerSafetyConfiguration expected,
    CancellationToken cancellationToken = default);
Task<ForeignSrdoObserveResult> ObserveForeignSrdoAsync(byte peerNodeId, uint cobId1,
    ReadOnlyMemory<byte> frame1, ReadOnlyMemory<byte> frame2, CanOpenDeviceDescription peerDescription,
    IForeignPdoSink sink, CancellationToken cancellationToken = default);
```

**§9.2** (`ConfigurePeerSafetyAsync`), jeder Transfer durch das Peer-SDO-Tor (FR-CO-029):
Count aus `13FFh:00`; je SRDO Download in Lösch-zuerst-Reihenfolge, nicht genannte SRDOs
sub1 = 0, dann `1300h`; `13FFh:n` = `SrdoCrc.Compute` (0 je gelöschtem); alles zurücklesen und
byteweise vergleichen, zusätzlich CRC aus den zurückgelesenen Werten gegen zurückgelesenes
`13FFh:n`; ohne Abweichung `13FEh` = `A5h` schreiben, zurücklesen → `Acknowledged`. Mit
Abweichung kein `A5h`, Ergebnis listet die Paare. Abort, Timeout, Tor → Ausnahmen wie
`SdoDownloadAsync`. Eine Operation je Peer gleichzeitig. Kein NMT-Kommando.

**Schritt D** (`VerifyPeerSafetyConfigurationAsync`): nur Uploads. `13FEh` = `A5h`; je
erwartetem SRDO `13FFh:n` gegen `SrdoCrc.Compute(expected)` sowie Comm-Record und Mapping gegen
die Erwartung (§8.3.1 D nennt die Parameter selbst). Ergebnis `Verified`.

**Boot-up** (`ConsiderStart`): für einen Safety-Slave (Entscheidung 4) wird vor NMT Start
`VerifyPeerSafetyConfigurationAsync(id, FromDeviceDescription(…))` angestoßen, Ergebnis auf den
Aktor zurückgepostet. Verifiziert → NMT Start. Nicht verifiziert oder Ausnahme →
`FlyingMasterChanged` mit neuem Signal `SlaveSafetyConfigurationInvalid`, kein Start dieses
Slaves; Pflicht-Slave (`1F81h` Bit 3) oder Simultanstart (`1F80h` Bit 1, ein Broadcast kann ihn
nicht ausnehmen) → Boot angehalten wie bei `SlaveBootTimeout` (`_bootHalted`, Reaktion nach
`1F80h` Bit 4/6, kein Selbststart). Simultanstart wartet auf alle laufenden Verifikationen.
Laufende Verifikationen werden bei Active → Standby, Reset und Dispose abgebrochen.

**Fremde SRDOs** (`ObserveForeignSrdoAsync`): der Aufrufer liefert das Paar (Frame 2 auf
cobId1 + 1). Record-Suche über `1301h`–`1340h:05` live vor Datei (Regeln FR-CO-030), Längen- und
`~`-Prüfung, Mapping (ungerade Einträge) live `1381h + n` vor Datei, Zerlegung in den
vorhandenen `IForeignPdoSink` mit `ForeignPdoKind.Srdo`, `PdoNumber` = SRDO-Nummer. SRVT/SCT
bewertet der Aufrufer, er hat die Zeitstempel. Nichts wird ins eigene OD geschrieben.

### Grenzfälle

- Node-ID > 64: keine Vorgabe-COB-IDs, Record 1 steht auf 0/0, `Configure*` ohne COB-IDs →
  `ArgumentException`.
- Leeres Mapping bei Richtung ≠ 0: erlaubt, L = 0.
- EMCY `8210h` mit Error-Register wie der RPDO-Pfad, einmal je Rebuild.
- `SrdoReceived` nicht-kritisch (verdrängbar unter `EventQueueCapacity`); Zustandswechsel und GFC
  kritisch, Schlüssel (SRDO-Nummer, Grund) faltet identische wartende Ereignisse.
- Reset Node/Communication: Safety-Objekte auf Power-on-Werte, `LeaveOperational`, CRC-Prüfung
  beim nächsten Übergang erneut. Stop: alle rx `NotOperational`, tx-Zyklen aus.
- Peer-Konfiguration bricht mittendrin ab: Ausnahme; der Peer steht mit `13FEh` = 0, weil jeder
  Write es gelöscht hat — sicher durch Konstruktion.
- Verifikation im Boot-up wirft: wie „nicht verifiziert", Ausnahme zusätzlich über
  `BackgroundExceptionOccurred`.
- Zeitbasis ist die Zeitquelle des Aktors; keine Plausibilisierung SRVT < SCT u. ä.

### Was nicht gebaut wird

- Diversitäre Redundanz (§9.5 *„two different ways"*, *„compared bit by bit in the
  application"*): die Bibliothek baut den invertierten Frame aus dem Klartext-Frame und prüft
  beim Empfang bitweise. Die Bibliothek ist nicht nach IEC 61508 / DIN V VDE 0801 entwickelt
  und erhebt keinen SIL-Anspruch. Steht in der README.
- Bit-granulares SRDO-Mapping, mehr als 8 Objekte je SRDO, 29-Bit-COB-IDs.
- Zeitbewertung in `ObserveForeignSrdoAsync`.
- Abweichungen von V1.1 / EN 50325-5 (Entscheidungsfrage 1).

### Tests

- Unit: `SrdoCrcTests` (XMODEM-Primitive gegen `"123456789"` → `31C3h`, kanonische Bytefolge
  eines Records als Golden-Vektor, Feldänderung ändert die CRC), `SrdoFramesTests` (Paar-Codec,
  Inversion, Längen, ungerade/gerade, Vorgabe-COB-IDs für Node 1, 32, 33, 64, 65),
  `SrdoEngineTests` (`ProtocolActor` + `ManualTimeSource` + `ObjectDictionary`, Send-Delegat
  fängt Frames; Producer-Takt, Anfangsverzögerung, `Trigger`; Consumer OutOfOrder, Mismatch,
  kurzer Frame, SRVT/SCT durch Uhr-Vorrücken, Re-Validierung, `LeaveOperational`),
  `CanOpenSafetyCommunicationProfileTests` (Anlegen, jeder Abort-Code, `13FEh`-Autoreset,
  OPERATIONAL-Sperre, Kollisionen beidseitig, Store/Restore/Reset),
  `CanOpenSafetyDeviceDescriptionTests` mit Fixture `safety.dcf`.
- Integration (Virtual Loopback, `EchoWorlds`): Producer ↔ Consumer; Master konfiguriert per
  `ConfigurePeerSafetyAsync` (Happy Path; Mismatch mit `PeerSdoLaboratory`); `Commit…` lokal;
  manipuliertes `13FFh` → `ConfigurationInvalid`; GFC; Schritt D (verifiziert → Start;
  manipuliert → Signal, kein Start; Pflicht-Slave → Halt); `ObserveForeignSrdoAsync` live vor
  Datei. Negative nur per Ordnungszeuge; keine Wanduhr (#92).
- Mutation vor Behauptung (CLAUDE.md): je Zeit- und Prüfpfad einmal die Quelle brechen und den
  roten Test sehen; Liste in der PR-Beschreibung.
- API-Approval aus `.received.txt`.

### Dokumentation

SRS §4.3.2 (Ist-Zustand, FR-CO-035 ff., Traceability), arc42 (§5 Baustein, ADR-12), Paket-README
(Abschnitt „CANopen Safety (CiA 304)", Coverage, „Not built", Layout, Disclaimer),
`docs/packages/index.md` (Standard-Spalte), Root-README (Roadmap ohne „CANopen Safety"),
csproj (`Description`, `PackageTags`).

### Die Posten

| | Was | Woher die Notwendigkeit kommt | Art der Arbeit |
|---|---|---|---|
| 1 | `1300h`, `1301h`–`1340h`, `1381h`–`13C0h`, `13FEh`, `13FFh` als verwaltete Objekte, Anlegen über `SrdoCount` bzw. Beschreibung, Defaults, Store/Restore/Reset | §8.4.2, Table 6 | Comm-Profile-Erweiterung nach PDO-Muster |
| 2 | Validator: Wertebereiche, OPERATIONAL-Sperre `0800 0022h`, Lösch-zuerst, Paare, Länge ≤ 8, Kollisionen beidseitig, `13FEh`-Autoreset | §8.3.2.4, §8.4.2.2, Regel aus #133 | Zweig in `ValidateCommunicationWrite` |
| 3 | `SrdoCrc`: Reihenfolge nach §8.4.2.2, CRC-16/XMODEM, MSB-first (Entscheidung) | §8.4.2.2; Startwert und Bytefolge Entscheidung | reine Klasse, Golden-Vektor |
| 4 | Prüfung beim Übergang nach OPERATIONAL, `ConfigurationInvalid` | §9.5, §8.3.1 D | Hook in `ApplyNmtTransition` |
| 5 | Producer: Zyklus, Anfangsverzögerung, Paar, `Trigger`, CoS | §8.1, §8.1.3.1, §9.5 | `SrdoEngine` |
| 6 | Consumer: Reihenfolge, Inversion, SRVT, SCT, Länge, EMCY `8210h`, OD-Schreiben, Ereignisse | §8.1.1, §8.1.3.1, §9.5 | `SrdoEngine` |
| 7 | GFC Producer/Consumer, `1300h` | §8.2 | `SrdoEngine`, Filter `001h` |
| 8 | `ICanOpenSafety` + Extension, Typen, Ereignisse, Options | Entscheidung 7 | API, Approval |
| 9 | EDS/DCF-Pfad für die Safety-Objekte, Findings | CiA 306, FR-CO-025..028 | Loader-Erweiterung |
| 10 | `ConfigurePeerSafetyAsync` nach §9.2 | §9.2, Figure 9 | SDO-Client-Ablauf durch das Tor |
| 11 | `VerifyPeerSafetyConfigurationAsync` | §8.3.1 D | SDO-Client-Ablauf |
| 12 | Schritt D im Boot-up, Signal `SlaveSafetyConfigurationInvalid`, Halt-Regel | §8.3.1 D, Entscheidung 4 | `ConsiderStart`/`TryFinishBoot` |
| 13 | `ObserveForeignSrdoAsync`, `ForeignPdoKind.Srdo` | Entscheidung 3 | nach `ObserveForeignPdoAsync` |
| 14 | README, SRS, arc42, Index, Roadmap, csproj | — | Doku |
| 15 | Issue: Abgleich V1.1 / EN 50325-5 | Entscheidung 2 | vor dem PR |

## Entscheidungsfragen

1. **Normtext.** Was ändern CiA 304 V1.1 (2003) und EN 50325-5:2010 gegenüber DSP 304 V1.0?
   Betroffen sind mindestens: Startwert und Bytefolge der CRC, der `ro`/`rw`-Konflikt bei
   `13FFh`, die Vorgabewerte, mögliche neue Objekte.

   Beantwortet (09.10.2026): V1.0 jetzt, Abgleich als Issue, sobald der Text vorliegt. Bis dahin
   nennen README und SRS V1.0 und das Issue.
2. **Paketgrenze.** Eigenes Paket oder im CANopen-Paket?

   Beantwortet (09.10.2026): im Paket, Entscheidung 1.
3. **Rollen.** Nur Gerät, nur Master/Tool oder beides?

   Beantwortet (09.10.2026): beides, Entscheidung 3.
4. **Schritt D.** Im Boot-up-Manager, nur als API oder beides?

   Beantwortet (09.10.2026): im Boot-up-Manager, DCF-gesteuert, Entscheidung 4.
5. **Struktur.** Partial am Node oder eigene Engine-Klasse?

   Beantwortet (09.10.2026): Engine-Klasse, Entscheidung 5.
6. **Lieferung.** Ein PR oder zwei?

   Beantwortet (09.10.2026): einer, Entscheidung 6.
7. **`ICanOpenNode`.** Erweitern (Major) oder Extension + eigene Schnittstelle?

   Beantwortet (09.10.2026): Extension + `ICanOpenSafety`, Entscheidung 7.
8. **Re-Validierung, CoS, Echo-GFC.** Automatische Re-Validierung nach Invalid; SRDO im
   CoS-Vorfilter; eigener GFC auf dem Echo-Bus wird gemeldet.

   Beantwortet (09.10.2026): alle drei wie vorgeschlagen (Abschnitte oben).
