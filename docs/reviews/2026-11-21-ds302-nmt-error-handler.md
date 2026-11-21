# CiA 302: Laufzeitreaktion auf NMT-Überwachungsfehler

Stand: 21.11.2026. Basis ist `main` bei `6181bf89`, Merge von
[#588](https://github.com/dborgards/CanKit.Pro/pull/588). FR-CO-071 ergänzt nach dem
[Error-Control-Start](2026-11-21-ds302-error-control.md) den Laufzeitpfad nach
CiA 302-2 v4.1.0 §4.2, Abbildung 12.

Gelesen: §4.2, §6.2 (1F80h, Tabellen 12/14) und §6.3 (1F81h, Tabelle 20).
Abbildung 12 unterscheidet den Pflicht-Slave mit „Stop all“ beziehungsweise „Reset all“
vom individuellen Reset mit anschließendem „Boot NMT slave“. Die Broadcast-Adresse ist
hier ausdrücklich null; der bisherige initiale Boot-Fehlerpfad bleibt unverändert.
Tabelle 20 erlaubt bei gelöschtem Boot-Bit einen Reset, untersagt aber Konfiguration und
automatischen Start nach Fehler oder Boot-up. Die gelieferten Normdateien werden nicht
ins Repository kopiert; Herkunft und Prüfsummen stehen im
[Boot-Nachtrag](2026-11-21-ds302-boot-followup.md).

## Verhalten und Umfang

`EnableNmtErrorHandler` ist standardmäßig false, bleibt in Optionskopien erhalten und
schließt das Verhalten von `EnableBootErrorControl` ein. Identitäts- und
Konfigurationsoptionen bleiben unabhängig. Laufzeitreaktionen gelten für zugeordnete
Slaves nach Abschluss ihrer automatischen Prüfungen (StartRequested oder AlreadyOperational)
sowie für anwendungseigene Slaves ohne Boot-Bit oder mit unterdrücktem Start.
Während des initialen automatischen Boots bleibt dessen bisherige Fehlerstrategie aktiv.
Dies ist ein begrenztes Inkrement, keine Behauptung vollständiger §4.2-/DS302-Konformität.
Der unabhängige Boot-up-Handler nach §4.3 und Keep-alive nach §3.6 bleiben offen.

Bei einem Pflicht-Slave hat 1F80h-Bit 6 Vorrang: Broadcast Stop. Andernfalls bewirkt Bit 4
Broadcast Reset Node. Beide halten die automatische Boot-Arbeit an; es folgt kein
automatischer Netzwerk-Neustart. Bei allen anderen geeigneten Fehlern erfolgt individueller
Reset Node. Die erneute Boot-Sequenz betrifft nur diesen Peer; erfolgreiche Prüfungen und
wartende Starts anderer Peers bleiben erhalten. Ein bereits wartender eigener Start wird
durch seinen Peer-Abbruch-Token ungültig.

Einzel-Recovery hält den Peer-SDO-Kanal über Reset und Boot-up. Sie verwendet denselben
bestätigten Reset-/Boot-up-Handshake wie der Konfigurationsdownload: klassischer Datenframe
mit genau einem Nullbyte, positiver Host-Übergabezeitpunkt des Resets und Empfang frühestens
zu diesem Zeitpunkt. Ein schnelles Boot-up während noch ausstehender Sendebestätigung wird
gemerkt; Freigabe erfolgt erst nach Bestätigung. `SdoTimeout` beginnt danach. Der gleiche
Kanal verhindert überlappende Reset-Listener eines noch auslaufenden alten Downloads.

Nach gültigem Boot-up werden aktivierte Identität, Konfiguration, Error Control und Safety
erneut geprüft. Eine alte Operational-Meldung kann keinen Schritt überspringen. Bei
anwendungseigenem Start endet die Recovery nach dem Boot-up ohne Konfiguration oder Start.
Ein verworfener erzwungener Master-Reset setzt die gehaltene Prüfung anschließend fort.

Nichtnull 1F89h begrenzt als Bibliotheksentscheidung die gesamte Recovery jedes betroffenen
Peers einschließlich Kanal-/Sendewartezeiten und nachgelagerter Prüfungen. Gleichzeitige
Fehler desselben Peers werden zusammengefasst. Reset-/Boot-up-Fehler und Fristablauf melden
`ErrorRecoveryFailed` mit Ursache sowie `SlaveErrorRecoveryFailed`; die Episode bleibt bis
zu einem neuen Boot, relevanter Konfigurationsänderung oder Rollenzyklus gesperrt. Dadurch
lösen wiederholte Consumer-Timeouts keinen Reset-Sturm aus. Fehler nachgelagerter Gates
behalten die vorhandene Pflicht-/Optional-Strategie.

Die bisherigen Timeout-Ereignisse und `SlaveErrorControlEvent` werden vor der Reaktion
eingereiht. Öffentliche Ereignisse bleiben asynchron; deren Zustellung ist kein Vetopunkt.
Synchrone Application-Callbacks können den Boot beziehungsweise Consumer verändern;
Generation und wirksame Consumer-Zeit werden vor der Reaktion erneut geprüft.
`ErrorControlReactionRequested` beschreibt eine angeforderte Reaktion, keine bestätigte
Übertragung oder erfolgreiche Wiederherstellung. Reset, Rollenverlust, neuer Boot und
Disposal verwerfen veraltete Arbeit. Es entstehen keine automatischen entfernten
1017h-/100Ch-/100Dh-Schreibzugriffe.

## Prüfung

Die Tests in `CanOpenBootIdentityTests.NmtErrorHandler.cs`, `.NmtErrorLifecycle.cs`,
`CanOpenSafetyBootUpTests` und `CanOpenApplicationSeamTests.BootErrorControl.cs` verwenden
den virtuellen Bus, eine gemeinsame manuelle Uhr sowie Actor-/Send-Witness.
Protokollfristen werden virtuell vorgerückt; gehaltene Sendungen und Kanalbelegungen werden
explizit freigegeben. Reale Runner-Timeouts dienen nur als Abbruchsgrenze gegen Hängenbleiben.
Die API-Baseline stammt aus der erzeugten `.received.txt`.

Gezielte Produktionsmutationen wurden einzeln kompiliert, getestet und vollständig
zurückgenommen. Jede der folgenden Varianten ließ den angegebenen Test fehlschlagen:

| Mutation | Test |
| --- | --- |
| Pflicht-Bedingung vor Broadcast Stop entfernt | `Runtime_Error_Uses_Mandatory_And_Startup_Bits` |
| Stop-Bit ignoriert | `Runtime_Error_Uses_Mandatory_And_Startup_Bits` |
| Broadcast-Ziel null durch Peer-ID ersetzt | `Runtime_Error_Uses_Mandatory_And_Startup_Bits` |
| Alter Peer-Token nicht abgebrochen | `Runtime_Error_Cancels_Only_The_Failed_Peers_Queued_Start` |
| Recovery-Frist nicht aktiviert | `Runtime_Recovery_Deadline_Includes_Channel_And_Transmission_Waits`, `Runtime_Recovery_Deadline_Cancels_Post_Reset_Verification` |
| Alte Identitätsfreigabe erhalten | `Runtime_Recovery_Rechecks_Identity_And_Configuration` |
| Alte Konfigurationsfreigabe erhalten | `Runtime_Recovery_Rechecks_Identity_And_Configuration` |
| Alte Safety-Freigabe erhalten | `Runtime_Error_Recovery_Repeats_Safety_Verification` |
| Gehaltenen Master-Reset ignoriert | `Runtime_Recovery_Resumes_After_An_Abandoned_Forced_Reset` |
| Bestätigten Reset-/Boot-up-Handshake übersprungen | `Individual_Error_Recovery_Reboots_Only_The_Failed_Slave_And_Requires_A_New_Heartbeat` |

Die zusätzliche Regression `Runtime_Broadcast_Reaction_Cannot_Outlive_Its_Master_Role`
schlug vor der Korrektur für Stop und Reset Node, jeweils bei Rollenende und Neuzuordnung,
fehl: Eine gehaltene Broadcast-Reaktion wurde danach noch gesendet. Die Reaktion besitzt
nun einen eigenen Abbruch-Token, der das interne Halt überlebt, aber bei neuem Boot oder
Rollenende verworfen wird.

Der Fristbefund aus dem [Codex-Thread](https://github.com/dborgards/CanKit.Pro/pull/589#discussion_r4654982249)
und dem [Bugbot-Thread](https://github.com/dborgards/CanKit.Pro/pull/589#discussion_r4654989771)
wurde gegen `2c39b76a` reproduziert: Die ursprüngliche 1F89h-Bootfrist ersetzte den Status
laufender oder bereits gesperrter Recovery durch BootTimeout und brach sie ab. Alle sechs
neuen Fälle schlugen vor der Korrektur fehl. `OnBootTimeout` nimmt nun Runtime-Episoden aus
seiner Fehlerliste heraus; andere initial noch ausstehende Pflicht-Peers behalten ihre Frist.
`Runtime_Recovery_Is_Not_Truncated_By_The_Initial_Boot_Deadline` prüft Reset-Wartezeit,
Identität, Konfiguration, Heartbeat und gesperrten Fehler.
`Runtime_Recovery_Does_Not_Disable_Another_Peers_Initial_Boot_Deadline` prüft den weiterhin
fälligen anderen Pflicht-Peer. Die normalen Freigabeprüfungen bleiben unverändert, sodass
ein fehlgeschlagener Peer keinen noch ausstehenden Netzwerkstart freigibt.

Lokale Prüfung des abschließenden Stands:

- Release-Build mit `CI=true`: keine Warnungen oder Fehler.
- Gesamtsuite net10.0: 5.848 bestanden, keine fehlgeschlagenen oder übersprungenen Tests.
- Cobertura gegen Basis `6181bf89`, alle Klassen einschließlich generierter Zustandsautomaten:
  156/157 geänderte ausführbare Einträge und 156/168 Zweigbedingungen getroffen.
  Nicht getroffen ist der defensive `ObjectDisposedException`-Catch beim Posten des bereits
  abgeschlossenen Reset-Workers. Die normalen Abbruch-/Dispose-Pfade sind geprüft; ein
  künstlicher Scheduling-Hook allein für dieses Restfenster wurde nicht ergänzt.
  Dies ist eine lokale Diff-Auswertung, kein vorweggenommenes Codecov-Ergebnis.
- `dotnet format --verify-no-changes`: unverändert.
- Frisches Release-Pack und `eng/verify-packages.py`: alle neun Pakete geprüft.
- net48-Testprojekt mit `CanKitProTestNetFrameworkLeg=true` und `CI=true`: ohne Warnungen
  oder Fehler kompiliert; die Windows-Laufzeitprüfung bleibt der CI vorbehalten.
- Anforderungsnachverfolgung: keine neuen ungetraceten Must-Anforderungen.
