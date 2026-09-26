# CANopen: Zuschnitt der Master-/Tool-Rolle

Stand: 26.09.2026, `main` @ `871d153`. Zweite Zuschnitt-Runde — CANopen, Master- und Tool-Rolle.
Issue [#131](https://github.com/dborgards/CanKit.Pro/issues/131). Die Geräterolle ist entschieden
und übersetzt (`docs/reviews/2026-09-15-canopen-scope.md`, FR-CO-013..028). Diese Runde durfte
danach kommen; sie kommt jetzt. Sie entscheidet die offenen Punkte nicht zu Ende: was unten als
Empfehlung steht, wartet auf den Maintainer. Solange das so ist, bleibt #131 offen, und die SRS
bekommt aus dieser Datei keine neue `Must`-Zeile.

Die Frage ist dieselbe wie in der ersten Runde, und sie wird genauso eng gestellt. Nicht „steht
das in einer Norm", sondern **was die Anwendung hier nicht selbst tun kann**. Eine Protokolltabelle
ist kein Implementierungsauftrag, und eine Bibliothek ist kein Gerät. Die vier Prüffragen stehen
unten noch einmal; die zweite hat die dritte Lesart, die #131 verlangt: **gilt der Posten für beide
Rollen?**

## Woher die Belege kommen

Drei Stufen, dieselben wie in [`2026-09-15-norm-gap.md`](2026-09-15-norm-gap.md). Was hier steht,
trägt seine Stufe mit.

| Stufe | Was in dieser Runde so belegt ist |
|---|---|
| **gemessen** | Öffentliche API und Quelltext auf `main` @ `871d153`, und `EdsDcfNet` **1.13.0** (das ist die Version in `Directory.Packages.props`, gelesen am Tag `v1.13.0`, nicht an einem neueren Stand der Bibliothek) |
| **Norm** | Nur Abschnitte, die die Geräterunde am Volltext von CiA 301 v4.2.0 nachgeschlagen hat. Der Text liegt in diesem Arbeitsstand nicht noch einmal bei; ein Abschnitt, den jene Runde nicht belegt hat, wird hier nicht nachgetragen |
| **Sekundärquelle** | CiA 302. Der Volltext lag auch diesmal nicht vor. Flying Master wird deshalb aus dem Produkt und aus dem, was das Paket über sich selbst sagt, beurteilt — nicht aus einem Paragraphen, den niemand nachgelesen hat |

Der Satz, den #131 der GAP-Analyse zuschreibt — Flying Master sei *„praktisch nie gebraucht"* —,
steht so nicht in [`2026-09-15-norm-gap.md`](2026-09-15-norm-gap.md). Was auf der Platte steht, ist
enger: die Paket-README führt *„CiA 302 (boot-up manager, flying master)"* als außerhalb von CiA 301
und außerhalb des Pakets (`src/CanKit.Pro.CANopen/README.md:92`), und die Geräterunde lässt die
CiA-302/304/305-Themen am Ende offen. Mehr gibt die Ablage nicht her. Daraus wird hier keine
Normpflicht und keine Normfreiheit zitiert.

## Was die Rolle schon ist

`ICanOpenNode` ist eine Instanz und zwei Rollen. Der Schnittstellenkommentar sagt es selbst: ein
Knoten dient als Gerät (eigenes Objektverzeichnis, eigene PDOs, von einem Master per SDO
konfiguriert) und als Werkzeug oder Master (SDO-Client, NMT-Master, Heartbeat- und
Node-Guarding-Consumer, SYNC-Produzent) (`ICanOpenNode.cs:18-24`). Die SRS meint denselben Akteur:
die Stakeholder-Tabelle nennt *„CANopen-Master/-Node-Anwendung"* als eine Zeile. Es gibt keinen
zweiten Typ, den diese Runde erst erfinden müsste.

Wer `CanOpen.OpenNode` aufruft, ist danach ein Knoten. Der Konstruktor geht nach Pre-Operational
und sendet den Boot-up (`CanOpenNode.cs:248-255`). Die Default-SDO-Kennungen tragen die Node-ID des
**Servers** (`0x600 + Server`, `0x580 + Server`, `CanOpenCobId.cs:16-18`), nicht die des Clients.
Der Boot-up ist also keine Notwendigkeit des SDO-Kanals, sondern die Folge davon, dass der Client
auf einem Knoten lebt. Das Sample sagt die praktische Folge: die Node-ID des Scanners muss frei
sein (`samples/CanKit.Pro.Sample.CanOpenBusScan/Program.cs:363-364`).

### Bereits da — behalten

| Dienst | Was die API heute tut | Anforderung | Beleg |
|---|---|---|---|
| NMT-Master | `SendNmtCommandAsync(command, targetNodeId)` sendet COB-ID `0x000`; `0` ist Broadcast | FR-CO-007 | `ICanOpenNode.cs:139-146`, `CanOpenNode.cs:260-266` |
| NMT-Zustand fremder Knoten | `HeartbeatReceived` für jeden Producer auf `0x700 + id` (Boot-up als `Initializing`), außer dem eigenen, solange dafür kein Consumer registriert ist | FR-CO-008, der Empfang; das Timeout ist der Consumer darunter | `CanOpenNode.cs:770-806`, `928-944` |
| Heartbeat-Consumer | `AddHeartbeatConsumer` / `RemoveHeartbeatConsumer`, Timeout über `1016h` | FR-CO-008 | `ICanOpenNode.cs:161-171` |
| Node-Guarding-Consumer | `StartNodeGuardingConsumer` pollt per RTR, `NodeGuardingTimeout` nach Guard Time × Life Time Factor | FR-CO-009 | `ICanOpenNode.cs:332-343` |
| SYNC-Produzent | `StartSyncProducer`, `StopSyncProducer`, `SendSyncAsync` | FR-CO-010 | `ICanOpenNode.cs:177-187` |
| SYNC-Consumer | `SyncReceived` feuert für die COB-ID aus dem **eigenen** `1005h`, nicht für jedes SYNC auf dem Bus | FR-CO-010 | `ICanOpenNode.cs:85-88`, `CanOpenNode.cs:721-726` |
| EMCY empfangen | `EmcyReceived` für fremde EMCY auf `0x081..0x0FF`; die eigene, zurückgehallte, wird verworfen | FR-CO-011 | `CanOpenNode.cs:760-768` |
| SDO-Client | `SdoUploadAsync` / `SdoDownloadAsync`, expedited, segmented, block. Eine Übertragung je Server gleichzeitig; ein zweiter Aufruf gegen denselben Server wirft, verschiedene Server laufen parallel | FR-CO-002, FR-CO-003, FR-CO-004 | `CanOpenNode.cs:488-511`, `1519-1523` |
| PDO des **eigenen** Knotens | `ConfigureTpdo` / `ConfigureRpdo`, höchstens vier je Richtung. `RpdoReceived` kommt, nachdem die Nutzdaten ins **eigene** OD geschrieben wurden | FR-CO-005, FR-CO-006, FR-CO-017 | `ICanOpenNode.cs:289-300`, `CanOpenEvents.cs:87-90`, `CanOpenNode.Pdo.cs:554-604` |
| EDS/DCF des **eigenen** Knotens | `CanOpenDeviceDescription.Load` liest die Datei; `OpenNode(..., description)` baut daraus **diesen** Knoten | FR-CO-025..028 | `CanOpenDeviceDescription.cs:69-81`, `CanOpen.cs:61-66` |

NMT-Kommandos an andere Knoten hebt der Knoten nicht. `HandleNmtCommand` kehrt zurück, wenn das
Ziel weder die eigene Node-ID noch der Broadcast ist (`CanOpenNode.cs:862-863`); der Kommentar
sagt, warum: `NmtCommandReceived` gilt dem, was diesen Knoten adressiert. Ein Master, der das
Kommando selbst geschickt hat, kennt es. Ein Beobachter, der fremde NMT-Kommandos sehen will,
hängt sich eine Ebene tiefer an den Bus.

PDO-Frames, die keinem gültigen RPDO dieses Knotens gehören, fallen aus `HandleIncoming` unten
heraus. Nach dem RPDO-Zweig kommen EMCY, Heartbeat und SDO; ein TPDO auf `0x180 + fremde Node-ID`
ist keines davon (`CanOpenNode.cs:746-847`). Die Subscription sieht den Bereich `0x080..0x77F`
(`CanOpenNode.cs:231-237`) und tut mit dem fremden PDO nichts.

Das ist der Bestand. Nichts davon ist eine Lücke, und nichts davon wird in dieser Runde zur
Diskussion gestellt. Wer einen dieser Dienste zurückbauen wollte, müsste eine ausgelieferte
`Must`-Zeile zurücknehmen. Die Empfehlung ist, das nicht zu tun.

## 1. Die EDS/DCF eines fremden Knotens

Die erste Runde hat entschieden, dass eine EDS **unser** Objektverzeichnis und **unsere**
PDO-Konfiguration speist. Für ein Werkzeug ist der häufigere Fall der andere: die Beschreibung
des Geräts, mit dem man spricht. Dieselbe Bibliothek, anderer Zweck. Der Unterschied ist keine
Formulierung. `OpenNode` mit dieser Datei **wird** das Gerät: es legt die Objekte bei sich an,
wertet `$NODEID` gegen die eigene Node-ID aus, sendet den Boot-up und beantwortet SDO. Auf einem
Bus, an dem das beschriebene Gerät schon hängt, sind das zwei Knoten mit einem Anspruch. Die
README zeigt nur diesen Weg (`README.md:169-170`). Wer ihr für ein fremdes Gerät folgt, betritt
den Bus als dieses Gerät.

`Load` ohne `OpenNode` tut das nicht. `CanOpenDeviceDescription.Objects` ist das Modell von
`EdsDcfNet`, nicht das OD des Knotens (`CanOpenDeviceDescription.cs:43-45`). An `EdsDcfNet` 1.13.0
hängt daran bereits, was #131 als Werkzeugnutzung beschreibt, soweit es die **Datei** betrifft:

- `CanOpenObject.ParameterName` und `CanOpenSubObject.ParameterName` benennen den Index.
- `AccessType` trägt `ReadOnly`, `WriteOnly`, `ReadWrite`, `ReadWriteInput`, `ReadWriteOutput`,
  `Constant`; dazu `LowLimit` und `HighLimit`.
- `ObjectDictionaryExtensions.GetPdoCommunicationParameters` und `GetPdoMappingParameters` listen
  die PDO-Records über `0x1400..0x15FF` / `0x1800..0x19FF` und `0x1600..0x17FF` / `0x1A00..0x1BFF`,
  also auch jenseits der vier, die der Knoten selbst kann.
- `GetParameterValueAsObject(..., nodeId)` wertet `$NODEID` aus und nimmt bei einer DCF den
  gesetzten Wert vor dem Default.

`EdsDcfNet` ist eine Abhängigkeit, wie in der Geräterunde festgehalten: sie liefert, was sie
liefert, und ein Mangel an ihr ist keiner von uns. Hier ist es umgekehrt. Sie liefert das Modell.
CanKit ruft die genannten Mitglieder nicht auf — `GetPdoCommunicationParameters` hat im Paket
keinen Treffer — und reicht das Modell nur durch. Die Namen neu zu bauen wäre ein zweites
Verzeichnis neben einem, das es schon gibt.

Was die Anwendung damit **nicht** selbst aus einem öffentlichen CanKit-Aufruf bekommt, sind zwei
Dinge, und beide liegen neben Code, den der Gerätepfad schon hat, nur privat:

1. **Die Bytes für den SDO-Client.** `SdoDownloadAsync` nimmt ein `byte[]`. Die Umwandlung aus
   dem Wert der Datei in die Little-Endian-Breite des Typs steht in `ToBytes`
   (`CanOpenNode.DeviceDescription.cs:572`) und ist `private`. `CanOpenValueConverter.Format` in
   `EdsDcfNet` gibt die Zeichenkette der Datei zurück, nicht den Frame. Jedes Werkzeug schreibt
   sich die Breite selbst, und die Gerätepfad-Tabelle (Boolean, die Integerbreiten, Real32/64,
   die drei String-Arten) driftet dann gegen den Knoten.
2. **Eine Nutzlast, die nicht ins eigene OD soll.** Der einzige Entpacker schreibt die Bytes in
   das lokale Verzeichnis und meldet danach `RpdoReceived` (`CanOpenNode.Pdo.cs:579-604`). Er
   kennt keine Namen aus einer fremden Datei.

Die Schreibprüfung gegen die Beschreibung ist der schmale Fall dazwischen. `AccessType.ReadOnly`
und `Constant` kann der Aufrufer am Modell selbst lesen, bevor er `SdoDownloadAsync` ruft. Eine
Funktion, die nur das noch einmal sagt, beschreibt eine API, die es schon gibt. Tragfähig wird
sie zusammen mit den Bytes: existiert der Eintrag, erlaubt die Datei den Schreibzugriff, passt
die Breite zum Typ, liegt ein gesetzter Wert zwischen `LowLimit` und `HighLimit`, wenn beide
sich lesen lassen — und das Ergebnis sind die Bytes oder eine Ablehnung **aus der Datei**. Was
sie nicht ist: die Abort-Tabelle des eigenen SDO-Servers. `0609 0030h` heißt bei uns „diese
Übertragungsart kann dieser Knoten nicht". Das ist eine Aussage über unsere Engine. Ein fremdes
Gerät kann eine Art können, die wir nicht können. Die Vorprüfung, die das vorhersagt, würde
dieselbe Überdehnung wiederholen, an der die Geräterunde die Abort-Codes zurückgenommen hat.

**Prüffragen.**

1. **Verhalten oder API?** Die Namen sind eine API, und sie ist da. Die Arbeit, die fehlt, ist
   die Abbildung Datei → Bytes und Datei + COB-ID + Nutzlast → benannte Werte. Beides ist eine
   Funktion über dem Modell, keine zweite PDO-Engine.
2. **Beide Pfade, und beide Rollen?** Der Gerätepfad (`OpenNode` mit EDS, Fallback ohne EDS)
   bleibt, wie FR-CO-025 ihn beschreibt. Der Peer-Katalog ist ein dritter Gegenstand: er wird
   nicht installiert, er hat keine Laufzeit, und er gilt für die Tool-Rolle. Ein Prozess, der
   beides ist — eigener Knoten und Werkzeug —, hält sein OD und die fremde Beschreibung
   auseinander. Eine Option „diese EDS ist ein Peer" an `OpenNode` würde die Rollen in denselben
   Konstruktor legen und den Boot-up der fremden Datei nicht verhindern.
3. **Beide Richtungen?** Hier gibt es kein OD des Peers in unserem Prozess. Die beiden Quellen
   sind die Datei und, wo der Bus abweicht, die Records, die der SDO-Client von `1800h`/`1A00h`
   bzw. `1400h`/`1600h` liest. Wer nur die Datei dekodiert, liegt falsch, nachdem ein Master das
   Mapping umgeschrieben hat. Die Datei ist der Anfang, der gelesene Record der Vorrang. Das ist
   Posten 2b, nicht ein Zusatz in einem Kommentar.
4. **Deckt der Beleg die Zusage?** Die Zusage ist das, was die Datei sagt: Name, Zugriff, Typ,
   Grenzen, Mapping-Einträge, `$NODEID` gegen die Node-ID des **Peers**. Eine DCF bringt die
   Node-ID mit (`CanOpenDeviceDescription.NodeId`), eine EDS nicht; der Aufrufer nennt sie. Die
   Zusage ist nicht, dass der Peer sich heute noch so verhält.

**Empfehlung, nicht entschieden.** Einen Peer-Katalog bauen, der `Load` benutzt und `OpenNode`
nicht ruft. Namen und PDO-Listen nicht nachbauen. Die Byte-Abbildung des Gerätepfads für den
SDO-Client verwendbar machen und die Vorprüfung auf das begrenzen, was die Datei hergibt. Das
Schreiben einer DCF an den Peer — die Werte, die die Datei als gesetzt führt, über den vorhandenen
Client — ist dieselbe Abbildung, nur in einer Schleife. Es ist die Tool-Seite von FR-CO-025 und
nicht der Boot-up-Manager aus CiA 302; die README nennt beide in einem Atemzug, und genau das
soll hier nicht in einen Topf.

## 2. Fremde PDOs beobachten

Dekodieren braucht das Mapping. Zwei Herkünfte, beide in #131 genannt, und beide heute ohne
Empfänger: die EDS des Geräts (Abschnitt 1) oder ein SDO-Upload von `1600h`/`1A00h` und der
COB-ID aus `1400h:01`/`1800h:01`. Der Upload ist der Client aus FR-CO-002. Das Mapping-Wort ist
die Kodierung, die der Gerätepfad schon liest (`CanOpenNode.Pdo.cs:348-358`: Index, Sub-Index,
Bitlänge). Es fehlt der Schritt, der eine empfangene Nutzlast damit zerlegt, ohne sie in das
eigene OD zu schreiben.

`ConfigureRpdo` ist dieser Schritt nicht. Die Prüffrage nach dem Verhalten der Engine fällt hier
eindeutig aus, und zwar gegen die Wiederverwendung:

- Der Knoten hat vier RPDOs (`CanOpenNode.CommunicationProfile.cs:64`). Eine fremde EDS darf
  mehr deklarieren; `EdsDcfNet` listet sie bis `0x19FF`/`0x1BFF`, der Gerätepfad legt einen
  fünften Record nicht an (`CanOpenNode.DeviceDescription.cs:307-310`). Durch die eigenen Slots
  beobachtet man vier PDOs und verliert den Rest still.
- `HandleRpdo` wirkt nur in Operational und nur auf ein gültiges RPDO (`CanOpenNode.Pdo.cs:554-557`).
  Ein Werkzeug, das den Bus lesen will, während es selbst in Pre-Operational ist, sieht nichts.
  Ein synchrones RPDO hält die Daten bis zum nächsten SYNC **dieses** Knotens
  (`CanOpenNode.Pdo.cs:571-574`). Beobachten will den Frame, wenn er kommt.
- Die Nutzdaten landen im eigenen OD (`CanOpenNode.Pdo.cs:595`). Ein Treffer auf einem Objekt,
  das der Knoten selbst in einem TPDO führt, ist der Zustand des Werkzeugs und nicht der des
  Peers. Die Change-of-State-Sperre für busbürtige Schreibzugriffe verhindert die Echoschleife;
  sie macht das OD nicht zum richtigen Ort.
- Bit 31 der COB-ID heißt „PDO existiert nicht" (FR-CO-018, CiA 301 Table 70, in der Geräterunde
  belegt). Ein rohes Übernehmen der Kennung als Filter würde ein abgeschaltetes PDO einschalten —
  derselbe Fehler wie Posten 25 der Geräterunde, nur auf der Leseseite.

Die Frames selbst sind keine Lücke der Bibliothek. `ICanBusService.Subscribe` ist der Weg, auf
dem der Knoten sie auch sieht; ein zweiter Abonnent auf demselben Dienst bekommt sie, ohne dass
`HandleIncoming` sie kennen muss. Ein Beobachter, der kein Knoten sein will, öffnet keinen. Er
vermeidet damit den Boot-up. SDO und NMT bleiben an den Knoten gebunden, weil der Client dort
lebt. PDO-Dekodieren nicht.

Bitgranulares Mapping bleibt draußen, aus demselben Grund wie in der Geräterunde.
`PdoMappingEntry` lehnt eine Bitlänge ab, die nicht ein Vielfaches von 8 ist
(`Pdo/PdoMapping.cs:31-32`). Eine fremde EDS kann so ein Mapping führen. Der Dekodierer meldet
es als nicht zerlegbar. Ihn dazu zu bringen, Bits zu ziehen, wäre der Umfang, den die erste
Runde ausdrücklich nicht entschieden hat, durch die Hintertür der Tool-Rolle.

**Empfehlung, nicht entschieden.** Eine Funktion: COB-ID, Nutzlast, Mapping, und — wenn eine
Beschreibung dazu gehört — Name und Typ je Feld. Das Mapping kommt aus der Datei oder aus den
per SDO gelesenen Records; liegen beide vor, gelten die gelesenen. Die Funktion schreibt kein OD
und verlangt keinen NMT-Zustand. Das Abonnement der Frames bleibt beim Aufrufer. Ein stiller
Knoten ohne Boot-up ist dafür nicht nötig; `OpenNode` um eine Option zu erweitern, die den
Boot-up unterdrückt, würde einen Knoten bauen, der sich nicht ankündigt, damit er Frames sehen
kann, die er auch ohne Knoten sehen kann.

## 3. Knoten-Scan

`samples/CanKit.Pro.Sample.CanOpenBusScan` tut, was #131 beschreibt. Es hört `HeartbeatReceived`,
nimmt Node-IDs ohne Heartbeat aus und liest `1000h:00` per `SdoUploadAsync`
(`Program.cs:55-90`). Wer antwortet — auch mit einem Abort außer Timeout —, gilt als anwesend;
danach liest es `1018h` (`Program.cs:141-156`, `178-210`). Die Parallelität über die Node-IDs ist
von der Client-Regel gedeckt: eine Übertragung je Server, verschiedene Server gleichzeitig.

Das ist eine Anwendung auf FR-CO-002 und FR-CO-008, keine fehlende Fähigkeit. Die Namen der
Identity-Sub-Indizes stehen im Sample fest (`Program.cs:300-311`), einschließlich eines Namens
für Sub-Index 5. Die Geräterunde hat am Volltext gelesen, dass der Identity-Record die Sub-Indizes
`01h`–`04h` führt und ein Minimum bei `sub0 = 01h` aufhört. Sub-Index 5 ist damit keine Aussage
von CiA 301, die diese Runde wiederholen könnte, und schon gar keine, die in die Bibliothek
gehört. Wer eine EDS des Peers hat, nimmt den Namen dorther. Wer keine hat, zeigt den Rohwert.

Ein Knoten ohne Heartbeat und ohne SDO-Server kommt in diesem Scan nicht vor. Ein Gerät nach
CiA 301, das SDO kann, hat den Server; eines, das beides nicht spricht, ist mit diesen beiden
Diensten nicht zu sehen. Das ist die Grenze der Methode, und sie ist im Sample ehrlich: es gibt
keine dritte Suche.

**Empfehlung, nicht entschieden.** Kein FR-CO für den Scan. Das Sample bleibt das Beispiel. Ein
Helfer in der Bibliothek würde eine Schleife und eine Policy kapseln (wen das Timeout ausschließt,
welche Sub-Indizes mit festem Namen belegt sind) und damit eine Anwendung in den Stack ziehen,
die der Stack schon ermöglicht. Wenn der Maintainer den Helfer trotzdem will, dann roh: `1000h`
und `1018h:00`–`04h` als Bytes, Namen nur aus einer Beschreibung, und der Aufrufer stellt die
Node-ID, die den Boot-up sendet.

## 4. Flying Master

CiA 302 ist in dieser Runde nicht gelesen. Die Empfehlung steht auf dem, was das Paket ist.

Der Master, den die API heute ist, ist einer. Ein Prozess ruft `SendNmtCommandAsync` und
`StartSyncProducer`. Nichts handelt aus, wer das sein darf, und nichts übernimmt, wenn ein
anderer aufhört. Ein Werkzeug oder eine Steuerung, die **der** Master des Busses ist, braucht
diese Aushandlung nicht. Sie zu einem Hauptfall zu machen verstärkt die Dienste, die schon
`Must` sind. Es erzeugt nicht den Fall, für den mehrere Knoten denselben Anspruch hätten.

Flying Master ist dieser andere Fall: mehr als ein Teilnehmer kann Master werden, und das Netz
einigt sich. Das ist eine Eigenschaft des Netzes, nicht der Tool-Rolle. Die Geräterunde hat
CiA 302 unter der Annahme geparkt, der Knoten sei ein Gerät. Die Tool-Rolle hebt das nicht auf.
Ein Werkzeug, das der Master sein soll, verhandelt nicht; ein Gerät, das keiner ist, auch nicht.
Die Empfehlung ist deshalb dieselbe Auslassung, aus einem Grund, der ohne den Normtext trägt:
**der eine Master ist der Hauptfall, den die API schon abbildet, und Flying Master ist der
bestrittene.** Ihn zu bauen wäre ein neues Protokoll neben CiA 301, und dafür fehlt hier der
Text, an dem die Geräterunde ihre Posten festgemacht hat.

Die README koppelt daran den Boot-up-Manager. Der ist hier nicht mitentschieden. Werte aus einer
DCF an einen vorhandenen Knoten zu schreiben ist Posten 1, über den SDO-Client, und bleibt eine
Empfehlung auch dann, wenn Flying Master draußen bleibt. Wer beides in einem Satz absagt, streicht
die Tool-Seite der Gerätebeschreibung mit.

**Empfehlung, nicht entschieden.** Flying Master nicht bauen. CiA 304 und CiA 305 bleiben, wo die
Geräterunde sie gelassen hat: nicht in diesem Zuschnitt. Der Boot-up-Manager als eigenes
CiA-302-Verfahren ebenfalls nicht, solange sein Text nicht vorliegt; das gezielte Schreiben der
DCF ist davon getrennt und hängt an Posten 1.

## 5. Rückwirkung auf die erste Runde

#131 stellt die Frage, ob die Fallback-Records ohne EDS an Wert verlieren, wenn das Werkzeug der
Hauptfall ist. Die Posten sind gebaut (FR-CO-013..024, der EDS-Pfad FR-CO-025..028). Eine
Priorität nach der Umsetzung ist die Frage, ob man sie zurücknimmt.

Der Knoten, den ein Werkzeug öffnet, **ist** der Fallback. Das Scan-Sample ruft `OpenNode` ohne
Beschreibung (`Program.cs:49-52`) und betritt den Bus mit genau den Records, die FR-CO-013
anlegt. Sie beschreiben den Knoten, der den Client trägt. Sie zu schwächen, weil die interessanten
EDS die der **anderen** sind, macht den eigenen Knoten wieder zu dem stillen leeren Verzeichnis,
das die erste Runde verworfen hat.

Die fremde Beschreibung kommt dazu. Sie ersetzt den Fallback nicht, und sie ersetzt FR-CO-025
nicht: der eine Pfad baut uns, der andere beschreibt den Peer. Beide bleiben, weil ein Prozess
beides zugleich sein kann — die Schnittstelle ist dafür gebaut.

**Empfehlung, nicht entschieden.** FR-CO-013..028 bleiben in Bestand und Priorität. Die neue
Arbeit, wenn sie angenommen wird, ist der Peer-Katalog. Sie konkurriert nicht mit den
Geräteposten, weil die schon geliefert sind.

Ob die Master-Rolle „der" Hauptfall ist, ist damit die kleinere Frage. Die SRS führt beide in
einem Akteur, FR-CO-007 verlangt beide Seiten als `Must`, und der Typ ist einer. Die Empfehlung
ist, beide als Hauptfall zu lassen. Ein Vorrang des Werkzeugs würde an den gebauten
Geräteposten nichts ändern und an Flying Master auch nicht.

## Die Posten

Die Tabelle ist die Liste, die eine spätere Übersetzung in Anforderungen vorfindet. Was nur im
Fließtext stünde, hätte die Geräterunde beim Übersetzen verloren; das gilt hier genauso. Die
Spalte Empfehlung ist eine Empfehlung. Die letzte Spalte ist der Punkt, den der Maintainer noch
ausdrückt. Nichts davon ist eine `Must`-Zeile, und diese Runde schreibt keine.

| | Was | Woher | Art der Arbeit | Empfehlung | Maintainer |
|---|---|---|---|---|---|
| — | Die Tabelle „Bereits da" | gemessen; FR-CO-002/003/004/007/008/009/010/011 | nichts bauen | behalten | bestätigen, dass daran nichts zurückgenommen wird |
| 1 | Peer-Katalog: eine geladene EDS/DCF beschreibt einen fremden Knoten und wird nicht über `OpenNode` installiert | Architektur. Der Gerätepfad ist der andere Zweck derselben Datei (FR-CO-025) | neuer Gegenstand neben dem Knoten, kein Modus des Konstruktors | bauen, nach der Bestätigung | ja — ob überhaupt |
| 1a | Indexnamen, Zugriff, Grenzen, PDO-Listen, `$NODEID` | gemessen an `EdsDcfNet` 1.13.0, durch `CanOpenDeviceDescription.Objects` erreichbar | nicht nachbauen | die Bibliothek benutzen | — |
| 1b | Dateiwert → Bytes, die `SdoDownloadAsync` sendet, einschließlich der Vorprüfung dessen, was die Datei sagt (Zugriff, Breite, lesbare Grenzen) | Architektur. `ToBytes` ist privat (`CanOpenNode.DeviceDescription.cs:572`) | dieselbe Abbildung wie der Gerätepfad, für den Client. Keine vorhergesagten Abort-Codes der eigenen Engine | bauen, als Teil von 1 | ja — der Umfang der Vorprüfung |
| 1c | Gesetzte Werte einer DCF über den SDO-Client an den Peer schreiben | Architektur, nicht CiA 302. Dieselbe Abbildung wie 1b, über die Objekte, die die Datei als schreibbar führt | Schleife über den vorhandenen Client | bauen, nachdem 1b steht | ja |
| 2 | PDO-Nutzlast eines fremden Knotens zerlegen: COB-ID, Nutzlast, Mapping → Felder, mit Name und Typ, wenn eine Beschreibung dazu gehört | Architektur. Die Kodierung des Mapping-Worts ist die, die der Gerätepfad liest; CiA 301 Table 70 (Bit 31) ist in der Geräterunde belegt | eine Funktion. Schreibt kein OD, hängt nicht am NMT-Zustand, kennt mehr als vier PDOs | bauen, als Teil von 1 | ja |
| 2b | Liegen Datei und per SDO gelesene `1400h`/`1600h`/`1800h`/`1A00h` vor, gelten die gelesenen Records | Architektur. Der Client dafür ist FR-CO-002 | Vorrang, keine zweite Bedeutung | so, wenn 2 gebaut wird | ja |
| 2c | Das Abonnement der Frames | gemessen: `ICanBusService.Subscribe` sieht sie, `HandleIncoming` wirft fremde PDOs weg | beim Aufrufer lassen | nicht in den Knoten ziehen | ja — falls der Katalog das Abo doch besitzen soll |
| 3 | `ConfigureRpdo` als Beobachtung fremder TPDOs | gemessen: vier Slots, nur Operational, SYNC hält zurück, Schreibzugriff ins eigene OD | — | nicht dieser Weg | — |
| 4 | Knoten-Scan über `1000h`/`1018h` als Anforderung | gemessen: das Sample auf dem vorhandenen Client und `HeartbeatReceived` | — | kein FR. Sample bleibt | ja — falls doch ein Helfer, dann roh |
| 5 | Flying Master | Paket-README: außerhalb. CiA 302 in dieser Runde nicht gelesen | neues Protokoll | nicht bauen | ja |
| 6 | Boot-up-Manager nach CiA 302, als Verfahren | derselbe Satz der README; Text nicht gelesen | — | nicht bauen. Das DCF-Schreiben ist Posten 1c und fällt nicht mit | ja |
| 7 | Priorität und Bestand von FR-CO-013..028 | gemessen: der Tool-Knoten ohne eigene EDS ist dieser Fallback (`CanOpenBusScan`) | — | nicht zurücknehmen. Beide Rollen bleiben Hauptfall | ja |
| 8 | Stillen Knoten ohne Boot-up | gemessen: der Boot-up hängt an `OpenNode` (`CanOpenNode.cs:255`). Beobachten braucht den Knoten nicht | — | nicht bauen | ja |
| 9 | Bitgranulares PDO-Mapping | Geräterunde, offener Rest. `PdoMappingEntry` lehnt es ab | — | nicht bauen. Posten 2 meldet es als nicht zerlegbar | — |
| 10 | Nodelist-Projekt (`.cpj`) | gemessen: `EdsDcfNet` 1.13.0 führt `NodelistProject`, das Paket referenziert es nicht | eine Schleife über Posten 1, sobald der steht | zurückstellen | — |
| 11 | TIME (`1012h`) auf der Tool-Seite, CiA 304, CiA 305 | Geräterunde: TIME optional und ausgelassen; 304/305 nicht in diesem Zuschnitt | — | nicht in dieser Runde | — |

Posten 2c ist die Stelle, an der eine API leicht zur Engine wird. Die Funktion aus Posten 2
bekommt die Nutzlast. Wer sie im Knoten auf jedes fremde PDO hebt, baut die Beobachtung in
`HandleIncoming` und damit in den NMT-Zustand und in die vier Slots zurück — die Fragen 1 und 2
wären dann wieder nur für den eigenen Knoten beantwortet. Deshalb steht das Abo in einer eigenen
Zeile.

## Die vier Prüffragen, für die Übersetzung

Sie stehen in der Geräterunde am Ende der Postenliste. Hier gelten sie unverändert, mit der
dritten Lesart der zweiten:

1. **Beschreibt die Zeile Verhalten oder nur eine API?** Posten 1a ist eine API und wird nicht
   noch einmal gebaut. Posten 1b und 2 sind Verhalten: Bytes erzeugen, eine Nutzlast zerlegen.
   Wo eine Zeile eine Signatur nahelegt, ist die offene Frage, was mit dem eigenen OD und dem
   NMT-Zustand geschieht. Die Antwort für 2 ist: nichts.
2. **Gilt der Posten für beide Pfade — und für beide Rollen?** Der EDS-Pfad und der Fallback der
   Geräterunde bleiben die des eigenen Knotens. Der Peer-Katalog gilt der Tool-Rolle und teilt
   sich das OD nicht mit ihr. Eine Zeile, die nur den Knoten meint, der die Datei geladen hat,
   beschreibt FR-CO-025 noch einmal und nicht diese Runde.
3. **Gilt er in beide Richtungen?** Für den Peer gibt es kein Laufzeit-OD bei uns. Die beiden
   Seiten sind Datei und Bus. Posten 2b ist diese Frage; ohne ihn dekodiert die Datei einen
   Knoten, den ein Master schon umgemappt hat.
4. **Deckt der Beleg die Zusage?** Posten 1b sagt, was die Datei sagt, und nicht, welchen
   Abort der Peer schicken wird. Posten 5 sagt nicht, CiA 302 verlange oder erlasse etwas.
   Posten 4 sagt nicht, ein Scan sei unmöglich; er sagt, der vorhandene Client ihn schon kann.

## Was der Maintainer noch entscheidet

Fünf Punkte, und keiner ist in dieser Datei geschlossen:

1. **Beide Rollen bleiben Hauptfall**, und FR-CO-013..028 bleiben, wie sie sind.
2. **Flying Master wird nicht gebaut.** Der Boot-up-Manager nach CiA 302 auch nicht; das
   Schreiben einer DCF an den Peer (Posten 1c) ist davon getrennt.
3. **Der Knoten-Scan wird keine Anforderung.** Das Sample bleibt der Beleg.
4. **Der Peer-Katalog (Posten 1, 1b, 1c, 2, 2b) wird gebaut**, als eigene Anforderungen, nachdem
   das angenommen ist — mit dem Pfad, nicht davor, wie Anhangpunkt 6 der SRS es für FR-CO-025..028
   festgehalten hat.
5. **Beobachten hängt am vorhandenen Bus-Abonnement und an Posten 2**, nicht an `ConfigureRpdo`
   und nicht an einem Knoten ohne Boot-up.

Bis dahin ist diese Datei die Vorlage und nicht der Zuschnitt. #131 bleibt offen.
