# CanKit.Pro – Deep Review (komplettes Repository)

**Datum:** 2026-09-30 · **Stand:** `main` @ `8cec2ee` (v1.3.0 veröffentlicht, Merge #247) ·
**Umfang:** 9 Pakete, ~30.100 Zeilen Bibliothekscode, ~43.500 Zeilen Tests, CI/Release-Pipeline,
Engineering-Skripte, Dokumentation, Samples.

> Dieses Dokument ist das zweite Review von **CanKit.Pro selbst**. Der Vorgänger ist
> `2026-09-10-repository-review.md` (Stand `1270ed1`, v1.2.0); seine Befund-IDs (1.1, 1.2, Q1–Q6,
> I1–I5, J1–J8, C1–C8, B1–B10, §3, §4) werden hier weiterverwendet. Zwischen beiden Ständen liegen
> 125 gemergte Pull Requests und 785 Commits (`git log --first-parent 1270ed1..8cec2ee`).
>
> **Wie das Review entstand.** Neun parallele Teil-Reviews haben je ein Paket (CANopen in zwei
> Hälften) beziehungsweise Build/CI/Docs auditiert, Quelltext und zugehörige Tests vollständig
> gelesen und für jeden Vorbefund den heutigen Stand belegt. Ein konsolidierender Hauptfaden hat
> das Gate ausgeführt, jeden kritischen und wichtigen Befund am Code nachgelesen, die
> Upstream-Behauptungen gegen einen Klon von `pkuyo/CanKit` @ `v0.5.6` geprüft und die von den
> Teil-Reviews vorgeschlagenen Mutationen ausgeführt (Abschnitt 4.2). Normzitate sind, wo nicht
> anders vermerkt, aus dem Gedächtnis; der Normtext lag nicht vor.

---

## Gesamteinschätzung

Das Repository hat in drei Wochen fast den gesamten Befundbestand des Vorgänger-Reviews
abgearbeitet; Anhang A führt den Stand jedes Vorbefunds mit der Zählung. Die beiden kritischen
CANopen-Befunde, die Querschnittsbefunde am Actor, die ISO-TP-, J1939- und UDS-Interop-Punkte sind
geschlossen, und die Fixes sind gepinnt: jede Mutation an einer Fix-Stelle wurde vom benannten Test
erkannt (Abschnitt 4.2).

**Verifiziert in dieser Umgebung (Linux, .NET SDK 10.0.112):**

| Schritt | Ergebnis |
| --- | --- |
| `dotnet build -c Release -p:CI=true` (netstandard2.0 + net10.0, Warnungen = Fehler) | erfolgreich, 0 Warnungen |
| `dotnet test --framework net10.0` (3 Läufe hintereinander) | 1385 / 1385 bestanden, 156–159 s, keine Flakes |
| `dotnet format --verify-no-changes` | keine Änderungen |
| `dotnet pack` + `eng/verify-packages.py` | 9 nupkg + 9 snupkg, alle 9 „ok“ |
| `eng/verify-requirements-traceability.py` | 117 SRS-IDs, 98 Must, 82 mit Test, 5 gewaivert, 11 bekannte Lücken, 0 neue |
| CI auf `main` @ `8cec2ee` | grün (per API geprüft) |
| Requirement-IDs in Code/Tests vs. SRS | 71 (src) / 96 (tests) zitiert, 0 undefiniert |

**Das neue Reifegefälle** liegt nicht mehr zwischen L2 und L3/L4, sondern zwischen dem, was gegen
den Virtual-Adapter und die Test-Doubles bewiesen ist, und dem, was reale Adapter tun. Jedes
Paket-README sagt ehrlich, dass nichts auf Hardware gelaufen ist. Der eine kritische Befund dieses
Reviews (K1) ist genau die Stelle, an der diese Lücke zu deterministischem Fehlverhalten führt:
zwei der vier Hardware-Adapter von CanKit 0.5.6 liefern das Sende-Echo anders, als RawCan und der
J1939-Knoten es voraussetzen. Die übrigen wichtigen Befunde sind engere Race-Fenster, fehlende
Abbruchpfade in Batch-Sendern, Norm-Nachsichtigkeiten ohne Signal und Doku-Drift.

**Die Testsuite** ist mit 1385 Tests dreimal so groß wie beim Vorgänger und in der Form deutlich
besser: Timer laufen auf einer virtuellen Uhr mit exakt armierten Intervallen, Races werden über
Ordnungszeugen statt über `Sleep` erzwungen, und die Regressionstests zitieren Ursache und Issue.
Was fehlt, sind normative Negativtests für die Pfade, die kein Fix je berührt hat: Antwortvalidierung
im UDS-Client, client-seitige Toggle-Prüfungen in CANopen, CTS(0) außerhalb der Wartephase in
J1939-TP, ein Dispose des Services unter dem ISO-TP-Kanal. Die Mutationen in Abschnitt 4.2 belegen
jede dieser Lücken.

**Empfehlung in einem Satz:** K1 als Issue mit Upstream-Anteil anlegen und die Echo-Annahmen in
RawCan und J1939 auf Frame-Marker statt auf `WorkMode` umstellen; die Batch-Sender in CANopen und
J1939-TP abbrechbar machen; die in Abschnitt 6 genannten Negativtests nachziehen, bevor die nächste
Minor-Version erscheint.

---

## 1. Kritische Befunde

### K1 · Echo-Bestätigung und Echo-Erkennung setzen ein Adapterverhalten voraus, das PCAN und Vector in CanKit 0.5.6 nicht liefern

**Mechanismus in CanKit.Pro.** `CanBusService.SendConfirmedAsync` wählt den Echo-Pfad, sobald der Bus
`CanFeature.Echo` deklariert und `WorkMode == Echo` ist (`src/CanKit.Pro.RawCan/CanBusService.cs:321-326`),
und matcht ein Echo nur, wenn der Adapter das Frame mit `IsEcho == true` liefert (`:189-190`). Der
J1939-Knoten entscheidet „dieser Bus echot“ ausschließlich am `WorkMode`
(`src/CanKit.Pro.J1939/J1939NodeImpl.cs:868-869`) und wertet das per Frame gelieferte `IsEcho` für
Address-Claim-Frames nicht aus (`:724`, `:1379-1401`).

**Was die Upstream-Adapter tun** (gelesen in `pkuyo/CanKit` @ `v0.5.6`):

| Adapter | Echo-Modus | Normal-Modus | Beleg |
| --- | --- | --- | --- |
| SocketCAN | Echo geflaggt (`MSG_CONFIRM`) | kein Echo | `SocketCanClassicTransceiver.cs:322`, `SocketCanFdTransceiver.cs:368` |
| Kvaser | Echo geflaggt (`canMSG_TXACK`) | kein Echo | `KvaserClassicTransceiver.cs:169`, `KvaserFdTransceiver.cs:173` |
| PCAN | Echo **ungeflaggt** (`AllowEchoFrames` an, `CanReceiveData` ohne `IsEcho`) | kein Echo | `PcanBus.cs:167-172`, `PcanClassicTransceiver.cs:183` |
| Vector Classic | eigenes TX-Ereignis wird **verworfen** (`if (recEcho && isEcho) return (null, null)`) | TX-Ereignis wird **geflaggt geliefert** | `VectorClassicTransceiver.cs:145-147, 236-246` |
| Vector FD | TX-Ereignis wird nie geliefert (`TX_OK` → `return false`) | dito | `VectorFdTransceiver.cs:142-147` |
| Virtual | Echo ungeflaggt | kein Echo | `VirtualBusHub.cs:59, 73-76` |

PCAN und Vector deklarieren `CanFeature.Echo` (`PcanProvider.cs:24`, `VectorChannelInfo.cs:64`).

**Folgen.**
(a) Auf PCAN und Vector im Echo-Modus läuft jede `SendConfirmedAsync` in den Timeout (Default 1 s),
obwohl der Frame auf dem Draht war; jeder darauf aufsetzende Pfad, der Bestätigungen braucht
(J1939-Claim, ISO-TP-Sendekette über `SendConfirmedAsync`, CANopen-Steuerframes), meldet Transportfehler.
(b) Auf Vector Classic im Normal-Modus (Default) kommt das eigene Address-Claimed-Frame geflaggt
zurück; `BusEchoesOwnTransmits` ist false, der Marker-Pfad wird übersprungen, der Knoten sieht einen
Peer mit gleichem NAME auf der eigenen Adresse und verliert den Contest gegen sich selbst
(`J1939NodeImpl.cs:737-746` → `LoseEqualNameContest`): **jeder Claim scheitert deterministisch.**
(c) Die Testinfrastruktur kennt zwei Echo-Welten (`EchoWorlds.cs:22-36`: Echo-Modus mit und ohne
Flag); die dritte reale Welt „Normal-Modus, geflaggtes Echo“ existiert weder dort noch im Code.

**Kausalität und Einordnung.** Ursache ist das Adapterverhalten upstream (bei Vector sieht die
Bedingung wie ein invertiertes Paar aus, bei PCAN fehlt das Flag). CanKit.Pro hat die Annahme
„Feature-Flag + WorkMode ⇒ geflaggtes Echo“ aber zum Vertrag gemacht (`ICanBusService.cs:25-51`,
`README.md:93-96`, arc42 ADR-7). Der Befund ist damit beides: ein Upstream-Ticket für
`pkuyo/CanKit` (`docs/upstream-candidates.md`) und ein eigener Härtungsbedarf, weil eine Bibliothek,
die vier Adapter beerbt, nicht auf zwei davon still falsch sein darf.

**Fix.** Im J1939-Knoten `isEcho` aus dem Reader bis in `HandleIncomingAddressClaim` durchreichen und
ein geflaggtes Frame, das einem eigenen ausstehenden Claim-Marker entspricht, unabhängig vom
`WorkMode` als Echo werten; Marker immer aufzeichnen und über die vorhandene Grace-Mechanik ablaufen
lassen. In RawCan die Echo-Erkennung um ein inhaltsbasiertes Fallback für ungeflaggte Echos im
Echo-Modus ergänzen oder die Adapterliste im Vertrag benennen. Dritte `EchoWorld` („FlaggedNormal“)
in die vier `[MemberData(Both)]`-Theorien aufnehmen. Upstream: Vector-Bedingung und PCAN-Flag melden.

**Konfidenz.** Hoch für beide Codepfade (alle Zeilen gelesen); mittel für die Hardware-Prämisse
(Vector liefert `XL_TRANSMIT_MSG` auf dem sendenden Port per Default — aus dem Gedächtnis, nicht
gemessen). Kein Test, kein Hardwarelauf.

---

## 2. Wichtige Befunde

### 2.1 Querschnitt

**Q7 · CAN-FD-Frames ohne Padding haben ungültige Datenlängen, und ihr Echo matcht nie** —
`src/CanKit.Pro.IsoTp/IsoTpFrameCodec.cs:236, :379, :429` (`frameLen = padding ? NextValidFrameLength(…) : payloadLen`),
`src/CanKit.Pro.RawCan/PendingSend.cs:80-84` (byteweiser Vergleich), Upstream `CanFrame.cs:339-365`
(`Validate` prüft nur 0..64, `LenToDlc` rundet auf).
Mit `UseCanFd = true, UsePadding = false` entsteht z. B. ein SF mit 9 Nutzbytes als 11-Byte-Payload;
CAN FD kennt nur 0–8, 12, 16, 20, 24, 32, 48, 64. Virtual reicht das durch, Vector sendet
`LenToDlc(13) = 14` und baut das Echo mit `DlcToLen(14) = 16` Bytes (`VectorFdTransceiver.cs:170, :291`),
also matcht `PendingKey` nie → `Timeout`. Die Options-Doku behauptet „CAN-FD padding is optional per
ISO 15765-2 §5“ (`IsoTpChannelOptions.cs:28-33`); ISO 15765-2:2016 verlangt für FD das Auffüllen bis
zur nächsten DLC-Stufe (Gedächtnis). Kein Kanaltest setzt `UsePadding = false`; die Codec-Tests
prüfen nur `≤ 64` und zementieren die ungültige Länge mittelbar. Mutation isotp-M5 (Fix im Codec
eingespielt) blieb grün: der Fix wäre heute unbewiesen. *Fix:* bei `isCanFd` immer auf die
DLC-Stufe runden und `padding` nur über das Auffüllen bis TX_DL entscheiden lassen, oder
`UseCanFd && !UsePadding` bei `Open` ablehnen; in RawCan nicht-kanonische FD-Längen in
`SendConfirmedAsync` abweisen. Konfidenz: hoch (Code), mittel (Norm).

**Q8 · `TxConfirmFailureReason.BusOff` ist auf keinem 0.5.6-Adapter erreichbar** —
`CanBusService.cs:659-690`. `OnFaultOccurred` löst nur bei `FaultOccurred` und `BusState == BusOff`
auf; upstream lösen alle sechs Bus-Klassen `FaultOccurred` mit Severity `Fault` ausschließlich aus,
wenn ihre Poll-Schleife stirbt (`SocketCanBus.cs:862`, `KvaserBus.cs:547`, `VectorBus.cs:637`,
`PcanBus.cs:474`, `ZlgCanBus.cs:717`, `ControlCanBus.cs:464`); Bus-Off kommt als `BusState` und
Error-Frame. Ausstehende Sends enden beim Bus-Off als `Timeout`, nicht als `BusOff`; arc42 ADR-7 und
die XML-Doku beschreiben einen Pfad, den nur `ControllableBus.RaiseFault` nimmt. Die Guard-Zeile
`:665` ist von keinem Test gepinnt (Mutation rawcan-M1 blieb grün). *Fix:* `ErrorFrameReceived` +
`BusState` koalesziert abonnieren (wie `BusStateMonitor`) oder die Doku auf „nur bei Fault“
korrigieren. Konfidenz: hoch.

**Q9 · `Dispose` blockiert synchron auf Arbeit, die den eigenen Actor braucht** — derselbe Pfad in
drei Paketen: `UdsClientImpl.Dispose` joint den Keep-Alive-Loop ohne Timeout (`UdsClientImpl.cs:1647`)
und wartet 5 s auf den Request-Lock (`:1560`); beide brauchen den ISO-TP-Actor (`SettleAsync`,
`DiscardPendingPdus`, TX-Idle). `CanOpenNode.Dispose` wartet 2 s auf den eigenen Event-Pump-Task
(`CanOpenNode.cs:686`); `PeriodicSchedule.Dispose` in J1939 bis 2 s (`J1939NodeImpl.cs:1884`).
Aus einem `BackgroundExceptionOccurred`- oder Event-Handler (die auf dem Actor laufen) heißt das
Deadlock mit Keep-Alive beziehungsweise ein stehender Kanal für die Dauer des Timeouts. Kein
Interface-Vertrag verbietet den Aufrufort. *Fix:* `DisposeAsync` anbieten oder den Join durch
Cancel ersetzen; in den Verträgen festhalten, dass `Dispose` nicht aus Kanal-Callbacks heraus
aufgerufen werden darf. Konfidenz: mittel (Mechanismus sicher, Aufrufort Annahme).

### 2.2 Actor und Reliability

**A1 · Tick-Arithmetik von `DueTimestamp` und `NextTimerDelayAsync` ist nicht exakt; die
Test-Infrastruktur trifft das latent** — `src/CanKit.Pro.Actor/ProtocolActor.cs:854-864, :151-154`,
`tests/…/Infrastructure/VirtualClock.cs:209-214`. `delay.TotalSeconds * Frequency` mit `Math.Ceiling`
liegt für rund 6 % der Millisekundenwerte (35, 70, 85, 101, 139, 140 … ms) ein Tick über der ganzen
Zahl; die Rückrechnung über `TimeSpan.FromSeconds(double)` schneidet auf .NET 10 ab und verliert
für weitere rund 6 % (43, 51, 71, 86 … ms) einen Tick. Auf der `ManualTimeSource` matcht
`WaitUntilTimerArmedAsync(d)` für diese Werte nie, oder ein `AdvanceAsync(d)` lässt den Timer einen
Tick vor der Fälligkeit stehen. Alle heute verwendeten Erwartungswerte (10, 20, 30, 50, 80, 100,
150, 200, 250, 300, 500 … ms) sind zufällig sicher; rund 60 Aufrufstellen hängen daran, und die
naheliegende „Reparatur“ eines künftigen Ausfalls wäre ein `lateBy`, also genau die
Toleranzverbreiterung, die `CLAUDE.md` verbietet. Gemessen (Abschnitt 4.2): mit dem Poll-Intervall
43 ms oder 35 ms statt 20 ms in `BusStateMonitorTests` schlägt `StateChanged_Is_Not_Raised_While_The_State_Is_Unchanged`
auf net10.0 fehl, mit 30 ms besteht er — die Rechnung stimmt, und der Fehler sieht wie ein
Protokollfehler aus.
*Fix:* ganzzahlig rechnen (`delay.Ticks * Frequency / TicksPerSecond` in `decimal` oder `BigMul`),
in beiden Richtungen. Einordnung: Testinfrastruktur-Defekt plus harmlose Bibliotheksrundung.
Konfidenz: hoch (IEEE-754-Nachrechnung), mittel für den exakten Fehlertext.

### 2.3 RawCan und Addressing

**R1 · Ein verspätetes Echo nach dem Timeout bestätigt den nächsten inhaltsgleichen Send** —
`CanBusService.cs:589-657`. Matching ist rein inhaltsbasiert; ein Echo, das erst nach dem Timeout
seines Sends eintrifft, ist von dem des nächsten byte-identischen Sends nicht unterscheidbar. Auf
gesättigtem Bus mit einem niederprioren Frame, das > 1 s in der TX-Queue bleibt, gilt der Retry als
gesendet, während er noch in der Queue liegt; ein L3-Layer startet seine Antwort-Deadline auf ein
fremdes Echo. README (`:116-117`) und `ICanBusService.cs:152-153` versprechen „never cross-matched“.
*Fix:* mindestens dokumentieren; ein Tombstone mit Ablauffrist ist eine Maintainer-Entscheidung,
weil er bei wirklich verlorenen Frames die #24-Kaskade zurückbringen kann. Konfidenz: hoch (Code),
mittel (Häufigkeit).

### 2.4 ISO-TP

**I6 · `ReceiveAsync` hängt, wenn der geteilte Service unter dem Kanal disposed wird** —
`src/CanKit.Pro.IsoTp/IsoTpChannel.cs:583-598`. Vollendet `CanBusService.Dispose` die Subscription,
verlässt der Reader-Task seine Schleife regulär und tut nichts: kein `_pduInbox.Writer.TryComplete()`,
kein Fault-Item, kein Event. Ein wartendes `ReceiveWithArrivalAsync` (`:299`) bleibt ohne Token für
immer stehen; `SendAsync` scheitert dagegen sofort. Der funktionale Listener behandelt genau diesen
Fall (`IsoTpFunctionalListener.cs:96-98`), der Kanal nicht; der UDS-Client sieht erst seinen P2.
Kein Test disposed den Service unter dem Kanal. *Fix:* nach regulärem Schleifenende und im
`catch (Exception)` die Inbox vollenden oder ein Fault-Item schreiben und Receptions abräumen.
Konfidenz: hoch.

**I7 · `BeginSendOnLoop` hat keinen Disposed-Guard** — `IsoTpChannel.cs:711-722` (nur
`IsSendAlreadyCanceled`), `:235`/`:249` (Flag-Prüfung vor dem Post), `:531` (`FailInFlightSend`).
Fällt `_disposed` zwischen `:235` und `:249`, liegt `FailInFlightSend` vor `BeginSendOnLoop` in der
Mailbox, findet `_tx == null`, und der Send geht im `FinalDrain` noch auf den Bus; die Bestätigung
will auf den bereits disposten Actor, die ODE wird geschluckt (`:888-892`), `tcs` bleibt offen, ein
`SendAsync` ohne Token hängt. `HandleReceivedFrame` hat genau den Guard, der hier fehlt (`:1141-1146`).
Fenster: Mikrosekunden, aber die Klasse, die #205/#206/#216 sonst systematisch abgedichtet haben.
*Fix:* `if (_disposed != 0) { tcs.TrySetException(new ObjectDisposedException(…)); return; }` am
Anfang. Konfidenz: Mechanismus hoch, Eintritt Vermutung.

**I8 · Negative `NBs`/`NCr` werden bei `Open` nicht abgelehnt und lassen Send/Receive ohne Deadline
hängen** — nur `LocalStMin` wird im Konstruktor validiert (`IsoTpChannel.cs:162-164`);
`DeadlineScheduler.Arm` wirft bei negativem Timeout (`DeadlineScheduler.cs:98-99`), die Exception
verlässt den Actor-Callback als Hintergrundausnahme, `_tx` bleibt in `WaitFcInitial` ohne N_Bs,
`_rx` ohne N_Cr. Dieselbe Klasse in J1939-TP: `J1939TpOptions.Validate` prüft T1..T4 und
`BamPacketSpacing` nicht (`J1939TpOptions.cs:153-170`); mit `T3 < 0` registriert `StartTx` die
Session und sendet das RTS, bevor `Arm` wirft (`J1939TpChannel.cs:915-931`) — Session ohne Deadline,
Slot belegt, Ziel blockiert. *Fix:* alle Timer-Optionen im Konstruktor validieren; in `StartTx` erst
armen, dann registrieren. Konfidenz: hoch.

### 2.5 UDS

**U1 · Multi-DID: eine teilweise beantwortete Anfrage wird als Protokollfehler verworfen** —
`src/CanKit.Pro.Uds/UdsClientImpl.cs:301-308`. Nach ISO 14229-1 (Gedächtnis: Tabelle A.1, NRC 0x31
„none of the requested dataIdentifier values are supported“) antwortet ein konformer Server positiv
nur mit den unterstützten DIDs; der Client wirft dann `UdsProtocolException` und verwirft die bereits
geparsten Records. Kein Test sendet eine Teilantwort (Mutation uds-M5b blieb grün). *Fix:* fehlende
DIDs als Ergebnis liefern, unerwartete und duplizierte bleiben Fehler. Konfidenz: mittel-hoch.

**U2 · P2Server/P2\*Server aus der Session-Antwort werden weder dekodiert noch übernommen** —
`UdsClientImpl.cs:170-181`. Der `sessionParameterRecord` wird roh zurückgegeben; die Client-Timer
bleiben die bei Konstruktion fixierten `P2ClientMax`/`P2StarClientMax`. Eine ECU, die in der
Programming-Session P2\*Server_max = 10 s deklariert und ihr nächstes 0x78 nach 8 s sendet, ist
konform, der Client mit Default 5 s wirft `UdsTimeoutException(P2Star)`. *Fix:* Decode-Hilfe plus
dokumentierter Weg, die Client-Timer je Session zu setzen (additiv). Konfidenz: mittel (Norm).

**U3 · Antwortvalidierung des physischen Clients ist ungetestet** — Sub-Function-Echo (0x10 `:173`,
0x11, 0x31, 0x27, 0x3E), DID-Echo (0x22, 0x2E), BSC-Echo in der positiven 0x36-Antwort, leere
Antwort, NRC < 3 Bytes, Stray-positiv für ein anderes SID: kein Test des physischen Clients sendet
eine positive Antwort mit falschem Echo. Mutation uds-M5 (Sub-Function-Prüfung entfernt) blieb grün.
Dazu: `UploadAsync` dekodiert `memorySize` ohne die Breitenprüfung, die #182 dem Download gab
(`:983-988` vs `:895-906`), und das README-Quick-Start-Beispiel wirft seit #182, weil
`memorySize` 512 mit einem beliebigen `firmwareChunk` kollidiert (`README.md:65-72`).

### 2.6 J1939-Knoten

**J9 · Multi-Frame-Fallback-Kanal sendet eine komplette TP-Session unter einer bereits verlorenen
Adresse** — `J1939NodeImpl.cs:1213-1221`, `:1254-1255`. Der Zweig `tpChannel.SourceAddress != sa` ist
auf dem Commit-Pfad unerreichbar (Happens-before über Volatile-Writes stimmt) und wird nur im
Verlust-Race erreicht: der Sender hat das Gate mit `sa` passiert, der Actor verarbeitet den
stärkeren Peer-Claim und rebindet auf 0xFE, der Sender öffnet einen frischen Kanal auf der verlorenen
Adresse und sendet BAM/RTS plus alle DT-Frames, bevor `HasReclaimCrossed` wirft. Ohne den Fallback
hätte der disposte Kanal sofort `ObjectDisposedException` → `J1939NoAddressException` ohne
Drahtverkehr geliefert. Kein Test erreicht den Zweig (Mutation j1939-M3 blieb grün).
*Fix:* Fallback durch `throw new J1939NoAddressException()` ersetzen. Konfidenz: hoch (Mechanismus).

**J10 · Race `Dispose()` ↔ `RebindTransportOnLoop` leakt einen `J1939TpChannel` samt Subscription
und Actor-Thread** — `J1939NodeImpl.cs:1495-1535` vs `:1632-1676`. `Dispose` disposed `_transport`
vom Aufruferthread; trifft es zwischen Schließen des alten und Zuweisen des neuen Kanals ein, wird
der neue nie geschlossen. Realistisches Muster: `using`-Ende direkt nach `await ClaimAddressAsync`.
*Fix:* Transport-Teardown auf den Actor verlegen und nach `OpenTransport` `_disposed` erneut prüfen.
Konfidenz: mittel. Dazu **J11**: `_claimEchoGraces` wächst bei jedem Re-Arm ohne Rückbau, und ein
lebender Marker ohne Echo lässt eine Grace endlos im 1-Hz-Takt re-armen (`:1088-1108`).

**J12 · `ReceiveBufferCapacity` und die RX-Inbox haben keinen öffentlichen Konsumenten** —
`J1939NodeOptions.cs:54-58` verspricht einen Empfangspuffer, `IJ1939Node` hat keine Pull-API,
`InboxAll` ist `internal` und unbenutzt. Öffentliche Option ohne Wirkung; additiv ein
`ReadAllAsync` anbieten oder beides entfernen (Major). Konfidenz: hoch.

### 2.7 J1939-TP

**T1 · Abgebrochener BAM-Send hinterlässt einen Spacing-Timer, der eine sofort neu gestartete
Session mit derselben PGN doppelt bedient** — `src/CanKit.Pro.J1939Tp/J1939TpChannel.cs:948, :1115`
(`_actor.Schedule(…, () => TrySendNextBamDt(key))`, Handle verworfen), `:942-949`, `:1086-1094`,
`:315-334` (`CancelTxOnLoop` disposed für BAM nichts). Die Kette ist nur über den `TxSessionKey`
adressiert, nicht über die Session-Instanz. Nach Cancel und Neusenden derselben PGN innerhalb des
Spacing-Fensters (Default 50 ms) sendet der alte Timer DT 1 auf die neue Session, deren eigener
Timer DT 1 ebenfalls sendet; die zweite Bestätigung wird als „stale“ verworfen, die Kette läuft mit
DT 2..N weiter. Auf dem Draht `BAM, DT1, DT1, DT2, …`: ein konformer Empfänger bricht ab, der Sender
**meldet Erfolg**. Dieselbe Identitätslücke in `OnBamAnnounceConfirmed(key)`. Das ist die Klasse, die
J3 gerade geschlossen hat, über einen anderen Pfad. Als „Wichtig“ eingeordnet, weil der Auslöser ein
Cancel plus Neusenden derselben PGN innerhalb von ≤ 50 ms ist. *Fix:* Session-Instanz in beide
Closures übernehmen und mit `ReferenceEquals(_txSessions[key], session)` prüfen (wie `FailOrRaiseTx`
`:1237` es tut); Schedule-Handle in der Session halten und in allen Abschlusspfaden disposen.
Konfidenz: hoch (Mechanismus), mittel (Häufigkeit). Kein Test.

**T2 · CTS(0) während `WaitEom` lässt den Send ohne jeden Timer hängen, samt Warteschlange des
Ziels** — `J1939TpChannel.cs:967-972` (CTS(0) disposed T3, armt T4, unabhängig vom Zustand) und
`:1212` (`OnTxT4Expired`: `if (session.State != TxStage.WaitCts) return;`). Nach dem letzten DT
steht die Session in `WaitEom`; ein CTS(0) schaltet auf T4 um, T4 feuert ins Leere, kein Timer bleibt
armiert, die Session hält den Admission-Slot und blockiert über `HasSessionTo` jede weitere Sendung
an dieses Ziel. Norm: T4 ist genau der Abbruchgrund nach CTS(0) (J1939-21 §5.10.2.4, Gedächtnis).
Mutation j1939tp-M1 (Guard entfernt) blieb grün: kein Test sendet CTS(0) außerhalb `WaitCts`, der
Guard kann ohne Testverlust fallen. *Fix:* Guard streichen (T4-Ablauf ist in jedem Zustand ein
Abbruch) oder CTS(0) außerhalb `WaitCts` ignorieren. Konfidenz: hoch.

**T3 · Bidirektionale CM-Sessions mit demselben Peer und derselben PGN: ein Peer-Abort beendet beide
Richtungen** — `J1939TpChannel.cs:670-692` (Abort cancelt die RX-Session bei PGN-Gleichheit und
faultet zusätzlich den eigenen Send), `J1939TpFrames.cs:132-143` (Byte 3..5 = 0xFF). Ob die
Ziel-Revision von J1939-21 ein Abort-Rollenfeld kennt, ist aus dem Gedächtnis unsicher (Frage 9).
Konfidenz: hoch für den Code, niedrig für den Normbezug.

### 2.8 CANopen

**C9 · Der Segment-Batch des Block-Download-Clients läuft nach Abort oder Cancel weiter; der Server
liest Nachzügler als klassische Initiates, im ungünstigen Fall als Expedited-Write** —
`src/CanKit.Pro.CANopen/CanOpenNode.cs:2370-2412` (`SendOrderedControlFrames`: `Task.Run`-Schleife
ohne Abbruchmöglichkeit zwischen zwei Frames), `CanOpenNode.SdoBlock.cs:207-217` (Peer-Abort räumt
nur die Client-Session), `CanOpenNode.cs:1430-1434` (`(cs & 0xE0) == 0x20` ist ein Download-Initiate).
Trifft während eines bis zu 127 Segmente langen Sub-Blocks ein Server-Abort ein (Seqno-Fehler,
OutOfMemory an der Kappe, Supersede durch einen zweiten Master, Reset) oder bricht der Aufrufer ab,
sendet die Kette die restlichen Segmente. Ohne Block-Session liest der Server jedes Segment als
Command-Specifier: seqno 0x20..0x3F mit c = 0 ist ein Download-Initiate, Bytes 1..3 der Nutzdaten
werden zum Multiplexer, 0x23/0x27/0x2B/0x2F (Segmente 35/39/43/47) sind Expedited mit 4/3/2/1 Bytes
und enden in `TryWriteRaw`, sobald der zufällige Multiplexer ein beschreibbares Objekt passender
Breite trifft (1017h startet dann den Heartbeat-Producer). Die Trefferwahrscheinlichkeit ist gering,
die Folge ein OD-Write, den niemand gemeldet bekommt. *Fix:* je Session ein Token, das `AbortBlockClient`,
der Abort-Empfang und `CancelSdoClient` auslösen und das `SendOrderedControlFrames` zwischen zwei
Frames prüft. Konfidenz: hoch (beide Pfade gelesen), mittel (Wahrscheinlichkeit). Kein Test bricht
einen Block-Download mitten im Sub-Block ab und zählt, was danach noch auf 0x600+id erscheint.

**C10 · Klassischer Upload-Client nimmt eine Größenabweichung zur angekündigten Länge still an** —
`CanOpenNode.cs:2122-2146`. Mehr Bytes als angekündigt: der Puffer wächst (gedacht für `declared = 0`);
weniger mit c = 1: Kürzung auf `Offset` ohne Fehler. Server-Spiegel (`:1704-1708`, `:1728-1733`) und
Block-Upload-Client (`SdoBlock.cs:405-412`) brechen beide ab. Ein Gerät, das die OD-Maximallänge
ankündigt und weniger sendet (bei VISIBLE_STRING nicht selten), liefert ein gekürztes Ergebnis ohne
Signal. Kein Test sendet eine abweichende Länge (Mutation canopenB-M1 blieb grün). *Fix:* „mehr als angekündigt“ →
0607 0012h zwingend; „weniger“ als Maintainer-Entscheidung, mindestens über
`BackgroundExceptionOccurred` melden. Konfidenz: hoch (Code), mittel (Norm).

**C11 · Linearer Pufferzuwachs in drei Empfangspfaden** — `CanOpenNode.cs:2128-2133`,
`SdoBlock.cs:811-816`, `:502-508`: je Segment `new byte[Offset + 7]` plus Vollkopie. Nur der
klassische Server-Pfad wurde mit `a3ec1f5` geometrisch. Bei der Standardkappe 1 MiB sind das ≈ 150 000
Allokationen und ≈ 78 GB memcpy, ab ≈ 12 000 Segmenten jede im LOH. Erreichbar durch jeden Peer, der
s = 0 setzt. *Fix:* dieselbe Verdopplung mit Klammer wie `:1680-1702`. Konfidenz: hoch.

**C12 · Block-Download-Client: Anforderungs-Timer läuft über den ganzen Sub-Block** —
`SdoBlock.cs:247`, `:549-589`. Rearm erst beim Sub-Block-ACK; bei 127 Segmenten und 10–20 kbit/s
(0,8–1,65 s) läuft der Default `SdoTimeout` (1 s) während des Batches ab, danach wird das ACK eines
gesunden Servers verworfen. Die Optionen-Doku (`CanOpenNodeOptions.cs:17-19`) beschreibt es anders.
Konfidenz: hoch (Mechanismus), mittel (Praxis).

**C13 · EMCY 8210h ohne Entprellung für jedes zu kurze RPDO-Frame** — `CanOpenNode.Pdo.cs:559-568`.
Ein Peer mit zyklisch zu kurzem PDO (100 Hz) macht den Knoten zum EMCY-Sender mit 100 Frames/s;
1001h wird nicht gesetzt. Der Test `Rpdo_Shorter_Than_Its_Mapping_…` prüft ein Frame. *Fix:* einmal je
Übergang, Bit 4 in 1001h setzen/löschen. Konfidenz: hoch.

**C14 · `SendSyncAsync` umgeht NMT-Zustand und Bit 30 von 1005h** — `CanOpenNode.cs:429-437`. Der
Tick-Pfad ist gated (`:1313`), der manuelle nicht: ein Knoten in Stopped sendet auf Aufruf einen SYNC
(gegen Table 37, FR-CO-022 und die README-Tabelle `:444-450`), ein Knoten mit „does not generate
SYNC“ in 1005h ebenso. Zwei Integrationstests (`Tpdo_SyncTriggered_FiresEverySync`, `L2Demux_…`)
zementieren (b), weil sie `SendSyncAsync` ohne `StartSyncProducer` aufrufen. Kein Test unterscheidet
Stopped von Pre-Operational für `SendSyncAsync` (Mutation canopenA-W1, Stopped-Gate eingebaut, blieb
grün). Konfidenz: hoch. Entscheidung: roher Werkzeug-SYNC (dann Doku) oder gated wie `SendEmcyAsync`.

**C15 · Selbstanwendung eines eigenen NMT-Broadcasts hängt vom Echo-Verhalten des Adapters ab** —
`CanOpenNode.cs:294-300` (`SendNmtCommandAsync` sendet nur), `:1232-1270` (Anwendung nur in
`HandleNmtCommand` über die Subscription). Auf dem Virtual-Adapter startet `SendNmtCommandAsync(Start, 0)`
den Sender mit, auf einem Adapter ohne Echo nicht: der README-Schnellstart (`README.md:638-639`)
liefert dort einen Pre-Operational-Master ohne PDOs. Zusammen mit K1 die zweite Stelle, an der das
Adapter-Echo Protokollsemantik trägt. *Fix:* Kommando mit Ziel 0 oder eigener ID lokal auf dem Actor
anwenden und die Echo-Kopie verwerfen. Konfidenz: hoch.

**C16 · Reservierte Heartbeat-Zustandsbytes werden als `Initializing` (= Boot-up) gemeldet** —
`CanOpenNode.cs:1345-1353` (`_ => NmtState.Initializing`), `NodeGuarding.cs:208-216`. Anwendungscode
im `BootupWatch`-Muster liest einen Neustart, der keiner ist; `CanOpenDiscovery.Record` verwirft
dieselben Bytes. *Fix:* reservierte Bytes nicht als `HeartbeatReceived` melden oder Rohbyte/`Unknown`
in den EventArgs ergänzen (additiv). Konfidenz: hoch.

### 2.9 Build, CI, Release, Docs

**B11 · Das „Two minutes“-Snippet im Wurzel-README kompiliert nicht** — `README.md:86-91`:
`service.Subscribe(view => view.IsExtendedFrame)` gegen ein Prädikat über `CanFrameEvent`, das die
Member `Frame`, `HostArrivalTimestamp`, `IsEcho`, `ReceiveTimestamp` hat
(`ApiApprovals/CanKit.Pro.RawCan.approved.txt:15, :21-33`); der Kommentar nennt noch `CanFrameView`.
`docs/index.md:425` und `getting-started.md:54` haben die richtige Form (`e => e.Frame.IsExtendedFrame`).
Das RawCan-README (`:24-27`, `:104`) braucht zudem zwei `using`s, die im Wurzel-README inzwischen da
sind. Konfidenz: hoch.

**B12 · Required Checks decken `pack`, `validate release config` und `traceability` nicht; Merge
Queue ist nicht aktiviert, die Texte beschreiben ein offenes Ticket** — Ruleset `main` (per API
gelesen): required sind die drei OS-Legs, `format`, `codecov/patch`. Ein PR, der ein README aus dem
Paket wirft oder ein neues untraced `Must` einführt, kann grün gemergt werden und fällt erst im
Release-Lauf. `merge_group`-Läufe: 0; keine `merge_queue`-Regel. #106 wurde am 2026-09-26 durch
PR #160 („name merge-queue refs as 1.2.4-queue.N“) geschlossen, die Aktivierung selbst ist nicht
erfolgt; `CLAUDE.md:36-44` („#106 … will carry the change“), `ci.yml:7-17`, `GitVersion.yml:45-69`
und `eng/verify-merge-queue-version.sh` (läuft in jedem `version`-Job) beschreiben beziehungsweise
simulieren einen Fall, der nie eintritt. Dazu ist `codecov/patch` als required check ein Gate auf
Anwesenheit, nicht auf Wert (`codecov.yml:18-20` informational; bleibt der Upload aus, hängt der PR).
Provenienz-Nachtrag: die Merges von #81 und #83 liegen 2 min 31 s auseinander (`9cd969b` 22:50:19,
`8c94d51` 22:52:50), nicht „four minutes“ (`ci.yml:11-12`, `CLAUDE.md:37-38`). Konfidenz: hoch.

**B13 · Release-Kette: `npm ci` ohne `--ignore-scripts` im OIDC-Job; NuGet-Testwerkzeug-Bumps
veröffentlichen Patch-Releases; keine Package Validation** — `release.yml:89, :205`, `ci.yml:244`
(Lifecycle-Skripte transitiver Pakete laufen im Job mit `id-token: write`);
`dependabot.yml:9-10` (`build(deps)` für den ganzen NuGet-Block inkl. `test-tooling`) +
`.releaserc.json:13` (`build`/`deps` → patch), Beleg `CHANGELOG.md:606-609`; kein
`EnablePackageValidation`/`PackageValidationBaselineVersion` gegen 1.3.0, obwohl seit dem Tag jeder
Bruch eine Major kostet und die Approval-Tests additiv von brechend nicht unterscheiden.
Konfidenz: hoch (Fakten), mittel (Fix-Varianten für Dependabot).

**B14 · „Vier Pakete“- und Vor-1.3.0-Reste in nutzerseitigen Texten** — `SECURITY.md:25` (vs. `:5`
„nine“), `CONTRIBUTING.md:79-80` (Scopes nur L2), `:140-141` (chinesische Übersetzungen; 0 CJK-Zeichen
in `src/`, `tests/`), `feature_request.yml:18-23`, `docs/migration-from-legacy.md:94-95`,
`THIRD-PARTY-NOTICES.md:39-41` (Polyfills „nur RawCan“; tatsächlich sechs Pakete) und `:52`
(`GitVersion.MsBuild` gelistet, das CLI-Tool ist referenziert). Konfidenz: hoch.

---

## 3. Geringfügige Befunde (Auswahl)

Die vollständigen Listen stehen in den Teil-Reviews; hier die Punkte mit Verhaltensfolge oder mit
sichtbarer Doku-Drift.

**Actor / Reliability**
- `A_Clean_Dispose_Reports_Nothing` gated auf 500 ms Wanduhr (`ProtocolActorTimerTests.cs:207`);
  `shutdownTimeout: null` kostet nichts und nimmt die Marge aus dem Spiel.
- Dispose-Timeout wird off-loop gemeldet (`ProtocolActor.cs:518-519`): die eine Situation, in der ein
  `BackgroundExceptionOccurred`-Handler nicht single-writer-sicher ist; `IProtocolActor.cs:66-72`
  sagt es nicht. `WaitForLoopTask` fängt mit falscher Begründung und schluckt echte Loop-Fehler
  (`:536-541`).
- `BusStateMonitor.PostRecheck` lässt das Gate bei einer Nicht-ODE-Ausnahme dauerhaft zu
  (`BusStateMonitor.cs:238-248`); mit `ProtocolActor` unerreichbar, mit fremdem `IProtocolActor` nicht.
- `FaultOccurred`-Hint, werfender `BusState`-Getter und Handler-Detach bei Dispose sind ungetestet
  (Mutationen reliability-M3/M4/M5, Abschnitt 4.2).
- `IsOnCurrentActor` existiert nur auf `ProtocolActor`, nicht auf `IProtocolActor`; ISO-TP verliert
  mit einer fremden Implementierung den Inline-Pfad still (`IsoTpChannel.cs:371, :415`) — 2.0-Notiz.

**RawCan / Addressing**
- Bereits abgebrochenes Token wird erst nach dem Senden beachtet, auf dem Approximated-Pfad gar
  nicht (`CanBusService.cs:307-327`, `:466-484`): zwei Pfade, zwei Antworten auf denselben Aufruf.
- Stamps in `TxConfirmation` sind pfadabhängig, die XML-Doku beschreibt es falsch
  (`TxConfirmation.cs:73-75`); „Dispose cancelt alle ausstehenden Sends“ gilt nur für den Echo-Pfad
  (`README.md:117-118`).
- `default(CanIdFilter)` ist ein gültiger Filter, der nur Standard-ID 0 matcht (`CanIdFilter.cs:27-43`).
- Paketbeschreibung von Addressing verspricht „filter overlap detection“, die in RawCan liegt
  (`CanKit.Pro.Addressing.csproj:4`). Zwei Test-Kommentare zitieren FR-RAW-010 für FR-RAW-015-Inhalte.
- `ControllableBus.EchoCapable` liefert das Echo synchron in `Transmit` und nennt das „exactly as a
  real echo-mode adapter does“; real tun das nur Virtual, alle Hardware-Adapter liefern asynchron auf
  dem RX-Thread. Das hardwaretreue Modell `DeferredEchoCapable` nutzt genau ein Test.

**ISO-TP**
- N_Ar nicht modelliert (Fehlschlag der FC-Bestätigung bricht die Reception nicht ab, `:1519-1544`);
  N_Cr startet beim FC-Handoff statt bei dessen Bestätigung (`:1275-1285`); reserviertes FlowStatus
  wird still verworfen statt mit `N_INVALID_FS` abzubrechen (`IsoTpFrameCodec.cs:588-590`); Escape-FF
  mit FF_DL > `int.MaxValue` wird ignoriert statt mit FC(OVFLW) beantwortet (`:555-556`). Alle vier
  mit Normbezug aus dem Gedächtnis (Frage 10).
- `EncodeStMin` rundet im Sub-ms-Band ab (190 µs → 0xF1 = 100 µs), Doku verspricht „nearest“
  (`IsoTpFrameCodec.cs:641-646`); für eine Mindestzeit ist Abrunden die unsichere Richtung.
- Kein TX_DL, kein BRS; funktionale Adressierung nur Normal; Mixed- und NormalFixed-Adressierung auf
  Kanalebene ungetestet (0 Treffer in den fünf Kanal-Testdateien).
- #246 (README-STmin-Absatz): `README.md:102-111` behauptet eine Wanduhrmessung „within ±1 ms“, die
  seit dem #92-Umbau nirgends stattfindet und der `README.md:22-24` selbst widerspricht; die SRS-Zeile
  NFR-003 (`SRS-CanKit.Pro.md:354`, „gemessene Inter-Frame-Zeit“) trägt denselben Widerspruch.
- Norm-Abschnittsnummern mischen Ausgaben (§6.x neben §9.x, `IsoTpChannelOptions.cs:10, :69, :75, :84`
  vs. `IsoTpChannel.cs:1206, :1227, :1307`).

**UDS**
- `SimulatedUdsEcu` maskiert das SID-Byte mit 0x7F (`SimulatedUdsEcu.cs:122-123`): die Request-SIDs
  0x83–0x87 würden als 0x03–0x07 nachgeschlagen; ein künftiger Test für ein 0x8x-Service scheitert
  am Double, nicht am Client. Unterdrückte Requests ≠ 0x3E beantwortet das Double positiv, was ein
  konformer Server nicht tut.
- Kanal während eines Requests disposed → `InvalidOperationException` statt `IsoTpException`/
  `ObjectDisposedException` (`IsoTpChannel.cs:296-305`); README und `UdsException.cs:12-13` versprechen
  anderes.
- Functional-Client: kein Gegenstück zu `MaxResponsePendingCount` (`UdsFunctionalClient.cs:460-473`);
  ein gefaulteter Listener-Task bleibt als Eintrag stehen (`:417-486`).
- NRC-0x21-Doku behauptet „‚repeat‘, not ‚wait‘“ (`UdsClientOptions.cs:82-86`); die Norm sagt
  „delayed by a time specified in the implementation documents“ (Gedächtnis).
- `IUdsClient.cs:16-18` nennt sieben Services, das Interface hat elf; `requestSeedLevel`-Grenze in der
  Doku 0x7F, im Code 0x7D; doppelte `<summary>` in `SuppressedResponseWindows.cs:47-53`.

**J1939**
- Deadline-Callbacks laufen nach `_disposed = 1` weiter; `OnClaimAnnounceElapsed` sendet dann ein
  unnötiges Cannot Claim und faultet mit dem falschen Fehler (`J1939NodeImpl.cs:603-661`).
- `_equalNameHeardOn` ist zeitlich unbegrenzt (`:747-753`): eine Stunden alte Beobachtung lässt einen
  späteren Claim auf diese Adresse sofort verlieren.
- `PeriodicSchedule.Reschedule` endlos bei `_periodTicks == 0` (nur mit niederfrequenter
  `ITimeSource`); Periodic-Handles werden vom Node nicht verwaltet, mit injiziertem Actor re-armen sie
  nach Node-Dispose endlos (`:1284-1325`, `:1826-1857`).
- „240 Runden pro Scan“ in Kommentar und Test (`:963-964`, `J1939NodeTests.cs:2465`): das Feld
  0x80..0xF7 hat 120 Adressen.
- `IJ1939Node.cs:24-28` verspricht, Dispose cancele `SendAsync`; ein Single-Frame-Send auf geteiltem
  Service läuft durch und meldet Erfolg. `J1939Message.cs:20-24` behauptet DA = 0xFF für PDU2; der
  Multi-Frame-Pfad routet nach `DestinationAddress`.

**J1939-TP**
- Empfänger akzeptiert TP.DT über die eigene CTS-Zusage hinaus; Tabelle-7-Code 6 wird nie gesendet
  (`J1939TpChannel.cs:717`).
- RTS/BAM von Quelladresse 0xFF/0xFE öffnet Sessions und schickt CTS an die Global-Adresse
  (`:487`, `:511`, `:662`).
- Beim Dispose nach einem vorher entsorgten *geliehenen* Actor bleiben Sends für immer offen; ein Test
  zementiert das (`Disposing_After_The_Borrowed_Actor_Leaves_Its_Sessions_To_It`, `:3179`).
- T3 nach RTS und T2 nach CTS werden vor der Drahtbestätigung armiert (`:930`, `:662-666`).
- Klassen-Doku `:37-43` behauptet Parallelität pro (dst, PGN), die seit #32 nicht mehr existiert;
  `J1939TpAbortReason.cs:40-42` („this stack does not retransmit“) ist seit #58 falsch;
  `IJ1939TpChannel.cs:66-67` nennt vier von fünf Ausnahmearten nicht.

**CANopen**
- Konstruktor: der Actor-Thread leakt, wenn `ApplyDeviceDescription` wirft (`CanOpenNode.cs:205-221`
  vs. `:232-278`); der Reader startet vor dem Init-Post (`:280` vs. `:285-290`); eine aus der
  Gerätebeschreibung gestartete Wahl kann vor der Subscription senden (`:221` vs. `:259`). Alle drei
  sind Fenster-Vermutungen; zusammen eine Änderung „Init-Reihenfolge im Konstruktor“.
- Scan-Abbruch lässt bis zu 125 SDO-Probes weiterlaufen; ein sofort folgender `ScanAsync` meldet die
  IDs still als abwesend (`CanOpenDiscovery.cs:176-179`, `CanOpenNode.cs:1900-1905`).
- Nicht alternierendes Toggle-Bit im Guarding wird still verworfen (`NodeGuarding.cs:223-224`);
  Life Guarding ist an `RespondToNodeGuardingRtr` gekoppelt (`:246` vor `:261`), FR-CO-021 knüpft den
  Start an den *empfangenen* RTR.
- EMCY mit DLC < 8 wird still verworfen (`CanOpenNode.cs:1329`), SDO-Frames werden aufgefüllt.
- Block-Upload eines leeren Domain-Werts endet erst im Client-Timeout (`SdoBlock.cs:1065-1098`);
  Block-Steuerframes außerhalb ihrer Phase fallen in den klassischen Server (`:695-712`);
  Expedited-Download mit e = 1, s = 0 auf ein 1..3-Byte-Objekt wird abgewiesen (`SdoFrames.cs:155-164`).
- `ObserveForeignPdoAsync` kostet je Aufruf bis zu 9 + N SDO-Uploads ohne Cache und blockiert bei
  stummem Peer ≥ 9 s (`ForeignPdo.cs:108-125`); das README nennt die Kosten nicht.
- README „Layout“ (`:649-674`) und „Dependencies“ (`:766`, `EdsDcfNet` fehlt) sind unvollständig; „Not
  built“ nennt 1003h, 1015h, 1019h, 1029h nicht; Normverweise für Boot-up, Node Guarding und Heartbeat
  sind über neun Stellen uneinheitlich; die Paketbeschreibung nennt FR-CO-029/030/033/034 nicht.

**Repo / Docs / CI**
- `eng/docs-requirements.txt:9-13` begründet einen Pin auf 0.6.2, der Pin ist 0.6.3 (Dependabot #231).
- `ci.yml:266-267` („only the three OS legs are required“) und `:298-301` (`format` „belongs in“) sind
  überholt, `format` ist required.
- CONTRIBUTING (`:26-27`, `:43-47`) und PR-Template (`:28-29`) beschreiben ein schwächeres Gate als
  `CLAUDE.md` (kein `-p:CI=true`, `format` „optional“).
- Ruleset verlangt Code-Owner-Review ohne `CODEOWNERS` (No-op); `ci.yml`-Checkouts persistieren das
  Token, `release.yml` nicht; `.releaserc.json:59` hängt nur `.nupkg` ans Release.
- CHANGELOG trägt Session-Trailer in den Release-Notes (40 Zeilen über sechs Releases), weil der
  Parser alles nach `BREAKING CHANGE:` bis zum Nachrichtenende übernimmt (`CHANGELOG.md:15-16`);
  der Footer gehört als *letzter* Footer geschrieben.
- SRS NFR-012 sagt „MÜSSEN“, Priorität ist `Should` (`SRS:363`); FR-CO-004 verweist Block-Transfer auf
  CiA 302, er steht in CiA 301.

---

## 4. Testqualität

### 4.1 Gesamtbild

**Stärken.** Die Suite hat die richtige Form: Kanäle und Knoten laufen auf einer Uhr, die der Test
besitzt (`VirtualClock.WaitUntilTimerArmedAsync` als Arm-Barriere, `ManualTimeSource` über interne
Konstruktoren), Races werden über gehaltene Actors und Ordnungszeugen erzwungen
(`HoldingActor`, `MailboxActor`, `RequestLockContended`, `SpinUntilAtTheWriteGate`), Echo-Zeitpunkte
sind Eingaben (`DeferredEchoQueue`), Allokationen werden gemessen statt argumentiert, Codecs sind
property-getestet mit Seed. Der Delay-Sleep-Audit vom 25.09. ist umgesetzt: die dort gelisteten
Kategorie-2-Stellen sind auf Signale umgebaut, übrig sind dokumentierte Negativfenster.

**Verbleibende Wanduhr-Abhängigkeiten** (Größe, die der Host stört, und Marge):

| Test | Störgröße | Marge | Richtung |
| --- | --- | --- | --- |
| `Cm_Sender_T3Timeout_WhenEomAckMissing` (`J1939TpTests.cs:1367`) | RTS→CTS-Verarbeitung gegen das erste T3 = 150 ms | 150 ms | falsch rot, mit irreführender Meldung |
| `An_Answer_After_The_Sdo_Timeout_Does_Not_Revive_The_Transfer` (`CanOpenSdoClientSendFailureTests.cs:100-124`) | Actor-Scheduling zwischen 50-ms-Fälligkeit und Post der Antwort | 150 ms | falsch rot (Mutation canopenB-M4) |
| `An_Unseated_Node_Loses_The_Address_At_Once_…` (`J1939NodeTests.cs:1234-1275`) | Reader→Actor→Event-Latenz gegen `< 75 ms` | 75 ms | falsch rot |
| `Sdo_BlockUpload_Server_Deadline_Measures_Peer_Idle_Time…` (#240) | drei Thread-Pool-Hops + `Task.Delay(100)` je Lücke gegen 2 s | 1,9 s | falsch rot; kein Produktpfad gefunden, auf dem die Deadline bei antwortendem Peer feuert |
| `A_Clean_Dispose_Reports_Nothing` (`ProtocolActorTimerTests.cs:207`) | Thread-Wakeup gegen 500 ms | 500 ms | falsch rot |
| `Echo_Bus_Timeout_Is_Configurable_Per_Call` (`TxConfirmTests.cs:484-501`) | Stall im kurzen Aufruf gegen 400 ms Differenz | 400 ms | falsch rot |
| `A_Pending_Answer_Extends_The_Window_…` (`UdsFunctionalClientTests.cs:658-688`) | zwei Collections + Listener-Arming gegen 2400 ms | 750 ms | falsch rot |
| `QuiesceAsync` mit `Task.Delay(40)` (`CanOpenFlyingMasterTests.cs:1307-1316`, sechs Positivtests) | Pool-Latenz von `SendControlFrame` + Hub-Zustellung | 40 ms | falsch rot |
| `RebindTransport_DoesNotDeliverBamMoreThanOncePerRebind` `finalSent > 5` (`J1939NodeTests.cs:3139`) | Timer-Granularität des Hintergrundstroms | 3 BAMs | falsch rot |
| `A_Retransmit_Request_For_The_Last_Of_255_Packets_Is_Served` (`J1939TpTests.cs:2418`) | 512 Pool-Hops in 5 s | lastabhängig | falsch rot |

Keiner dieser Tests kann falsch grün werden; die Negativfenster (`Task.Delay(100/200/300)` als
„nichts darf kommen“) können es, sind aber im Test als bewusste Restlücke kommentiert.

**Tests, die nicht fehlschlagen können oder etwas Falsches zementieren.**
- `A_Force_Reset_Completion_After_Dispose_Is_Swallowed` (`CanOpenFlyingMasterGapTests.cs:1321-1344`)
  und die zweite Hälfte von `A_Frame_During_The_Cold_Reset_Is_Dropped_…` (`:753-779`): keine
  Assertion; eine unbeobachtete Task-Exception lässt xunit grün.
- `Disposing_After_The_Borrowed_Actor_Leaves_Its_Sessions_To_It` (`J1939TpTests.cs:3179`) zementiert
  ewig offene Sends.
- `Tpdo_SyncTriggered_FiresEverySync`, `L2Demux_…` zementieren SYNC von einem Nicht-Produzenten (C14);
  `SingleFrame_RoundTrip(true, false)` und der Codec-Property-Test zementieren die ungültige FD-Länge
  (Q7); `IsoTpStminTimingTests.cs:113-115` zementiert STmin vor dem ersten CF als Normeigenschaft.
- `Schedule_Fires_Callback_After_The_Configured_Delay` erlaubt 25 % zu frühes Feuern
  (`ProtocolActorTests.cs:214-217`; Mutation actor-M1).
- `Cm_ExactBoundaryPayload_Reassembles` und `Bam_Roundtrip` zitieren FR-TP-032, prüfen FR-TP-033.
- `AssertEqualNameContest` (`J1939NodeTests.cs:1112-1127`) akzeptiert „Claim faultete“ und „Claim
  erfolgreich, später entthront“.

### 4.2 Mutationsprüfungen

Jede Behauptung „Test X fängt Y“ oder „Y ist ungetestet“ aus den Teil-Reviews wurde durch eine
Mutation im Quelltext gemessen: Änderung einspielen, Solution bauen, den benannten Test (oder die
Klasse) ausführen, Änderung zurücknehmen (`git checkout`). Erwartung „rot“ heißt: der Test muss die
Mutation erkennen; „grün“ heißt: es gibt keinen Test, der sie erkennt, und das belegt die Lücke.
Der Arbeitsbaum ist nach jeder Mutation wieder identisch mit `8cec2ee`.

<!-- MUTATION-TABLE-START -->
| Mutation | Erwartung | Ergebnis |
| --- | --- | --- |
| uds-M1 P2\* ab „jetzt“ statt ab Ankunft des 0x78 (`UdsClientImpl.cs:1219`) | rot | rot (`C_P2Star_Restarts_From_The_Pending_Response_Arrival`) |
| uds-M2 Null-Seed-Regel entfernt (`:399`) | rot | rot (`SecurityAccess_Treats_An_AllZero_Seed_As_Already_Unlocked`) |
| uds-M3 SID-Filter der Reception entfernt (`:1449`) | rot | rot (`P2_Is_Not_Extended_By_A_MultiFrame_Transfer_For_Another_Service`) |
| uds-M4 Handoff-Cutoff entfernt (`:1171`) | rot | rot (`UdsExpiredDeadlineTests` J und L) |
| uds-M5 Sub-Function-Echo-Prüfung entfernt (`:173`) | grün (Lücke U3) | grün |
| uds-M5b Multi-DID „missing“-Prüfung entfernt (`:301`) | grün (Lücke U1) | grün |
| j1939tp-M1 T4-Zustands-Guard entfernt (`J1939TpChannel.cs:1212`) | grün (Lücke T2) | grün |
| j1939tp-M2 T1 statt T2 nach erster CTS (`:666`) | rot | rot |
| j1939tp-M3 T2 nach Folge-CTS entfernt (`:784`) | grün (Lücke) | grün |
| j1939tp-M4 DA-Prüfung der Kontrollframes invertiert (`:572`) | rot | rot |
| j1939tp-M5 `maxPacketsPerCts == 0` als 255 (`:645-646`) | rot | rot |
| isotp-M1 BS/STmin aus jedem FC (`IsoTpChannel.cs:1397`) | rot | rot |
| isotp-M2 CAN_DL-Validierung der CF entfernt (`:1316`) | rot | rot |
| isotp-M3 stale STmin-Timer, beide Linien (`:1121` + `:811`) | rot | rot |
| isotp-M3a nur Handle-Dispose entfernt | grün (zweite Linie hält) | grün |
| isotp-M3b nur Identitätsprüfung entfernt | grün (erste Linie hält) | grün |
| isotp-M4 WFTmax `>` → `>=` (`:1411`) | rot | rot |
| isotp-M5 FD-Padding-Fix im Codec (`IsoTpFrameCodec.cs:236/379/429`) | grün (Lücke Q7) | grün |
| rawcan-M1 Bus-Off-Guard entfernt (`CanBusService.cs:665`) | grün (Lücke Q8) | grün |
| rawcan-M2 Claim-by-Completing entfernt (`:648`) | rot | rot |
| rawcan-M3 FIFO rückwärts (`:624-626`) | rot | rot (`Echo_Bus_Matches_Identical_Pending_Sends_In_Fifo_Order`) |
| rawcan-M4 DropOldest → DropNewest (`Subscription.cs:86`) | rot | rot |
| rawcan-M5 Rtr aus dem Key (`PendingSend.cs:33`) | rot | rot (nur `PendingKeyTests`; kein Bus-Test) |
| actor-M1 `DueTimestamp` × 0,8, Echtzeit-Test | grün (Untergrenze vakuum) | grün |
| actor-M1b `DueTimestamp` × 0,8, VirtualClock-Tests | rot | rot |
| actor-M2 Poll-Intervall 20 → 43 ms (`BusStateMonitorTests.cs:52, :118`) | rot (A1, Hälfte 2) | rot |
| actor-M2b Poll-Intervall 20 → 35 ms | rot (A1, Hälfte 1) | rot |
| actor-M2c Poll-Intervall 20 → 30 ms (Kontrolle) | grün | grün |
| reliability-M3 `FaultOccurred`-Hint entfernt (`BusStateMonitor.cs:218`) | grün (Lücke) | grün |
| reliability-M4 `try/finally` um `RecheckOnLoop` entfernt (`:274-281`) | grün (Lücke) | grün |
| reliability-M5 Handler-Detach in Dispose entfernt (`:201-208`) | grün (Lücke) | grün |
| canopenA-M1 Boot-up-Baseline (`NodeGuarding.cs:177`) | rot | rot |
| canopenA-M2 SYNC-Stopped-Gate im Tick-Pfad (`CanOpenNode.cs:1313`) | rot | rot |
| canopenA-M3 Zustands-Heartbeat-Gate (`CommunicationProfile.cs:664`) | rot | rot (`Nmt_Start_Without_A_Heartbeat_Producer_Puts_Nothing_On_The_Heartbeat_CobId`) |
| canopenA-M4 Discovery-Timeout-Klassifikation (`CanOpenDiscovery.cs:321`) | rot | rot |
| canopenA-M5 Equal-Claim-Guard (`FlyingMaster.cs:333-334`) | rot | rot |
| canopenA-W1 Stopped-Gate in `SendSyncAsync` eingebaut (`CanOpenNode.cs:436`) | grün, wenn kein Test Stopped unterscheidet (Lücke C14) | grün |
| j1939-M1 Überlappungsverbot der Periodic-Emission (`J1939NodeImpl.cs:1833`) | grün (Lücke) | grün |
| j1939-M2 Tick-Koaleszierung (`:1866-1869`) | grün (Lücke) | grün |
| j1939-M3 Fallback-Kanal tot (`:1214`) | grün (J9) | grün |
| j1939-M4 gerichtete Request for Address Claimed (`:1598`) | grün (Lücke) | grün |
| j1939-M5 NAME-AAC-Bit (`:579`) | grün (Lücke) | grün |
| canopenB-M1 Größenabweichung beim Upload (`CanOpenNode.cs:2140`) | grün (C10) | grün (492 CANopen-Tests) |
| canopenB-M2 Start-Rearm des Block-Upload-Servers (`SdoBlock.cs:1000`) | grün (#240-Relevanz) | grün |
| canopenB-M3 ACK-Rearm des Block-Upload-Servers (`:1007`) | rot | rot (`Sdo_BlockUpload_Server_Deadline_…`, Abbruch nach 2 s) |
| canopenB-M4 `Task.Delay(200)` → 0 im Timeout-Test | rot (Wanduhr-Abhängigkeit) | rot (Upload vervollständigt statt `SdoAbortException`) |
| canopenB-M5 client-seitige Toggle-Prüfung (`CanOpenNode.cs:2114`) | grün (Lücke) | grün |
<!-- MUTATION-TABLE-END -->

Bilanz: 47 Mutationen in 48 Läufen (canopenA-M3 nach einem Einspielfehler des Läufers wiederholt,
weil die Zielzeile zweimal vorkommt). 25 Läufe rot, 22 grün, jedes Ergebnis entspricht der
Vorhersage des jeweiligen Teil-Reviews. Die roten Läufe belegen, dass die benannten Tests ihre
Fix-Stellen tatsächlich pinnen; die grünen belegen ebenso viele Abdeckungslücken (Abschnitt 4.3).
Ein Lauf (canopenA-W1) war in der Läuferkonfiguration mit der falschen Erwartung „rot“ eingetragen;
das Ergebnis „grün“ ist das vom Teil-Review vorhergesagte.

### 4.3 Fehlende normative Negativtests (Rangfolge nach Nutzen)

1. K1: dritte Echo-Welt („Normal-Modus, geflaggtes Echo“) in `EchoWorldFixture`; ungeflaggtes Echo im
   Echo-Modus für `SendConfirmedAsync`.
2. T1/T2: BAM-Cancel plus Neusenden derselben PGN; CTS(0) in `WaitEom` und mitten im Block.
3. C9: Server-Abort mitten im Sub-Block eines Block-Downloads, danach Zählung auf 0x600+id.
4. U3: positive Antworten mit falschem Sub-Function-/DID-/BSC-Echo; Multi-DID-Teilantwort.
5. C10 und die client-seitigen Toggle-Prüfungen (`CanOpenNode.cs:2087`, `:2114`, Server `:1524`).
6. I6/I7: Service unter dem Kanal disposed; Dispose/Send-Race; `UseCanFd && !UsePadding`;
   negative `NBs`/`NCr`/T1..T4.
7. Q8: Fault ohne Bus-Off lässt Sends pendent; Bus-Off per Error-Frame + `BusState` ohne Fault.
8. J1939: gerichtete Request for Address Claimed, NAME-AAC-Ableitung, 250-ms-Default, Rebind-Fehlschlag
   beim Commit über das `openTransport`-Seam.
9. Reliability: `FaultOccurred`-Hint, werfender `BusState`-Getter, Handler-Detach bei Dispose.
10. CANopen: reserviertes Heartbeat-Byte, `SendSyncAsync` in Stopped, zweites zu kurzes RPDO → genau
    eine EMCY, Block-Upload von 0 Bytes, EMCY mit DLC < 8.

---

## 5. Was gut gelöst ist

1. **Der Befundbestand wurde abgearbeitet, nicht verwaltet.** Anhang A zeigt es Zeile für Zeile,
   und die Regressionstests treffen die Fix-Stellen: jede Mutation an einer Fix-Stelle wurde erkannt
   (Abschnitt 4.2), darunter zwei Verteidigungslinien für denselben Fehler (ISO-TP STmin-Timer,
   isotp-M3a/M3b).
2. **Timer sind messbar geworden.** `VirtualClock` mit `WaitUntilTimerArmedAsync` als exakter Barriere,
   `ManualTimeSource` über interne Konstruktoren in jedem Paket, Bracketing von beiden Seiten
   (`DeadlineTests.cs:114-132`, `ScheduleAt` 19/20 ms). Das ist die richtige Antwort auf #92.
3. **UDS-Zeitmessung als Stempelarithmetik**: Budgetstart = Handoff des letzten Frames, Ende =
   Ankunft des ersten Antwortframes, Cutoff gegen Stray-Antworten (`UdsClientImpl.cs:1111-1178`);
   erster Frame beendet P2, N_Cr übernimmt, mit SID-Filter gegen fremde Transfers.
4. **CANopen-Attribution vor Wirkung** (#18/#167): jedes SDO-Frame wird Phase, Command-Specifier und
   Multiplexer zugeordnet, bevor Timer oder Zustand angefasst werden; der Sende-Ausgang entscheidet
   über Timeouts, nicht die Uhr (#197); Block-Retransmission nach Norm auf beiden Seiten.
5. **J1939-TP-Admission** unter einem Lock mit `TxCompletion`: Slot-Freigabe und Ergebnis sind atomar,
   per-Ziel-Warteschlange in O(1), Retransmit-Frontier mit `int`-Zähler gegen den Byte-Wrap.
6. **J1939-Adress-Claim-Zustandsmaschine**: Adresse vor Announce invalidiert, Transport vor `Claimed`
   rebunden, Arbitrationsfenster erst nach TX-Bestätigung, Verlust faultet erst nach Handoff, Dispose
   settelt jeden Waiter; SPN-Indikatoren nicht ignorierbar (`J1939SpnValue`).
7. **RawCan-Echo-Matching claimt durch Vervollständigen** (`CanBusService.cs:632-648`): die einzige
   Form, die gegen den lockfreien Timeout-Pfad nicht racen kann; `DeferredEchoQueue` macht den
   einzigen Zustand, in dem FIFO beobachtbar ist, ohne Timer darstellbar.
8. **Actor-Lifecycle**: `_disposeGate`, `WithdrawableCall` mit einem CAS, Drei-Zustands-`TimerEntry`,
   Timer-Inserts am SynchronizationContext vorbei mit Test gegen einen wirklich verzögernden Kontext.
9. **Release-Kette mit sauberer Vertrauensgrenze**: OIDC nur im letzten Job, PAT nur im `env` eines
   Steps, `persist-credentials: false`, Paketinhalt und Version vor dem Tag geprüft, alle Actions per
   SHA gepinnt, Runbook mit Recovery-Pfad, netstandard2.0 auf dem net48-Leg tatsächlich ausgeführt.
10. **Entscheidungsdisziplin im Repo**: #53 → #102/#103, der Scope-Dialog zu CANopen (FR-CO-013..034),
    ADR 0001 mit Outcome — pro Punkt steht, was gemacht, was abgelehnt und warum.

---

## 6. Empfohlene Reihenfolge

Kein Befund dieses Reviews ist eine Regression eines offenen Branches; nach § *Stay inside the task*
gehen alle in Issues, und die Reihenfolge unten ist eine Priorität, keine Bündelung.

1. **Hardware-Annahmen (K1, Q7, Q8, C15):** Issue mit Upstream-Anteil (Vector-Bedingung, PCAN-Flag,
   FD-Längenvalidierung) an `pkuyo/CanKit`; hier J1939-Echo-Erkennung auf Frame-Marker umstellen,
   RawCan-Vertrag um Adapterliste oder Fallback ergänzen, dritte Echo-Welt in die Tests, NMT-Broadcast
   lokal anwenden.
2. **Abbrechbare Batch-Sender und Session-Identität (C9, T1, T2, J9):** Token in
   `SendOrderedControlFrames`, Session-Instanz statt Schlüssel in den BAM-Closures, T4-Guard streichen,
   Fallback-Kanal entfernen. Alle vier sind kleine Änderungen mit je einem neuen Negativtest.
3. **Vor der nächsten Minor-Version:** A1 (ganzzahlige Tick-Arithmetik), I6/I7/I8, U1/U2, J10/J11,
   C10–C13, C16, Q9 (`DisposeAsync` oder Vertragsklausel); dazu die Negativtests aus Abschnitt 4.3.
4. **Prozess (B12, B13):** Required Checks um `pack`, `validate release config`, `traceability`
   ergänzen; Merge-Queue-Entscheidung festhalten und die vier Stellen angleichen; `codecov/patch`
   entweder aus den required checks nehmen oder zum echten Gate machen; `npm ci --ignore-scripts`;
   Dependabot-NuGet-Präfix für Testwerkzeuge; `EnablePackageValidation` mit Baseline 1.3.0.
5. **Doku-Ehrlichkeit (B11, B14, #246 und die Drift-Listen in Abschnitt 3):** README-Snippet,
   „vier Pakete“-Reste, STmin-Absatz plus NFR-003, Normverweise je Dienst einmal festlegen.

---

## 7. Entscheidungen, die beim Maintainer liegen

1. **K1:** Echo-Erkennung frame-basiert machen (Marker + `IsEcho`) oder die Adapterliste im Vertrag
   benennen und PCAN/Vector im Echo-Modus ausschließen? Beides braucht das Upstream-Ticket.
2. **Q7:** Ist `UseCanFd = true, UsePadding = false` eine unterstützte Konfiguration? Wenn ja: Fix im
   Codec (Ausgabeänderung, SemVer-Einordnung nötig); wenn nein: Ablehnung bei `Open`.
3. **Q9:** `DisposeAsync` einführen (additiv) oder den Aufrufort „aus Kanal-Callbacks“ in den Verträgen
   ausschließen?
4. **U1:** Fehlende DIDs als Teilresultat liefern — Fix oder Breaking Change?
5. **C10:** Größenabweichung beim klassischen Upload abbrechen (Norm, CANopenNode) oder nachsichtig
   bleiben (canopen.py, Geräte mit angekündigter Maximallänge)?
6. **C14:** `SendSyncAsync` als roher Werkzeug-SYNC dokumentieren oder gated wie `SendEmcyAsync`?
7. **C16:** Reservierte Heartbeat-Bytes verwerfen oder Rohbyte in den EventArgs (additiv, `feat:`)?
8. **T2:** T4-Guard streichen oder CTS(0) außerhalb `WaitCts` ignorieren?
9. **T3:** Welche Revision von J1939-21 ist Referenz? Davon hängt das Abort-Rollenfeld ab.
10. **Normtext (der Maintainer hat CiA 301 im Volltext):** N_Ar-Abbruch, N_Cr-Start, `N_INVALID_FS`,
    STmin nur zwischen CF, FD-Auffüllen bis zur DLC-Stufe (ISO 15765-2); Guarding-Ereignis bei nicht
    alternierendem Toggle, Dummy-Mapping 0001h–0007h, Boot-up-/Node-Guarding-Abschnitte (CiA 301).
11. **B12:** Merge Queue aktivieren oder die Konfiguration entfernen? Required Checks erweitern?
12. **B13:** Dependabot-NuGet: `build(deps)` → kein Release mit manuellen `fix(deps)` für
    Laufzeit-Bumps, oder Testwerkzeuge aus Dependabot nehmen?
13. **A1:** Fix in der Bibliothek (ganzzahlig) oder Toleranz in `VirtualClock`? Empfehlung: Bibliothek.
14. **#240:** Test auf `ManualTimeSource` umstellen (Seam existiert) und das Issue schließen, oder offen
    lassen und beim nächsten Ausfall den Sub-Block-Index lesen?

---

## Anhang A · Status der Vorbefunde aus dem Review vom 2026-09-10

Belege (Datei:Zeile, PR/Commit, Test) stehen in den Teil-Reviews; hier der Stand in einer Zeile je
Befund beziehungsweise je Befundgruppe des Vorgängers. „bewusst offen“ heißt: dokumentiert
entschieden, mit Begründung im Code oder Issue. Zählung über die 65 Zeilen dieser Tabelle:
55 behoben oder entschieden, 4 bewusst offen, 4 teilweise, 2 offen (#246, #240).

| ID | Kurz | Status |
| --- | --- | --- |
| 1.1 | Block-Upload-Server nie nachgetriggert | behoben (`fd52d5d`, #17) |
| 1.2 | SDO-Client prüft Index/Subindex nicht | behoben (`2c86298` #18, `fa52f6e` #167) |
| Q1 | `IsOnCurrentActor` per `AsyncLocal` | behoben (`[ThreadStatic]`, PR #88, #19) |
| Q2 | Timer auf der Wanduhr | behoben (`MonotonicTimeSource`, PR #88, #20) |
| Q3 | Mailbox-Drain ohne Fairness | behoben (Count-Snapshot, PR #88, #21) |
| Q4 | `BusStateMonitor` postet pro Error-Frame | behoben (CAS-Gate, PR #87, #22) |
| Q5 | Subscriptions verlieren `IsEcho`/Timestamps | behoben (`CanFrameEvent`, #23) |
| Q6 | Echo-FIFO vergiftet; `PendingKey` ohne Flags | behoben (#24, #92; PR #100) |
| I1 | Stale STmin-Timer | behoben (`1e011f1`, #25; zwei Verteidigungslinien) |
| I2 | Unbegrenzte FD-Allokation | behoben (`MaxReceivePduLength`, #26) |
| I3 | Keine CAN_DL-Validierung | behoben (#27); FF-DLC-Stufe selbst ungeprüft |
| I4 | P2 endet erst nach Reassembly | behoben (`820757b` #28, `c9af8af` #143) |
| I5 | Null-Seed | behoben (`820757b`, #29) |
| J1 | DA für TP.CM ungeprüft | behoben (`7eeb4b1`, #30) |
| J2 | Tr statt T2 | behoben (`8a21aa8`, #31; Kommentar-Leiche `J1939TpTests.cs:1315`) |
| J3 | Parallele BAMs verschachteln | behoben (`0b5a2ad` #32, `742b87c` #204) |
| J4 | Abort-Codes ≠ Tabelle 7 | behoben (`38c0461`, `8a21aa8`, #33) |
| J5 | Keine Antwort auf Request for Address Claimed | behoben (`522b6ae`, #34; nur globale Requests getestet) |
| J6 | Kein Arbitrary-Fallback nach Verlust | behoben (`522b6ae`, #35) |
| J7 | CTR-Leak pro Send | behoben (`8a21aa8`, #36) |
| J8 | SPN-Sentinel, signed | behoben (`e6afc0a` #37/#98, `9b3b783` #99) |
| C1 | 0x20 als Expedited, 0x40 ignoriert | behoben (`0eaa3c1`, #38) |
| C2 | NACK-Sturm, Initiate-Heuristik | behoben (`21656f0`, #39) |
| C3 | Mapping ignoriert Subindex, kein Dummy | behoben (#40); 0001h offen (Frage 10) |
| C4 | RPDO-COB-IDs außerhalb 0x080..0x77F | behoben (#41) |
| C5 | `PdoMapping` per Referenz geteilt | behoben (#42) |
| C6 | Guarding-Toggle-Baseline; Life-Guarding fehlt | behoben (`14a60e3` #43/#114; `196eaa8`) |
| C7 | `SdoTransferMode` nicht durchgesetzt | behoben (Breaking, 1.3.0) |
| C8 | README überzeichnet | behoben; neue Drift in Abschnitt 3 |
| B1 | READMEs behaupten „nicht veröffentlicht“ | behoben (`ffd5efe`, #245) |
| B2 | Release-PAT persistiert | behoben (`85b2d45`, `fdc1e1a`) |
| B3 | Release nicht an 3-OS-CI gekoppelt | behoben über Ruleset |
| B4 | Runbook Tag-Reihenfolge, Recovery | behoben (`2e43e1e`) |
| B5 | Dependabot-Bumps lösen Releases aus | behoben für npm/pip; offen für NuGet-Testwerkzeuge (B13) |
| B6 | Actions per mutable Tag | behoben (SHA-Pins) |
| B7 | netstandard2.0 nie ausgeführt | behoben (`3997873`, net48-Leg) |
| B8 | Reliability-Polyfills | behoben (`f40b15c`) |
| B9 | Approval-Renderer blind | behoben (PublicApiGenerator, `8f0e3d4`); Package Validation offen (B13) |
| B10 | Historie / PR-Refs | bewusst offen (dokumentiert) |
| §3 RawCan | Transmit unter `_pendingGate` | bewusst offen (#102, im Code begründet, gepinnt) |
| §3 RawCan | `TryMatchEcho`-Allokation; Payload N-mal kopiert | behoben (`458ff08`) |
| §3 RawCan | Zweite Kopie in L3/L4-Readern | entschieden (#103: ISO-TP begründet, J1939-TP verschoben, CANopen begründet) |
| §3 RawCan | `CanIdFilter.Range/Mask` ohne Validierung | behoben (`45e10c7`, `c20f361`) |
| §3 RawCan | Interlocked auf volatile; Callback-Subscribe | behoben (PR #100; `11cd32c` #53) |
| §3 RawCan | `ProtocolErrorCodes` 6002..6005 | bewusst offen; Tripwire-Test, upstream unbelegt (verifiziert) |
| §3 Actor | Busy-Spin-Rundung | behoben (`Math.Ceiling`, #54) |
| §3 Actor | O(n)-Timerliste mit Leichen | teilweise: Leichen behoben, O(n) by design |
| §3 Actor | Dispose gibt still auf | behoben (`TimeoutException`, #54) |
| §3 Reliability | Deadline-Zombie; `Cancel()`-Doku | teilweise: dokumentiert, `Rearm` erzwingt Cancelled; kein viertes Flag (2.0) |
| §3 Reliability | `IsDegraded(Unknown)` | behoben |
| §3 Addressing | `J1939Id.Decompose`, `ComposePgn`, NAME-Endianness | behoben (`9d9e45e`, #55) |
| §3 Actor/…, Doc-Comments | zweisprachige Kommentare | behoben (0 CJK-Zeichen) |
| §3 ISO-TP | FF ≤ SF-Kapazität; BS/STmin aus jedem FC; Selbstempfang; Discard-Deadlock | behoben (`495d959`, `4e510ff`, `c747766`, #56) |
| §3 UDS | 0x7F-Maske; Suppress-Wait; NRC 0x21; `Elapsed`; kein Functional; Dispose-Semaphore | behoben (`e460d7f` #57 u. a.) |
| §3 J1939-TP | PDU1-Normalisierung; CTS-Retransmit; `maxPacketsPerCts == 0`; Event vor Inbox | behoben (`7130884` #58 u. a.) |
| §3 J1939 | Pseudozufallsverzögerung; zweiter Claim; Threads pro Runde; README-IDs; Options-Doku | behoben (`568dd5e`, `35c8799`, `77583b9`) |
| §3 CANopen | `MaxSdoTransferBytes`-Grenze; CTR-Leak; `blksize == 0`; Abort-Origin | behoben (#59) |
| §3 CANopen | RTR mit DLC 0 | bewusst offen (#59, Upstream-Behauptung nicht verifiziert) |
| §3 CANopen | Eigene Echo-Frames; Selbst-Start | teilweise (EMCY/Heartbeat/Claim gefiltert; NMT-Broadcast über Echo → C15) |
| §3 CANopen | SDO-Server in Stopped; Doku-Abweichungen; OD „Thread-safe“ | behoben (`196eaa8`); `SendSyncAsync` → C14 |
| §3 Repo/Docs | `.gitattributes`; `.NET 8`; getting-started; GitVersion; Coverage; `ci.yml:133`; Warnungen als Fehler; Upstream-Header | behoben |
| §3 Repo/Docs | README-Snippet; „vier Pakete“-Reste | teilweise (B11, B14) |
| §4 | FIFO-Test kann nicht fehlschlagen; Drop-Oldest; TryRead/Reconfigure; Codec-Throw-Test; Dispose-Exception-Typ; Stray-Segment-Test; CRC-KAT; Codec-Unit-Tests; `Task.Delay(30)`; `Task.Delay(50)`; `received <= sent` | alle behoben |
| #246 | README-STmin-Absatz | offen (Abschnitt 3, ISO-TP) |
| #240 | Block-Upload-Deadline-Test flaky | offen; Analyse in Abschnitt 4.1 |
