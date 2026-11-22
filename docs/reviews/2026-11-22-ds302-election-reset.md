# CiA 302: Wahl-Resets und Masterausfall

Stand: 22.11.2026; Basis `main` bei `d69bc7f9`, Merge von
[#593](https://github.com/dborgards/CanKit.Pro/pull/593).
Quelle ist CiA 302-2 v4.1.0; Identität und Prüfsumme der bereitgestellten PDF stehen im
[Normquellen-Nachtrag](2026-11-21-ds302-boot-followup.md).
Gelesen wurden §3.1 d, §3.4/§3.6, §3.7, §5.4/§5.5 und §6.3 Tabelle 22.

## Korrektur des bisherigen Ausblicks

Der kalte Broadcast-Reset ist nicht schon wegen Keep-alive eine zu entfernende Lücke.
§3.7 d verlangt nach Power-on und erfolgloser Mastererkennung ausdrücklich Reset
Communication an Node-ID 0. §5.5.2 verlangt denselben Broadcast nach Ausfall des aktiven
Masters. Beide starten den Wahlprozess; die Reset-Auswahl beim nachfolgenden NMT-Startup
steht gesondert in §3.1 d. Die Keep-alive-Prüfung des einzelnen Slaves ist wiederum §3.4/§3.6.

| Pfad | Vorgabe der gelesenen Ausgabe | Befund dieses Schritts |
| --- | --- | --- |
| Power-on, kein Master gefunden | §3.7 d: Reset Communication, Node-ID 0; anschließend warmer Wahlablauf | Bereits vorhanden; bleibt auch mit `EnableBootKeepAlive` erhalten. |
| Heartbeat des aktiven Masters verloren | §5.5.2: Wahl durch Reset Communication, Node-ID 0, einleiten | Bisher wurde ohne diesen Reset neu gewählt; hier korrigiert. |
| Erzwungene Neuwahl | §5.4: aktiver Master fordert Reset für alle einzeln oder Node-ID 0 an | Vorhandener Broadcast-Pfad bleibt unverändert; keine vollständige Neubewertung. |
| Nachfolgendes NMT-Startup | §3.1 d: Reset-Auswahl anhand Operational/Keep-alive, einschließlich nicht gelisteter IDs und ohne den Master selbst | Separater Audit-/Implementierungsschritt, siehe [#594](https://github.com/dborgards/CanKit.Pro/issues/594). |

Diese Lesart verspricht keinen durchgehend resetfreien Flying-Master-Neustart oder
Failover für Keep-alive-Slaves. Der generelle Keep-alive-Schutz ersetzt hier nicht die
ausdrücklichen Wahl-Reset-Vorgaben. Softwareversionsverwaltung und vollständige
CiA-302-Konformität werden weiterhin nicht behauptet.

## Änderung an FR-CO-031

Ein Standby, dessen aktiver Master ausfällt, verwirft dessen aktive Identität und fordert
den Broadcast-Reset an. Während die Bestätigung aussteht, werden weder ein neuer
Erkennungszyklus noch Wahltrigger oder Masteranspruch ausgelöst. Wiederholte Heartbeat-
Timeouts stellen keinen zweiten Reset an. Erst die Bestätigung oder ein tatsächlich
empfangener Broadcast-Reset wendet den lokalen Reset an und startet die warme Wahl.

Der Pfad verwendet denselben Reset-Handshake wie der Kaltstart. Ablehnung, Abbruch oder
Transportfehler geben die Wahl nicht frei. Stop und Dispose verwerfen die ausstehende
lokale Reset-Fortsetzung. Eine bereits beim Treiber befindliche Sendung wird dadurch nicht
zurückgeholt. Die öffentliche API ändert sich nicht; die SRS und das Paket-README ersetzen
die bisherige Aussage, Masterverlust löse eine resetfreie warme Wahl aus.

## Prüfungen

Der bestehende Paar-Test `Losing_The_Active_Masters_Heartbeat_Resets_Before_A_Warm_Election`
verlangt den zuvor fehlenden zusätzlichen Broadcast. Die neuen Fälle in
`CanOpenFlyingMasterLossTests.cs` halten beziehungsweise verweigern die Bestätigung,
prüfen die Reihenfolge auf dem Bus, Stop/Dispose und die Abgrenzung zu Keep-alive.
Der Paar-Test und die vier anfänglichen Bestätigungsfälle scheiterten gegen die Basis:
der Reset fehlte tatsächlich. Protokollfristen laufen über virtuelle Zeit; die vorhandenen
Actor-/Sendebarrieren warten auf reale Worker-Fortsetzungen.

Vier kompilierende Produktionsmutanten wurden getrennt geprüft: Wahlbeginn vor
Reset-Bestätigung, Freigabe trotz fehlgeschlagenem Reset, Unterdrücken des kalten Resets
bei Keep-alive sowie Anwenden der Reset-Fortsetzung nach Stop. Die erste Mutation wurde
anfänglich nicht erkannt: der Test prüfte nur Trigger/Anspruch, der einzelne Zeitsprung
ließ jedoch bereits die Erkennung anlaufen. Nach Ergänzung des Rollenzustands und des
Erkennungsframes scheiterte auch diese Mutation. Die übrigen drei scheiterten an ihren
zugeordneten Assertions. Nach jedem Lauf wurden die Originalquellen wiederhergestellt.

Der abschließende lokale Prüflauf bestand:

- Release-Build mit `CI=true`: keine Warnungen oder Fehler.
- Gesamtsuite `net10.0`: **5953 bestanden**, keine Fehler oder übersprungenen Tests.
- Formatprüfung, frisches Pack samt Paketvalidierung und net48-Kompilierung bestanden.
- Requirements-Traceability: keine neuen ungetracten Must-Anforderungen.
- Generierte API-Dokumentation und `mkdocs build --strict` bestanden.
- Cobertura gegen `d69bc7f9`, über sämtliche Klassen/Zustandsautomaten:
  **15/15 geänderte ausführbare Einträge** getroffen, keine geänderten Zweigbedingungen.

Die net48-Laufzeitprüfung erfolgt zusätzlich in Windows CI. Die Tests verwenden den
virtuellen CAN-Bus; Hardware-Interoperabilität wurde hier nicht gemessen.

## Reviewkorrektur: Sperre nach fehlgeschlagenem Reset

[Bugbot meldete](https://github.com/dborgards/CanKit.Pro/pull/595#discussion_r4658468295),
dass das Löschen der Sendesperre nach einem fehlgeschlagenen Reset die normalen
Wahltelegramm-Handler wieder freigab. Trigger, Force und Masteransprüche konnten so
ohne den erforderlichen Reset einen anderen Wahlzustand herstellen.

Acht kontrollierte Fälle scheiterten gegen `18eb142a`: vier Arten von Steuertelegrammen,
jeweils mit tatsächlichem Reset beziehungsweise erneutem `StartFlyingMaster` als späterem
Wiederanlauf. Der Masterverlust hält jetzt eine gesonderte Reset-Anforderung fest, die
ein Sendefehler nicht löscht. Die Bereinigung bei tatsächlichem NMT-Reset oder explizitem
Stop/Start gibt sie frei. Das verändert den bestehenden kalten oder erzwungenen Reset-Pfad
nicht; die Sperre wird nur bei Masterverlust gesetzt.

Die Wiederanlauffälle prüfen zusätzlich, dass ein späterer besserer Master wieder
angenommen wird. Eine kompilierende Mutation, die das Löschen der neuen Sperre aus
`StopFlyingMasterCore` entfernt, ließ alle acht Fälle an genau dieser Wiederfreigabe
scheitern. Die Produktionsquelle wurde anschließend wiederhergestellt.

Der erneute vollständige `net10.0`-Lauf bestand mit **5961 bestandenen Tests**, ohne
Fehler oder übersprungene Fälle. Die erneute Cobertura-Auswertung gegen `d69bc7f9`
erfasst **18/18 geänderte ausführbare Einträge** und **4/4 Zweigbedingungen**.
Auch Release-Build mit `CI=true`, Formatprüfung, frisches Pack samt Validierung,
net48-Kompilierung, Requirements-Traceability und strikter Dokumentationsbuild
bestanden erneut nach der Reviewkorrektur.
