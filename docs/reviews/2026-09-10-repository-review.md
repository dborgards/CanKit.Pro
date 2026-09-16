# CanKit.Pro – Repository-Review

**Datum:** 2026-09-10 · **Stand:** `main` @ `1270ed1` (v1.2.0, nach dem History-Rewrite) · **Umfang:** 9 Pakete, ~16.700 Zeilen
Bibliothekscode, ~13.000 Zeilen Tests, CI/Release-Pipeline, Dokumentation, Samples.

> Dieses Dokument ist ein Review von **CanKit.Pro selbst**. Das ältere
> `2026-07-14-deep-code-review.md` im selben Ordner betrifft das Upstream-Projekt CanKit und
> wird hier nur referenziert.

---

## Gesamteinschätzung

CanKit.Pro ist ein ungewöhnlich sorgfältig begründetes Repository. Fast jede Sperre, jeder
`Volatile.Read` und jede Dispose-Reihenfolge trägt einen Kommentar, der das konkrete Race
benennt; die Kommentare stimmen mit dem tatsächlichen Verhalten des Upstream-Pakets
(CanKit 0.5.6) überein. Die Schichtung L2 (RawCan-Demux, Actor, Deadlines, Addressing) →
L3/L4 (ISO-TP, J1939-TP, CANopen, J1939, UDS) ist konsequent durchgezogen, jedes Paket hat
einen Single-Writer-Actor, Timeouts laufen über einen zentralen `DeadlineScheduler`, und die
Tests treiben Races deterministisch mit Test-Doubles statt mit `Sleep`.

**Verifiziert in dieser Umgebung (Linux, .NET SDK 10.0.104):**

| Schritt | Ergebnis |
| --- | --- |
| `dotnet build` (netstandard2.0 + net10.0, Release) | erfolgreich, 0 Warnungen |
| `dotnet test` (3 Läufe hintereinander) | 403 / 403 bestanden, 44–46 s, keine Flakes |
| `dotnet pack` | 9 nupkg + 9 snupkg, README/MIT/Symbole in jedem Paket |
| Samples (6) | kompilieren gegen die aktuellen Projekte |
| CI auf `main` | grün (CI, Release, CodeQL) |
| Requirement-IDs in Code/Tests vs. SRS | 72 zitiert, alle definiert |

**Das Reifegefälle:** Die L2-Schicht ist produktionsnah und hat keine kritischen Befunde. Die
L3/L4-Pakete sind mit Version 1.2.0 auf nuget.org veröffentlicht worden, ihre READMEs, ihre
Paketbeschreibungen („Experimental“) und ihre Spezifikationskonformität sagen aber alle noch
„Pre-Release“. Gegen die eigene Implementierung funktionieren sie zuverlässig; gegen
Fremd-Stacks, Konformitätstester oder langsame reale ECUs treten die unten beschriebenen
Abweichungen sofort auf. Die Tests sind breit, decken aber überwiegend Happy-Paths und
bereits bekannte Regressionen ab, während die normativen Negativfälle (falscher Index in der
SDO-Antwort, RTS an die globale Adresse, verkürztes CF, Null-Seed) fehlen.

**Empfehlung in einem Satz:** Die zwei kritischen CANopen-Befunde und die als „Wichtig“
markierten L3/L4-Interop-Punkte vor der nächsten Minor-Version beheben, die veröffentlichten
READMEs auf den Release-Stand bringen und die Release-Pipeline an die 3-OS-CI koppeln.

---

## 1. Kritische Befunde

### 1.1 CANopen: Block-Upload-Server wird nie nachgetriggert → jeder Block-Upload über ~50 KB bricht ab

`src/CanKit.Pro.CANopen/CanOpenNode.SdoBlock.cs:829` armt die Server-Deadline beim Initiate mit
`SdoTimeout` (1 s). `RearmBlockServer` wird im gesamten Paket nur ein einziges Mal aufgerufen
(`:706`, Download-Pfad). `HandleBlockUploadServerStart` (`:838`) und
`HandleBlockUploadServerSubBlockAck` (`:844`) rearmen nicht. Ein Client, der einen
200-KB-Domain-Eintrag per Block-Upload liest, bekommt nach einer Sekunde (bei 1 Mbit/s ≈
50 KB) `SdoProtocolTimedOut`, obwohl die Übertragung gesund ist. Die Tests übertragen maximal
300 Byte und sehen das nicht.

*Fix:* `RearmBlockServer(session)` in beiden Upload-Handlern aufrufen; Server-Pfad konsequent
auf `SdoServerTimeout` umstellen (die Optionen-Doku behauptet das bereits).

### 1.2 CANopen: SDO-Client prüft Index/Subindex von Erfolgsantworten nicht → falsche Daten nach Timeout

`src/CanKit.Pro.CANopen/CanOpenNode.cs:1449-1478` (Upload-Antworten), `:1409-1423`
(Download-Ack) und die Block-Init-Antworten in `SdoBlock.cs:177-197, 275-301` werten nur den
Command Specifier aus; nur der Abort-Pfad (`:1391`) vergleicht `(index, subindex)`. Zudem wird
der Client-Timer „on any valid response byte“ (`:1400-1405`) rearmt.

Szenario: Request A auf 0x2000:00 läuft in den Timeout, der Anwender startet sofort Request B
auf 0x2001:00. Die verspätete Antwort des Servers auf A wird als Antwort auf B akzeptiert; B
liefert still den Wert von 0x2000. Dasselbe passiert, wenn ein zweiter Master (Diagnosetool)
denselben Server über den Default-SDO-Kanal anspricht, denn 0x580+node ist ein Broadcast.

*Fix:* Bei allen Initiate-Antworten `SdoFrames.ReadIndex(data)` gegen die Session prüfen, bei
Mismatch das Frame ignorieren (nicht abbrechen), Timer nur bei passenden Frames rearmen.

---

## 2. Wichtige Befunde

### 2.1 Querschnitt (betrifft alle Protokollpakete)

**Q1 · `IsOnCurrentActor` per `AsyncLocal` liefert Falsch-Positive** —
`src/CanKit.Pro.Actor/ProtocolActor.cs:67, 90, 228`. `AsyncLocal` fließt per
`ExecutionContext` in jedes `Task.Run` und jede `await`-Continuation, die aus einem
Actor-Callback gestartet wird. Genau das passiert in `IsoTpChannel.cs:544`,
`CanOpenNode.cs:1754/1786` und `J1939TpChannel.cs:890ff`: Pool-Threads sehen
`IsOnCurrentActor == true`. Folgen: `CanOpenNode.ConfigureTpdo/Rpdo` (`:370, :401`) führen
„inline“ auf einem Pool-Thread aus und mutieren Actor-Zustand parallel; der CoS-Echo-Guard
(`CanOpenNode.cs:1666`) verwirft legitime Writes; `ProtocolActor.Dispose()` überspringt den
Join.
*Fix:* `[ThreadStatic] static ProtocolActor? t_current` in `RunSafely` um `work()` setzen
(auch innerhalb des `SynchronizationContext.Send`-Delegates); die Sende-Tasks alternativ unter
`ExecutionContext.SuppressFlow()` starten.

**Q2 · Timer laufen auf der Wanduhr** — `ProtocolActor.cs:199, 341, 361` nutzen
`DateTime.UtcNow`. Ein NTP-Sprung um −1 h lässt jede Deadline (N_Bs, N_Cr, P2, SDO,
Heartbeat-Consumer) eine Stunde zu spät feuern, +1 h lässt alle sofort feuern.
*Fix:* `Stopwatch.GetTimestamp()`-Ticks (auf netstandard2.0 verfügbar).

**Q3 · Kein Fairness-Limit im Mailbox-Drain → Timer-Starvation unter Buslast** —
`ProtocolActor.cs:345-348` arbeitet die Mailbox bis zur Leere ab, erst danach
`FireDueTimers`. Alle RX-Reader posten pro Frame; bei gesättigtem Bus (~8 k Frames/s) kann
die Queue dauerhaft nicht leer werden und keine Deadline feuert mehr, ausgerechnet die
Fehlerklasse, die das Reliability-Paket ausschließen soll.
*Fix:* Pro Iteration nur einen `Count`-Snapshot abarbeiten oder fällige Timer alle N Items
einschieben.

**Q4 · `BusStateMonitor` postet pro Error-Frame** — `BusStateMonitor.cs:188-207`. In
Error-Passive-/Bus-Off-Stürmen sind das tausende Posts pro Sekunde in die Mailbox, genau
dann, wenn der Actor für Protokollarbeit gebraucht wird.
*Fix:* Koaleszieren mit `Interlocked.CompareExchange(ref _recheckPending, 1, 0)`.

**Q5 · Subscriptions verlieren `IsEcho` (und Timestamps)** — `CanBusService.cs:166` reicht
nur `e.CanFrame` weiter; `CanFrameView` hat kein Echo-Bit. Auf Echo-Bussen (Voraussetzung
für den echten `SendConfirmed`-Pfad) sieht jeder Subscriber seine eigenen Sendungen als RX.
J1939 (`J1939NodeImpl.cs:436`, NAME-Vergleich), CANopen (`IsOnCurrentActor` als Echo-Guard)
und der TP-Kanal (SA-Vergleich) haben sich bereits drei verschiedene Workarounds gebaut.
*Fix:* Echos per Default nicht zustellen und `includeEcho: true` als Opt-in anbieten, oder
den Item-Typ um `IsEcho`/Timestamps erweitern. Beides ändert `ISubscription` → ApiApprovals.

**Q6 · Echo-FIFO wird durch abgelaufene Einträge vergiftet** — `CanBusService.cs:352-372`
setzt bei Timeout/Cancel nur das `Tcs`; der Listenknoten wird erst im `finally` (`:344`)
entfernt, das wegen `RunContinuationsAsynchronously` später auf einem Pool-Thread läuft.
`TryMatchEcho` (`:413`) nimmt `list.First` ohne `IsCompleted`-Prüfung. Ein byte-identischer
Send B verliert so sein Echo an den bereits abgelaufenen Send A und läuft ebenfalls in den
Timeout (Kaskade). Zusätzlich ignoriert `PendingKey` Flags und FrameKind (Std-ID 0x100 und
Ext-ID 0x100 mit gleichem Payload matchen sich gegenseitig).
*Fix:* Knoten im Registration-Callback unter `_pendingGate` entfernen; completed Einträge
in `TryMatchEcho` überspringen; Flags/FrameKind in den Key aufnehmen.

### 2.2 ISO-TP (`CanKit.Pro.IsoTp`)

**I1 · Stale STmin-Timer feuert in den nächsten Send** — `IsoTpChannel.cs:719-729` verwirft
das Timer-Handle; `SendNextConsecutiveFrame` (`:498`) prüft nur `_tx is null`, nicht ob es
dieselbe `TxState` ist. Cancel während STmin, sofort neuer `SendAsync` (UDS-Retry,
TesterPresent) → der alte Timer sendet ein CF mit den Bytes des neuen PDUs.
*Fix:* Handle in `TxState` speichern, in allen Abschlusspfaden disposen, im Callback
`ReferenceEquals(_tx, tx)` prüfen.

**I2 · Unbegrenzte Reassembly-Allokation auf CAN-FD** — `MaxPduLength` ist für FD
`int.MaxValue` (`:92-95`). Ein einziger Escape-FF `10 00 7F FF FF 00` führt zu
`new byte[~2 GB]`; der OOM-Catch (`:864`) greift nur, wenn die Allokation fehlschlägt.
*Fix:* `MaxReceivePduLength`-Option (z. B. 64 KiB) und `FC(OVFLW)` vor der Allokation.

**I3 · Keine CAN_DL-Validierung von FF/CF** — `:855-857, :911-922`. Ein verkürztes CF (DLC 5
statt 8) wird mit 4 Bytes akzeptiert; die Reassembly endet „erfolgreich“ mit verschobenem
Inhalt. ISO 15765-2 verlangt Verwerfen; für UDS heißt das korrumpierte DID-Daten ohne Fehler.

**I4 · UDS-P2 endet erst nach vollständiger Reassembly** — `UdsClientImpl.cs:786-798`.
Nach ISO 14229-2 endet P2 mit dem ersten Frame der Antwort. Mit dem im README empfohlenen
`P2ClientMax = 50 ms` scheitert jede Multi-Frame-Antwort, die länger als 50 ms auf dem Bus
liegt (4-KB-DID mit STmin 5 ms ≈ 3 s).

**I5 · SecurityAccess: „bereits entsperrt“ ist ein Null-Seed, kein leerer Seed** —
`UdsClientImpl.cs:311` prüft `seedLen == 0`; ISO 14229-1 definiert Seed aus lauter 0x00.
Reale ECUs antworten auf den dann gesendeten Key mit NRC 0x24.

### 2.3 J1939-TP und J1939-Node

**J1 · DA wird für TP.CM-Steuerframes nicht geprüft** — `J1939TpChannel.cs:313` lässt
`da == self || da == 0xFF` durch, `HandleRxTpCm` (`:355`) ignoriert `da` danach komplett.
Ein RTS an 0xFF öffnet auf *jedem* Knoten eine Session, alle antworten mit CTS und nach Tr
mit Abort (Abort-Sturm). Global gesendete CTS/EOMA/Abort beenden fremde TX-Sessions.

**J2 · Empfänger-Timeout CTS→erstes DT nutzt Tr (200 ms) statt T2 (1250 ms)** —
`:424-426, :1153-1159`. Ein spezifikationskonformer, langsamer Sender (300 ms bis zum ersten
DT) wird abgewiesen. Sendeseitig ist „T2“ (`:821`) tatsächlich T3; nur Namensdrift.

**J3 · Parallele BAM-Sends verlieren Daten und melden trotzdem Erfolg** — TX-Sessions sind
nach `(DA, PGN)` verschlüsselt (`:141, :160`); zwei gleichzeitige `SendBamAsync` mit
verschiedenen PGNs verschachteln ihre TP.DT-Frames (kein PGN im DT), jeder konforme
Empfänger bricht die erste ab, beide Tasks melden Erfolg. Über `StartPeriodicSend` mit
>8-Byte-Payload ohne Wissen des Anwenders erreichbar. Die Interface-Doku verspricht
Serialisierung pro Peer, die es nicht gibt.

**J4 · Abort-Reason-Codes weichen von J1939-21 Tabelle 7 ab** —
`J1939TpAbortReason.cs:18-45`: „Session already open“ sendet 7 (Standard: 1), falsche
DT-Sequenznummer sendet 5 (Standard: 7), `ReceiverAbort = 250` liegt im reservierten Bereich,
die Beschreibung von Code 1 ist erfunden. Peer-Stacks interpretieren unsere Aborts falsch.

**J5 · Keine Antwort auf „Request for Address Claimed“** — `J1939NodeImpl.cs:925-949`
reicht Requests nur an `MessageReceived`. J1939-81 verlangt Address Claimed bzw. Cannot
Claim als Antwort; Netzwerkmanagement-Tools sehen diesen Knoten nicht.

**J6 · Adressverlust nach erfolgreichem Claim ohne Arbitrary-Fallback** —
`J1939NodeImpl.cs:488-505` geht direkt nach `CannotClaim`, auch bei
`ArbitraryClaimingEnabled`. Das Gerät wird dauerhaft stumm, obwohl es sich umadressieren
dürfte.

**J7 · `CancellationTokenRegistration` pro Send wird nie freigegeben** —
`J1939TpChannel.cs:176-193`. Mit einem app-weiten Shutdown-Token wächst die
Registrationsliste pro Multi-Frame-Send; über Tage ein messbarer Leak.
`ClaimAddressAsync` macht es richtig vor.

**J8 · SPN-Extraktion ignoriert J1939-71-Sentinelwerte** — `J1939Spn.cs:70-75`. SPN 190 =
0xFFFF („not available“) wird als 8191,875 rpm geliefert; 0xFF bei 1-Byte-SPNs als
physikalischer Wert. Signed-SPNs fehlen. Anwendungen bekommen plausible, falsche Messwerte.

### 2.4 CANopen (zusätzlich zu 1.1 / 1.2)

**C1 · `ccs=1, e=0, s=0` (0x20) wird als Expedited fehlinterpretiert** —
`CanOpenNode.cs:931, :1106`; `PdoMapping.cs:338`. Segmentierter Download ohne
Size-Indicator ist legal; hier werden 4 Nullbytes committed und die folgenden Segmente mit
`CommandSpecifierInvalid` beantwortet. Spiegelbildlich ignoriert der Client Upload-Init
0x40 ohne Size-Indicator (`:1458`, exakt 0x41), der Lazy-Grow-Code dahinter ist unerreichbar.

**C2 · Block-Empfang: NACK-Sturm und Initiate-Heuristik** — `SdoBlock.cs:673-683, :374-385`
senden bei jedem Segment mit falscher Seq sofort einen ACK (bis 126 pro verlorenem Segment);
`:534-544` behandelt ein Segment, dessen Byte 0 wie ein Initiate aussieht (seq 0x21, 0x23 …),
als neues Initiate und killt die laufende Session. Ein verlorenes Segment ist auf einem
realen Bus der Normalfall.

**C3 · Dynamisches Mapping ignoriert die Subindex-Position** — `PdoMapping.cs:505` hängt
jeden Entry-Write an, unabhängig vom Subindex. Sub0 wird als 4-Byte-U32 statt UNSIGNED8
hochgeladen (`:359-383`), ein Test zementiert das
(`CanOpenDynamicMappingTests.cs:87`). Dummy-Objekte 0x0001–0x0007 fehlen.

**C4 · RPDO-COB-IDs außerhalb 0x080..0x77F kommen nie an** — statischer
Subscription-Filter (`CanOpenNode.cs:209-215`), keine COB-ID-Validierung in
`ConfigureRpdo/Tpdo`. Bit 31 erzeugt via `unchecked((int)cobId)` einen negativen Frame.

**C5 · `PdoMapping` ist mutabel und wird per Referenz geteilt** — `Entries` alloziert bei
jedem Zugriff (`Pdo/PdoMapping.cs:107`), auf dem Hot-Path pro PDO; Änderungen nach
`ConfigureTpdo` rennen gegen den Actor.

**C6 · Node-Guarding: Bootup vergiftet die Toggle-Baseline** — `NodeGuarding.cs:174-175`;
die erste echte Antwort nach Reset (toggle 0) wird als Duplikat verworfen. Der Test umgeht
das mit `Task.Delay(100)`. Life-Guarding auf Producer-Seite (§7.2.8.3.3) fehlt, das README
behauptet es.

**C7 · `SdoTransferMode.Expedited/Segmented` werden nicht durchgesetzt** —
`CanOpenNode.cs:490-499` unterscheidet nur Block/nicht-Block.

**C8 · README überzeichnet den Umfang** — „Every CiA 301 Must, Should and Could
requirement … is implemented“ (`README.md:12`). Es fehlen u. a. OD-Objekte 0x1005/0x1006/
0x1016/0x1017/0x1400ff/0x1800ff, Transmission-Types 0 und 2–240, RTR-PDOs, Inhibit-Time,
Sync-Window, Dummy-Mapping, Life-Guarding, Reset-Communication-Semantik.

### 2.5 Build, Release, CI

**B1 · Die in den nupkg ausgelieferten READMEs behaupten „nicht veröffentlicht“** —
`src/Directory.Build.props:13` packt jedes `README.md` ins Paket. nuget.org zeigt für 1.2.0
daher: `IsoTp/README.md:14,128,132` („IsPackable=false“, „pre-release (0.1.x), codec-only“,
„not published to nuget.org yet — reference the project from a clone“), `J1939Tp:10,54`,
`CANopen:110`, `J1939:3,91,95`, `Uds:8,87`. Das ist die sichtbarste Stelle des Projekts.
Dazu falsche Pfade (`tests/CanKit.Tests/...`, `SRS-CanKit.md`) und die Uds-Paketbeschreibung
listet 0x34–0x37 nicht, obwohl implementiert.

**B2 · Release-PAT liegt während `npm ci`, restore, build, test im Repo** —
`release.yml:37,43` (`persist-credentials: true` mit `RELEASE_TOKEN`). semantic-release baut
die Push-URL selbst aus `GITHUB_TOKEN`; das Persistieren ist unnötig. Zusätzlich hat der
Standard-`GITHUB_TOKEN` `contents/issues/pull-requests: write` (`:17-21`), obwohl alle
Schreibzugriffe über den PAT laufen.
*Fix:* `persist-credentials: false`, kein `token:` im Checkout, PAT nur im `env:` des
Release-Steps; Standard-Token auf `contents: read` + `id-token: write`.

**B3 · Release ist nicht an die 3-OS-CI gekoppelt** — `release.yml` läuft parallel zu
`ci.yml` und testet nur auf Ubuntu. Die Windows/macOS-Timing-Fehler, die
`docs/migration-from-legacy.md:162-184` selbst beschreibt, blockieren kein Release.
*Fix:* Required Status Checks im Ruleset oder `workflow_run`-Trigger.

**B4 · `docs/release-process.md:114-116` beschreibt die Tag-Reihenfolge falsch** —
semantic-release erzeugt und pusht den Tag *nach* `prepare` und *vor* `publish`. Ein
fehlgeschlagener `dotnet nuget push` hinterlässt Tag + CHANGELOG-Commit ohne Pakete; der
nächste Lauf überspringt die Version. Es gibt keinen dokumentierten Recovery-Pfad
(„Manual release: there isn't one“). Das Runbook nennt zudem `NUGET_API_KEY` und
`github-actions[bot]`-Bypass, der Workflow nutzt OIDC + `RELEASE_TOKEN`.

**B5 · Dependabot-npm-Bumps lösen NuGet-Releases aus** — `dependabot.yml:31-32` Prefix
`build(deps)` + `.releaserc.json:13` (`build`/`deps` → patch): jede Aktualisierung des
Release-Toolings veröffentlicht alle 9 Pakete ohne Codeänderung (Beleg: CHANGELOG 1.1.0).
*Fix:* npm auf `chore(deps)`.

**B6 · Third-Party-Actions per mutable Tag** — `NuGet/login@v1` (steuert die
Publishing-Credential), `micnncim/action-label-syncer@v1` (unmaintained), `actions/*@vN`.
Auf SHAs pinnen; Dependabot hält sie aktuell.

**B7 · netstandard2.0 wird gebaut, aber nie ausgeführt** — Tests sind net10.0-only
(`tests/Directory.Build.props:6`), obwohl SRS NFR-004/CON-001 eine TFM-Matrix fordern und
als Motiv genau einen ns2.0-only-Bug nennen. Ein Fehler in der ns2.0-Assembly bleibt
unsichtbar. *Fix:* Testprojekt auf `net10.0;net48` multitargeten, `net48` nur auf Windows.

**B8 · `Reliability` zieht unnötige Runtime-Dependencies** —
`CanKit.Pro.Reliability.csproj:10-12` referenziert die drei Polyfills unbedingt (mit
redundantem `VersionOverride`); der Code nutzt keine davon. Im nuspec landet
`Microsoft.Bcl.AsyncInterfaces` in der net10.0-Gruppe (verifiziert).

**B9 · Approval-Rendering hat blinde Flecken** — `PublicApiSurfaceTests.cs:98-152` rendert
weder Basistypen/Interfaces noch `sealed`/`abstract`, Parameter-Defaults, `in`/`ref`/`out`,
Attribute oder Nullability. Eine Klasse `sealed` zu machen oder ein Interface zu entfernen
passiert den Test. *Fix:* `PublicApiGenerator` oder ergänzend `EnablePackageValidation` mit
Baseline 1.2.0 (prüft zusätzlich die TFM-Konsistenz).

**B10 · Privatprotokoll-Paket in der öffentlichen Historie — teilweise erledigt** — Der
ursprüngliche Befund: Das Paket wurde zwar aus `main` entfernt, seine Quellen blieben aber
über den Migrations-Commit, den Entfernungs-Commit und die Tags v1.0.0 / v1.1.0 samt
Release-Archiven öffentlich abrufbar.

Am 2026-09-10 wurde die Historie umgeschrieben: Projektverzeichnis und Testdatei sind aus
allen Commits, Trees, Branches und Tags entfernt, der Produktname ist in Dateiinhalten und
Commit-Messages durch einen neutralen Platzhalter ersetzt, und die 21 Commit-Links im
CHANGELOG zeigen auf die neuen SHAs, damit das Repository nicht mehr in die verwaiste
Historie verweist. Verifiziert: kein Treffer mehr in Pfaden, Blobs, Commit-Messages,
Autorenfeldern oder Tags; Build und 403/403 Tests unverändert grün.

**Was bewusst offen bleibt:** GitHub führt `refs/pull/<n>/head` als serverseitige Refs, die
ein Force-Push nicht berührt und der Repository-Eigentümer nicht löschen kann. Zwölf dieser
Refs tragen den vollständigen Quellbaum, und die Dateiansicht des Pull Requests, der das
Paket entfernt hat, zeigt den Quelltext weiterhin im Diff. Zusätzlich bleiben die
Vor-Rewrite-Objekte über ihre SHA erreichbar, bis GitHub eine Garbage Collection ausführt.
Beides beseitigt nur ein Ticket beim GitHub-Support (Purge der PR-Refs plus gc) oder ein
Neuanlegen des Repositories. Diese Restexposition wurde nach Abwägung in Kauf genommen: Der
Inhalt ist ein generisches SPI ohne Protokolldetails — das csproj sagt selbst „no service
IDs, frame layouts, session logic, or secrets“, und alle Hex-Werte im entfernten Code sind
synthetische Test-Bereiche —, das Paket war `IsPackable=false` und damit nie auf nuget.org,
und das Repository hatte zum Zeitpunkt des Rewrites null Forks und null Stars.

---

## 3. Geringfügige Befunde (Auswahl)

**RawCan / Actor / Reliability / Addressing**
- `Transmit` läuft unter `_pendingGate` (`CanBusService.cs:315-322`); bei blockierenden
  Vendor-Treibern hängt der Adapter-RX-Thread in `TryMatchEcho` und damit alle
  Subscriptions. `TryMatchEcho` alloziert pro Echo-Frame ein `byte[]`.
- Bei N matchenden Subscriptions wird der Payload N-mal kopiert (`Subscription.cs:129`);
  eine lazy angelegte unveränderliche Kopie würde reichen. L3/L4-Reader kopieren danach
  ein *zweites* Mal (`IsoTpChannel.cs:347`, `J1939TpChannel.cs:305`, `CanOpenNode.cs:588`),
  die Kommentare dort beschreiben eine Gefahr, die die Subscription bereits beseitigt.
- `CanIdFilter.Range/Mask` validieren nicht gegen den ID-Raum: `Range(0x18FEF100, …)` mit
  vergessenem `idType` matcht nie, ohne Fehler (`CanIdFilter.cs:68-82`).
- `Interlocked.Exchange` auf `volatile`-Feld (`Subscription.cs:87, 94`, CS0420).
- Callback-`Subscribe` (`CanBusServiceExtensions.cs:488-507`): `onNext`-Exceptions werden
  für fremde `ICanBusService`-Implementierungen verschluckt; `Dispose` wartet bis 2 s und
  leakt den Pump-Task danach.
- `ProtocolErrorCodes.cs:377-394` belegt `CanKitErrorCode` 6002..6005, die upstream nicht
  existieren; Kollisionsrisiko bei jedem CanKit-Bump.
- `ProtocolActor`: `(int)ms` rundet ab → bis 1 ms Busy-Spin vor jedem Timer-Fire
  (`:342, :449`); O(n)-Timerliste mit Leichen (jedes `Deadline.Rearm` hinterlässt einen
  gecancelten Eintrag bis zur ursprünglichen Fälligkeit); `Dispose` gibt nach 5 s still auf.
- `Deadline` nach Actor-Dispose bleibt ewig „Pending“ und ist von einer gesunden nicht
  unterscheidbar; die Doku nennt ein `Cancel()`, das nicht existiert
  (`DeadlineScheduler.cs:194`, `README.md:719`).
- `IsDegraded()` behandelt `Unknown` als gesund (`BusStateExtensions.cs:20-22`).
- `J1939Id.Decompose(uint)` prüft nicht auf Extended-Frame; `ComposePgn` verwirft still das
  Low-Byte einer PDU1-PGN; `J1939Name` hat keine LE-(De)Serialisierung, weshalb
  `J1939NodeImpl.cs:434` `BitConverter.ToUInt64` (plattform-endian) nutzt.
- Zweisprachige Doc-Comments mit chinesischem Teil (`ProtocolActor.cs:78-81`,
  `CanBusService.cs:23`, `DeadlineScheduler.cs:10`) sind in IntelliSense sichtbar und
  passen nicht zu „not affiliated with the CanKit project“.

**ISO-TP / UDS**
- FF mit FF_DL ≤ SF-Kapazität wird akzeptiert und mit FC beantwortet statt ignoriert
  (`IsoTpChannel.cs:876-887`); ein Test zementiert das
  (`IsoTpChannelIntegrationTests.cs:258-276`).
- BS/STmin werden aus jedem FC.CTS übernommen statt nur aus dem ersten (`:989-995`).
- Kein Schutz gegen Selbstempfang bei `TxCanId == RxCanId` auf Echo-Bussen.
- `DiscardPendingPdus` aus einem `BackgroundExceptionOccurred`-Handler deadlockt den Actor
  (`:229-251`, `:1086`).
- `DiagnosticSessionControlAsync(byte)` maskiert still mit 0x7F; `SendRawAsync` mit
  Suppress-Bit wartet auf eine Antwort, die nie kommt; NRC 0x21 ohne Retry-Semantik;
  `UdsTimeoutException.Elapsed` liefert mal Budget, mal Ist-Zeit; kein
  Functional-Addressing-Pfad im UDS-Client (funktionales `3E 80` nur per Hand).
- `UdsClientImpl.Dispose` disposed nach 5-s-Lock-Timeout das Semaphore unter dem Halter.

**J1939**
- PDU1-PGN aus TP.CM wird nicht normalisiert (Peer mit DA im Low-Byte erreicht unsere
  TX-Session nie); CTS-Retransmit-Anforderung wird mit Abort beantwortet statt bedient;
  `maxPacketsPerCts == 0` als „unbegrenzt“ gewertet.
- `DatagramReceived` wird vor dem Inbox-Write auf dem Actor aufgerufen
  (`J1939TpChannel.cs:936-947`); ISO-TP hat genau das bereits umgebaut.
- Pseudozufallsverzögerung 0–153 ms (J1939-81 §4.4.4.3) vor Cannot Claim / Re-Claim fehlt;
  zweiter `ClaimAddressAsync` cancelt still den ersten; jeder Claim-Round erzeugt zwei
  Threads (voller Arbitrary-Scan = 240 Thread-Erzeugungen).
- README ordnet FR-TP-032/033 vertauscht zu; `J1939NodeOptions.cs:63` beschreibt eine
  Factory-Überladung, die nicht existiert.

**CANopen**
- Übertragung von exakt `MaxSdoTransferBytes` scheitert mit `OutOfMemory`
  (`SdoBlock.cs:387-405`); `CancellationTokenRegistration` pro SDO-Operation nie disposed
  (`CanOpenNode.cs:1271-1282`); `blksize == 0` still durch Default ersetzt statt Abort;
  RTR mit DLC 0 (`NodeGuarding.cs:112`); eigene Echo-Frames nicht gefiltert
  (Selbst-Start per eigenem NMT-Broadcast); `SdoAbortException` nutzt `ProtocolPeerAbort`
  auch für lokale Timeouts; SDO-Server verwirft in Stopped, Sessions/SYNC/EMCY laufen aber
  weiter; ~10 Doku/Code-Abweichungen in XML-Docs (`ICanOpenNode.cs:42-44, 60-62`,
  `NmtState.cs:4-6`, `ObjectDictionary.cs:7` „Thread-safe“ vs. ungesicherte Lesezugriffe
  in `OdEntry.cs:83-92`).

**Repo / Docs**
- `.gitattributes:4-5` (CRLF für csproj/sln) vs. `.editorconfig:6` (LF für alles).
- `global.json` verlangt SDK ≥ 10.0.100, `CONTRIBUTING.md:30` verspricht „.NET 8 or newer“;
  `net8.0`-Reste in SECURITY, THIRD-PARTY-NOTICES, arc42, SRS, getting-started.
- README `:117-120` Roadmap „Publish the L3/L4 packages“ ist erledigt; das
  „Two minutes“-Snippet kompiliert ohne zwei fehlende `using`s nicht
  (`CanKit.Abstractions.API.Can.Definitions`, `…Common.Definitions`);
  `docs/getting-started.md:164-181` skizziert APIs mit nicht existierenden Namen.
- „Only the four published L2 packages are tracked“ (`ApiApprovals/README.md:17`),
  `bug_report.yml` und `labels.yml` kennen nur L2-Pakete; CHANGELOG-Platzhalter „No
  release has been published yet“.
- `GitVersion.yml:28-32` (`assembly-versioning-scheme`) ist wirkungslos, CI-Builds auf
  `main` heißen `1.2.1+n` und sehen wie ein Release aus (`label: ci` wäre ehrlicher).
- Coverage wird gesammelt (`ci.yml:94`), aber nirgends ausgewertet. `ci.yml:133` behauptet,
  der Pack-Job beweise README/License/Symbole; er packt nur.
- 0 Warnungen bei `AnalysisLevel=latest` ist der richtige Moment für
  `TreatWarningsAsErrors` und `EnforceCodeStyleInBuild`.
- `docs/reviews/2026-07-14-deep-code-review.md` (Upstream-Review) hat nicht den
  „externer Kontext“-Header, den `migration-from-legacy.md:44` für alle drei übernommenen
  Dokumente behauptet.

---

## 4. Testqualität

**Stärken:** 403 Tests, dreimal hintereinander grün, ~45 s Wallclock trotz abgeschalteter
Collection-Parallelität. `ControllableBus`/`EchoCapableOptions` machen Echo, Bus-State und
TX-Ablehnung zu expliziten Eingaben; der `PoisoningBufferAllocator`-Test beweist den
Lease-Hazard deterministisch; `DelayingConfirmService`/`HoldConsecutiveFrameConfirmService`
parken Confirmations gezielt; Bugbot-Regressionsfälle sind sauber referenziert;
`migration-from-legacy.md` erklärt jede geänderte Timing-Assertion.

**Lücken (die wichtigsten):**
- **Der FIFO-Test für Echo-Matching kann nicht fehlschlagen** (`TxConfirmTests.cs:181-196`):
  `ControllableBus` liefert das Echo synchron *innerhalb* von `Transmit` unter dem Lock; die
  Pending-Liste hat nie mehr als einen Eintrag. FR-RAW-031 ist faktisch ungetestet, der
  Kommentar im Test gibt das zu.
- `Send_Faults_On_Codec_Throw_And_Channel_Remains_Usable`
  (`IsoTpChannelIntegrationTests.cs:715-751`) erreicht den Actor nie (`SendAsync` wirft
  schon in der Vorprüfung) und akzeptiert drei Exception-Typen.
- `RebindTransport_DoesNotDeliverBamMoreThanOncePerRebind` (`J1939NodeTests.cs:799-875`)
  prüft nur `received <= sent`; eine Regression, die alle BAMs verliert, besteht.
- `Sdo_ExpeditedInitiate_ClearsStaleSegmentedServerSession` besteht auch ohne Zustellung
  der Stray-Segmente; `Dispose_Unblocks_Pending_ReceiveAsync` akzeptiert jede Exception.
- Drop-Oldest (FR-RAW-011) wird nirgends asserted; `TryRead`, `Reconfigure(predicate)`,
  `Reconfigure` nach Dispose fehlen.
- CRC-16 im Block-Transfer nur symmetrisch geprüft; ein Known-Answer-Test
  (`"123456789"` → `0x31C3`) fehlt; `SdoFrames`/`SdoBlockFrames` haben keine Codec-Unit-Tests.
- Timing-basierte Negativtests (`Task.Delay(30)` dreimal in
  `CanOpenNodeIntegrationTests.cs:662-672`, `Task.Delay(50)` vor `cts.Cancel()` in
  `J1939TpTests.cs:528`) sind unter CI-Last Flake-Kandidaten, auch wenn sie hier dreimal
  bestanden haben.
- Fehlende normative Negativtests: SDO-Antwort mit falschem Index (1.2), RTS an 0xFF (J1),
  verkürztes CF (I3), Null-Seed (I5), P2 bei Multi-Frame-Antwort (I4), zwei parallele BAMs
  (J3), Abort-Bytes gegen die Standardtabelle (J4), 0x20/0x40-SDO-Frames (C1),
  Mapping-Subindex-Reihenfolge (C3), 1000 Fault-Hints → ≤ 1 Post (Q4).

---

## 5. Was gut gelöst ist

1. **RX-Hot-Path in `CanBusService`**: Copy-on-write-Snapshot, kein Lock, keine Allokation
   pro Frame außer der nachweislich notwendigen Payload-Kopie, Prädikat-Isolation pro
   Subscription mit Fault-Kanal statt Abbruch des Adapter-Multicasts.
2. **`ProtocolActor`-Lifecycle**: `_disposeGate` schließt das Post/Dispose-Race vollständig;
   `FinalDrain`-Semantik; `Send` statt `Post` im SyncContext-Modus mit Begründung;
   Timer-Inserts bewusst am Kontext vorbei. Die Regressionstests treffen exakt die
   riskanten Pfade.
3. **`Deadline`-Zustandsmaschine**: CAS-basierte Einmal-Auflösung plus Generation-Counter,
   ehrlich als „best-effort, not linearizable“ dokumentiert.
4. **ISO-TP FC-Deferral**: `DeferredFcs` löst die drei echten Race-Fenster (FC vor
   FF-Confirm, FC vor Last-CF-Confirm, mehrere Waits im Fenster) korrekt und zählt gegen
   WFTmax. Das machen viele Stacks falsch. Reservierte STmin-Werte → 127 ms, FF_DL-Kappung,
   RX-Aborts als Inbox-Fault, sodass `ReceiveAsync` nie hängt.
5. **J1939-Adress-Claim-Zustandsmaschine**: Adresse vor Neu-Ankündigung invalidieren,
   Transport vor `Claimed` rebinden, Arbitration-Fenster erst nach TX-Bestätigung;
   `J1939Name`-Bitlayout exakt nach J1939-81; `PeriodicSchedule` mit Fixed-Rate-Grid und
   Tick-Koaleszierung.
6. **CANopen §7.2.4.3.4-Supersede** wird auf dem Draht per Abort signalisiert und der Client
   ignoriert Aborts fremder Transfers; `MaxSdoTransferBytes` deckelt beide Richtungen;
   Change-of-State-TPDO mit koaleszierter Dirty-Menge.
7. **Versionierung ohne Build-Magie**: keine Versionsberechnung im Build, ein einziger
   Knopf (`eng/Dependencies.props`) für das Upstream-Update, OIDC Trusted Publishing, `[skip
   ci]`-Loop-Guard, `verify-release-config.mjs` fängt kaputte Konfiguration im PR.
8. **Traceability**: 72 zitierte Requirement-IDs, alle in der SRS definiert; Tests und
   Paketbeschreibungen zitieren sie konsequent; Entscheidungen (FluentAssertions-Pin,
   Dependabot-Ignores, GitVersion außerhalb des Builds) sind an Ort und Stelle erklärt.

---

## 6. Empfohlene Reihenfolge

1. **Sofort (Patch-Release):** 1.1, 1.2, I2 (FD-Allokation), Q4 (BusStateMonitor-Flut),
   B1 (READMEs in den Paketen), B2 (PAT-Exposition), B8 (Reliability-Dependencies).
2. **Vor der nächsten Minor-Version:** Q1 (`AsyncLocal`), Q2 (monotone Uhr), Q3
   (Drain-Fairness), Q5/Q6 (Echo-Bit, Echo-FIFO), I1/I3/I4/I5, J1–J8, C1–C7; dazu die in
   Abschnitt 4 genannten Negativtests, damit die Fixes abgesichert sind.
3. **Prozess:** B3 (Release an 3-OS-CI koppeln), B4 (Runbook + Recovery-Pfad), B5
   (npm-Bumps ohne Release), B6 (Action-Pins), B7 (ns2.0-Tests), B9 (Package Validation),
   B10 (Rest-Exposition über die PR-Refs, siehe dort).
4. **Ehrlichkeit der Docs:** CANopen-README auf „CiA-301-Teilmenge“ zurückschneiden,
   „Experimental“ in den Paketbeschreibungen mit der SemVer-Major 1 in Einklang bringen
   (bewusst entscheiden, ob 1.x „stabil“ bedeuten soll), net8.0/„vier Pakete“-Reste
   entfernen, chinesische Doc-Comment-Anteile entfernen.
