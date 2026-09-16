# CANopen: Zuschnitt gegen CiA 301 — gegen den Normtext geprüft

Stand: 15.09.2026, `main` @ `2109967`. Erste von vier Zuschnitt-Runden.

Quelle: **CiA 301 v4.2.0 (CiA 2011), Volltext**, vom Maintainer bereitgestellt. Damit ist die
„zu prüfen"-Spalte aus [`2026-09-15-norm-gap.md`](2026-09-15-norm-gap.md) für CANopen aufgelöst:
was hier steht, ist am Dokument nachgelesen und mit Abschnitt zitiert, nicht erinnert.

Das ist der Unterschied, auf den es ankommt. Im Gap-Dokument stand über dieselben Punkte
ausdrücklich, sie seien meine Erinnerung. Zwei davon haben sich beim Nachlesen verschoben, einer
in jede Richtung.

**Und ein zweiter Fehler, der erst im Review aufflog.** Dieses Dokument hat „die Norm definiert X"
mit „die Implementierung muss X haben" gleichgesetzt — über fünf Review-Runden hinweg **acht
Mal**, und dreimal davon in der jeweiligen Korrektur der Runde davor — beim
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
wegzulassen. **Ein Minimum-Record ist trotzdem möglich, und zwar wahrheitsgemäß** — die Norm gibt
die Vokabel dafür: für Sub `01h` gilt *„The value 0000 0000h shall indicate an invalid vendor-ID"*,
also ist 0 dort ein **definierter** Wert und keine Erfindung. Für die Subs `02h`–`04h` ist
`0000 0000h` dagegen *reserved*. Und Sub `00h` („Highest sub-index supported") hat Wertebereich
`01h`–`04h`. Ein Record mit **sub0 = `01h` und Vendor-ID = 0** ist damit strukturell konform und
sagt genau das, was zutrifft: keine Vendor-ID zugeteilt. Die Subs 02–04 gehören dann
**weggelassen, nicht genullt** — das ist der Unterschied zwischen „unbekannt" und einem
reservierten Wert. Das Paket ist also nicht dadurch nicht konform, dass `_od` leer startet
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

Der Befund entstand durch das Gegeneinanderhalten zweier Quellen — und er ist der Grund, warum die
Antwort „0x1000, 0x1001, 0x1018" vorher als unbelegt markiert war.

**Er gehört aber nicht in diesen Zuschnitt (Maintainer, 16.09.).** EdsDcfNet ist für CanKit.Pro
eine externe Abhängigkeit, wie `pkuyo/CanKit` es für L0/L1 ist — sie liefert, was sie liefert. Tut
sie etwas nicht, bauen wir notfalls einen Workaround; wir führen keine Erwartungen an sie als
unsere Mängel. `CLAUDE.md` schreibt dieselbe Haltung für den Upstream bereits fest: *als NuGet-Paket
konsumiert, nicht geforkt … lies nach, statt anzunehmen.*

Was für **uns** daraus folgt, ist eine Randbedingung und kein Ticket: wer ein Objektverzeichnis aus
einer **XDD** befüllt, darf sich auf deren `MandatoryObjects` nicht verlassen und prüft die
Pflichtobjekte selbst. Beim Lesen aus **EDS/DCF** entfällt das — dort parst die Bibliothek die
Sektion der Datei und nimmt keine eigene Position ein.

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

Er verhindert auch kein **stimmiges** Gerät — solange der Record statisch und `ro` ist, siehe
unten. Offen bleibt genau ein Fall, und der ist schmal:

| Record | Per SDO beschreibbar? | Wirkt es? |
|---|---|---|
| `1600h`/`1A00h` | ja | **ja** — `ApplyTpdoMappingFromSdo` ersetzt das Mapping des laufenden TPDO-Slots (`CanOpenNode.PdoMapping.cs:282`) |
| `1400h`/`1800h` | nur als selbst angelegte OD-Einträge | **nein** — kein Pfad liest sie |

**Zweimal zurückgenommen (Codex auf #127), und was danach übrig bleibt, ist deutlich kleiner:**

Ich hatte behauptet, ein von der Anwendung angelegtes `1800h:02` sei zwangsläufig eine Zusage, die
das Gerät nicht einhält. Das stimmt nicht für den statischen Fall: `AddU8`/`AddU32` nehmen
`OdAccess.ReadOnly`, der SDO-Server weist Schreibzugriffe darauf ab (`CanOpenNode.cs:1063`), und
dieselbe Anwendung kann `ConfigureTpdo` dieselben Werte geben. Dann liest der Master einen
zutreffenden Record — **ganz ohne Verdrahtung zwischen OD und Engine.** Ein kohärentes Gerät ist
also baubar, und 2a ist keine zwingende Anforderung.

Die Sorge schrumpft damit auf **einen** Fall, den die Anwendung nicht auflösen kann, und einen, den
sie mit Disziplin auflösen kann:

1. **Beschreibbare Records — nicht auflösbar.** Ein `rw`-Eintrag nimmt den Schreibzugriff eines
   Masters an, und die Engine sieht ihn nie. Wer `1400h`/`1800h` beschreibbar anbietet, muss ihn
   verdrahten.
2. **Drift — auflösbar, aber von Hand.** Ruft die Anwendung später `ConfigureTpdo` erneut, kann sie
   den OD-Eintrag über `ObjectDictionary.WriteRaw`/`WriteUnsigned` nachziehen; diese lokalen Setter
   prüfen die Zugriffsflags **nicht** (`ObjectDictionary.cs:131-141`), es funktioniert also auch bei
   einem `ro`-Record. Die tatsächliche Grenze ist damit schmaler, als ich geschrieben hatte: der
   Abgleich ist **weder automatisch noch atomar**, aber möglich. Als „von der Anwendung nicht
   lösbar" zu führen hätte Drift-Vermeidung unberechtigt in den Zuschnitt gehoben (Codex auf #127).

**Und die Inhibit Time gehört gar nicht hierher.** `ConfigureTpdo` nimmt Übertragungsart, COB-ID
und Event-Timer-Intervall entgegen — **keine Inhibit Time** (`ICanOpenNode.cs:184-187`), und die
Paket-README sagt es selbst: *„a change-of-state TPDO is not rate-limited"* (`README.md:51`). Sie
lebt also nicht „ausschließlich in `ConfigureTpdo`", wie ich geschrieben hatte, sondern nirgends.
Ein wirksames `1800h:03` verlangt **neues Scheduling-Verhalten**, nicht das Anschließen vorhandener
Zustände. In einem Posten namens „verdrahten" sähe sie erledigt aus und bliebe wirkungslos.

**Nachgeschlagen (16.09.):** der Sub-Eintrag selbst ist `Entry category: **Optional**`. Aus der
Norm folgt also keine Pflicht, ihn anzubieten — die Hälfte hatte im ersten Entwurf gefehlt. Dass er
trotzdem gebaut wird, folgt aus der Architekturentscheidung unten, nicht aus CiA 301.

**`1200h` gehört nicht in denselben Posten (Codex auf #127).** Es ist der SDO-Server-Parameter und
hat mit der PDO-Engine nichts zu tun: `CanOpenNode.cs:774` erkennt eine Anfrage am Vergleich
`cobId == CanOpenCobId.SdoRx(_nodeId)`, also fest `0x600 + Node-ID`, und antwortet auf
`0x580 + Node-ID` — nie über einen OD-Eintrag. Es in eine Zeile mit `1400h`/`1800h` zu schreiben
hätte genau das erlaubt, wovor der Befund warnt: die PDO-Arbeit als erledigt zu verbuchen, während
`1200h` weiter wirkungslos ist.

Für `1200h` gibt es deshalb zwei getrennte Wege, und der zweite ist der kleinere: entweder die
SDO-Server-COB-IDs tatsächlich aus dem Record lesen, oder — weil die Fußnote `ro` ausdrücklich
erlaubt — **einen schreibgeschützten Record bereitstellen, der die festen Vorgabe-IDs abbildet.**
Der Stack unterstützt ohnehin nur diese; ein `rw`-Record würde eine Beweglichkeit zusagen, die es
nicht gibt — derselbe Fehler wie bei `1800h:02`, nur auf dem SDO-Pfad.

### 3. Die Übertragungsart: das Enum kann die Wertetabelle nicht abbilden

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

Die drei Enum-Namen entsprechen nicht den Bytes, die gleich heißen — `EventDriven` ist `0x00`, und
`0x00` ist normativ *synchronous acyclic*. **Daraus folgt aber nicht, was ich zuerst gefolgert
hatte (Codex auf #127): „falsch kodiert" ist es nicht, weil nichts es kodiert.** Das Enum ist
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

**Zwei Nachträge vom 16.09., beide gemessen:**

- **`FCh`/`FDh` ist nicht dasselbe wie `02h`–`F0h`.** „Jeder n-te SYNC" ist ein Zahlenwert, den die
  API nicht ausdrücken kann; RTR-only ist ein *Verhalten*, das der Stack nicht hat — kein TPDO-Pfad
  antwortet auf Remote-Frames. Die Verrohrung steht allerdings: `frame.IsRemoteFrame` wird gelesen
  und als `isRtr` bis in den Dispatcher gereicht (`CanOpenNode.cs:645`, `:703`), genutzt bisher nur
  vom Node Guarding. Also eine Erweiterung an vorhandener Naht, keine neue Naht — aber ein anderer
  Aufwand als der Zahlenwert, und deshalb ein eigener Posten.
- **Die Wertetabelle ist ohne API-Bruch erreichbar.** Der EDS-Pfad nimmt das rohe Byte aus der
  Gerätebeschreibung; `TpdoTransmission` bleibt unverändert die Komfort-API für Handkonfiguration.
  Damit erledigt sich die Umnummerierungs-Frage endgültig: es gibt keinen Grund, ein öffentliches
  Enum anzufassen, dessen Werte ohnehin niemand serialisiert.

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

- **#38 — beide Hälften, nicht nur die Server-Seite (Codex auf #127).** Die Norm trennt `e` und `s`
  auf beiden Wegen, und das Ticket führt beide; mein erster Entwurf hat nur die erste bestätigt.
  - **Server**, §7.2.4.3.3: *„e: transfer type 0: normal transfer, 1: expedited transfer"* und
    *„e = 0, s = 0: d is reserved for further use."* Ein Download-Initiate mit `e=0, s=0` (cs
    `0x20`) ist ein legaler segmentierter Transfer ohne Größenangabe. `CanOpenNode.cs:1037` fängt
    ihn über die Expedited-Maske ab und committet vier Nullbytes.
  - **Client**, §7.2.4.3.6: dieselbe Trennung für die Upload-Initiate-Antwort (`scs = 2`). Ein
    Server, der mit `0x40` antwortet — normal, Größe unbekannt —, ist konform.
    `CanOpenNode.cs:1564` vergleicht exakt auf `0x41`, lässt die Antwort also liegen, und der
    Transfer läuft in den Timeout.

  Beide Hälften gehören in eine Anforderung. Sonst lässt sich #38 schließen, während Uploads von
  konformen Peers weiterhin nicht funktionieren — genau die Lücke, die der Befund benennt.
- **#39, erste Hälfte** — §7.2.4.3.10 und Figur 28: ein Sub-Block ist eine Folge von Segmenten,
  abgeschlossen durch **ein** *Confirm block*; `ackseq` nennt das letzte korrekt empfangene
  Segment, und der Client sendet ab `ackseq + 1` erneut. Ein ACK je Segment außer der Reihe ist
  nicht das Protokoll. Bestätigt.

## Entschieden (16.09.): EDS speist beides, Fallback nur ohne EDS

Der Zuschnitt ist keine Liste von Einzelfällen mehr, sondern folgt aus einer Architekturentscheidung
des Maintainers:

1. **Der Normalfall ist die EDS.** Sie speist **das Objektverzeichnis *und* die
   PDO-Konfiguration** — eine Quelle für beides.
2. **Ein codiertes Minimum greift nur, wenn keine EDS vorliegt.** Dessen Records sind statisch
   `ro`: die Anwendung hält dort beide Enden, der Record beschreibt genau das eine Verhalten, das
   der Stack kann, und `ro` sagt dem Master wahrheitsgemäß, dass daran nichts zu drehen ist.
3. **Was eine EDS angibt und der Stack nicht umsetzen kann, wird degradiert und gemeldet** — nicht
   abgelehnt (sonst ist der Pfad unbrauchbar, bis alles gebaut ist) und vor allem nicht
   stillschweigend ignoriert. Das wäre wieder die Zusage ohne Deckung, um die sich die halbe
   Reviewrunde gedreht hat.

### Was diese Entscheidung auflöst

**Posten 2a verschwindet als Frage.** Die Wahl „statisch `ro` oder verdrahtet" bestand nur, solange
Record und Verhalten aus zwei Händen kamen. Speist dieselbe EDS beides, stimmen sie per
Konstruktion überein, und es gibt nichts abzugleichen.

**Posten 1 löst sich weitgehend auf.** `1000h`, `1001h` und `1018h` kommen aus der EDS — dort
gehören sie hin, die Datei hat eine `[MandatoryObjects]`-Sektion. Das codierte Minimum deckt nur
noch den Fall ohne EDS.

### Was sie hinzufügt

Und das ist die interessantere Richtung: **die Posten 3 und 2c werden nötig, obwohl CiA 301 sie
nicht verlangt.** Eine reale EDS enthält Werte, die `ConfigureTpdo` nicht entgegennehmen kann —
Übertragungsart `02h`–`F0h` kennt das Enum nicht, und eine Inhibit Time hat die Signatur gar nicht
(`ICanOpenNode.cs:184-187`). Wer solche Dateien einliest, steht vor derselben Wahl wie bei 2a, nur
eine Ebene tiefer: umsetzen, oder beim Laden melden, was nicht umsetzbar ist.

Die Norm bleibt, was sie ist — der Inhibit-Time-Eintrag ist `Optional`, die Wertetabelle
verpflichtet niemanden. **Notwendig werden beide durch die Architektur, nicht durch CiA 301**, und
diese Unterscheidung gehört in die Anforderungen, damit später niemand eine Pflicht daraus liest,
die dort nicht steht.

### Die Posten

| | Was | Woher die Notwendigkeit kommt | Art der Arbeit |
|---|---|---|---|
| 1 | EDS → Objektverzeichnis **und** PDO-Konfiguration | Architekturentscheidung | neuer Pfad, Abhängigkeit auf EdsDcfNet |
| 2 | Degradieren mit Meldung, wenn eine EDS Nicht-Umsetzbares angibt | Architekturentscheidung | gehört zu 1, aber eigene Anforderung — sonst wird es zum stillen Ignorieren |
| 3 | Fallback: `1000h`, `1001h`, `1018h` (sub0 = `01h`, Vendor-ID = 0) | CiA 301 §7.5.2.21, Objektübersicht `ro M` | klein, additiv. Subs 02–04 weglassen, nicht nullen |
| 4 | Fallback: `1400h`/`1800h`/`1200h` statisch `ro` | Fußnoten der Objektübersicht (Pflicht bei PDO-/SDO-Support) | klein |
| 5 | Übertragungsart `02h`–`F0h` | **Architektur** (die Norm verpflichtet nicht) | API-Erweiterung **ohne Bruch**: EDS-Pfad nimmt das rohe Byte |
| 6 | Übertragungsart `FCh`/`FDh` (RTR-only) | **Architektur** | Verhaltenserweiterung an vorhandener Naht (`isRtr` erreicht den Dispatcher) |
| 7 | Inhibit Time `1800h:03` | **Architektur** (Norm: `Entry category: Optional`) | **das größte Stück** — neues Scheduling im TPDO-Pfad |
| 8 | Abort-Code `0504 0003h` | normativ; der Blocktransfer erkennt den Zustand | trivial |
| 9 | CRC-Test auf `31C3h`, Zitat auf §7.2.4.3.16 | Testvektor liefert die Norm | trivial |
| 10 | TIME `1012h` | `rw O` — **optional** | nicht bauen, in den Ausnahmekatalog |

### Was offen bleibt

**Ein Master, der zur Laufzeit auf `1800h:02` schreibt.** Das ist unabhängig davon, woher der
Record beim Start kam: wer die Records `rw` anbietet, muss den Schreibzugriff bis zur Engine
führen. Die EDS-Entscheidung berührt das nicht, weil sie nur die Befüllung regelt.

Was CiA 301 **nicht** entscheidet und offen bleibt: bit-granulares PDO-Mapping und die
CiA-302/304/305-Themen aus der GAP-Analyse. Die stehen in anderen Dokumenten.
