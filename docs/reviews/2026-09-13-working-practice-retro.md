# Retro – Arbeitsweise der Issue-Welle vom 12./13. September 2026

**Datum:** 2026-09-13 · **Stand:** `main` @ `17d40e6` · **Umfang:** 6 Pull Requests, 6 geschlossene Issues, 6 neue Issues
**Gegenstand:** nicht der Code, sondern wie er entstanden ist

> **Zweck und Verhältnis zu `CLAUDE.md`.** Die Regeln, die aus dieser Welle folgen, stehen in
> `CLAUDE.md` § *Pull requests*, § *Before claiming something is true* und § *Decisions that
> belong to the maintainer* — dort wirken sie, weil sie vor der Arbeit gelesen werden. Dieses
> Dokument ist die Herleitung: die Chronologie und die Zahlen, aus denen die Regeln stammen.
> Wer nur wissen will, was gilt, braucht es nicht.
>
> Anlass war die Einschätzung des Maintainers, das Ergebnis sei „nicht sehr befriedigend"
> gewesen — bei objektiv brauchbarem Endstand. Genau diese Lücke ist der Gegenstand.

---

## Was entstanden ist

Gemerged: #93, #96, #97, #98, #100, #101. Geschlossen: #23, #24, #37, #44, #53, #82.
Neu und dokumentiert: #92, #94, #95, #99, #102, #103.

Inhaltlich trägt das. Zwei Befunde waren echte Fehler im Produktivcode, die ohne diese Welle
geblieben wären:

- **#24 / `TryMatchEcho`** — ein abgelaufener Pending-Send verschluckte das Echo des nächsten
  byte-gleichen Sends. Der erste Fix führte dabei eine zweite Race derselben Bauart ein
  (`IsCompleted` testen, dann `TrySetResult` — check-then-act); beseitigt durch atomares
  Beanspruchen.
- **`macos-latest` auf `main`** war rot, weil ein Test dem Scheduler ein Verhalten vorwarf, das
  dieser laut eigener Dokumentation zeigt (Coalescing auf 2 × Periode unter Last).

Der Preis: mindestens sechs öffentliche Selbstkorrekturen auf GitHub, und zweimal musste der
Maintainer nachfassen, bevor etwas passierte.

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

Dazu zwei Nebenbefunde: in einem selbst geschriebenen Test stand ein `Thread.Sleep(50)` — genau
das Anti-Pattern, das in #92 kritisiert wird. Und ein Review-Thread war aufgelöst, enthielt aber
keine Antwort, weil GitHub den Reply mit 500 abgewiesen hatte und die Antwort ausgelagert wurde.

## Die Zahlen, die es entschieden haben

Was in dieser Welle tatsächlich Klarheit gebracht hat, war ohne Ausnahme Messung, nicht
Überlegung:

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

**Eigene Argumentation wurde als Beweis behandelt.** In jedem der acht Fälle war die Gegenprobe
billig und verfügbar — ausführen, mutieren, unter Last laufen lassen, nachmessen — und in jedem
Fall kam zuerst das Argument.

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

## Was nicht verallgemeinerbar ist

Ein Teil der Runden war echtes Erkunden und wäre nicht abkürzbar gewesen. Dass sich Grid-Alignment
bei 120 ms Periode nicht auflösen lässt — weil der Beobachtungs-Jitter auf dem Spectator-Bus
~50 ms beträgt, also 40 % des Rasterabstands — war vorher nicht wissbar und ist erst durch die
Lastläufe sichtbar geworden. Das gehört zur Sache, nicht zum Fehler.

Die Trennlinie: Erkunden erzeugt Wissen, das vorher nicht da war. Nacharbeit an einer Behauptung,
die vor der Messung aufgestellt wurde, erzeugt nur den Zustand, der ohne die Behauptung schon
gegolten hätte. Von den acht Zeilen oben fällt keine in die erste Kategorie.

## Was daraus in `CLAUDE.md` steht

- Eine Aussage über einen Test verlangt eine Mutation, kein Argument.
- Widerspricht ein Werkzeug der eigenen Messung, zuerst das Instrument prüfen.
- Nur die kaputte Hälfte einer Assertion ersetzen.
- Bei Uhr-Assertions die Störgröße benennen und die Marge dagegen prüfen — nicht gegen bisher
  beobachtete Zeiten.
- Ein Ersatzweg beendet die Aufgabe nicht; der ursprüngliche Weg ist nachzuholen.
- Ein PR zur Zeit; Ausnahme ist ein PR, der aus dem laufenden entsteht.
- Entscheidungen des Maintainers als Frage mit Auswahl stellen und dann schweigen.

## Offen aus dieser Welle

- **#92** — Schritt 1 (UDS-Starvation, einziger Produktbug-Kandidat) und Schritt 2 (virtuelle Uhr
  für die drei verbleibenden zeitabhängigen Tests).
- **#102** — `Transmit` unter `_pendingGate`, mit FIFO-Ordnung, reentrantem Echo-Pfad und
  Dispose-Race als Randbedingungen.
- **#103** — RX-Kopie in J1939-TP und CANopen.
- **Nach dem 1.3.0-Release** — der veraltete `Block 3→1 / remap`-Hinweis in `CHANGELOG.md` und im
  Release-Text (vermerkt auf #44). Das ist der einzige Rest, der direkt aus Fehler #2 stammt und
  den kein Issue automatisch erledigt.
