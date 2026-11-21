# CiA 302-2: Aktiver Slave-Boot und Wiederholung

Stand: 21.11.2026, Ausgangspunkt `main` bei `8c6474d7` (Merge von
[#583](https://github.com/dborgards/CanKit.Pro/pull/583)). Die Normquelle und ihre Prüfsumme
stehen im [vorherigen Nachtrag](2026-11-21-ds302-boot-followup.md). Dieser Schritt ergänzt
FR-CO-066; er erklärt nicht die gesamte CiA-302-Reihe für konform.

§3.3 verlangt einen Prozess pro zugeordnetem Slave, Wiederholungen für optionale Slaves und
bei Pflicht-Slaves mit Fehler B bis zur Bootfrist. Die bisherige Implementierung beginnt erst
nach einer Ankündigung. Mit `ActiveSlaveBootRetryInterval` beginnt der Master nun selbst mit
dem Upload von `1000h:00`, auch bei Null-Erwartungen und ohne Heartbeat. Null bleibt der
Standard; ein gesetztes Intervall muss positiv sein. Die Empfehlung von einer Sekunde über
125 kbit/s wird im API-Beispiel verwendet. Gemessen wird die Pause nach dem fehlgeschlagenen
Versuch, nicht eine feste Periode vom Anfang eines Versuchs zum nächsten.

Bei nicht Keep-alive gesetzten Slaves wartet der Upload auf die Adapterbestätigung des
vorherigen Reset Communication. Ein abgelehnter oder abgebrochener Reset wird erneut versucht;
der Zustand `ResetCommunicationFailed` und das Signal `SlaveBootResetFailed` zeigen das an.
Auch dieser Wartezustand unterliegt bei Pflicht-Slaves der Bootfrist. Die Bestätigung betrifft
den lokalen Versand, nicht den Empfang oder Neustart des entfernten Geräts.

Fehler B wird eng abgegrenzt: lokaler SDO-Protokolltimeout beim Upload von `1000h:00`.
Ein Peer-Abort, auch mit `05040000h`, ist eine Antwort; ein Timeout bei `1018h` ist kein
Gerätetyp-Timeout. Solche Fehler und Identitätsabweichungen halten Pflicht-Boots an.
`1F89h = 0` schaltet die Frist aus. Optionale Identitäts- und Safety-Fehler werden bis zum
Erfolg oder Abbruch wiederholt. Nach ihrem ersten Versuch dürfen Pflicht-Slaves und Master
starten, auch bei gemeinsamem Startzeitpunkt; spätere optionale Erfolge kommen einzeln hinzu.

Erreichbarkeit ersetzt keine NMT-Zustandsmeldung. Eine SDO-Antwort setzt weder `_slaveSeen`
noch einen erfundenen Wert in `1F82h`. Eine Operational-Meldung aus dem aktuellen Boot vermeidet
Start, ein alter gespeicherter Wert nicht. Ohne aktuelle Meldung fordert die Bibliothek nach
den aktivierten Prüfungen einen einzelnen Start an. Broadcast-Start wird unterdrückt.
Diese Entscheidung ersetzt noch nicht den vollständigen Keep-alive-Zweig aus §3.4/§3.6.

Nachweise stehen in `CanOpenBootIdentityTests.Active.cs`, `.ActiveLifecycle.cs` und dem
aktiven Safety-Fall in `CanOpenSafetyBootUpTests`. Sie verwenden virtuelle Protokollzeit,
Actor-Barrieren und kontrollierte Sendebestätigungen. Ein neuer Boot, Reset, Rollenverlust
oder Halt verwirft alte Arbeit; verspätete Reset-Ergebnisse tragen ihre Bootgeneration.

Nächste getrennte Bausteine bleiben Konfigurationsprüfung/-download (§3.5, Teil 3), der
automatische Start der Fehlerüberwachung (§4.1) und der vollständige Keep-alive-Prüfzweig.
Danach ist der noch offene systematische Normabgleich von Flying Master und Objektsemantik
weiterzuführen. Die übrigen Teile 302-1 und 302-3 bis -9 sind hier nicht auditiert.
