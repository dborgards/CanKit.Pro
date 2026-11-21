# CiA 302: Error-Control-Start im Slave-Boot

Stand: 21.11.2026. Basis ist `main` bei `fa16f7d4`, Merge von
[#587](https://github.com/dborgards/CanKit.Pro/pull/587). FR-CO-070 ergänzt nach dem
[automatischen Konfigurationsdownload](2026-11-21-ds302-automatic-configuration.md)
die Stufe „Start error control“ vor Safety und Start.

Gelesen: CiA 302-2 v4.1.0 §4.1, Abbildung 11, sowie §6.3, 1F81h.
§4.1 startet den Heartbeat-Timer mit Eintritt in den Prozess; Guarding wird ohne
Warten auf eine erste Antwort begonnen. §6.3 gibt dem nichtnull-konfigurierten
Heartbeat Vorrang und deaktiviert Guarding sowohl bei Guard Time null als auch
Retry Factor null. Die gelieferten Normdateien werden nicht ins Repository kopiert;
Herkunft und Prüfsummen stehen im [Boot-Nachtrag](2026-11-21-ds302-boot-followup.md).

## Verhalten und Bibliotheksentscheidungen

`EnableBootErrorControl` ist standardmäßig false und bleibt in Optionskopien erhalten.
Es aktiviert weder Identitätsprüfung noch Konfigurationsdownload. Die Stufe gilt für
zugeordnete Slaves mit Boot-Bit, sofern automatische Starts nicht unterdrückt sind.
Auch eine Operational-Meldung muss diese aktivierte Prüfung bestehen. Starts erfolgen
einzeln, auch zum simultanen Startzeitpunkt.

Lokales 1016h bestimmt das Heartbeat-Fenster. Die Prüfung akzeptiert einen klassischen
Einbyte-Datenframe mit Zustand 4, 5 oder 127; Boot-up, RTR, FD, Extended, falsche Länge,
Guarding-Toggle und reservierte Zustände genügen nicht. Der positive Host-Empfangszeitpunkt
muss im neu begonnenen Zeitfenster liegen. Ein benutzerdefinierter Bus-Service muss dieselbe
monotone Zeitbasis verwenden. Die Verarbeitung bleibt Actor-basiert: Eine bereits ausgeführte
Timeout-Aktion wird durch eine später verarbeitete Empfangsaktion nicht zurückgenommen.

Ohne Heartbeat startet Guarding aus 1F81h. Retry Factor N wird als N zusätzliche
fehlgeschlagene Guard-Time-Intervalle nach dem ersten Ausfall umgesetzt; erst danach
erscheint das bestehende `NodeGuardingTimeout`-Ereignis. Eine akzeptierte Antwort setzt den
Vorrat zurück. Dieses Intervallmodell ist die konkrete Umsetzung der in §6.3 verlangten
Wiederholungen; es verwendet nicht den entfernten 100Dh-Life-Time-Factor. Automatische
Consumer führen im bestehenden Ereignis daher LifeTimeFactor 1.

Die Option ersetzt bei Eintritt vorhandenes Guarding des ausgewählten Slaves.
Boot-eigene Poller und noch wartende Sendungen enden bei Reset, Rollenverlust, Halt,
neuem Boot und Disposal; eine später ausdrücklich installierte Consumer-Ersetzung bleibt
anwendungseigen. Abgeschlossene erfolgreiche Boots behalten ihre Poller für den laufenden
Betrieb. Relevante Änderungen von 1016h beginnen einen neuen Boot; unveränderte wirksame
Werte und Einträge nicht betroffener Slaves tun dies nicht. Der kalte Master-Reset bewahrt
1016h einschließlich des Zählers. Ein verworfener erzwungener Reset erhält beim Fortsetzen
ein frisches Fenster.

`WaitingForErrorControl`, `ErrorControlReady` und `ErrorControlFailed` ergänzen die
unveränderlichen Boot-Snapshots. Heartbeat-Fehler melden lokalen Index 1016h und
`SlaveErrorControlFailed`. Pflichtfrist 1F89h, Pflichtfehler, optionale Fehler und
Hintergrundwiederholungen folgen der vorhandenen Boot-Strategie.

## Umfang

Dies ist §4.1 als optionale Stufe im bestehenden Boot-Ablauf. Der Keep-alive-Ablauf nach
§3.6, der laufende Boot-Fehlerhandler nach §4.2 und automatische Schreibzugriffe auf
entfernte 1017h/100Ch/100Dh gehören nicht dazu. Allgemeine Heartbeat-/Guarding-Ereignisse
bleiben verfügbar. Vollständige DS302-Konformität oder Hardware-Interoperabilität wird
nicht behauptet.

## Prüfung

Tests liegen in `CanOpenBootIdentityTests.ErrorControl.cs`, `.ErrorControlLifecycle.cs`,
`.ErrorControlSends.cs` und `CanOpenSafetyBootUpTests`. Sie verwenden den virtuellen Bus,
eine gemeinsame manuelle Uhr und den vorhandenen Actor-/Send-Witness. Guarding-Intervalle
werden einzeln vorgerückt, damit jede erneute Deadline tatsächlich eingeplant wird.

Die API-Baseline stammt aus der erzeugten `.received.txt`.

Gezielte Produktionsmutationen werden einzeln eingebaut und vollständig zurückgenommen.
Die folgenden Varianten kompilieren und lassen jeweils den zugehörigen Test fehlschlagen:

| Mutation | Test |
| --- | --- |
| Error-Control-Stufe übersprungen | `Boot_Error_Control_Requires_A_Fresh_Heartbeat_Before_Start_Or_Operational` |
| Empfang vor Fensterbeginn zugelassen | `Buffered_Or_Untimed_Heartbeat_Cannot_Approve_The_New_Boot_Window` |
| Boot-up als Heartbeat akzeptiert | `Malformed_Or_NonHeartbeat_Frames_Do_Not_Complete_Error_Control` |
| Automatisches Guarding übersprungen | `Guard_Time_And_Retry_Factor_Control_Automatic_Guarding` |
| Wiederholungsgrenze um ein Intervall verkürzt | `Boot_Guarding_Retry_Factor_Counts_Additional_Missed_Windows` |
| Wiederholungsvorrat bei Antwort nicht erneuert | `A_Guarding_Reply_Restores_The_Full_Retry_Allowance` |
| Eigene Guarding-Consumer beim Abbruch nicht entfernt | `Boot_Cancellation_Stops_Own_Guarding_But_Preserves_An_Explicit_Replacement` |
| Vorhandenes Guarding trotz Heartbeat-Vorrang belassen | `Heartbeat_Has_Priority_Over_Existing_And_Configured_Guarding` |

| Abbruch-Token beim wartenden Poll nicht weitergegeben | `Cancelled_Boot_Guarding_Cannot_Send_A_Queued_Poll` |

Abschließende lokale Prüfung dieses Stands:

- Release-Build der Solution mit `CI=true`: keine Warnungen oder Fehler.
- Gesamtsuite unter net10.0: 5.798 bestanden, keine übersprungenen oder fehlgeschlagenen Tests.
- `dotnet format --verify-no-changes`: unverändert.
- Frisches Release-Pack und `eng/verify-packages.py`: alle neun Pakete geprüft.
- net48-Testprojekt mit `CanKitProTestNetFrameworkLeg=true` und `CI=true`: kompiliert;
  die Windows-Laufzeitprüfung bleibt der CI vorbehalten.
- Anforderungsnachverfolgung: keine neuen ungetraceten Must-Anforderungen.
- Cobertura gegen Basis `fa16f7d4`, alle Klassen einschließlich generierter Zustandsautomaten:
  143/143 geänderte ausführbare Einträge getroffen; 145/152 Zweigbedingungen getroffen.
  Dies ist eine lokale Diff-Auswertung, kein vorweggenommenes Codecov-Ergebnis.
