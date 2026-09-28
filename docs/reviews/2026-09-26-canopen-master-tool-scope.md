# CANopen: Zuschnitt der Master-/Tool-Rolle

Stand: 26.09.2026, `main` @ `871d153`. Zweite Zuschnitt-Runde — CANopen, Master- und Tool-Rolle,
Issue [#131](https://github.com/dborgards/CanKit.Pro/issues/131). Die Geräterolle ist entschieden
und übersetzt (`docs/reviews/2026-09-15-canopen-scope.md`, FR-CO-013..028).

Am 26.09. hat der Maintainer sechs Punkte dieser Runde entschieden. Sie stehen unten als
Entscheidungen, nicht als Vorschläge. Was sie nicht festlegen, steht am Ende als nummerierte
Entscheidungsfragen. Dorthin wird nichts ergänzt, was die Entscheidungen nicht hergeben: kein
Verfahrensablauf für Flying Master, kein Inhalt des Abruf-Scans, keine Signatur.

#131 bleibt offen, solange diese Fragen offen sind. Diese Datei schreibt keine neue
`FR-CO`-Zeile. Eine `Must`-Zeile ohne das Verhalten, das sie prüft, wäre eine geratene
Implementierung.

Nachgetragen (28.09.2026): Alle acht Fragen sind beantwortet, die Antworten stehen unter der
jeweiligen Frage. Die Rolle steht jetzt als FR-CO-029..034 in der SRS (§4.3.2). Jede dieser
Zeilen wurde erst geschrieben, als das Verhalten gebaut war, das sie prüft.

## Woher die Belege kommen

| Stufe | Was so belegt ist |
|---|---|
| **gemessen** | Öffentliche API und Quelltext auf `main` @ `871d153` |
| **Norm** | Nur, wo die Geräterunde einen CiA-301-Abschnitt am Volltext belegt hat. CiA 302 lag nicht vor |
| **Entscheidung** | Die sechs Punkte des Maintainers vom 26.09., wörtlich angewendet |

Die frühere Einstufung von Flying Master als optional — im Issue als Urteil der GAP-Analyse
geführt, in der Paket-README als außerhalb des Pakets geparkt (`README.md:92`) — ist
zurückgenommen. Sie begründet nichts mehr.

## Was schon da ist

`ICanOpenNode` ist eine Instanz und zwei Rollen (`ICanOpenNode.cs:18-24`). Der SDO-Client, der
NMT-Master, der Heartbeat-Consumer, der Node-Guarding-Consumer und der SYNC-Produzent sind
ausgeliefert (FR-CO-002, FR-CO-003, FR-CO-004, FR-CO-007, FR-CO-008, FR-CO-009, FR-CO-010).
`HeartbeatReceived` meldet Heartbeat und Boot-up fremder Knoten; der Boot-up kommt als
`NmtState.Initializing` (`CanOpenNode.cs:928-944`). `EmcyReceived` meldet fremde EMCY
(FR-CO-011).

Das ist der Bestand der Rolle. Die Entscheidungen ändern ihn nicht. Sie benennen, was darauf
fehlt.

Gemessen fehlt heute:

- `SdoUploadAsync` nimmt jeden Index. Kein Pfad verlangt vorher eine EDS oder DCF des fremden
  Knotens, und keiner begrenzt den Lesezugriff auf Objekte, die in einer solchen Datei stehen
  (`ICanOpenNode.cs:211-228`). `CanOpenDeviceDescription` speist den **eigenen** Knoten
  (`CanOpen.cs:61-66`), nicht die Leseliste eines Peers.
- Fremde PDO-Nutzdaten werden nicht zerlegt. Frames ohne gültiges eigenes RPDO fallen aus
  `HandleIncoming` (`CanOpenNode.cs:746-759`). Weder ein gelesenes `1600h`–`1603h` /
  `1A00h`–`1A03h` noch das Mapping einer fremden EDS/DCF ist eine Quelle für diese Zerlegung.
- `samples/CanKit.Pro.Sample.CanOpenBusScan` hört eine einstellbare Zeit (Vorgabe 2000 ms) und
  liest danach bei jeder Node-ID ohne Heartbeat `1000h` per SDO (`Program.cs:33`, `79-90`).
  Ein Hörfenster, nach dem **nicht** alle IDs abgefragt werden, hat die Bibliothek nicht.
- Flying Master kommt im Paket nicht vor. Der einzige Satz dazu stellt es mit dem
  Boot-up-Manager nach draußen (`README.md:92`).

Nachgetragen (28.09.2026): Diese Liste ist der Stand von `871d153`. Seitdem sind alle vier Punkte
geschlossen: das Lese-Tor mit #174 (FR-CO-029), das Zerlegen fremder PDOs mit #163 (FR-CO-030),
Flying Master und Boot-up-Manager mit #164 (FR-CO-031, FR-CO-032) und das Hörfenster mit
`CanOpenDiscovery` (FR-CO-033, FR-CO-034).

## Entscheidungen

### 1. Gerätebeschreibung vor dem Lesen

Eine EDS oder DCF des fremden Knotens ist Pflicht, bevor das Werkzeug diesen Knoten gültig
lesen darf. Ohne diese Datei wird nicht frei alles gelesen, was sich an Indizes raten lässt.
Gelesen werden nur Objekte, die in der Gerätebeschreibung stehen.

Der SDO-Client hält das vor dem Senden ein. Ohne geladene Peer-Datei dürfen ausschließlich
die Pflichtobjekte `1000h:00`, `1001h:00` und die gesamte Identity `1018h` (`00h`–`04h`)
übertragen werden. Optionale Objekte — `1003h` eingeschlossen — gehören nicht dazu, und
`1018h` oberhalb von `04h` auch nicht. Liegt eine Datei vor, gilt nur, was sie führt, auch
für `1000h`, `1001h` und `1018h`.

### 2. Fremde PDOs

Primärquelle des Mappings ist das lebende Objektverzeichnis: die Records `1600h`–`1603h` und
`1A00h`–`1A03h`, nicht nur `1600h` und `1A00h`. Rückfall ist das Mapping aus der vorhandenen
EDS oder DCF. Beide Wege sind nicht implementiert und sind zu bauen.

Der ausgelieferte SDO-Client kann die Bytes eines Index holen. Eine Beobachtung, die daraus oder
aus der Datei eine fremde Nutzlast zerlegt, gibt es auf dem hier gemessenen Stand nicht.

Nachgetragen (26.09.2026, #163): `ObserveForeignPdoAsync` zerlegt die Nutzlast. Live vor Datei,
einschließlich der Antwort auf Frage 3.

### 3. Erkennung der Knoten

Voller Scan der Node-IDs 1..127, der aktiv abfragt, ist nur der zweite Weg. Die Node-ID des
Scanners selbst fällt aus 1..127 heraus, so wie das Sample `clientNodeId` ausnimmt
(`Program.cs:35`, `82`). Die primäre Anwesenheitserkennung hört am Bus Heartbeat **und**
Boot-up, etwa 1–2 Sekunden, und scannt danach nur auf Abruf. Alle übrigen Node-IDs aktiv
anzufragen ist nicht der Default.

`HeartbeatReceived` liefert die beiden Meldungsarten schon. Das Fenster und die Regel „Scan nur
auf Abruf" sind nicht das, was das Sample tut: es fragt danach die IDs ohne Heartbeat per SDO ab.

Nachgetragen (28.09.2026): Mit den Antworten auf die Fragen 1, 2, 6 und 8 ist die Erkennung ein
Teil der Bibliothek. `CanOpenDiscovery.ListenAsync` hört, ohne einen Knoten zu öffnen und ohne zu
senden (FR-CO-033). `CanOpenDiscovery.ScanAsync` ist der Abruf-Scan und läuft nur, wenn er
aufgerufen wird (FR-CO-034). Das Sample nutzt beide; #178 hatte dort schon das Hören zum Default
gemacht.

### 4. Flying Master

Die Einstufung als optional ist zurückgenommen. Flying Master ist Pflicht und wird
implementiert. Er ist eine geforderte Fähigkeit der Master-Rolle, keine Kür.

CiA 302 ist in dieser Runde nicht gelesen. Deshalb steht hier kein Zustandsautomat, kein Index
und keine Zeit. Die Pflicht ist entschieden; der Ablauf nicht.

Nachgetragen mit #164: die Wahl des aktiven Masters ist umgesetzt, und der Maintainer hat die
Bindung auf **CiA 302-2 Version 4.1.0** festgelegt (Network management, NMT flying master,
Objekt `1F90h`). DSP 302 Abschnitt 5.5 ist nur die historische Herkunft. Der Boot-up-Manager
(`1F80h`, `1F81h`, `1F82h`, `1F89h`) gehört zu derselben Umsetzung. Der Mitgliedstext liegt
nicht im Repository; was die öffentlichen Beschreibungen offen lassen, steht am Pull Request.

### 5. Aufmerksamkeit gegenüber der Geräterunde

Tool und Master sind für die Aufmerksamkeit der Hauptfall. Manche Geräteposten bleiben fachlich
richtig und treten in der Priorität zurück, damit sie nicht mit den offenen Master-Lücken
konkurrieren. Das Beispiel aus der Entscheidung sind die Fallback-Records ohne EDS.

FR-CO-013..028 bleiben, was sie sind, einschließlich dieser Records. An ihrer
`Must`-Einstufung ändert diese Runde nichts. Was sich ändert, ist die Reihenfolge der
Aufmerksamkeit. Vor den Fallback-Records ohne EDS kommen:

1. die Gerätebeschreibung als Tor für das Lesen eines fremden Knotens,
2. das Zerlegen fremder PDOs aus `1600h`–`1603h` und `1A00h`–`1A03h` und, als Rückfall, aus der EDS/DCF,
3. die Anwesenheitserkennung (Hören, Scan nur auf Abruf),
4. Flying Master.

Welche weiteren Geräteposten in dieselbe Zurückstufung gehören, ist nicht gesagt. Das ist
Frage 7.

Nachgetragen (28.09.2026): Die Reihenfolge ordnet offene Arbeit, und von der Geräterunde ist
keine mehr offen. Nach Frage 7 ist die Zurückstufung deshalb gegenstandslos.

### 6. Keine Annahme

Jeder Punkt, den die fünf Entscheidungen oben nicht schließen, ist eine Entscheidungsfrage.
Er wird nicht durch eine Implementierung ersetzt.

## Entscheidungsfragen

1. **Anwesenheit im Hörfenster.** Genügt je Node-ID eine der beiden Meldungen — Heartbeat oder
   Boot-up — oder müssen im Fenster beide beobachtet worden sein?

   Beantwortet (28.09.2026): Eine Meldung genügt. Ein laufender Knoten sendet im Fenster keinen
   Boot-up; hieße es „beide“, würden nur Knoten erkannt, die gerade starten. Als Meldung zählen
   die Zustandsbytes `00h` (Boot-up) sowie `04h`, `05h` und `7Fh` (Heartbeat) auf
   `700h` + Node-ID. Andere Bytes sind keine dieser beiden Meldungen (FR-CO-033).
2. **Abruf-Scan.** Was wird gesendet, wenn die Node-IDs 1..127 auf Abruf aktiv abgefragt werden?
   Die eigene Node-ID des Scanners ist davon ausgenommen. Lesen von Objekten, die nicht in einer
   vorliegenden EDS oder DCF stehen, lassen die Entscheidungen nicht zu. Für den SDO-Client ist
   die Ausnahme in Entscheidung 1 benannt: ohne Datei nur `1000h:00`,
   `1001h:00` und `1018h:00`–`04h`. Was ein Abruf-Scan sonst sendet, steht hier weiter nicht.

   Beantwortet (28.09.2026): Genau ein SDO-Upload von `1000h:00` je Node-ID, die nicht ausgenommen
   ist, und sonst nichts. Das geht durch das Peer-SDO-Tor: Ist eine Datei gebunden, die `1000h:00`
   nicht führt, wird die Node-ID nicht angefragt. Anwesend ist, wer antwortet, auch mit einem
   SDO-Abbruch. Die eigene Zeitüberschreitung des Clients bedeutet Abwesenheit (FR-CO-034).
3. **Live-Mapping und das Lese-Tor.** Primärquelle sind `1600h`–`1603h` und `1A00h`–`1A03h`.
   Gültiges Lesen setzt die Datei voraus und erlaubt nur Objekte aus ihr. Dürfen diese Records
   gelesen werden, wenn die Datei sie nicht führt? Gehören `1400h:01` und `1800h:01` (die COB-ID)
   zur Primärquelle, oder nur die genannten Mapping-Records?

   Beantwortet (26.09.2026), beides ja, live vor Datei: Mapping-Subindizes dürfen per SDO gelesen
   werden, auch wenn die Datei sie nicht führt. `1400h:01` und `1800h:01` werden live gelesen,
   nicht nur aus der Datei. Die Datei bleibt der Rückfall, wenn das Live-Lesen abbricht, die Zeit
   überschreitet oder kein verwendbares Wort liefert. `ObserveForeignPdoAsync` (#163) folgt dem.

   Nachgetragen mit #174: der Aufruf geht über `SdoUploadAsync`. Verweigert das Peer-SDO-Tor das
   Paar, ist das ein gescheitertes Live-Lesen und die an den Aufruf übergebene Datei wird
   genommen. Ein Subindex, den die für den Knoten gebundene Beschreibung nicht enthält, geht
   deshalb nicht auf den Bus.
4. **Flying Master, Umfang.** Welche Ausgabe und welcher Abschnitt von CiA 302 binden die
   Pflicht? Ohne diese Angabe wird kein Ablauf geschrieben.

   Beantwortet: CiA 302-2 Version 4.1.0, NMT flying master, Objekt `1F90h`. DSP 302 Abschnitt 5.5
   ist nur die historische Herkunft, nicht die Bindung. Der Mitgliedstext ist nicht im
   Repository; umgesetzt ist die öffentliche Beschreibung dieser Ausgabe.
5. **Boot-up-Manager.** Die Paket-README nennt ihn im selben Satz wie Flying Master. Die
   Entscheidung vom 26.09. nennt nur Flying Master. Gehört der Boot-up-Manager zur Pflicht?

   Beantwortet: ja. Er ist Teil von #164, gebunden an dieselbe Ausgabe, mit `1F80h`, `1F81h`,
   `1F82h` und `1F89h`. Die Identitätsprüfung `1F84h`–`1F88h` ist nicht dabei.
6. **Dauer des Hörfensters.** „Etwa 1–2 Sekunden" ist das Band. Liegt darin ein fester Wert,
   oder wählt der Aufrufer die Dauer in diesem Band?

   Beantwortet (28.09.2026): Der Aufrufer wählt die Dauer frei. Die Vorgabe ist 2 s und liegt im
   Band. Ein Netz mit Heartbeat-Zeiten über 2 s kann das Fenster verlängern (FR-CO-033).
7. **Weitere Geräteposten.** Welche Posten der Geräterunde außer den Fallback-Records ohne EDS
   treten hinter die vier Master-Lücken zurück? Die Records selbst bleiben fachlich richtig;
   ihre `Must`-Zeile bleibt stehen.

   Beantwortet (28.09.2026): gegenstandslos. FR-CO-013..028 sind ausgeliefert, und zu keinem
   ihrer Posten ist ein Issue offen. Neue Befunde zur Geräterolle werden wie jeder andere Befund
   nach ihrer Schwere gewichtet.
8. **Ort von Hörfenster und Abruf-Scan.** Ist das geforderte Verhalten ein Teil der Bibliothek,
   oder Anwendungscode auf dem vorhandenen `HeartbeatReceived`?

   Beantwortet (28.09.2026): Es gehört in die Bibliothek, als öffentliche API mit Hören als Default
   und dem Scan als eigenem Aufruf: `CanOpenDiscovery.ListenAsync` und `CanOpenDiscovery.ScanAsync`.
