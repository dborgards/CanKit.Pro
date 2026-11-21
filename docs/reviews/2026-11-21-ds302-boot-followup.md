# CiA 302: Normquelle und nächster Boot-Baustein

Stand: 21.11.2026. Ausgangspunkt ist `main` bei `77bac569`, der Merge von
[#582](https://github.com/dborgards/CanKit.Pro/pull/582). Dieser Nachtrag dokumentiert die
Normquelle und die Erweiterung des vorhandenen Bootpfads. Er ist kein abgeschlossener
Konformitätsaudit der gesamten CiA-302-Reihe.

## Quelle für Teil 2

Der Maintainer hat **CiA 302-2, Version 4.1.0, CANopen additional application layer functions,
Part 2: Network management**, Draft Specification Proposal, **02.02.2009**, bereitgestellt.
Dateiname der gelieferten Datei: `302_2v04010007.pdf` (im Upload mit Präfix `7-`). Titelblatt,
Versionsnummer und Datum wurden am Dokument geprüft. SHA-256 der gelieferten PDF:

```text
489f57b9b7b9ce9ceaf22216099ff7f58d719be744b4987a7c2e0b8d240ee84a
```

Das ist der separat identifizierbare Teil 2 der in ADR-13 genannten Ausgabe, nicht der
monolithische Entwurf `dsp30232.pdf` von 2003. Damit ist die Quellenfrage aus
[#560](https://github.com/dborgards/CanKit.Pro/issues/560) beantwortet. Die normative PDF
bleibt außerhalb des Repositorys. Der vollständige Audit von Wahlverfahren, Bootablauf,
Fehlerüberwachung und Objektsemantik bleibt Arbeit; die Verfügbarkeit des Texts ersetzt ihn nicht.

## Abgegrenzter Schritt: 1000h vor automatischem Start

§3.4, Teil 1, Schritt c (S. 12) verlangt den Upload von `1000h` vor den Vergleichen in d/e.
`1F84h = 0` schaltet nach §6.6 den Vergleich aus. Es schaltet in diesem Normablauf nicht den
Upload aus. #582 hat absichtlich nur eine optionale Identitätsprüfung eingeführt: Ohne
Erwartungswerte gab es weiterhin keine SDOs, und eine alleinige Vendor-ID-Erwartung las nur
`1018h:01`. Das ist der dokumentierte Vertrag von FR-CO-064.

Die neue Option `CanOpenNodeOptions.RequireBootDeviceTypeUpload` ergänzt den Upload, ohne den
Standardvertrag umzudeuten. Aktiviert liest die Prüfung zuerst genau einmal `1000h:00`, auch
ohne Sollwert. Vier Antwortbytes sind erforderlich. Nichtnull-Sollwerte werden weiterhin
verglichen; die übrigen Nullwerte verursachen keine zusätzlichen Uploads. Der gemeinsame
Peer-SDO-Kanal, die gebundene Beschreibung, Bootgeneration, Fehlerreaktion und Ergebnisse
werden wiederverwendet. Kopierte Optionen behalten die Einstellung.

Der Upload wird wie die bisherige Identitätsprüfung nach einer Ankündigung begonnen. Das ist
ein Erreichbarkeitsnachweis vor Start, kein aktiver Suchlauf und noch kein vollständiger
§3.4-Boot. Für den gleichzeitigen Start werden einzelne verifizierte Slaves adressiert: Ein
Broadcast könnte einen noch ungeprüften Teilnehmer erreichen. Diese Bibliotheksentscheidung
führt die Schutzregel von #582 fort. `StartRequested` bezeichnet weiterhin das Einreihen des
NMT-Kommandos, keine entfernte Startbestätigung.

Nachweis: `CanOpenBootIdentityTests.Reachability.cs` für Upload, Reihenfolge, Vergleich,
Fehlerdaten, Beschreibungstor, Timeout, Kanalbelegung, Rollenverlust, ausgeschlossene Slaves,
Operational-Ankündigung und Optionskopien; die übrigen Dateien dieser Testklasse halten den
bestehenden Vertrag fest. Die Anforderung steht als FR-CO-065 in der SRS.

## Verbleibende Arbeit in Teil 2

| Normstelle | Bestehender Ansatz / verbleibender Unterschied |
| --- | --- |
| §3.2–3.4, Start und Wiederholung der Slave-Bootprozesse | `BeginBootUp` setzt die Liste zurück; `NoteSlaveNmtState` stößt die Prüfung nach einer Ankündigung an. Aktive Erreichbarkeitsabfrage ohne Ankündigung und normgerechte Wiederholungen bis zur Bootfrist fehlen. |
| §3.4, Identität; §6.6–6.10 | #582 vergleicht die fünf Erwartungsfelder, einschließlich Major-Gleichheit und Mindest-Minor. Der hier ergänzte Upload deckt Schritt c im bestehenden Ankündigungspfad ab. |
| §3.4 Teil 2 / §3.6, Keep-alive | Der vorhandene Pfad verzichtet bei Keep-alive auf den Reset und nutzt Ankündigungen. Das normierte Prüfen eines aktuellen Heartbeats bzw. die ersatzweise Guarding-Abfrage ist damit nicht vollständig umgesetzt. |
| §3.5, Konfiguration | Versionsprüfung `1F26h`/`1F27h` gegen `1020h`, Restore und Konfigurationsdownload sind nicht in den Boot-Manager integriert. DCF-Laden ist keine Ausführung dieses Ablaufs. Teil 3 (Konfigurations-/Programmdownload) ist hierfür mit abzugrenzen. |
| §4.1, Fehlerüberwachung starten | Heartbeat- und Guarding-APIs existieren. Der Boot-Manager führt daraus nicht automatisch den vorgeschriebenen Startprozess aus; Guarding-Zeit und Retry-Faktor in `1F81h` werden bisher nur gespeichert. |
| §3.7 / §5.3 / §6.12, Flying Master | Die vorhandene Wahl folgt bislang öffentlichen Beschreibungen. Ein vollständiger Vergleich aller Zustandsübergänge, Zeitbedingungen und Drahtformate mit diesem Volltext steht aus. |
| §6, Objektsemantik | Die vorhandenen Objekte allein belegen keine Konformität. Insbesondere unterstützte Bits, nicht unterstützte Funktionen, Start-/Fehlerreaktionen und Rückgabewerte sind noch systematisch abzugleichen. |

Als nächstes folgt der aktive Slave-Bootprozess mit explizitem Zustand und Wiederholungen,
damit ein erreichbarer Slave ohne Heartbeat nicht bereits vor der SDO-Prüfung hängen bleibt.
Danach Konfigurationsprüfung/-download und automatischer Start der Fehlerüberwachung. Änderungen
an ausgelieferten Standardabläufen müssen den bestehenden Vertrag und SemVer berücksichtigen.
Die übrigen Teile 302-1 und 302-3 bis -9 sind durch diese Arbeit nicht als umgesetzt oder
konform bewertet; ihre vorhandenen Teilfunktionen brauchen jeweils einen eigenen Abgleich.
