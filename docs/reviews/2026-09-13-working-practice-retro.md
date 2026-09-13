# Retro – Arbeitsweise der Issue-Welle vom 12./13. September 2026

**Datum:** 2026-09-13 · **Stand:** `main` @ `67f70a9` · **Umfang:** 9 gemergte Pull Requests, 1 ungemergt geschlossener, 6 geschlossene Issues, 7 neue Issues
**Gegenstand:** nicht der Code, sondern wie er entstanden ist

> **Zweck und Verhältnis zu `CLAUDE.md`.** Die Regeln, die aus dieser Welle folgen, stehen in
> `CLAUDE.md` § *Pull requests*, § *Stay inside the task*, § *Before claiming something is true*
> und § *Decisions that belong to the maintainer* — dort wirken sie, weil sie vor der Arbeit
> gelesen werden. Dieses
> Dokument ist die Herleitung: die Chronologie und die Zahlen, aus denen die Regeln stammen.
> Wer nur wissen will, was gilt, braucht es nicht.
>
> Anlass war die Einschätzung des Maintainers, das Ergebnis sei „nicht sehr befriedigend"
> gewesen — bei objektiv brauchbarem Endstand. Genau diese Lücke ist der Gegenstand.

---

## Was entstanden ist

Gemerged: #93, #96, #97, #98, #100, #101, #104, #107, #108. Ungemergt geschlossen: #105.
Geschlossen: #23, #24, #37, #44, #53, #82. Neu und dokumentiert: #92, #94, #95, #99, #102, #103,
#106.

Die letzten drei Pull Requests (#104, #107, #108) sind selbst Gegenstand dieser Retro: sie tragen
die Regeln, und ihr Verlauf hat den Befund geliefert, der weiter unten unter *Der Befund, der erst
am Ende sichtbar wurde* steht. Der Review dieses Dokuments (#109) hat denselben Befund noch zweimal
bestätigt und ist dort mit aufgenommen.

Inhaltlich trägt das. Zwei Defekte kamen heraus, die ohne diese Welle geblieben wären — einer im
Produktivcode, einer in der Suite:

- **#24 / `TryMatchEcho`** — ein abgelaufener Pending-Send verschluckte das Echo des nächsten
  byte-gleichen Sends. Der erste Fix führte dabei eine zweite Race derselben Bauart ein
  (`IsCompleted` testen, dann `TrySetResult` — check-then-act); beseitigt durch atomares
  Beanspruchen.
- **`macos-latest` auf `main`** war rot, weil ein Test dem Scheduler ein Verhalten vorwarf, das
  dieser laut eigener Dokumentation zeigt (Coalescing auf 2 × Periode unter Last). Der Defekt lag
  in der Behauptung des Tests, nicht im Produktivcode — die Suite meldete dauerhaft einen Bug, den
  es nicht gab, und verdeckte damit, was sie sonst hätte melden können.

Der Preis: elf öffentlich korrigierte Fehlbehauptungen — die Chronologie unten zählt sie
einzeln —, dreimal musste der Maintainer
nachfassen, und zweimal ging eine Korrektur beim Mergen verloren, weil sie kurz vor dem Merge
gepusht wurde.

## Chronologie der Fehlschläge

| # | Behauptung | Warum sie falsch war | Kosten |
|---|---|---|---|
| 1 | #93: „PR-Beschreibung ist korrekt" | aus dem Gedächtnis, nicht nachgelesen — sie behauptete noch eine gelöschte Prüfung | 1 Korrektur |
| 2 | #97: „veralteter Footer erledigt" | Squash-Merge verkettet Commit-Messages; der falsche `Block 3→1`-Hinweis landete trotzdem auf `main` (`a723b3c`) | 1 Korrektur, offener Nachtrag am Release |
| 3 | Codecov #100: „Datei ist aus dem Report" | Diff-Zeilennummern waren zwei Commits alt | 1 Runde |
| 4 | Codecov #100: „Bot-Lag" | eigenes Skript dedupliziert Cobertura nach Dateiname — Cobertura schreibt aber ein `<class>` pro **Typ**, verschachtelte Typen fielen weg. Real unabgedeckt: der äußere `catch` der Pump | 1 Runde + Nachfassen des Maintainers |
| 5 | Codecov #100: „beide Partials unerreichbar" | einer war ersatzlos entfernbar (64-Bit-Shift statt Sonderfall), der andere brauchte nur `InternalsVisibleTo` — bereits Hauskonvention in zwei anderen Paketen | 1 Runde + Nachfassen des Maintainers |
| 6 | #101: „Claim-Test repariert" | Cancel feuerte vor dem Armen der Deadline; der Test bestand die Regression, für die er existiert | Codex-P2 |
| 7 | #101: „Slot-Untergrenze ist lastunabhängig" | ein verspäteter Tick verkürzt den Abstand zum nächsten Grid-Slot legitim | 2/8 bzw. 1/12 rot unter Last |
| 8 | #101: „Mittelwert ist die stärkste wahre Aussage" | ein später erster Tick senkt den Mittelwert unter die Schranke, ohne dass der Scheduler etwas falsch macht | Codex-P2 |
| 9 | #104: ein zweiter PR (#105) für einen Fehler, den der Docs-PR nicht verursacht hatte | Ich berief mich auf eine Ausnahme, die ich drei Stunden zuvor selbst in dieselbe Datei geschrieben hatte | PR geschlossen, Nachfassen des Maintainers |
| 10 | #107: „Widerspruch aufgelöst" | Ich behielt die permissive Regel und verengte ihre Bedingungen — ein genehmigter Freibrief ist immer noch ein Freibrief | Korrektur durch den Maintainer |
| 11 | Vier Widersprüche *zwischen Absätzen* in `CLAUDE.md` | jeder Absatz für sich korrekt; der Konflikt existiert nur zwischen ihnen | 4 Codex-Befunde, davon 1 P1 |

Dazu zwei Nebenbefunde: in einem selbst geschriebenen Test stand ein `Thread.Sleep(50)` — genau
das Anti-Pattern, das in #92 kritisiert wird. Und ein Review-Thread war aufgelöst, enthielt aber
keine Antwort, weil GitHub den Reply mit 500 abgewiesen hatte und die Antwort ausgelagert wurde.

## Die Zahlen, die es entschieden haben

Im Code-Teil der Welle hat ausschließlich Messung Klarheit gebracht, nicht Überlegung. (Für den
Regel-Teil gilt das nicht — dort war es Gegenlesen; siehe *Der Befund, der erst am Ende sichtbar
wurde*.)

| Messung | Ergebnis |
|---|---|
| Alter Periodic-Test unter 8× CPU-Last | **5 von 6 rot**, Mediane 200–272 ms, geclustert auf 240 ms = 2 × Periode |
| Meine Slot-Untergrenze, gleiche Last | 2/8 und 1/12 rot — 69, 76 und 32 ms Abstände bei korrektem Scheduler |
| Median-Fassung, gleiche Last | **16/16 grün** |
| Mutationen gegen die Median-Fassung | Periode halbiert 60 ms · Burst 0,5 ms · 15 % zu schnell 102 ms — alle gefangen (Schranke 108 ms) |
| Claim-Test gegen wiederhergestellte Regression, vorher | alte Fassung rot, **meine 3/3 grün** |
| Claim-Test gegen dieselbe Regression, nachher | **5/5 rot** (4× als `Claiming`, 1× als `Claimed`) |
| `macos-latest` über fünf Heads eines Branches | fünf **verschiedene** Tests, ein sauberer Durchlauf dazwischen, ein Pass auf demselben Commit direkt nach einem Fehlschlag |

Die letzte Zeile ist der wichtigste Einzelbefund der Welle und steht als #92: die Suite hat eine
*Population* lastempfindlicher Tests, keine Handvoll kaputter. Sie einzeln nachzuziehen, sobald
einer rot wird, ist ein Rennen gegen die Runner-Auslastung.

## Ursache

**Eigene Argumentation wurde als Beweis behandelt.** In jedem der ersten acht Fälle war die
Gegenprobe billig und verfügbar — ausführen, mutieren, unter Last laufen lassen, nachmessen — und
in jedem Fall kam zuerst das Argument.

Zwei Verstärker, die das über Einzelfälle hinaus systematisch machen:

**Widerspricht ein Werkzeug, gilt das Werkzeug als veraltet.** Zweimal hintereinander bei
Codecov (#3, #4). Beide Male lag der Fehler lokal. Ein Bot, der nach einem „Fix" dasselbe
weitermeldet, ist Evidenz über die Messung, nicht über den Bot.

**Beim Reparieren wird mehr weggeworfen als kaputt war.** Der Periodic-Test hatte im Original
bereits den Median; falsch war nur die *obere* Schranke (`≤ 1,6 × Periode`), die das Coalescing
bauartbedingt reißt. Die obere Schranke zu entfernen war der Fix. Den Median mitzunehmen war eine
Überkorrektur und hat zwei weitere Runden und zwei Review-Kommentare gekostet, um wieder dort
anzukommen, wo der Test angefangen hatte.

Ein Kontrollexperiment liefert die Welle gleich mit: der Periodic-Test wurde mutationsgeprüft und
hielt; der Claim-Test wurde es nicht und war wertlos. Beide am selben Tag, von derselben Hand.

## Der Befund, der erst am Ende sichtbar wurde

Die zweite Hälfte der Welle bestand daraus, die Regeln aufzuschreiben. Dabei kam eine Schwäche
heraus, die im Code-Teil nicht auffiel, weil dort Tests und Messungen sie abfangen.

Sieben Codex-Befunde über #101, #104, #107 und #108. **Alle sieben berechtigt. Vier davon
Widersprüche zwischen Absätzen** — nicht Fehler in einem Absatz, sondern zwei Absätze, die je für
sich korrekt sind und zusammen nicht gelten können. Beispiele:

- Die Ausnahme erlaubte einen Folge-PR für „a fix the review made necessary elsewhere"; die
  Scope-Regel drei Abschnitte später schickte genau das in ein Issue.
- Der Abschnitt eröffnete mit „Kausalität entscheidet" und schloss mit „nur Dringlichkeit
  entscheidet, ob es jetzt passiert".
- „This rule has no exception" stand drei Absätze über „The exception above permits that".

Der letzte Fall ist der aussagekräftigste. Der Maintainer hatte ausdrücklich gebeten, die ganze
Datei noch einmal auf Widersprüche zu prüfen. Ich fand fünf und behob sie. Codex fand danach eine
sechste — an einem Satz, den ich nicht bearbeitet hatte. Er war korrekt gewesen, bis ich zwei
Commits vorher eine Regel invertierte.

**Die Regel umzudrehen macht stillschweigend jeden Satz falsch, der sich auf sie bezieht, auch
außerhalb des Diffs.** Ein diff-förmiger Review kann die nicht sehen, weil sich an ihnen nichts
geändert hat. Das ist strukturell derselbe Fehler wie der, den dieselbe Datei für Code verbietet
— *Scope entscheidet Kausalität, nicht welche Dateien der Diff geöffnet hat* — angewandt auf
Prosa: **nicht welche Zeilen sich geändert haben zählt, sondern welche Aussagen die Änderung
falsch gemacht hat.** Ich hatte diese Regel vier Commits zuvor geschrieben und nicht auf das
Dokument angewandt, das sie enthält.

Praktische Folge für den nächsten, der eine Regeldatei ändert: nach jeder Umkehrung oder
Verschärfung einer Regel die Datei nach Verweisen auf sie durchsuchen, nicht nach geänderten
Zeilen. Und Absätze gegeneinander lesen, nicht jeden gegen seine Absicht — Letzteres findet diese
Klasse nie, wie hier zweimal belegt.

Ein dritter und vierter Beleg entstanden beim Schreiben dieses Dokuments selbst; Codex fand beide
in #109.

Der dritte ist exakt die Klasse von oben. Der Entwurf wurde von acht auf elf Chronologie-Zeilen
erweitert; die Kopfzeile zählte danach noch acht gemergte Pull Requests bei neun im Inventar, und
zwei Sätze weiter unten bezogen sich weiter auf „die acht Zeilen oben". Keine der falschen Stellen
lag in einer Zeile, die die Erweiterung angefasst hatte.

Der vierte ist die Variante ohne Änderung: die Einleitung kündigte „zwei echte Fehler im
Produktivcode" an, und ihr zweiter Aufzählungspunkt beschrieb selbst eine falsche Test-Behauptung
über dokumentiertes Scheduler-Verhalten. Beide Sätze standen seit dem ersten Entwurf so
nebeneinander — hier hatte keine Umkehrung sie auseinandergebracht, die Überschrift war von Anfang
an weiter als ihre Liste. Das erweitert die Regel: nicht nur nach einer Änderung sind Absätze
gegeneinander zu lesen, sondern auch beim ersten Schreiben, denn eine Zusammenfassung wird gegen
ihre Absicht gelesen und nicht gegen ihre Belege.

Vier Belege in einem Dokument über diese Fehlerklasse — was die Regel eher stützt als schwächt:
sie ist nicht durch Vorsatz einzuhalten, sondern nur durch die Gegenprobe Behauptung gegen Beleg.

## Die Sorte Fehler, die zweimal identisch auftrat

Zwei Korrekturen gingen beim Mergen verloren: `9b5248f` und `9f172d6` verpassten #104, `8ebced1`
verpasste #107. Beide Male hatte ich den neuen Head gemeldet und mich danach schlafen gelegt;
beide Male mergte der Maintainer den Stand, den er kannte. Die Lücke liegt zwischen Push und
Merge, nicht zwischen den Beteiligten — eine Meldung im Chat schließt sie nicht.

Vereinbart wurde deshalb ein Zustandssignal statt eines Vorsatzes: **„FERTIG — mergebar"** wird
erst geschrieben, wenn keine Pushes mehr kommen, und danach wird der Branch nicht mehr angefasst.

## Was nicht verallgemeinerbar ist

Ein Teil der Runden war echtes Erkunden und wäre nicht abkürzbar gewesen. Dass sich Grid-Alignment
bei 120 ms Periode nicht auflösen lässt — weil der Beobachtungs-Jitter auf dem Spectator-Bus
~50 ms beträgt, also 40 % des Rasterabstands — war vorher nicht wissbar und ist erst durch die
Lastläufe sichtbar geworden. Das gehört zur Sache, nicht zum Fehler.

Die Trennlinie: Erkunden erzeugt Wissen, das vorher nicht da war. Nacharbeit an einer Behauptung,
die vor der Messung aufgestellt wurde, erzeugt nur den Zustand, der ohne die Behauptung schon
gegolten hätte. Von den elf Zeilen oben fällt keine in die erste Kategorie.

## Was daraus in `CLAUDE.md` steht

- Eine Aussage über einen Test verlangt eine Mutation, kein Argument.
- Widerspricht ein Werkzeug der eigenen Messung, zuerst das Instrument prüfen.
- Nur die kaputte Hälfte einer Assertion ersetzen.
- Bei Uhr-Assertions die Störgröße benennen und die Marge dagegen prüfen — nicht gegen bisher
  beobachtete Zeiten.
- Ein Ersatzweg beendet die Aufgabe nicht; der ursprüngliche Weg ist nachzuholen.
- **Scope entscheidet Kausalität**, nicht welche Dateien der Diff geöffnet hat: reproduziert der
  Fehler auf der Basis-Revision nicht, hat der Branch ihn verursacht.
- **Was der Branch verursacht hat, wird in diesem PR geschlossen** — nicht als Issue abgelegt,
  nicht gesplittet, nicht erlassen. Diese Regel hat keine Ausnahme.
- Ein PR zur Zeit, an bewusst geschriebener Arbeit. Die einzige Ausnahme betrifft Probleme, die
  dieser PR *nicht* verursacht hat, und sie ist die Entscheidung des Maintainers, nicht des
  Autors.
- Ein PR ist fertig, wenn jeder Thread geschlossen ist — Bot-Befunde und Coverage-Report
  eingeschlossen, und ein Base-Merge macht das wieder auf.
- Entscheidungen des Maintainers als Frage mit Auswahl stellen und dann schweigen.

Die Fassung dieser Liste, die vor #107 hier stand, gab die Ein-PR-Regel falsch wieder — mit einer
Ausnahme für Probleme, die der PR selbst verursacht hat. Genau die wurde verworfen.

## Offen aus dieser Welle

- **#92** — Schritt 1 (UDS-Starvation, einziger Produktbug-Kandidat) und Schritt 2 (virtuelle Uhr
  für die drei verbleibenden zeitabhängigen Tests).
- **#102** — `Transmit` unter `_pendingGate`, mit FIFO-Ordnung, reentrantem Echo-Pfad und
  Dispose-Race als Randbedingungen.
- **#103** — RX-Kopie in J1939-TP und CANopen.
- **#106** — die Merge-Queue ist in `ci.yml` verdrahtet, aber nie gelaufen: null `merge_group`-
  Läufe, weil sie auf dem Branch nicht aktiviert ist. Gefunden beim Prüfen eines Codex-Befunds,
  nicht beim Suchen. Ein Schutz, der nur dekorativ ist, ist schlechter als keiner — der Fall, für
  den er gebaut wurde, ist bereits eingetreten (#85).
- **Die Konvention „FERTIG — mergebar"** steht noch nicht in `CLAUDE.md`.
- **Nach dem 1.3.0-Release** — der veraltete `Block 3→1 / remap`-Hinweis in `CHANGELOG.md` und im
  Release-Text (vermerkt auf #44). Das ist der einzige Rest, der direkt aus Fehler #2 stammt und
  den kein Issue automatisch erledigt.
