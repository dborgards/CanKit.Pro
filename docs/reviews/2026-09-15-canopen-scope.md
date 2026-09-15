# CANopen: Zuschnitt gegen CiA 301 — gegen den Normtext geprüft

Stand: 15.09.2026, `main` @ `2109967`. Erste von vier Zuschnitt-Runden.

Quelle: **CiA 301 v4.2.0 (CiA 2011), Volltext**, vom Maintainer bereitgestellt. Damit ist die
„zu prüfen"-Spalte aus [`2026-09-15-norm-gap.md`](2026-09-15-norm-gap.md) für CANopen aufgelöst:
was hier steht, ist am Dokument nachgelesen und mit Abschnitt zitiert, nicht erinnert.

Das ist der Unterschied, auf den es ankommt. Im Gap-Dokument stand über dieselben Punkte
ausdrücklich, sie seien meine Erinnerung. Zwei davon haben sich beim Nachlesen verschoben, einer
in jede Richtung.

**Und ein zweiter Fehler, der erst im Review aufflog.** Dieses Dokument hat „die Norm definiert X"
mit „die Implementierung muss X haben" gleichgesetzt — in der ersten Fassung an drei Stellen, und
nachdem ich diese korrigiert hatte, **noch zweimal in der Korrektur selbst** — beim
leeren Objektverzeichnis, bei der Übertragungsart und bei den Abort-Codes. Codex hat alle drei
auseinandergenommen, und alle drei zu Recht: CiA 301 bindet ein *Gerät*, diese Pakete sind eine
*Bibliothek*, und eine Protokolltabelle ist kein Implementierungsauftrag. Die betroffenen
Abschnitte sind entsprechend zurückgenommen; was übrig bleibt, ist schmaler und belastbarer.

Den Normtext zu haben schützt also nicht davor, ihn zu überdehnen — und einmal darauf hingewiesen
zu werden offenbar auch nicht. Die Frage, die bei jeder Zeile zu stellen ist, lautet nicht „steht
das in der Norm", sondern **„was genau kann die Anwendung hier nicht selbst tun"**. Erst die
zweite Frage trennt einen Bibliotheksdefekt von einer Tabellenzeile.

## Was die Norm entscheidet — und was das über zwei Codebasen sagt

### 1. Pflichtobjekte: es sind drei, nicht zwei

`1018h` Identity object trägt in §7.5.2.21 **`Category: Mandatory`**, und die Objektübersicht im
Anhang führt es als `1018h RECORD Identity Object IDENTITY (23h) ro M`. Ebenso
`1000h VAR device type … ro M` und `1001h VAR error register … ro M`.

| Objekt | | Kategorie | Im Paket |
|---|---|---|---|
| `1000h` | Device type | **M** | 0 Treffer |
| `1001h` | Error register | **M** | nur 2 Doku-Kommentare |
| `1018h` | Identity object | **M** | 0 Treffer |

**Folge für CanKit.Pro — enger gefasst, als ich sie zuerst geschrieben hatte (Codex auf #127):**
CiA 301 bindet das *fertige Gerät*, nicht eine wiederverwendbare Bibliothek. Vendor-ID, Produktcode
und Seriennummer kann ein Stack gar nicht kennen; sie zu erfinden wäre schlimmer als sie
wegzulassen. Das Paket ist also nicht dadurch nicht konform, dass `_od` leer startet
(`CanOpenNode.cs:65`) — die Anwendung *kann* die Objekte anlegen, und `ObjectDictionary.AddU32`
ist genau dafür öffentlich.

Was bleibt, ist schmaler und trotzdem ein Befund:

1. **Der Standardweg führt still zum nicht konformen Knoten.** Wer `CanOpen.OpenNode` aufruft und
   sonst nichts tut, hat ein Gerät ohne `1000h`, `1001h` und `1018h`; die erste SDO-Anfrage eines
   Konformitätswerkzeugs (`1000h:00`) bekommt Abort `0602 0000h`. Nichts warnt.
2. **Der dokumentierte Weg reicht nicht.** Die Paket-README zeigt in ihrem Beispiel
   `AddU32(0x1000, …)` und `AddU32(0x2000, …)` (`README.md:104-105`) — also *ein* Pflichtobjekt von
   dreien. Wer dem Beispiel folgt, baut einen Knoten, dem `1001h` und `1018h` weiterhin fehlen.

Das ist eine Frage an den Zuschnitt (wer legt sie an, und was passiert, wenn niemand es tut), keine
festgestellte Nichtkonformität des Stacks.

**Folge für EdsDcfNet:** `Parsers/XddCommNetProfileParser.cs:264` klassifiziert im XDD-Pfad
ausschließlich `0x1000` und `0x1001` als mandatory —

```csharp
// Mandatory objects: 1000h and 1001h
if (index == 0x1000 || index == 0x1001)
```

— und schiebt damit `1018h` in `OptionalObjects`. Nach CiA 301 ist das falsch. Der EDS/DCF-Pfad
ist nicht betroffen: er parst die `[MandatoryObjects]`-Sektion der Datei und nimmt keine eigene
Position ein. Betroffen ist nur die Klassifizierung beim XDD-Import.

Das ist ein Befund in *deinem* Repository, gefunden durch das Gegeneinanderhalten zweier Quellen —
und der Grund, warum die Antwort „0x1000, 0x1001, 0x1018" vorher als unbelegt markiert war.

### 2. PDO- und SDO-Records sind Pflicht, sobald das Gerät PDOs bzw. SDOs kann

Die Fußnoten der Objektübersicht sind hier eindeutig:

> \* If a CANopen device supports PDOs, the according PDO communication parameter and PDO mapping
> object entries in the object dictionary are mandatory. These may be ro.
> \*\* If a CANopen device supports SDOs, the according SDO parameters in the object dictionary
> are mandatory.

`CanKit.Pro.CANopen` kann beides. Damit ist die gemessene Asymmetrie kein Komfortthema:

| Record | | Kategorie | Im Paket |
|---|---|---|---|
| `1600h`/`1A00h` | PDO **mapping** | M (bei PDO-Support) | je 5 Treffer |
| `1400h`/`1800h` | PDO **communication** | M (bei PDO-Support) | **je 0** |
| `1200h` | SDO server parameter | M (bei SDO-Support) | **0** |

**Auch hier gilt die Einschränkung aus Punkt 1 (Codex auf #127), und sie ändert den Befund:** die
Anwendung *kann* `1200h`, `1400h`, `1800h` über `ObjectDictionary.AddU*` anlegen — die Fußnote
erlaubt sie sogar `ro`. Null Quelltext-Treffer belegen also nicht, dass der Stack ein konformes
Gerät verhindert.

Was er verhindert, ist ein **stimmiges** Gerät, und das kann die Anwendung nicht ausgleichen:

| Record | Per SDO beschreibbar? | Wirkt es? |
|---|---|---|
| `1600h`/`1A00h` | ja | **ja** — `ApplyTpdoMappingFromSdo` ersetzt das Mapping des laufenden TPDO-Slots (`CanOpenNode.PdoMapping.cs:282`) |
| `1400h`/`1800h` | nur als selbst angelegte OD-Einträge | **nein** — kein Pfad liest sie; Übertragungsart, Inhibit Time und Event Timer leben ausschließlich in `ConfigureTpdo` |

Ein von der Anwendung angelegtes `1800h:02` wäre also ein Wert, den der SDO-Server ausliefert und
annimmt, während der Knoten weiter sendet wie zuvor. Der Master liest eine Zusage, die das Gerät
nicht einhält — schlechter als das Fehlen des Objekts, und die fehlende Verdrahtung liegt innen,
wo die Anwendung nicht hinkommt.

Der Posten heißt damit nicht „Objekte anlegen", sondern **„OD und PDO-Engine für die
Kommunikationsparameter zusammenschließen, so wie es für das Mapping bereits geschieht"**.

### 3. Die Übertragungsart ist falsch kodiert, nicht nur zu grob

Die normative Wertetabelle der PDO-Kommunikationsparameter:

| Wert | Bedeutung |
|---|---|
| `00h` | synchronous (acyclic) |
| `01h`–`F0h` | synchronous (cyclic every n-th SYNC) |
| `F1h`–`FBh` | reserved |
| `FCh` | RTR-only (synchronous) |
| `FDh` | RTR-only (event-driven) |
| `FEh` | event-driven (manufacturer-specific) |
| `FFh` | event-driven (device profile / application profile specific) |

Dagegen `Pdo/PdoMapping.cs:63`:

| Enum | Wert | Was die Norm zu diesem Byte sagt |
|---|---|---|
| `EventDriven` | `0x00` | synchronous (acyclic) |
| `EventTimer` | `0x01` | synchronous, jeder SYNC |
| `Synchronous` | `0x02` | synchronous, jeder **zweite** SYNC |

**Korrektur (Codex auf #127): daraus folgt nicht, was ich daraus gefolgert hatte.** Das Enum ist
bereits `: byte` deklariert, seine Werte sind interne Diskriminanten, und **kein Pfad serialisiert
sie** — `ConfigureTpdo` vergleicht namentlich. `1800h` zu implementieren macht die Nummerierung
also *nicht* automatisch zum Draht-Defekt: die OD-Kodierung kann `Synchronous` explizit als `01h`
und die ereignisgesteuerten Modi als `FEh`/`FFh` schreiben. Meine Formulierung „erst das Enum auf
`byte` bringen" war zudem schlicht falsch — das ist es schon.

Dazu kommt ein Preis, den ich nicht bedacht hatte: `TpdoTransmission` steht in der öffentlichen
API-Baseline (`CanKit.Pro.CANopen.approved.txt:241`) und dient in `ConfigureTpdo` als
Default-Parameter `transmission = 0`. Umnummerieren wäre ein Bruch für jeden, der sich auf die
Zahlenwerte verlässt.

**Und noch eine Überdehnung, im zweiten Anlauf (Codex auf #127):** dass die Tabelle `02h`–`F0h` und
`FCh`/`FDh` *definiert*, verpflichtet kein Gerät, sie zu *unterstützen*. Eine Wertetabelle legt
Bedeutungen fest, keine Pflichten. Ein Knoten, der nur „jeder SYNC" und ereignisgesteuert kann, ist
zulässig — er muss das nur in seinem Kommunikationsrecord korrekt angeben, und zwar `ro`.

Solange keine Konformitätsklausel zitiert ist, die diese Modi verlangt, sind sie **Kandidaten für
den Zuschnitt, keine `Must`-Anforderung.** Was normativ bleibt, ist nur die Stimmigkeit: was das
Gerät kann, muss im Record stehen — und damit fällt dieser Punkt in Punkt 2 hinein statt daneben.

Die Reihenfolge-Behauptung „erst Enum, dann `1800h`" fällt ohnehin weg; sie stand auf der falschen
Prämisse.

### 4. Abort-Codes: 17 von 31

Tabelle 22 definiert 31 Codes. `Sdo/SdoAbortCode.cs` enthält 17 — alle 17 korrekt, keiner
erfunden. Es fehlen 14, darunter ausgerechnet der, den die Blockübertragung braucht:

`0504 0003h` invalid sequence number · `0604 0043h` general parameter incompatibility ·
`0604 0047h` general internal incompatibility · `0606 0000h` hardware error ·
`0609 0030h`/`0031h`/`0032h`/`0036h` Wertebereichs-Codes · `060A 0023h` resource not available ·
`0800 0020h`–`0800 0024h` (fünf Codes: Anwendung, local control, device state, OD-Generierung,
no data available).

**Aber „17 von 31" ist keine Mängelliste (Codex auf #127).** Tabelle 22 ist eine
Protokoll-Referenz, kein Implementierungsauftrag. Auf der Empfangsseite geht nichts verloren:
`SdoAbortException.AbortCode` ist ein roher `uint`, ein unbekannter Peer-Code kommt unverfälscht
bei der Anwendung an. Auf der Sendeseite braucht der Server nur benannte Werte für Zustände, die er
tatsächlich erkennt und meldet.

Damit schrumpft der Befund auf einen konkreten Eintrag: **`0504 0003h`**, weil der Blocktransfer
diesen Zustand erkennt und heute keinen passenden Code dafür hat. Für die Hardware-, Gerätezustands-
und Wertebereichs-Codes gibt es kein Verhalten im Paket, das sie auslösen würde — sie werden Scope,
wenn ein solches Verhalten dazukommt, und vorher nicht.

### 5. Was sich zugunsten des Codes aufgelöst hat

- **Die Block-Transfer-CRC ist korrekt.** §7.2.4.3.16 verlangt Polynom x¹⁶+x¹²+x⁵+1, Initialwert
  `0000h`, Prüfwert `31C3h` für `"123456789"`. `SdoBlockFrames.ComputeCrc16Xmodem` ist
  CRC-16/XMODEM mit Polynom `0x1021` und Init `0`; nachgerechnet liefert der Algorithmus für
  `"123456789"` exakt `0x31C3`. **Zwei Randnotizen:** kein Test pinnt diesen Wert, obwohl die Norm
  ihn als Testvektor mitliefert — und der XML-Kommentar zitiert §7.2.4.3.15, wo der Algorithmus in
  §7.2.4.3.16 steht (§7.2.4.3.15 ist *Protocol SDO block upload end*).
- **Das TIME-Objekt ist optional.** `1012h VAR COB-ID TIME UNSIGNED32 rw O`. Es als Lücke zu
  führen war zu streng; die GAP-Analyse liegt mit ihrem ⏸️ richtig. Gehört als bewusste
  Auslassung in den Ausnahmekatalog, nicht in die Mängelliste.
- **`pst = 0` erzwingen ist normkonform.** §7.2.4.3.13: *„pst = 0: Change of transfer protocol not
  allowed."* Die Einstufung des pst>0-Fallbacks als Ausnahmekandidat ist damit gedeckt.

### 6. Zwei offene Defekte, jetzt am Normtext bestätigt

- **#38** — §7.2.4.3.3: *„e: transfer type 0: normal transfer, 1: expedited transfer"* und
  *„e = 0, s = 0: d is reserved for further use."* Ein Download-Initiate mit `e=0, s=0` (also cs
  `0x20`) ist ein legaler segmentierter Transfer ohne Größenangabe. `CanOpenNode.cs:1037` fängt ihn
  über die Expedited-Maske ab und committet vier Nullbytes. Bestätigt.
- **#39, erste Hälfte** — §7.2.4.3.10 und Figur 28: ein Sub-Block ist eine Folge von Segmenten,
  abgeschlossen durch **ein** *Confirm block*; `ackseq` nennt das letzte korrekt empfangene
  Segment, und der Client sendet ab `ackseq + 1` erneut. Ein ACK je Segment außer der Reihe ist
  nicht das Protokoll. Bestätigt.

## Vorschlag für den Zuschnitt

| | Was | Normlage | Vorschlag |
|---|---|---|---|
| 1 | `1000h`, `1001h`, `1018h` | Pflicht **für das fertige Gerät** | wie entschieden: Stack füllt `1000h`/`1001h`, benannter Helfer für `1018h`. Zusätzlich das README-Beispiel auf alle drei erweitern — es zeigt heute nur `1000h` |
| 2 | `1400h`/`1800h`, `1200h` **an die PDO-Engine anschließen** | Pflicht bei PDO-/SDO-Support; die Verdrahtung liegt innen | der eigentliche Posten dieser Runde — nicht „Objekte anlegen", sondern sie wirksam machen, wie es `1600h`/`1A00h` bereits sind |
| 3 | Übertragungsart `02h`–`F0h`, `FCh`/`FDh` | Wertetabelle definiert, verpflichtet nicht | **Kandidat, keine `Must`** — was unterstützt wird, muss im Record stehen; mehr verlangt CiA 301 hier nicht |
| 4 | Abort-Code `0504 0003h` | normativ, und der Blocktransfer erkennt den Zustand | klein; die übrigen 13 erst, wenn ein Verhalten sie auslöst |
| 5 | CRC-Test auf `31C3h`, Zitat auf §7.2.4.3.16 | Testvektor liefert die Norm | trivial, gehört zu 4 |
| 6 | TIME `1012h` | **optional** | in den Ausnahmekatalog, nicht bauen |

Das ergäbe etwa sechs bis acht neue `Must`-Anforderungen zu den heutigen zwölf — und damit wäre
„CANopen vollständig" erstmals eine prüfbare Aussage statt einer Einschätzung.

Was CiA 301 **nicht** entscheidet und offen bleibt: bit-granulares PDO-Mapping und die
CiA-302/304/305-Themen aus der GAP-Analyse. Die stehen in anderen Dokumenten.
