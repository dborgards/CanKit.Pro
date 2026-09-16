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
also baubar, und eine zwingende Verdrahtung von `1400h`/`1800h` gegen die Engine folgt daraus
nicht.

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
2. **Ein codiertes Minimum greift nur, wenn keine EDS vorliegt.** Dessen **PDO-Records** sind
   statisch `ro`: die Anwendung hält dort beide Enden, der Record beschreibt genau das eine
   Verhalten, das der Stack kann, und `ro` sagt dem Master wahrheitsgemäß, dass daran nichts zu
   drehen ist. **Nur für sie trägt das die Norm:** Fußnote `*` der Objektübersicht erlaubt `ro`
   ausdrücklich für *„PDO communication parameter and PDO mapping object entries"* — und für
   sonst nichts. Wo ein Objekt selbst `rw` vorschreibt, ist `ro` keine zulässige Vereinfachung,
   sondern eine Abweichung; siehe Posten 19.
3. **Was eine EDS angibt und der Stack nicht umsetzen kann, wird degradiert und gemeldet** — nicht
   abgelehnt (sonst ist der Pfad unbrauchbar, bis alles gebaut ist) und vor allem nicht
   stillschweigend ignoriert. Das wäre wieder die Zusage ohne Deckung, um die sich die halbe
   Reviewrunde gedreht hat.
4. **Degradieren heißt: auch das Objektverzeichnis trägt den degradierten Wert** (Codex auf #129).
   Den Originalwert aus der EDS ins OD zu kopieren und daneben anders zu laufen wäre genau die
   Inkonsistenz, gegen die Punkt 1 geschrieben ist — der Master läse dann die Zusage, die das
   Gerät nicht einhält. Betroffene Einträge werden also auf den umgesetzten Wert korrigiert,
   weggelassen oder das PDO wird deaktiviert; die Meldung sagt, was davon geschah.

### Was diese Entscheidung auflöst

**Die Wahl „statisch `ro` oder verdrahtet" verschwindet als Frage.** Sie bestand nur, solange
Record und Verhalten aus zwei Händen kamen. Speist dieselbe EDS beides, stimmen sie per
Konstruktion überein, und es gibt nichts abzugleichen.

**Das codierte Minimum für `1000h`, `1001h` und `1018h` schrumpft auf den Ausnahmefall.** Im
Normalfall kommen sie aus der EDS — dort gehören sie hin, die Datei hat eine
`[MandatoryObjects]`-Sektion. Das codierte Minimum deckt nur noch den Fall ohne EDS.

### Was sie hinzufügt

Und das ist die interessantere Richtung: **Übertragungsart und Inhibit Time werden nötig, obwohl
CiA 301 beides nicht verlangt.** Eine reale EDS enthält Werte, die `ConfigureTpdo` nicht
entgegennehmen kann — Übertragungsart `02h`–`F0h` kennt das Enum nicht, und eine Inhibit Time
hat die Signatur gar nicht (`ICanOpenNode.cs:184-187`). Wer solche Dateien einliest, steht vor
derselben Wahl wie oben beim Record, nur eine Ebene tiefer: umsetzen, oder beim Laden melden, was
nicht umsetzbar ist.

Die Norm bleibt, was sie ist — der Inhibit-Time-Eintrag ist `Optional`, die Wertetabelle
verpflichtet niemanden. **Notwendig werden beide durch die Architektur, nicht durch CiA 301**, und
diese Unterscheidung gehört in die Anforderungen, damit später niemand eine Pflicht daraus liest,
die dort nicht steht.

### Die Posten

| | Was | Woher die Notwendigkeit kommt | Art der Arbeit |
|---|---|---|---|
| 1 | EDS → Objektverzeichnis **und** PDO-Konfiguration | Architekturentscheidung | neuer Pfad, Abhängigkeit auf EdsDcfNet |
| 2 | Degradieren mit Meldung, wenn eine EDS Nicht-Umsetzbares angibt — **einschließlich Korrektur des OD-Eintrags** | Architekturentscheidung | gehört zu 1, aber eigene Anforderung — sonst wird es zum stillen Ignorieren |
| 3 | Fallback: `1000h`, `1001h`, `1018h` (sub0 = `01h`, Vendor-ID = 0) | CiA 301 §7.5.2.21, Objektübersicht `ro M` | klein, additiv. Subs 02–04 weglassen, nicht nullen |
| 4 | Fallback: `1400h`/`1800h` statisch `ro` | Fußnote `*` der Objektübersicht: bei PDO-Support Pflicht, *„These may be ro"* | klein |
| 4b | Fallback: `1200h` | **korrigiert (16.09.):** die Objektdefinition sagt `Category Conditional` / *„Index 1200h: **Optional**"* — Pflicht sind erst `1201h`–`127Fh` je weiterem SDO-Server | klein, und *kann* entfallen. Wird er gebaut, sind sub0 und sub01 ohnehin `const` laut Objektdefinition — nicht über die PDO-Fußnote, die für SDO-Parameter kein `ro` erlaubt |
| 5 | Übertragungsart `02h`–`F0h` | **Architektur** (die Norm verpflichtet nicht) | zwei Hälften: Byte annehmen (API-Erweiterung **ohne Bruch**, EDS-Pfad nimmt das rohe Byte) **und** je TPDO SYNCs zählen — `HandleSync` sendet heute jedes synchrone TPDO bei **jedem** SYNC (`CanOpenNode.cs:868-880`) |
| 6a | Übertragungsart `FDh` (RTR-only, **event-driven**) | **Architektur** | Sampling bei Empfang des RTR, sofort senden. Verhaltenserweiterung an vorhandener Naht (`isRtr` erreicht den Dispatcher) |
| 6b | Übertragungsart `FCh` (RTR-only, **synchron**) | **Architektur** | **anderes Verhalten als 6a**: Sampling bei *jedem* SYNC, Wert puffern, den gepufferten Wert auf RTR senden — ein gemeinsamer „OD lesen und antworten"-Handler erfüllt 6a und verletzt 6b (Tabelle 72, Erläuterung) |
| 7 | Inhibit Time `1800h:03` | **Architektur** (Norm: `Entry category: Optional`) | **das größte Stück** — neues Scheduling im TPDO-Pfad. Normativ ist sie „the minimum interval for PDO transmission **if the transmission type is set to FEh and FFh**" — gilt also nicht für die synchronen Arten |
| 8 | Abort-Codes `0504 0003h` und `0609 0030h` | normativ; den ersten erkennt der Blocktransfer, den zweiten verlangt Posten 17 | trivial — beide fehlen im Enum |
| 9 | CRC-Test auf `31C3h`, Zitat auf §7.2.4.3.16 | Testvektor liefert die Norm | trivial |
| 10 | TIME `1012h`, Sync Window Length `1007h` | beide `Category: **Optional**` | nicht bauen, in den Ausnahmekatalog. `1007h` steht in derselben README-Zeile wie `1005h`/`1006h` — geprüft und bewusst draußen, damit es nicht als Übersehen wieder aufkommt |
| 11 | Übertragungsart `00h` (synchron-azyklisch) | **Architektur** | eigenes Verhalten: **beim nächsten SYNC senden, sofern vorher ein Ereignis auftrat** — ein Latch, gesetzt von Zustandsänderung *und* von `TriggerTpdoAsync`, verbraucht beim SYNC. Weder `EventDriven` noch `Synchronous` tut das |
| 12 | Synchrone **RPDO** (`1400h:02`) | **Architektur** | Empfangsseite: `ConfigureRpdo` hat kein Übertragungsart-Argument (`ICanOpenNode.cs:192`), `HandleRpdo` schreibt sofort ins OD statt bis SYNC zu halten (`CanOpenNode.cs:1697`) |
| 13 | EDS-Zugriffsrechte auf `1600h`/`1A00h` durchsetzen | **Architektur** | der Mapping-Pfad wird **vor** der generischen OD-Prüfung abgezweigt (`CanOpenNode.cs:1022`) und fragt `OdAccess` nie — geladene `ro`-Flags wären wirkungslos |
| 14 | Schreibbare Kommunikationsrecords zur Laufzeit | **offene Entscheidung des Maintainers** (siehe unten) | entweder Schreibzugriff bis zur Engine führen **oder** die Records beim Laden auf `ro` zwingen |
| 15 | Reset **Node und** Reset Communication stellen wieder her — **aus der EDS und aus dem codierten Fallback** | **Architektur** | beide Kommandos teilen sich heute denselben Zweig (`CanOpenNode.cs:848-857`), der nur den Zustand wechselt und Bootup sendet; `ResetNode` ist dabei der weitergehende — *„full application reset (implies reset communication)"* (`Nmt/NmtState.cs:39`). Sonst bliebe nach einem Remapping oder einem `ConfigureTpdo`-Aufruf die geänderte Laufzeitkonfiguration stehen — im Fallback-Fall ohne jede Quelle, aus der sie zurückzuholen wäre |
| 16 | Fallback: auch `1600h`/`1A00h` statisch `ro` | **Architektur** | Posten 4 deckt nur `1400h`/`1800h`/`1200h`, Posten 13 nur EDS-Flags — ohne diesen Posten bleibt gerade das Fallback dynamisch remappbar, obwohl die Entscheidung für es `ro` zusagt |
| 17 | SDO-Abort `0609 0030h` bei nicht unterstützter Übertragungsart | **normativ** | „An attempt to change the value of the transmission type to any not supported value shall be responded with the SDO abort transfer service (abort code: 0609 0030h)" — die Kehrseite des Degradierens: was der Stack nicht kann, lehnt er beim Schreiben ab. **Gilt nur für beschreibbare Einträge** — ist der Record `ro` (Posten 4, oder Posten 14 je nach Entscheidung), scheitert der Download vorher an der Zugriffsprüfung mit `0601 0002h` (`AttemptWriteReadOnly`, vorhanden). **`0609 0030h` fehlt im Enum** (`Sdo/SdoAbortCode.cs` führt von den `0609h`-Codes nur `0609 0011h`), gehört also zu Posten 8 |
| 18 | `1800h:04` nicht implementieren, Zugriff mit `0609 0011h` abweisen | **normativ** | „Sub-index 04h is reserved. It shall not be implemented; in this case read or write access leads to the SDO abort transfer service (abort code: 0609 0011h)" — betrifft Fallback-Record und EDS-Befüllung gleichermaßen. Der Code **ist** vorhanden (`SubIndexDoesNotExist`), nur das Verhalten fehlt |
| 19 | Fallback: `1005h`, `1006h`, `1014h` | **normativ** — dieselbe Fußnotenlogik wie Posten 4 | `1005h` *„Mandatory, if PDO communication on a synchronous base is supported"*, `1006h` *„Mandatory for SYNC producers"*, `1014h` *„Mandatory, if Emergency is supported"*. Der Stack kann alle drei (`StartSyncProducer`, `SendEmcyAsync`, synchrone TPDOs) — ohne die Records hätte auch der Fallback Verhalten, das sein OD nicht beschreibt. **Achtung:** alle drei sind laut Objektdefinition `rw` (`1005h` *„rw; const, if the COB-ID is not changeable"*, `1006h` `rw`, `1014h` `rw`), und die `ro`-Fußnote gilt nur für PDO-Records. **Die `ro`-Variante aus Posten 14 steht für sie deshalb nicht offen** — sie wäre die Abweichung, die zwei Absätze weiter oben ausgeschlossen wird. Ihre Schreibzugriffe müssen den zugehörigen Dienst erreichen; einzige Ausnahme ist `1005h`, das die Norm als `const` zulässt, *„if the COB-ID is not changeable"* |
| 20 | `1006h` und der SYNC-Produzent bleiben im Gleichschritt | **Architektur** | Gegenrichtung zu Posten 14: `StartSyncProducer` setzt nur `_syncProducerInterval` und plant den Tick (`CanOpenNode.cs:320-330`), `StopSyncProducer` ebenso (`:333-341`) — das OD erfährt nichts. `1006h` bliebe auf seinem Anfangswert, und **`0000 0000h` heißt normativ „transmission of SYNC messages shall be disabled"**, während der Knoten sendet. Entweder Start/Stop schreiben `1006h` mit, oder der Zeitplan wird aus `1006h` abgeleitet |
| 21 | Heartbeat `1016h`/`1017h` — **in beiden Pfaden**, nicht nur im Fallback | **Architektur** — *nicht* normativ, siehe rechts | `1016h` ist `Category: Optional`; `1017h` ist `Conditional; **Mandatory, if guarding not supported**`, und dieser Knoten *unterstützt* Guarding (er beantwortet Guarding-RTRs, README § Life guarding) — die Klausel greift also nicht. Gebaut wird es trotzdem, weil `StartHeartbeatProducer` (`CanOpenNode.cs:265-275`) und `AddHeartbeatConsumer` (`:290`) nur internen Zustand ändern. Eine EDS mit `1016h`-Konsumenten oder `1017h` ≠ 0 muss die Engine erreichen — Posten 1 befüllt nur die PDO-Konfiguration, und Heartbeat ist keine |
| 22 | `1001h` und der EMCY-Fehlerregister bleiben im Gleichschritt | **gemessene Invariante des Pakets** | Gegenrichtung wie Posten 20: `SendEmcyAsync` (`CanOpenNode.cs:352-358`) baut die Nachricht und sendet, ohne das OD zu berühren — während `Emcy/EmcyMessage.cs:13` Byte 2 ausdrücklich als *„mirror of OD 0x1001"* dokumentiert. Ein SDO-Read läse den Anfangswert, während die EMCY einen anderen Fehlerzustand meldet. Betrifft Fallback **und** EDS-Pfad |
| 23 | **Die Regel hinter 20, 21 und 22:** für jedes Kommunikationsobjekt, das der Knoten anbietet, stimmen OD und Laufzeit in **beiden Richtungen** und in **beiden Pfaden** überein | **Architektur** | Das Kriterium ist **die tatsächlich erfolgte Änderung**, nicht der versuchte Zugriff. OD → Laufzeit: *jede* Änderung, die das OD übernimmt, erreicht den Dienst — also der angenommene SDO-Download **und** der direkte Anwendungszugriff über das öffentliche `ObjectDictionary` (`ICanOpenNode.cs:33`), dessen `WriteRaw` nur die Existenz prüft und `OdAccess` nie (`ObjectDictionary.cs:131-142`); `EntryWritten` (`:141`) ist dafür eine **halbe** Naht: sie feuert bei `WriteRaw`/`WriteUnsigned`, aber ausdrücklich **nicht** bei den `Add*`-Methoden (`:33`) — und `Add` ersetzt einen bestehenden Eintrag per Indexer-Zuweisung stillschweigend (`:210-218`). Ein `AddU32(0x1006, …)` ändert das OD also, ohne dass irgendetwas davon erfährt. Entweder wird die Naht auf das Ersetzen erweitert, oder das Ersetzen verwalteter Kommunikationsrecords wird unterbunden; die Zeile verlangt das Ergebnis, nicht den Weg. Laufzeit → OD: jede API, die den Dienst ändert, schreibt den Wert zurück. Ein an der Zugriffsprüfung mit `0601 0002h` abgewiesener Download (Posten 17) fällt nicht darunter — er ändert nichts, und die Ablehnung ist das richtige Verhalten, sonst machte diese Zeile die Records aus den Posten 3, 4 und 16 beschreibbar. Bisher bekannte Instanzen: `1006h`/SYNC-Produzent (20), `1016h`/`1017h`/Heartbeat (21), `1001h`/EMCY (22), PDO-Records (13/14/16). Die Liste ist hier dreimal in Folge je Dienst nachgezogen worden — sie steht als Regel, damit der nächste Dienst nicht als vierter Befund auffällt |

**Posten 15 ist keine Altlast, sondern eine Folge der Entscheidung selbst** (Codex auf #129).
Die README des Pakets begründet heute, warum Reset Communication nichts wiederherstellt
(`src/CanKit.Pro.CANopen/README.md:56-57`): *„communication parameters are not re-initialized from
the OD, because they do not live in the OD"*. Diese Begründung trägt, solange es keine Quelle gibt,
aus der sie sich wiederherstellen ließen. Mit der EDS gibt es sie — und damit wird aus einer
dokumentierten Einschränkung eine offene Anforderung: nach einem Remapping durch den Master und
einem anschließenden Reset Communication bliebe sonst die geänderte Laufzeitkonfiguration stehen,
obwohl der Zuschnitt den EDS-Pfad als vollständig führt.

Die README selbst bleibt unverändert richtig, weil sie das heutige Verhalten beschreibt und der
EDS-Pfad noch nicht existiert. Sie wird mit dessen Umsetzung nachzuführen sein, nicht vorher.

### Vier Prüffragen je Posten

Die Liste oben ist im Review von #129 auf ein Vielfaches ihres ersten Entwurfs gewachsen, und
**jeder Zuwachs kam aus demselben kleinen Satz von Fehlern.** Sie stehen hier, weil der nächste
Schritt die Liste in Anforderungen übersetzt — und weil J1939, UDS und ISO-TP dieselbe Übung noch
vor sich haben. Wer einen Posten ergänzt, beantwortet sie:

1. **Beschreibt die Zeile Verhalten oder nur eine API?** Dreimal stand hier eine Signatur, wo die
   eigentliche Arbeit in der Engine liegt: `02h`–`F0h` braucht einen SYNC-Zähler, nicht nur ein
   Byte; die synchrone RPDO braucht die Empfangsseite, die `TpdoTransmission` gar nicht kennt;
   `FCh` braucht einen gepufferten Wert, den ein Antwort-Handler nie festhält. Wo eine Zeile eine
   API nennt, lautet die offene Frage: was muss die **Engine** zusätzlich tun?
2. **Gilt der Posten für beide Pfade — EDS *und* Fallback?** Dreimal deckte ein Posten nur einen:
   `ro`-Flags nur aus der EDS, Reset nur aus der EDS, Mappingrecords nur außerhalb des Fallbacks.
   Der Fallback ist der jüngere Pfad und fällt deshalb zuerst durch.
3. **Gilt er in beide Richtungen — OD → Laufzeit *und* Laufzeit → OD?** Die Liste fragte lange nur,
   ob ein Schreibzugriff die Engine erreicht. `StartSyncProducer` ändert die Laufzeit, ohne dass das
   OD davon erfährt — dieselbe Inkonsistenz, nur andersherum.
4. **Deckt der Beleg die Zusage, oder nur einen Teil davon?** Die `ro`-Fußnote gilt für
   PDO-Records; daraus wurde hier kurzzeitig ein `ro` für alle Fallback-Records, samt eines
   Widerspruchs zur eigenen Zeile zwanzig Zeilen weiter oben. Die Herkunftsspalte ist genau dafür
   da: Was sie behauptet, muss die genannte Stelle auch hergeben.

Keine der vier ist ein CANopen-Thema. Sie fragen, ob eine Anforderung das Ganze beschreibt oder
nur den Ausschnitt, den man beim Schreiben vor Augen hatte.

### Was offen bleibt

**Ein Master, der zur Laufzeit auf `1800h:02` oder `1400h:*` schreibt** — Posten 14 der Tabelle.
Das ist unabhängig davon, woher der Record beim Start kam: der generische SDO-Server übernimmt den
Wert ins OD, und kein Pfad trägt ihn weiter in die PDO-Engine. Die EDS-Entscheidung berührt das
nicht, weil sie nur die Befüllung regelt.

Es steht deshalb **als Posten in der Tabelle und nicht nur hier** (Codex auf #129): der nächste
Schritt schreibt die Tabelle in die SRS, und was dort nicht steht, deckt die Ratsche nicht ab —
ein Punkt unter „Was offen bleibt" wäre genau die Art Notiz, die beim Übersetzen verlorengeht.
Die Wahl selbst bleibt die des Maintainers: **Schreibzugriff durchreichen** (mehr Arbeit, aber ein
Gerät, das `rw` ehrlich anbietet) **oder beim Laden auf `ro` zwingen** (billig, und der Master
erfährt die Wahrheit sofort). Was nicht geht, ist die dritte Variante von heute: `rw` anbieten und
den Schreibzugriff wirkungslos quittieren.

Was CiA 301 **nicht** entscheidet und offen bleibt: bit-granulares PDO-Mapping und die
CiA-302/304/305-Themen aus der GAP-Analyse. Die stehen in anderen Dokumenten.
