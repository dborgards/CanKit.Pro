# Stand der Issue-Welle vom 14./15. September 2026

**Datum:** 2026-09-15 · **Stand:** `main` @ `8be4466` · **Umfang:** 7 gemergte Pull Requests, 1
ungemergt geschlossener, 4 geschlossene Issues, 4 neu angelegte (davon 1 sofort als Duplikat
geschlossen)
**Gegenstand:** wo der Backlog steht und was als Nächstes ansteht

> Fortsetzung von `docs/reviews/2026-09-13-working-practice-retro.md`. Jenes Dokument leitet die
> Regeln in `CLAUDE.md` her; dieses ist eine Bestandsaufnahme und beansprucht keine neuen Regeln.
> Die Zahlen gelten zum Datum oben.

## Was gelandet ist

| PR | Gegenstand | Typ |
|---|---|---|
| #113 | Timing-Tests auf eine Actor-Naht statt auf die Uhr | `fix` |
| #115 | `Th` als Argument statt als Default — 28 Pacing-Lücken gegen 5 s | `fix` |
| #116 | `Bam_MaximumPayload_Roundtrip` wartete 254-mal auf `Th` | `test` |
| #117 | UDS: `delayBetween` rannte gegen `P2StarClientMax` | `fix` |
| #119 | J1939/CANopen melden eigenen Verkehr nicht mehr als fremden (#95, #94, #103) | `fix` |
| #122 | CANopen: Bootup ist keine Node-Guarding-Antwort (#43, Baseline-Hälfte) | `fix` |
| #124 | Was eine blockierende `Transmit` tatsächlich kostet (#102) | `test` |

Commit-Typen seit dem 14.09. auf `main`: **12 `fix`, 9 `test`, 1 `style`** — ein Patch-Release,
kein Minor.

**#123 wurde zurückgezogen, nicht gemergt.** Der Versuch, #121 mit einem Echo-Zähler zu lösen, zog
in *einer* Review-Runde sieben Befunde, zwei davon High, darunter einer auf dem Normalpfad. Die
Begründung und alle sieben Randbedingungen an einen Ersatz stehen auf #121.

## Geschlossene Issues

| Issue | Ausgang |
|---|---|
| #95 | erledigt durch #119 — die dort offene Designfrage ist im Code beantwortet |
| #103 | erledigt durch #119 — beide offenen Kopierstellen entschieden, und zwar unterschiedlich |
| #102 | `not planned`, dokumentiert und abgelehnt; Beleg ist #124 |
| #120 | Duplikat von #43 — ohne vorherige Suche angelegt, Evidenz nach #43 umgezogen |

## Offen: 33 Issues

| Gruppe | Anzahl | Nummern |
|---|---|---|
| aus dieser Welle entstanden | 3 | #114, #118, #121 |
| Testinfrastruktur (übrige) | 3 | #92, #94, #52 |
| Sammeltickets je Bereich | 5 | #55–#59 |
| Produktdefekte | 20 | #17, #18, #25–#36, #38–#43 |
| J1939-SPN, zurückgestellt | 1 | #99 |
| CI | 1 | #106 |

Die 20 Produktdefekte sind seit dem 10.09. unverändert; diese Welle hat davon nur #43 zur Hälfte
angefasst. Das ist der eigentliche Befund dieser Bestandsaufnahme: die Welle hat fast ausschließlich
an Testinfrastruktur und an Defekten gearbeitet, die aus ihr selbst hervorgingen.

### Wo die drei neuen stehen

- **#114** — Audit erledigt und zweimal korrigiert. Die eigentliche Population ist **unangetastet**:
  56 Sites in Bucket B, 2 in C. Was diese Welle daraus fixierte, waren drei einzeln gefundene
  Flakes (#115, #116, #117), nicht der Bestand. Nächster Block nach dem Audit selbst: die 29 B+C-
  Sites, deren Nahtstellen seit #113 existieren — `BusStateMonitor` ist die billigste, sie nimmt
  seit jeher einen injizierten Actor.
- **#118** — gemessen, bleibt offen (unten).
- **#121** — keine Regression. Empfehlung auf dem Ticket: *offen lassen und nicht anfassen*, bis die
  per-Adress-Buchführung sich für mehr als dieses eine Ticket lohnt.

## Die Messung, die diese Welle hinterlässt

#118 fragte, ob das First-Hop-Budget der UDS-Tests zu dünn ist, und schrieb die Entscheidungsregel
vorher hin. Unter 8 CPU-Brennern auf 4 Kernen, beide betroffenen Tests gemeinsam abgesenkt:

| `P2ClientMax` | Fehlschläge |
|---|---|
| 60 ms | 0 / 6 |
| 40 ms | 0 / 6 |
| **25 ms** | **4 / 6** |
| 15 ms | 6 / 6 |
| 10 ms | 6 / 6 |

Klippe zwischen 40 und 25 ms, Marge bei 100 ms also **2,5–4×**. Weder „nahe 100 ms" noch „eine
Größenordnung darunter" — keiner der beiden Ausgänge, die das Ticket vorab benannt hatte. Nach dem
Maßstab von #92 ist das dünn. Kontrolle: die vollen 24 Tests von `UdsClientTests` unter derselben
Last, unverändert, 0 Fehlschläge in 2 Läufen.

Ein einzelner Fehlschlag bei den Ist-Werten (1 in 70 Lastläufen) blieb **unattribuiert** und zählt
deshalb nicht.

## Drei Belege aus dieser Welle

Keine neuen Regeln — Belege für die, die schon in `CLAUDE.md` stehen.

1. **Eine Korrektur macht die nächste nötig.** #119 brauchte **sechs Verengungen an einem einzigen
   `int`-Feld**; drei davon waren Defekte in der Korrektur der jeweils vorigen Runde, und zwei
   davon fand nicht ein Reviewer, sondern das absichtliche Kaputtmachen des frischen Codes.
2. **Lokal grün ist kein Beleg.** Auf #124 steckten vier Defekte in vier Fassungen, und *keinen*
   fand der eigene Testlauf: einer kam von Windows-CI (die `net48`-Leg, die lokal nur mit
   `-p:CanKitProTestNetFrameworkLeg=true` gebaut wird), drei von den Bots. Drei davon waren
   dieselbe Klasse — eine Assertion, die aus dem genannten Grund gar nicht fallen kann.
3. **Vor dem Anlegen eines Tickets suchen.** #120 war ein Duplikat von #43, seit fünf Tagen offen,
   mit denselben Zeilennummern und demselben Lösungsvorschlag.

## Nächste Schritte, in dieser Reihenfolge

1. **#94-Rest** — `Bam_Sender_On_An_Unflagged_Echo_Bus_…` auf `EchoWorldFixture.Both` umstellen.
   Klein, seit #119 möglich, schließt ein Ticket.
2. **#114, Schritt 2** — die 29 B+C-Sites mit vorhandener Naht (ISO-TP, J1939, Actor/Deadline,
   `BusStateMonitor`). Danach der CANopen-Block (20 Sites), der erst die #113-Naht in `CanOpenNode`
   braucht.
3. **#43, zweite Hälfte** — producer-seitiges Life Guarding. `feat`, eigene Designrunde; die
   Vorarbeit steht auf dem Ticket.

Bewusst **nicht** als Nächstes: #118 und #121. Beide sind gemessen bzw. durchgerechnet, und beider
Lösung ist dieselbe Klasse von Infrastruktur — Zeitnaht in `UdsClientImpl` bzw. per-Adress-
Buchführung im J1939-Knoten —, die sich erst lohnt, wenn mehr als ein Ticket davon abhängt.

Und die 20 Produktdefekte warten weiter. Wenn die nächste Welle wieder nur Testinfrastruktur
anfasst, ist das eine Entscheidung und sollte als solche getroffen werden.
