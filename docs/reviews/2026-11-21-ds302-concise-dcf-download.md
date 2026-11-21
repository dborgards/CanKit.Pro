# CiA 302: Expliziter Concise-DCF-Download

Stand: 21.11.2026, Basis `main` bei `8b8cc5ec`, Merge von
[#585](https://github.com/dborgards/CanKit.Pro/pull/585). FR-CO-068 liefert den
Download-Baustein für die spätere automatische Konfigurationsphase des Boot-Managers.

Gelesen wurden CiA 302-3 v4.1.0 (8. April 2010), §3.3 einschließlich Abbildung 1,
und CiA 302-2 v4.1.0, §3.5 sowie die Restore-Regeln in §6.3/§6.12. Die vom Nutzer
bereitgestellten PDFs, Ausgabedaten und Prüfsummen sind in den vorherigen Akten belegt:
[Konfigurationsprüfung](2026-11-21-ds302-configuration-check.md) und
[Boot-Nachtrag](2026-11-21-ds302-boot-followup.md). Normtexte werden nicht übernommen.

Die neue Erweiterungsmethode nimmt einen bereits vorliegenden binären Concise-DCF-Strom
entgegen. Sie verwaltet keinen lokalen 1F22h-Speicher und legt keine Auswahl- oder
Dateisystempolitik fest. Ein Array von Einträgen wird in der originalen Reihenfolge abgearbeitet;
mehrfaches Schreiben desselben Objektpaars ist erlaubt und für Konfigurationssequenzen relevant.
Null Einträge ergeben einen No-op, leere Werte bleiben echte SDO-Downloads.

Vor dem ersten Frame werden die vollständige Struktur, Übertragungsgrößen und der Zugriff
laut gebundener Beschreibung geprüft. Größen werden gegen die verbliebenen Bytes geprüft,
bevor UNSIGNED32-Werte nach int konvertiert werden. Die Erfassung der Nutzdaten erfolgt beim
Aufruf; die Beschreibung wird nach Übernahme des gemeinsamen Peer-SDO-Kanals eingefroren.
Die gesamte Folge hält diesen Kanal. Dies koordiniert Aufrufe desselben lokalen Knotens und
ist keine netzweite Sperre gegen andere CANopen-Master.

Der erste Übertragungsfehler bricht ab; bereits ausgeführte Schreibzugriffe bleiben bestehen.
Die API liefert die ursprüngliche Ausnahme einschließlich SDO-Objektpaar und Abort-Ursprung.
Abbruch und Disposal wirken auch beim Warten auf den Kanal. Sicherheitskommissionierung
bleibt der gesonderte CiA-319-/Safety-Ablauf, kein impliziter Bestandteil dieses Downloads.

Die Vorprüfung, der strikte Ausschluss angehängter Bytes und die Snapshot-/Kanalpolitik sind
Bibliotheksentscheidungen. Das Drahtformat stammt aus CiA 302-3 §3.3. Aus dem Format allein
folgen weder Transaktionsatomarität noch ein vollständiger CiA-302-Konfigurationsmanager.

Offen bleiben die automatische Auswahl und Anwendung im Boot-Ablauf sowie Restore gemäß
1F81h Bit 7 und 1F8Ah. Dabei verlangt Teil 2 §3.5 die Reihenfolge Restore, Reset Communication
oder Reset Node, anschließend Download. Dieser PR fügt keine dieser impliziten Aktionen hinzu;
explizite Einträge des übergebenen Stroms werden wie angegeben geschrieben.

## Gezielter Mutationsnachweis

Am 21.11.2026 wurden die folgenden Produktionsfehler einzeln eingebaut. Jeder mutierte
Stand kompilierte und ließ den genannten Test fehlschlagen. Nach jedem Lauf wurde der
Produktionscode wiederhergestellt. Dies ist eine gezielte Stichprobe, keine vollständige
Mutationsabdeckung.

| Absichtlicher Fehler | Nachweis in CanOpenConciseDcfTests |
| --- | --- |
| Eigenen statt gemeinsamen Peer-Kanal verwenden | The_Channel_Remains_Owned_Between_Records_While_Other_Peers_Can_Proceed |
| Doppelte Objektpaare entfernen | Literal_Little_Endian_Stream_Preserves_Order_And_Repeated_Pairs |
| Eintragsreihenfolge umkehren | Literal_Little_Endian_Stream_Preserves_Order_And_Repeated_Pairs |
| Angefügte Bytes akzeptieren | The_Whole_Stream_Is_Validated_Before_Any_Write |
| Größenprüfung vor dem Schreiben auslassen | The_Size_Limit_Applies_To_Every_Value_Before_Any_Write |
| ro/const-Prüfung auslassen | All_Access_Checks_Precede_The_First_Write |
| Beschreibung vor jeder Übertragung neu erfassen | Description_And_Data_Remain_Pinned_Between_Acknowledged_Writes |
| Disposal beim Warten nicht beachten | Cancellation_Or_Disposal_While_Waiting_Does_Not_Release_Another_Owners_Lease |
| Nach Peer-Abort fortfahren | A_Peer_Abort_Stops_At_The_Failing_Record_And_Leaves_The_Written_Prefix |
