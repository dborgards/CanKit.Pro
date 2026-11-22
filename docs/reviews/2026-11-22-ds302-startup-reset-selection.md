# CiA 302: nicht gelistete Geräte im Startup-Reset

Stand: 22.11.2026; Basis `main` bei `ae240751`, Merge von
[#595](https://github.com/dborgards/CanKit.Pro/pull/595). Dieser Schritt behandelt
[#594](https://github.com/dborgards/CanKit.Pro/issues/594).

## Norm und Abgrenzung

Quelle ist die bereitgestellte CiA 302-2 v4.1.0. Identität und Prüfsumme stehen im
[Normquellen-Nachtrag](2026-11-21-ds302-boot-followup.md). Erneut gelesen wurden
§3.1 d–e und §6.3, insbesondere Tabelle 22. Die Einordnung der vorgeschriebenen
Wahl-Broadcasts steht im [vorigen Review](2026-11-22-ds302-election-reset.md).

§3.1 d schließt bei individuellen Startup-Resets ausdrücklich Node-IDs außerhalb
von 1F81h ein und nimmt den Master selbst aus. Der bisherige Code beschränkte die
Einzel-Resets auf zugewiesene Slaves. Ein kalter Wahl-Broadcast kann das verbergen;
bei warmer Übernahme bleibt ein nicht gelistetes Gerät hingegen ohne Reset und
ohne dadurch ausgelöste Boot-up-Meldung.

Der bestehende individuelle Pfad bleibt erhalten. Er nimmt Keep-alive-Einträge
konservativ vom initialen Durchlauf aus, auch ohne bekannte Zustandsmeldung. Die
spätere Zustandsprüfung für zugewiesene bootfähige Keep-alive-Slaves bleibt das
separate Opt-in `EnableBootKeepAlive`: Ein bestätigter nicht-operationaler Zustand
erfordert dort den individuellen Reset. Dieser Schritt führt weder eine neue
Broadcast-Optimierung noch eine pauschale Zustandsabfrage aller Node-IDs ein.
Die Wahl-Resets nach §3.7 d und §5.5.2 bleiben unverändert.

## Änderung

FR-CO-032 umfasst im initialen Einzel-Reset jetzt IDs 1–127, ausgenommen die eigene
ID und Keep-alive-Einträge. Boot-Unterdrückung durch 1F80h Bit 3 bleibt erhalten.
Nicht gelistete Geräte werden dadurch nicht zugewiesen, konfiguriert oder gestartet
und erhalten keine Slave-Boot-Ergebnisse. Der vorhandene optionale Boot-up-Handler
kann ihre tatsächliche Boot-up-Meldung an die Anwendung weiterreichen.

Die zusätzlichen Sendungen verwenden die bestehende geordnete NMT-Warteschlange.
Auch initiale Resets für ankündigungsbasierten Slave-Boot erhalten das Token der
Boot-Generation; Rollenverlust, Stop, Halt und Neustart verwerfen noch eingereihte
Sendungen. Ein beim Adapter befindlicher Frame kann bereits gesendet worden sein.
Der bestätigte Reset-/Retry-Pfad aktiv gebooteter Slaves bleibt unverändert.
Fehler eines Resets an ein nicht zugewiesenes Ziel werden über die bestehende
Transportfehler-Meldung sichtbar; sie erzeugen kein Slave-Ergebnis und verhindern
nicht das Abarbeiten der übrigen NMT-Warteschlange. Ein Abbruch ist kein
Transportfehler. Die öffentliche API ändert sich nicht.

## Regressionen

Die neuen Tests in `CanOpenBootIdentityTests.StartupResets.cs` beginnen mit einem
lokal adressierten Reset eine warme Wahl und löschen anschließend den Frame-Log.
Ein vorangegangener kalter Broadcast kann den Befund deshalb nicht verdecken.
Gegen die unveränderte Basis scheiterten vier der ersten fünf Fälle: fehlende
unlisted-Ziele und die drei darauf aufbauenden Abbruchfälle; der Suppress-Fall
bestand bereits.

Nach der Korrektur bestanden alle **502 Boot-/Flying-Master-Fälle** im gezielten
Lauf. Neue Fälle prüfen die gesamte Zielmenge einschließlich Rand-IDs, tatsächliches
Boot-up eines virtuellen ungelisteten Peers, Erhalt eines Operational-Keep-alive-Peers,
NMT-Reihenfolge bei gehaltener Bestätigung, Sendefehler sowie Stop/Rollenverlust/Halt/
Dispose. Protokollfristen verwenden virtuelle Zeit; Actor-/Sendebarrieren warten
auf reale Fortsetzungen.

Drei frühere Tests wurden an die jetzt sichtbaren Startup-Resets angepasst: Die
beiden Claim-/Standby-Zähltests konfigurieren den Standby ausdrücklich als
Keep-alive und behalten ihre strikten Zähl-Assertions. Beim Entfernen einer
Slave-Zuweisung erwartet der Keep-alive-Lifecycle-Test jetzt den Reset der dann
nicht mehr gelisteten ID, weiterhin aber keinen Start oder fortgesetzten Slave-Boot.

Vier getrennte Produktionsmutationen kompilierten und scheiterten an den neuen
Tests: Entfernen des Selbstausschlusses, Entfernen des Keep-alive-Ausschlusses,
Ersetzen des Boot-Tokens durch das Default-Token sowie direktes Senden an der
NMT-Warteschlange vorbei. Die Abbruchmutation scheiterte bei Stop, Rollenverlust
und Halt; Dispose blieb durch seine zusätzliche Transportsperre geschützt.
Nach jedem Lauf wurde die unveränderte Produktionsquelle wiederhergestellt.

## Vollständiger lokaler Prüflauf

Die vollständige `net10.0`-Suite bestand nach Wiederherstellung der Produktionsquelle
mit **5972 bestandenen Tests**, ohne Fehler oder übersprungene Fälle. Der
Release-Build mit `CI=true` hatte keine Warnungen oder Fehler. Die Cobertura-Auswertung
gegen `ae240751` über sämtliche Klassen/Zustandsautomaten traf **3/3 geänderte
ausführbare Einträge** und **6/6 Zweigbedingungen**.

Formatprüfung, frisches Pack samt Validierung, net48-Kompilierung, Requirements-
Traceability sowie generierte API-Dokumentation und `mkdocs build --strict`
bestanden ebenfalls. Die net48-Laufzeitprüfung erfolgt zusätzlich in Windows CI.

## Reviewkorrektur: Abbruch ausdrücklich prüfen

[CodeQL meldete](https://github.com/dborgards/CanKit.Pro/pull/596#discussion_r4658690551)
den leeren `catch` im Abbruchtest. Statt eine `OperationCanceledException` nur zu
akzeptieren, verlangt der Test sie jetzt ausdrücklich von der alten Warteschlange.
Damit ist auch ein unerwartet normaler Abschluss ein Testfehler. Alle vier Fälle
bestanden mit dieser stärkeren Assertion; die Produktionslogik blieb unverändert.
Die erneut kompilierte Mutation ohne Boot-Token ließ nun alle vier Fälle scheitern,
auch Dispose: Die Transportsperre allein ersetzt keinen Abbruch der alten Queue.
Anschließend wurde die Produktionsquelle wiederhergestellt.
Alle oben aufgeführten lokalen Gates bestanden erneut nach der Reviewkorrektur;
Testanzahl und vollständige Abdeckung der geänderten Produktionszeilen und
Zweigbedingungen blieben unverändert.

Die Tests verwenden den virtuellen CAN-Bus. Hardware-Interoperabilität und
vollständige CiA-302-Konformität werden nicht behauptet.
