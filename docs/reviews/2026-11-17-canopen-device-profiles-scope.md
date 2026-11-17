# CANopen: Zuschnitt der Geräteprofile (CiA 401, CiA 408) und der Anwendungsnaht

Stand: 17.11.2026, `main` @ `09d4d315`. Vierte Zuschnitt-Runde — CANopen, Geräteprofile oberhalb
des Kommunikationsprofils. Die Geräterolle, die Master-/Tool-Rolle und CANopen Safety sind
entschieden und ausgeliefert (`docs/reviews/2026-09-15-canopen-scope.md`,
`docs/reviews/2026-09-26-canopen-master-tool-scope.md`,
`docs/reviews/2026-10-09-canopen-safety-scope.md`).

Am 17.11. hat der Maintainer vier Punkte entschieden. Sie stehen unten als Entscheidungen. Das
Design danach ist die Form, in der sie gebaut werden. Was als „Festlegung des Autors" markiert
ist, hat der Maintainer nicht entschieden, sondern nur noch nicht abgelehnt. Was weder das eine
noch das andere ist, steht am Ende als Entscheidungsfrage.

Diese Datei schreibt keine `FR-CO`-Zeile. Die Zeilen entstehen, wenn das Verhalten gebaut ist,
das sie prüfen.

## Woher die Belege kommen

| Stufe | Was so belegt ist |
|---|---|
| **gemessen** | Öffentliche API und Quelltext auf `main` @ `09d4d315` |
| **Norm** | Volltext aus der Sammlung des Maintainers, mit Abschnitt zitiert: CiA 301 v4.2.0, CiA 401-1 v3.1.0, CiA 408 v2.2.0 |
| **Entscheidung** | Die vier Punkte des Maintainers vom 17.11. |

Die Normtexte liegen nicht im Repository und kommen nicht hinein. Zitiert wird Abschnitt und
Ausgabe.

## Zwei Dinge, die auseinandergehören

**CiA 302 ist kein Geräteprofil.** Es erweitert die Kommunikationsschicht um Objekte im Bereich
`1Fxxh` und gilt quer zu jedem Profil. Ein Teil davon ist gebaut: Flying Master und
Boot-up-Manager nach CiA 302-2 v4.1.0 (FR-CO-031, FR-CO-032, ADR-13).

**CiA 401 und CiA 408 sind Geräteprofile.** Ein Geräteprofil beschreibt die Objekte *eines
logischen Geräts* (CiA 301 §4.5.1, „A device profile is a description of the objects of the object
dictionary of one logical device"). Welches Profil ein Gerät spricht, steht in den unteren 16 Bit
von `1000h`; die oberen 16 Bit sind profilspezifische Zusatzinformation (CiA 301 §7.5.2.1).

**Ein CANopen-Gerät kann mehrere logische Geräte enthalten**, eins bis acht (CiA 301 §4.1):

| Was | Regel | Quelle |
|---|---|---|
| Objektbereich | `6000h`–`67FFh` für das erste, je `800h` weiter für jedes folgende, bis `9800h`–`9FFFh` | CiA 301 §7.4.1, Tabelle 41 |
| PDO-Nummer | Profil-PDO-Nummer + 64 je logischem Gerät (2. Gerät +64, 8. Gerät +448) | CiA 301 §7.2.2.1 |
| Gerätetyp | `1000h` nennt das Profil des ersten; Zusatzinformation `FFFFh` heißt „es gibt weitere"; deren Typ steht in `67FFh + x·800h` | CiA 301 §7.5.2.1 |

Daraus folgt die Form: ein Profil ist nichts, was ein Knoten *ist*, sondern etwas, das an einen
Knoten *angehängt* wird, mit einer Nummer 0..7 für das logische Gerät.

## Was schon da ist (gemessen)

- `ObjectDictionary` ist öffentlich und über `ICanOpenNode.ObjectDictionary` erreichbar. Eine
  Anwendung kann Objekte im Profilbereich anlegen, lesen und schreiben.
- Was ein Profil für *Verhalten* braucht, ist intern:
  - `ObjectDictionary.WriteValidator` (`ObjectDictionary.cs`) — Schreibzugriff ablehnen mit
    SDO-Abort-Code oder als Kommando annehmen (`OdWriteDecision`). Das Verzeichnis hat genau einen
    Platz dafür, der Knoten belegt ihn (`CanOpenNode.CommunicationProfile.cs`,
    `ValidateCommunicationWrite`); für alles außerhalb der verwalteten Kommunikationsobjekte gibt
    er `Accept` zurück. Er läuft auf dem schreibenden Thread unter der Schreibsperre.
  - `ObjectDictionary.EntryWritten` — Benachrichtigung nach dem Speichern, ebenfalls auf dem
    schreibenden Thread, in den meisten Pfaden unter der Schreibsperre.
  - `IDeadlineScheduler` des Knotens — Fristen auf der Zeitquelle und der Aktor-Schleife des
    Knotens.
  - `ApplyNmtTransition` — der Knoten wechselt seinen eigenen NMT-Zustand.
- Ein Schreibzugriff der Anwendung auf ein Objekt, das in ein ereignisgesteuertes TPDO gemappt
  ist, löst das TPDO aus (`OnOdEntryWrittenForCoS`, `CanOpenNode.Pdo.cs`). Zwei Ausnahmen: ein
  Schreibzugriff auf der Aktor-Schleife löst nie aus (so werden Bus-Echos verhindert), und
  `EnableChangeOfStateTpdo = false` schaltet das Auslösen für den ganzen Knoten ab. Der Pfad
  postet bewusst nicht je Schreibzugriff auf den Aktor, weil dessen Postfach unbegrenzt ist; er
  sammelt in einer begrenzten Menge.
- `1000h` ist kein verwaltetes Objekt (`IsManagedCommunicationObject` nennt es nicht). Es trägt
  einen Platzhalter, den die Anwendung ersetzt (`ICanOpenNode.ObjectDictionary`, Kommentar).
- Ein NMT-Reset stellt die verwalteten Kommunikationsobjekte wieder her und die Objekte, die eine
  Gerätebeschreibung deklariert hat (`IsRestorableObject`, `_describedObjects`). Objekte, die die
  Anwendung ohne Beschreibung angelegt hat, überstehen den Reset unverändert und werden von
  `1010h` nicht gespeichert.
- `PdoCount` (Vorgabe 4) und `EnableErrorBehavior` (`1029h`, nur Sub-Index `01h`) stehen beim
  Öffnen des Knotens fest. PDOs oberhalb von 4 haben keine Default-COB-ID.
- Öffentlich vorhanden und für Profile nutzbar: `State`, `SendEmcyAsync`, `ConfigureTpdo` /
  `ConfigureRpdo`, `TriggerTpdoAsync`, `RpdoReceived`, `NmtCommandReceived`, `ApplicationReset`;
  das Fehlerverhalten `1029h` (`CanOpenNode.ErrorBehavior.cs`).
- Tool-Seite: `SdoUploadAsync` / `SdoDownloadAsync` hinter dem Peer-SDO-Tor (FR-CO-029),
  `ObserveForeignPdoAsync` (FR-CO-030), `CanOpenDiscovery.ScanAsync` liest `1000h:00`
  (FR-CO-034). Niemand zerlegt den gelesenen Gerätetyp.
- CiA 401 und CiA 408 kommen im Quelltext nicht vor.

## Entscheidungen

### 1. CiA 302 bleibt im Kernpaket

Weitere Teile von CiA 302 werden nach dem Muster von ADR-13 gebaut: im Paket
`CanKit.Pro.CANopen`, als partielle Klassen von `CanOpenNode` oder eigene Typen auf demselben
`ICanBusService`. Diese Runde schneidet sie nicht zu; siehe „Außerhalb dieser Runde".

### 2. CiA 401 und CiA 408 auf beiden Seiten

Tool-Seite: jedes Gerät lesen, steuern und seine PDOs deuten. Geräteseite: der Knoten kann selbst
ein 401-I/O-Modul oder ein 408-Gerät sein, etwa für Simulation.

### 3. ADR-12 wird überarbeitet

ADR-12 hat eine öffentliche Erweiterungs-Schnittstelle abgelehnt („friert eine Plugin-API ein, die
sonst niemand nutzt"). Für Geräteprofile gilt das nicht mehr.

### 4. Weg B: schmale Naht im Kern, ein Paket je Profil

Von drei Wegen ist B gewählt:

| Weg | Form | Warum (nicht) |
|---|---|---|
| A | Profile im Kernpaket, Naht bleibt intern (Muster `SrdoEngine` / `ISrdoEngineHost`) | Jedes weitere Profil vergrößert das ohnehin größte Paket; wer ein herstellerspezifisches Gerät baut, kann weiter kein Verhalten einhängen. |
| **B** | Naht im Kern, `CanKit.Pro.CANopen.Cia401`, `CanKit.Pro.CANopen.Cia408` | Ein neues Profil ist ein neues Paket, der Kern bleibt geschlossen. Die Naht ist dieselbe, die jede eigene Geräteanwendung braucht. Preis: sie ist ab Veröffentlichung eingefroren. |
| C | wie B, ein Sammelpaket `CanKit.Pro.CANopen.Profiles` | Alle Profile teilen einen Release-Takt; späteres Aufteilen ist ein Breaking Change. |

## Ziele, Nicht-Ziele, Anti-Ziele

**Ziele**

- Ein Knoten bekommt null bis acht Profil-Instanzen angehängt, jede auf ihrem logischen Gerät.
- Ein Werkzeug liest `1000h` (und bei `FFFFh` die `67FFh`-Kette) und bekommt je logischem Gerät
  einen typisierten Zugriff, wenn ein Profil dafür registriert ist.
- Ein weiteres Profil (402, 406, …) braucht keine Änderung am Kernpaket.
- Kein Major-Release: `ICanOpenNode` wird nicht verbreitert.

**Nicht-Ziele**

- Kein mitgeliefertes Streckenmodell. Die Simulation ist protokolltreu (Objekte,
  Zustandsautomat, PDOs, EMCY); woher die Prozesswerte kommen, bestimmt ein Modell, das die
  Anwendung einhängt (Entscheidungsfrage 3).
- Keine weiteren Profile in dieser Runde.
- CiA 401-2 (Joysticks) ist nicht dabei.
- Clause 7/8 von CiA 304 (Safety Application Layer der Profile) bleibt ungebaut (ADR-12).

**Anti-Ziele** — damit wäre es gescheitert, auch wenn es läuft:

- Indizes eines Profils stehen an zwei Stellen (Geräteseite und Tool-Seite getrennt gepflegt).
- Ein Profilpaket greift auf `CanOpenNode` zu statt auf die Naht.
- Die Naht wird veröffentlicht, bevor zwei Profile sie benutzt haben.
- Das Tool umgeht mit Profilwissen das Peer-SDO-Tor (Entscheidungsfrage 2: die Datei bleibt
  Pflicht).

## Design

### Die Anwendungsnaht (Kernpaket)

Namensraum `CanKit.Pro.CANopen.Application`. Erreicht wie Safety über eine Erweiterungsmethode
(`node.Application()`), nicht über `ICanOpenNode`.

Was die Naht dem Profil gibt (`ICanOpenApplicationHost`):

| Fähigkeit | Wozu, mit Normbeleg | Heute |
|---|---|---|
| Objektverzeichnis, NMT-Zustand | Grundlage | öffentlich |
| Profil anhängen / abhängen (`Attach` → `IDisposable`) | Lebensdauer | neu |
| Frist auf der Zeitquelle des Knotens | Filter, Zeitüberwachung; ein virtueller Takt in Tests (CLAUDE.md § „Before claiming something is true", Uhr-Regel) | intern |
| Bestimmen, ob eine Änderung sendet | Interrupt-Masken `6005h`–`6008h`, `6421h`–`6428h` (CiA 401-1 §8.2, §8.6). Das Profil braucht beides: von der Anwendung gesetzte Werte, die *nicht* senden, und Werte, die es selbst auf der Aktor-Schleife setzt und die senden | Automatik nur für Schreibzugriffe außerhalb des Aktors, nur knotenweit abschaltbar; `TriggerTpdoAsync` öffentlich |
| Fehler melden und zurücknehmen (EMCY-Code, Bit im Fehlerregister) | CiA 401-1 §5, CiA 408 §6 | `SendEmcyAsync` öffentlich; Kopplung an `1001h`/`1003h` prüfen |
| Eigenen NMT-Zustand anfordern | CiA 401-1 §5.2 (Geräteausfall → Pre-operational, über `1029h` umstellbar); CiA 408 §7.8 Tabelle 10 (Fault Reaction) | intern |
| Gerätetyp setzen (`1000h`, `67FFh + x·800h`) | CiA 301 §7.5.2.1 | möglich: `1000h` ist ein Platzhalter, den die Anwendung ersetzt. Neu ist nur die Regel für mehrere Profile (`FFFFh`, `67FFh`-Kette) |
| Objekte als wiederherstellbar anmelden | Reset Node stellt Profilparameter auf Einschaltwerte; `1010h`/`1011h` speichern und laden sie | nur für Objekte aus einer Gerätebeschreibung |

Was das Profil der Naht gibt (`ICanOpenApplication`):

| Rückruf | Läuft auf | Wozu |
|---|---|---|
| Beanspruchte Indexbereiche | — | Zuständigkeit; zwei Profile mit überlappendem Anspruch werden beim Anhängen abgelehnt |
| Schreibzugriff prüfen → speichern / ablehnen (Abort-Code) / als Kommando annehmen | schreibender Thread, wie `WriteValidator` heute | Wertebereich, „nicht in diesem Zustand" |
| Eintrag wurde geschrieben | Aktor-Schleife, zusammengefasst | Ausgang nachführen, Steuerwort auswerten |
| Reset (Anwendung / Kommunikation) | Aktor-Schleife | Zustandsautomat und flüchtige Zustände zurücksetzen; die Werte stellt der Knoten wieder her |
| NMT-Zustand hat gewechselt (von, nach) | Aktor-Schleife | CiA 401-1 §5.3 und §6.1 (beim Eintritt in Operational); CiA 408 Tabelle 10 |
| Kommunikationsfehler begann / endete | Aktor-Schleife | Fehlermodus der Ausgänge (`6206h`/`6207h`, `6443h`/`6444h`) |

Regeln der Naht:

- Ein Anspruch liegt außerhalb `1000h`–`1FFFh`. Der Kommunikationsbereich bleibt beim Knoten;
  Vorbelegungen dort (Default-PDOs, Gerätetyp) gehen über benannte Aufrufe der Naht, nicht über
  einen Anspruch.
- Der Knoten fragt für einen Index genau das Profil, das ihn beansprucht. `ValidateCommunicationWrite`
  bekommt dafür einen Zweig vor dem abschließenden `Accept`; die verwalteten Objekte gehen vor.
- Prüfen läuft wie heute auf dem schreibenden Thread unter der Schreibsperre und darf deshalb
  nichts tun außer lesen und entscheiden. Alle anderen Rückrufe laufen auf der Aktor-Schleife.
  Das ist für „Eintrag wurde geschrieben" neu: `EntryWritten` feuert heute auf dem schreibenden
  Thread. Der Weg dorthin folgt dem Muster von `OnOdEntryWrittenForCoS` — begrenzte Menge
  geänderter Einträge, höchstens eine Auswertung in der Warteschlange —, weil je Schreibzugriff
  zu posten das Postfach unbegrenzt wachsen lässt. Folge: das Profil erfährt, *dass* ein Eintrag
  sich geändert hat, nicht jede Zwischenstufe. Eine Ausnahme im Rückruf geht nach
  `BackgroundExceptionOccurred`.
- **Festlegung des Autors — Anhängen und die Optionen des Knotens.** Was ein Profil vom Knoten
  braucht, steht beim Öffnen fest (`PdoCount`, `EnableErrorBehavior`). Das Profil liefert deshalb
  eine Hilfe, die `CanOpenNodeOptions` um seinen Bedarf ergänzt, und `Attach` lehnt mit einer
  Meldung ab, die den fehlenden Punkt nennt, statt still weniger zu tun. Angehängt wird, bevor der
  Knoten die Initialisierung verlässt; später ist es ein Fehler.
- **Festlegung des Autors — Profil an einem Knoten aus EDS/DCF.** Die Datei gewinnt. Ein Profil
  hat zwei Wege, zu seinen Objekten zu kommen: aus einer `Configuration` (es legt sie an) oder aus
  dem vorhandenen Verzeichnis (es übernimmt, was die Datei deklariert hat, und leitet daraus ab,
  welche Funktionsblöcke es bedient). Ein Objekt des Katalogs, das die Datei mit anderem Typ
  deklariert, ist ein Fehler beim Anhängen. So bleibt eine echte EDS — die sagt, welche optionalen
  Objekte *dieses* Gerät hat — die Quelle für die Simulation genau dieses Geräts.
- **Festlegung des Autors — Reset und Speichern.** Gehört dazu. Objekte, die ein Profil anlegt,
  werden wie beschriebene Objekte behandelt: Reset Node stellt sie wieder her, `1010h`/`1011h`
  erfassen sie. Ohne das wäre das simulierte Gerät nach einem Reset nicht im Einschaltzustand.
- **Festlegung des Autors — Reifezeit.** Die Naht bleibt `internal`, bis 401 und 408 gegen sie gebaut sind.
  Die Profilprojekte sehen sie so lange über `InternalsVisibleTo` (das Kernprojekt nutzt das schon
  für die Tests) und sind `IsPackable=false`. Der letzte Schritt macht die Naht öffentlich,
  entfernt `InternalsVisibleTo` und gibt die Pakete frei. Vorher steht sie in keiner
  API-Approval-Datei, kann sich also noch ändern.

### Gerätetyp und logische Geräte (Kernpaket, öffentlich)

Von Anfang an öffentlich, weil klein, profilunabhängig und ohne die Naht nutzbar:

- `CanOpenDeviceType` — zerlegt das 32-Bit-Wort: Profilnummer, Zusatzinformation, „weitere
  logische Geräte folgen".
- Lesen der logischen Geräte eines Peers: `1000h`, bei `FFFFh` weiter über `67FFh + x·800h`, bis
  die Kette endet. Ergebnis: Liste aus (Nummer 0..7, Gerätetyp). Geht über `SdoUploadAsync`, also
  durch das Peer-SDO-Tor.

Erst mit der Freigabe der Naht öffentlich, aus demselben Grund wie die Naht (**Festlegung des
Autors**): ihre Signatur soll zwei Profile gesehen haben, bevor SemVer sie einfriert.

- `ICanOpenProfile` — Beschreiber eines Profils: Profilnummer, Name, „erzeuge den Peer-Zugriff
  für (Knoten, Peer-Node-ID, logisches Gerät, Gerätetyp)".
- Ein Katalog, den die Anwendung aus Beschreibern zusammenstellt, und der zu einem logischen Gerät
  den Peer-Zugriff liefert oder „unbekannt". Kein globales Register, keine Assembly-Suche: wer ein
  Profil will, übergibt dessen Beschreiber.

Unbekanntes Profil ist kein Fehler: das Werkzeug arbeitet dann wie heute mit der EDS/DCF des
Peers.

### Aufbau eines Profilpakets

Für 401 gezeigt; 408 folgt derselben Teilung.

| Teil | Aufgabe | Hängt ab von |
|---|---|---|
| `Cia401Objects` | Katalog: Index (relativ zum logischen Gerät), Sub-Indizes, Datentyp, Zugriff, PDO-Mapping-Fähigkeit, Default. Die einzige Stelle, an der Indizes stehen. | nichts |
| `Cia401Configuration` | Was dieses Gerät hat: Kanalzahlen, Breiten. Daraus folgen Zusatzinformation in `1000h` und Default-PDOs. | Katalog |
| `Cia401Device` | Geräteseite. Legt die Objekte an, setzt Gerätetyp und Default-PDOs, implementiert `ICanOpenApplication`. Der Anwendung zugewandt: Eingang setzen, Ausgang lesen, „Ausgang hat sich geändert". | Naht |
| `Cia401Peer` | Tool-Seite. Typisierter Zugriff auf einen fremden Knoten: Lesen und Schreiben per SDO, Deuten beobachteter PDOs über den Katalog. | `ICanOpenNode` |
| `Cia401Profile` | Der Beschreiber (`ICanOpenProfile`). | Katalog, Peer |

Zuordnung zu SOLID, damit sie prüfbar ist:

- **Eine Verantwortung:** Katalog kennt Objekte, Device kennt Verhalten, Peer kennt Fernzugriff.
  Keiner kennt den Bus.
- **Offen/geschlossen:** ein neues Profil ist ein neues Paket und ein neuer Beschreiber.
- **Ersetzbarkeit:** jedes `ICanOpenApplication` ist an jeden Knoten anhängbar; Tests hängen eine
  aufzeichnende Attrappe an (wie `ISrdoEngineHost`).
- **Getrennte Schnittstellen:** ein 401-Gerät hat eine Teilmenge der vier Funktionsblöcke
  (Digital-Ein, Digital-Aus, Analog-Ein, Analog-Aus; CiA 401-1 §7.2). Jeder Block ist eine eigene
  Schnittstelle; `Cia401Peer` bietet nur die an, die der Gerätetyp ausweist (§6.2.1). Bei 408:
  Gerätesteuerung getrennt von den Regelungsarten, die `1000h` Bit 16 ff. ausweist (§8.6.2).
- **Abhängigkeit nach innen:** Profilpakete hängen an `ICanOpenApplicationHost` und
  `ICanOpenNode`, nie an `CanOpenNode`.

**PDOs eines Peers** deutet `Cia401Peer` nach dem Mapping, das der Peer wirklich hat, nicht nach
dem Default des Profils: über `ObserveForeignPdoAsync`, live vor Datei, wie in der
Master-/Tool-Runde entschieden (Entscheidung 2 dort). Der Katalog benennt nur, was dabei
herauskommt — aus „`6000h:01` = 5Ah" wird „Digital-Eingänge 1–8". Ein umgemapptes Gerät wird so
richtig gelesen.

Das logische Gerät ist ein Konstruktor-Parameter (0..7). Es verschiebt Indizes um `n·800h` und
PDO-Nummern um `n·64` — an genau einer Stelle, einer gemeinsamen Hilfe im Kernpaket.

### CiA 401-1 v3.1.0 — Zuschnitt

- **Katalog:** alle Objekte aus §8 (Digital-Ein `6000h`–`6128h`, Digital-Aus `6200h`–`6328h`,
  Analog-Ein `6400h`–`6404h`, Analog-Aus `6410h`–`6414h`, Einstellungen `6420h`–`6450h`,
  `67FFh`). Die Tool-Seite kann damit alles lesen, was die Norm nennt.
- **Geräteverhalten, erste Scheibe** (**Festlegung des Autors**): die Default-Form der Norm —
  digitale E/A byteweise (`6000h`, `6200h` mit Polarität, Fehlermodus, Fehlerwert, Masken),
  analoge E/A 16 Bit (`6401h`, `6411h` mit Interrupt-Auslösung und Fehlermodus), Default-PDOs
  nach §6.2.5–§6.2.12, Fehlerverhalten §5.2, EMCY `0080h` nach §5.3. Bit-, 16-, 32-Bit-, Float-
  und herstellerspezifische Zugriffe sind im Katalog, ihr Geräteverhalten folgt als eigene
  Scheibe.
- Analoge Default-TPDOs sind ab Werk gesperrt (CiA 401-1 §1: alle TPDOs sind
  änderungsgesteuert, „Therefore, default TPDOs containing analog inputs are disabled").

### CiA 408 — Zuschnitt

- **Bindend ist CiA 408 v2.2.0** (Entscheidungsfrage 1). CiA 408-B / 408-C v1.0.0 sind
  Gegenprobe; eine Abweichung wird ein Issue, kein zweiter Zweig im Code.
- **Streckenmodell** (Entscheidungsfrage 3): `Cia408Device` nimmt optional ein Modell entgegen,
  das die Anwendung liefert. Die Schnittstelle gehört dem Profilpaket, nicht der Naht — sie ist
  fachlich (Sollwert, Zustand des Automaten, vergangene Zeit hinein; Istwerte heraus), und ein
  anderes Profil hätte eine andere. Aufgerufen wird sie auf der Aktor-Schleife im Takt einer
  Frist des Knotens, also auf dessen Zeitquelle; ein Test treibt sie mit `ManualTimeSource`. Ohne
  Modell setzt die Anwendung die Istwerte selbst. Die Bibliothek bringt kein Modell mit.
  `Cia401Device` bekommt dieselbe Möglichkeit in seiner Form (Ausgänge hinein, Eingänge heraus).
- **Geräteverhalten:** der Zustandsautomat (CiA 408 v2.2.0 §7.6, §7.7), seine Kopplung an NMT
  (§7.8, Tabelle 10), die Gerätesteuerung (§9.3), der Gerätetyp mit Fähigkeits-Bits (§8.6.2), die
  zusätzlichen Fehlercodes (§6.3).
- **Katalog:** der Umfang der Norm ist groß (mehrere hundert Objekttabellen). Wie der Katalog
  entsteht und woran er geprüft wird, bleibt dem Plan überlassen; siehe „Dem Bauenden überlassen".

### Paketierung

Jedes neue Paket braucht dasselbe wie die bestehenden: Projekt unter `src/`, Eintrag in
`CanKit.Pro.sln`, Beschreibung und Tags in der `.csproj`, eine API-Approval-Datei
(`tests/CanKit.Pro.Tests/ApiApprovals/`, aus `*.received.txt`), eine Seite unter `docs/packages/`
samt Zeile in `index.md` und `mkdocs.yml`, und es muss `eng/verify-packages.py` bestehen. Solange
`IsPackable=false` gilt, entfallen Approval, Paketseite und Paketprüfung.

### Architekturakte

- **ADR-12** bleibt für Safety gültig (Safety bleibt im Kernpaket). Sein Argument gegen eine
  öffentliche Schnittstelle bekommt den Nachtrag, dass es für Geräteprofile durch ADR-14 abgelöst
  ist.
- **ADR-14 (neu):** Anwendungsnaht im Kern, Geräteprofile als eigene Pakete. Status „entschieden",
  „umgesetzt" erst mit der Freigabe der Naht.
- Beide in `arc42-CanKit.Pro.md` und `-en.md`.

**Festlegung des Autors:** Nachtrag plus neue ADR statt Umschreiben von ADR-12 — die Akte bleibt
lesbar als das, was am 09.10. entschieden wurde.

### Tests

Muster des Pakets: virtueller Loopback, `ManualTimeSource` für alles Zeitliche, Attrappe für die
Naht. Jede Behauptung „dieser Test fängt X" braucht die Mutation (CLAUDE.md). Je Profil ein
Ende-zu-Ende-Test: `Cia401Device` an Knoten A, `Cia401Peer` an Knoten B, beide über den Katalog —
ein Widerspruch zwischen den beiden Seiten kann so nicht unbemerkt bleiben.

## Reihenfolge

Ein Pull Request zur Zeit (CLAUDE.md). Jeder Schritt ist für sich mergebar.

1. `docs`: diese Akte, ADR-12-Nachtrag, ADR-14.
2. `feat(canopen)`: `CanOpenDeviceType` und logische Geräte lesen — öffentlich. Kleine, sofort
   nützliche Tool-Funktion.
3. `feat(canopen)`: die Naht, `ICanOpenProfile` und der Katalog, intern, mit Attrappen-Tests.
4. `CanKit.Pro.CANopen.Cia401`, nicht paketiert: Katalog, Peer, Gerät (erste Scheibe).
5. `CanKit.Pro.CANopen.Cia408`, nicht paketiert.
6. `feat`: Naht, `ICanOpenProfile` und Katalog öffentlich, Pakete frei, ADR-14 „umgesetzt", SRS-Zeilen, Paketseiten.

Was 408 an der Naht ändert, ändert Schritt 5 in Kern *und* 401 zugleich — das ist der Zweck der
Reihenfolge.

## Außerhalb dieser Runde

- **Weitere Teile von CiA 302** (in der Sammlung: Teile 1, 3–9; aus Teil 2 fehlen laut
  `CanOpenNode.BootUp.cs` die Identitätsprüfung `1F84h`–`1F88h` und die Konfiguration eines
  Slaves). Eigene Zuschnitt-Runde je Teil, Muster ADR-13.
- **Abgleich des gebauten CiA 302-2 gegen den Volltext.** ADR-13 hält fest, dass nach öffentlichen
  Beschreibungen gebaut wurde, weil der Text nicht vorlag. Er liegt jetzt vor. Das ist ein Issue,
  kein Teil dieser Runde.
- Erweiterung von `tools/CanKit.Pro.CanOpenConformance` um Profilprüfungen.

## Dem Bauenden überlassen

- Namen und Signaturen der Naht im Einzelnen; die Tabellen oben nennen Fähigkeiten, keine
  Signaturen.
- `1029h` für Profile: CiA 401-1 §6.2.4 am Text lesen und klären, ob das Profil dort mehr braucht
  als den einen Sub-Index, den der Knoten heute führt und gegen Erweiterung schützt.
- Ob „Fehler melden" auf `SendEmcyAsync` aufsetzt oder einen eigenen Weg mit `1001h`/`1003h`
  braucht — am Code zu klären, nicht hier.
- Form des 408-Katalogs (von Hand, aus einer Tabelle erzeugt) und sein Prüfanker.
- Wann genau der Fehlermodus der Ausgänge greift: am Normtext von `6206h` und `6443h` lesen.

## Entscheidungsfragen

1. **Ausgabe von CiA 408.** In der Sammlung liegen zwei Nachfolger von v2.1.0: **CiA 408 v2.2.0**
   (Draft Specification, 12.09.2024, ein Dokument) und **CiA 408-B / 408-C v1.0.0** (Draft
   Specification Proposal, 09.09.2024; Teil B „Functional device description and parameter",
   Teil C „Mapping to CANopen CC", beide „replaces partly CiA 408 v2.1.0"). Welche bindet?
   Vorschlag des Autors: v2.2.0, weil es der höhere Reifegrad ist und ein Dokument; B/C als
   Gegenprobe, Abweichungen als Issue.

   Beantwortet (17.11.2026): CiA 408 v2.2.0 bindet. 408-B / 408-C sind Gegenprobe.
2. **Peer-SDO-Tor und Profilwissen.** Entscheidung 1 der Master-/Tool-Runde: ohne EDS/DCF liest
   das Werkzeug nur `1000h:00`, `1001h:00`, `1018h:00–04`. Damit sind `67FFh` und jedes
   Profilobjekt ohne Datei gesperrt. Zählt ein registriertes Profil, dessen Nummer der Peer in
   `1000h` meldet, als Beschreibung für die Objekte seines Katalogs — oder bleibt die Datei
   Pflicht? Vorschlag des Autors: die Datei bleibt Pflicht; das Profil typisiert, was die Datei
   erlaubt. Die Norm macht fast alle Profilobjekte optional, ein Katalog sagt also nicht, was
   *dieses* Gerät hat.

   Beantwortet (17.11.2026): Die Datei bleibt Pflicht. Entscheidung 1 der Master-/Tool-Runde
   gilt unverändert; das gilt auch für `67FFh + x·800h`. Ohne gebundene Datei endet das Lesen
   der logischen Geräte deshalb bei `1000h`: das Werkzeug weiß dann, *dass* weitere folgen
   (`FFFFh`), nicht welche.
3. **Tiefe der Simulation.** Protokolltreu mit Prozesswerten von der Anwendung (Vorschlag, oben
   als Nicht-Ziel geführt) — oder soll ein 408-Gerät von sich aus einen Istwert dem Sollwert
   nachführen?

   Beantwortet (17.11.2026): Protokolltreu, mit einer Schnittstelle für ein Streckenmodell, das
   die Anwendung liefert. Die Bibliothek bringt kein Modell mit.
