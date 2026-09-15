# Reihenfolge der 33 offenen Issues

Stand: 15.09.2026, `main` @ `8be4466`.

Bis heute gab es **keine festgehaltene Reihenfolge**: keine `priority:`-Labels in
`.github/labels.yml`, keine Milestones, kein Roadmap-Dokument. Gearbeitet wurde faktisch nach der
GitHub-Standardansicht (zuletzt aktualisiert), und die ist eine Rückkopplung — sie zeigt oben, was
die eigene letzte Welle angefasst hat. Dieses Dokument ist der erste Bezugspunkt, von dem eine
Abweichung überhaupt messbar ist.

## Kriterien, in dieser Rangfolge

1. **Schwere** — Wirkung, *wenn* der Defekt ausgelöst wird. Die Auslösewahrscheinlichkeit steht
   als eigene Spalte daneben, statt in die Schwere hineingerechnet zu werden; so bleibt sie
   nachprüfbar und umsortierbar.
2. **Layer** — niedriger zuerst (L2 → L3 → L4), nach dem Modell aus
   `docs/architecture/arc42-CanKit.Pro.md`. Begründung unten, mit einem Beleg aus diesem Review.
3. **Produkt** — CANopen, J1939, UDS. Die beiden L3-Transporte tragen je ein L4-Produkt und erben
   dessen Platz: `J1939Tp` → J1939, `IsoTp` → UDS.

### Schwere-Raster

| | |
|---|---|
| **S1** | Die Bibliothek lügt: falsche Daten als gültig geliefert, Erfolg für eine nicht zugestellte Nachricht gemeldet, oder ein Peer erschöpft den Prozess. Die Anwendung kann es nicht bemerken. |
| **S2** | Korrekte Nutzung schlägt fehl: ein konformer Peer kommt nicht durch, oder ein dokumentiertes Feature ist unbrauchbar. Sichtbar als Fehler — kostet Zeit, nicht Korrektheit. |
| **S3** | Nicht konform, aber verkraftbar: Peers sehen etwas Falsches und arbeiten weiter. |
| **S4** | Nach innen: Leaks, Allokationen, API-Form. Für den Peer unsichtbar. |

### Warum Layer vor Produkt, mit Beleg

Nicht aus Prinzip, sondern weil dieses Review einen Fall gefunden hat: **#28 ist ohne die ISO-TP-
Arbeit nicht sauber zu schließen.** UDS misst P2 gegen `received.ArrivalTimestamp`
(`UdsClientImpl.cs:833`); diesen Stempel setzt ISO-TP in `EmitPdu` — und zwar mit der Ankunft des
**letzten** CF, ausdrücklich so kommentiert (`IsoTpChannel.cs:1018-1020`). P2 beim ersten Frame zu
beenden verlangt also, dass L3 den FF-Stempel überhaupt herausgibt. Ein Fix, der allein in
`UdsClientImpl` bleibt, müsste raten.

Das ist genau das Muster, das die Layer-Regel abfängt: eine L4-Korrektur, die eine L3-Lücke
umgeht, wird später zurückgebaut.

**Die eine Stelle, an der (2) und (3) gegeneinander laufen:** in S1 schiebt der Layer die beiden
L3-Pakete vor CANopen, obwohl CANopen das erste Produkt ist. Wer Produkt vor Layer stellen will,
sortiert S1 zu #18, #40, #30, #32, #25, #26, #27 um. Der Rest der Liste ändert sich nicht.

## Vorbemerkung: die Tickets sind nachgeprüft, nicht abgeschrieben

Alle 20 Produktdefekte stammen aus dem Review vom 10.09. und tragen Zeilennummern von `main` @
`8ed80f6`. Diese Reihenfolge wäre wertlos, wenn sie auf fünf Tage alten Befunden stünde, also wurde
jeder gegen den aktuellen Stand gelesen. Kein einziger Commit in `main` referenziert #17–#42
(`git log --grep`), aber Bot-Befunde auf fremden PRs haben trotzdem an vier Stellen gewirkt:

| Ticket | Was sich seit dem 10.09. geändert hat |
|---|---|
| **#26** | Teilweise erledigt. Es gibt jetzt ein Gate **vor** der Allokation, das mit `FC(OVFLW)` antwortet (`IsoTpChannel.cs:938`, Bugbot 3596212802). Offen bleibt genau die erste Anstrichmarke des Tickets: `MaxFdFirstFrameLength = 0xFFFF_FFFF` (`IsoTpFrameCodec.cs:42`), also ist `MaxPduLength` für FD weiterhin `int.MaxValue` und das Gate bindet nichts. Der Rest-Fix ist eine Option mit vernünftigem Default — deutlich kleiner als bei Ablage. |
| **#18** | Der **Abort**-Pfad vergleicht `(index, subindex)` inzwischen (`CanOpenNode.cs:1497`). Die Erfolgsantworten nicht — weder `ScsDownloadInitAck` noch der Upload-Zweig (`:1515`, `:1555`, `:1564`) —, und die Client-Deadline wird weiterhin *vor* jeder Zuordnung nachgezogen (`:1505-1511`). Der Kern des Tickets steht. |
| **#39** | Zweite Hälfte verengt, nicht weg. Die Initiate-Heuristik verlangt jetzt `seq != NextExpectedSeq && LooksLikeSdoClientInitiate(cs)` (`CanOpenNode.SdoBlock.cs:537-538`). Ein Segment *in* der Reihenfolge ist damit sicher. Die beiden Hälften des Tickets greifen aber ineinander: nach einem verlorenen Segment ist jedes folgende außer der Reihe — und ein Segment mit Seqno 0x21 sieht dann aus wie `CcsDownloadInitSegmented` und tötet die Sitzung. Die Restgefahr sitzt genau im Schnitt der beiden Hälften. |
| **#40** | Der Duplikat-Fall scheitert jetzt laut statt leise: `staged.Count != count` beim Sub0-Commit (`CanOpenNode.PdoMapping.cs:157`) fängt das zweite Schreiben auf Sub1 als `LengthTooHigh`. Die Vertauschung bleibt still — `staged.Add(raw)` (`:247`) hängt weiter an, ohne den Subindex anzusehen. Dazu neu sichtbar: Sub0 wird als U32 **gelesen** (`:117`) und als U8 **geschrieben** (`:131`) — Lese- und Schreibpfad widersprechen sich über dieselbe Subindex-Breite. |

Und eine Korrektur am Ticket selbst statt am Code:

- **#25** — der Defekt reproduziert (`SendNextConsecutiveFrame` prüft nur `_tx is null`,
  `IsoTpChannel.cs:581`; `ScheduleNextCf` verwirft das Handle, `:808-818`; kein
  `ReferenceEquals(_tx, tx)`). Der im Ticket beschriebene **Mechanismus stimmt aber nicht**: der
  Callback liest `_tx`, also die *neue* Sitzung, und baut den CF mit deren `NextSn` — nicht „mit
  der alten Sequenznummer“. Was passiert, ist ein CF der neuen Übertragung *außer der Reihe*, der
  `Offset` und `NextSn` zusätzlich vorrückt. Wer nach dem Ticket-Text fixt, sucht an der falschen
  Stelle; der Text gehört vor dem Fix korrigiert.

Die übrigen 15 reproduzieren unverändert, mit aktuellen Fundstellen in der Tabelle unten.

## Die Reihenfolge

`R` = Auslösewahrscheinlichkeit im Feld: **hoch** = im Normalbetrieb, **mittel** = bei bestimmter
Peer-/Anwendungsform, **niedrig** = braucht einen ungewöhnlichen Auslöser.

### S1 — die Bibliothek lügt

| # | Layer · Produkt | R | Fundstelle heute |
|---|---|---|---|
| **#30** | L3 · J1939-TP | hoch | `HandleRxTpCm(sa, da, payload)` nimmt `da` entgegen und benutzt es im ganzen Rumpf **nicht** (`J1939TpChannel.cs:393-501`). Ein globales RTS öffnet eine Sitzung auf jedem Knoten. |
| **#32** | L3 · J1939-TP | mittel | `TxSessionKey(destinationAddress, pgn)` (`:1046`). Zwei parallele BAM an dasselbe Ziel verschränken ihre DT — und beide melden Erfolg. |
| **#25** | L3 · ISO-TP | mittel | `:581`, `:808-818`. Mechanismus im Ticket falsch beschrieben, siehe oben. |
| **#26** | L3 · ISO-TP | mittel | `IsoTpFrameCodec.cs:42`. Rest-Fix: `MaxReceivePduLength`-Option. |
| **#27** | L3 · ISO-TP | hoch | `HandleRxConsecutiveFrame` kopiert `min(remaining, available)` ohne CAN_DL-Prüfung (`:1000-1010`); ein kurzer CF verschiebt alles danach. |
| **#18** | L4 · CANopen | mittel | `CanOpenNode.cs:1505-1511`, `:1515`, `:1555`. |
| **#40** | L4 · CANopen | niedrig | `CanOpenNode.PdoMapping.cs:247`. Stille Hälfte: vertauschte Schreibreihenfolge. |

### S2 — korrekte Nutzung schlägt fehl

| # | Layer · Produkt | R | Notiz |
|---|---|---|---|
| **#31** | L3 · J1939-TP | mittel | `session.ArmTr()` für CTS → erstes DT (`J1939TpChannel.cs:463-464`). Der Kommentar beruft sich auf FR-TP-032 — der Fix muss den Anforderungstext mitziehen, nicht nur die Konstante. |
| **#17** | L4 · CANopen | hoch | `RearmBlockServer` hat genau **eine** Aufrufstelle (`CanOpenNode.SdoBlock.cs:706`, Download). Zwei Zeilen. |
| **#38** | L4 · CANopen | mittel | Server: `(cs & 0xE0) == CcsDownloadInitExpeditedBase` fängt auch 0x20 (`CanOpenNode.cs:1037`). Client: `cs == ScsUploadInitSegmented` trifft 0x40 nicht (`:1564`). |
| **#39** | L4 · CANopen | hoch | NACK je Segment außer der Reihe (`SdoBlock.cs:673-683`); ein verlorenes Segment ist der Normalfall. |
| **#41** | L4 · CANopen | mittel | `ConfigureRpdo`/`ConfigureTpdo` prüfen `pdoIndex` und `mapping`, die `cobId` nicht (`CanOpenNode.cs:363-401`). |
| **#43** | L4 · CANopen | mittel | Erste Hälfte in PR #122 erledigt. Offen: producer-seitiges Life Guarding — `feat`, eigene Designrunde (`NodeGuarding.cs:202` verweist darauf). |
| **#34** | L4 · J1939 | hoch | Kein Handler beantwortet eine eingehende Request für 0xEE00; nur `RequestPgnAsync` (ausgehend, `J1939NodeImpl.cs:812`). |
| **#35** | L4 · J1939 | mittel | Der Arbitrary-Fallback liegt nur im `_pendingClaim`-Pfad (`:528`). Der Pfad „schon geclaimt, dann entthront“ geht direkt auf `CannotClaim` (`:569-577`). |
| **#121** | L4 · J1939 | niedrig | Aus dieser Welle, durchgerechnet. |
| **#28** | L4 · UDS | hoch | **Hängt an der ISO-TP-Runde**, siehe oben. |
| **#29** | L4 · UDS | mittel | `if (seedLen == 0) return;` (`UdsClientImpl.cs:312`). |

### S3/S4 — konform-nah und nach innen

L2 zuerst, dem Layer-Kriterium folgend:

| # | Layer · Produkt | Notiz |
|---|---|---|
| **#55** | L2 · Addressing | Sammelticket. Einziges L2-Produktticket, führt die Gruppe allein per Layer an. |
| **#33** | L3 · J1939-TP (S3) | `SessionAlreadyOpen = 7` (Norm: 1), `UnexpectedCtsSequenceNumber = 5` (Norm: 7), `ReceiverAbort = 250` (reserviert) — `J1939TpAbortReason.cs:42,36,45`. |
| **#36** | L3 · J1939-TP (S4) | `ct.Register(...)` ohne Rückgabewert (`J1939TpChannel.cs:193`). |
| **#56** | L3 · ISO-TP | Sammelticket. |
| **#42** | L4 · CANopen (S4) | `Entries => _entries.ToArray()` (`Pdo/PdoMapping.cs:107`). |
| **#59** | L4 · CANopen | Sammelticket. |
| **#58** | L4 · J1939 | Sammelticket. |
| **#99** | L4 · J1939 | Zurückgestellt bis eine echte 3-Bit-SPN vorliegt. |
| **#57** | L4 · UDS | Sammelticket. |

### Eigene Spur: Testinfrastruktur und CI

Diese sechs konkurrieren nicht um dieselben Plätze — sie ändern die **Kosten** jedes Fixes oben.
Innerhalb der Spur:

1. **#92** — die wackeligen Uhrentests treffen jede einzelne PR der Liste oben.
2. **#94** — Restarbeit klein, und das Ticket trägt den Beleg: die fehlende Zwei-Welten-Naht hat
   zwei Regressionen verdeckt.
3. **#114** — Schritt 2, die 29 B+C-Sites mit vorhandener Naht.
4. **#52** — normative Negativtests; wächst mit jedem Fix oben mit.
5. **#118** — gemessen (Kliff zwischen 40 und 25 ms), Fix bewusst nicht gebaut.
6. **#106** — liegt beim Maintainer: die Merge Queue ist in `ci.yml` verdrahtet, aber am Branch
   nicht eingeschaltet.

## Daraus: PR-Zuschnitt

Nach der Gruppierungsregel in `CLAUDE.md` — gleiche Dateien zusammen, verschiedene Pakete
getrennt, eine PR nach der anderen.

| | PR | Tickets | Dateien |
|---|---|---|---|
| 1 | J1939-TP: Sitzungsidentität | #30, #32 | `J1939TpChannel.cs` |
| 2 | ISO-TP: Empfang und CF-Lebensdauer | #27, #26, #25 | `IsoTpChannel.cs`, `IsoTpFrameCodec.cs` |
| 3 | CANopen: SDO-Korrektheit | #18, #17, #38, #39 | `CanOpenNode.cs`, `CanOpenNode.SdoBlock.cs` |
| 4 | CANopen: PDO-Mapping | #40, #41, #42 | `CanOpenNode.PdoMapping.cs`, `Pdo/PdoMapping.cs` |
| 5 | J1939: Adressverwaltung | #34, #35, #121 | `J1939NodeImpl.cs` |
| 6 | UDS: P2 und SecurityAccess | #28, #29 | `UdsClientImpl.cs` — **nach PR 2** |
| 7 | J1939-TP: Kleinkram | #31, #33, #36 | `J1939TpChannel.cs`, `J1939TpAbortReason.cs` |

PR 1 und PR 7 fassen beide `J1939TpChannel.cs` an und dürfen sich deshalb nicht überlappen; die
Trennung ist der Zuschnitt nach Schwere, nicht nach Datei. PR 6 hängt sachlich an PR 2.

Release-Wirkung: PR 1–7 sind durchgehend `fix`, also Patch. Die zweite Hälfte von #43 ist `feat`
und gehört nicht in eine dieser PRs.

## Was diese Reihenfolge sagt, das die bisherige Praxis nicht sagt

Nach diesem Raster steht kein einziges Testticket vor einem S1-Produktdefekt — die Testspur läuft
daneben, nicht davor. Gemessen daran ist die Lage: **die sieben S1-Defekte sind seit dem 10.09.
unangetastet.** Kein Commit in `main` referenziert eines von ihnen (`git log --grep`), während in
denselben fünf Tagen sieben PRs gemerged wurden. Die vier Stellen, an denen sich trotzdem etwas
bewegt hat, kamen von Bot-Befunden auf PRs zu anderen Themen — also zufällig, nicht geplant.

Das ist keine Anklage, sondern der Nullpunkt: ab jetzt ist jede Abweichung von dieser Liste eine
Entscheidung, die man als solche sehen kann.
