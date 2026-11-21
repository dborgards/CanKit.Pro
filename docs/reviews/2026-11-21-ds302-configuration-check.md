# CiA 302: Konfigurationsversion vor Slave-Start

Stand: 21.11.2026, Ausgangspunkt `main` bei `4d331a81` (Merge von
[#584](https://github.com/dborgards/CanKit.Pro/pull/584)). FR-CO-067 ergänzt die Prüfung
in CiA 302-2 §3.5, ohne den gesamten Konfigurationsablauf als implementiert zu erklären.

Die Quelle für Teil 2 steht im [Boot-Nachtrag](2026-11-21-ds302-boot-followup.md).
Zusätzlich gelesen: das vom Nutzer bereitgestellte `302_3v04010004.pdf`, CiA 302-3
Version 4.1.0, 8. April 2010, §3.7 und §3.8. SHA-256:
`311d464dc33fb229f5de6c0be21f63962ccb7008999fd173f21bcd346de1b84f`.
Die Normtexte werden nicht ins Repository übernommen.

Die erwarteten Datum-/Zeitwerte in `1F26h`/`1F27h` werden mit `1020h:01`/`:02` verglichen.
Anders als bei Identität bedeutet null hier: Das Gerät soll konfiguriert werden. Deshalb
meldet das neue Opt-in bei einem Nullwert sofort `ConfigurationRequired`, ohne SDO-Upload.
Bei zwei Nichtnullwerten müssen beide exakt übereinstimmen; eine Abweichung meldet dasselbe
Ergebnis mit erwartetem und tatsächlichem Wert. Uploadfehler bleiben gesondert sichtbar.

`RequireBootConfigurationMatch` ist standardmäßig aus. Damit bleiben bisher anwendungseigene
Objekte unangetastet. Eingeschaltet verwaltet die Bibliothek beide Arrays einschließlich
EDS/DCF-Übernahme, Schreibprüfung und kaltem Master-Reset. Ein aktiver Schreibzugriff beginnt
einen neuen Boot. `1020h` bleibt auf der Geräteseite anwendungseigen; diese Änderung ergänzt
keine automatische Verwaltung oder Änderung einer Konfigurationssignatur.

Die Prüfung hält einen Peer-SDO-Kanal für beide Uploads und verwendet einen gemeinsamen
Beschreibungssnapshot. Eine gebundene EDS/DCF muss beide Zugriffe erlauben. Die bisherige
Bootstrap-Freigabe für ungebundene Peers wird nicht erweitert. Der Ablauf ist Identität,
Konfiguration, Safety, einzelner Start. Ein 1020h-Timeout ist kein wiederholbarer Pflichtfehler B
am Gerätetyp-Upload. Optionale Slaves folgen der aktiven Wiederholung aus FR-CO-066, wenn diese
eingeschaltet ist. Pflichtfristen erfassen auch das Warten auf den Kanal.

Die Option prüft auch bereits Operational gemeldete Slaves. Das ist eine bewusst strengere
Bibliothekspolitik, keine Behauptung, der vollständige Keep-alive-Zweig sei umgesetzt.
`ConfigurationRequired` stoppt vor automatischem Start: Restore (`1011h`), Reset nach Restore,
Konfigurations-/Concise-DCF-Download und anschließende Signaturpflege bleiben weitere Schritte.
Ebenso offen bleiben der vollständige Keep-alive-Ablauf und automatische Fehlerüberwachung.

Die Nachweise liegen in `CanOpenBootIdentityTests.Configuration.cs`,
`.ConfigurationLifecycle.cs` und `CanOpenSafetyBootUpTests`. Sie verwenden virtuelle
Protokollzeit, Actor-Barrieren und kontrollierte Peer-Antworten.

## Lokaler Mutationsnachweis

Am 21.11.2026 wurden die folgenden Fehler einzeln in den Produktionscode eingebaut.
Jeder kompilierte und führte zu einem fehlschlagenden Test; danach wurde jeweils der
unveränderte Produktionsstand wiederhergestellt. Dies ist eine gezielte Stichprobe,
keine vollständige Mutationsabdeckung.

| Absichtlich eingebauter Fehler | Nachweis in den Tests |
| --- | --- |
| Beschreibung vor dem zweiten Upload neu erfassen | Configuration_Reads_Pin_One_Description_Snapshot |
| Konfigurationsfreigabe beim Safety-Retry behalten | Active_Optional_Safety_Failure_Retries_Without_Halting_Simultaneous_Start |
| Nullwert als verifiziert melden | A_Zero_Configuration_Expectation_Requires_Configuration_Without_Any_Upload |
| Abweichung akzeptieren | A_Configuration_Mismatch_Blocks_Start_And_Reports_The_Pair |
| Zeitwert nicht lesen | A_Configuration_Mismatch_Blocks_Start_And_Reports_The_Pair |
| Längenbedingung umkehren | A_Configuration_Read_Failure_Retains_The_Upload_Cause |
| Alte Bootgeneration akzeptieren | A_Live_Configuration_Edit_Replaces_The_Waiting_Check_And_Rejects_Its_Stale_Result |
| Konfigurationswartezeit aus Bootfrist ausschließen | Configuration_Boot_Deadline_Includes_The_Wait_For_The_Shared_Channel |
| Option beim Kopieren verlieren | Configuration_Check_Defaults_Off_And_Leaves_Expectation_Objects_Application_Owned |
