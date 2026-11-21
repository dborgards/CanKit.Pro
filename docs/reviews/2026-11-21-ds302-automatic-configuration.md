# CiA 302: Automatische Konfiguration im Slave-Boot

Stand: 21.11.2026. Basis ist `main` bei `b3d2bd96`, Merge von
[#586](https://github.com/dborgards/CanKit.Pro/pull/586). FR-CO-069 verbindet den
[Konfigurationsvergleich](2026-11-21-ds302-configuration-check.md) mit dem
[Concise-DCF-Download](2026-11-21-ds302-concise-dcf-download.md).

Gelesene Normstellen: CiA 302-2 v4.1.0, §3.5 (insbesondere Schritte d/e), §6.3
(1F81h Bit 7) und §6.12 (1F8Ah); CiA 302-3 v4.1.0, §3.3. Herkunft und Prüfsummen
stehen in den verlinkten Akten und im [Boot-Nachtrag](2026-11-21-ds302-boot-followup.md).
Die vom Nutzer bereitgestellten Normtexte werden nicht ins Repository kopiert.

## Verhalten und API

`EnableBootConfigurationDownload` ist standardmäßig aus. Aktivierung schließt den
1000h-Upload und die Prüfung von 1F26h/1F27h ein. Zusätzlich wird 1F8Ah als
UNSIGNED8-Array mit konstantem Zähler 127, anfangs null und Wertebereich 0–127 verwaltet.
Der ältere reine Prüfmodus beansprucht dieses Objekt weiterhin nicht.

Die Anwendung registriert je Slave einen binären Concise DCF:

```csharp
// Node options: new CanOpenNodeOptions { EnableBootConfigurationDownload = true }
node.BindPeerDeviceDescription(5, peerDescription);
node.SetSlaveBootConfiguration(5, conciseDcfBytes);
node.ObjectDictionary.WriteUnsigned(0x1F26, 5, expectedDate);
node.ObjectDictionary.WriteUnsigned(0x1F27, 5, expectedTime);

// Optional restore: existing assignment must also have bit 7 set.
node.ObjectDictionary.WriteUnsigned(0x1F8A, 5, 1); // remote 1011h:01
```

Der Aufruf kopiert und prüft den gesamten Strom einschließlich Größenlimits, bevor er den
bisherigen Plan ersetzt. `null` entfernt ihn; vier Nullbytes sind ein expliziter leerer Plan.
Lokale NMT-Resets erhalten diese Anwendungspläne. Ein Wechsel bei aktivem Master beginnt
wie eine Änderung der Erwartungswerte einen neuen Boot und bricht dessen Vorgänger ab.
Der eigene Node-ID-Slot wird vom Slave-Boot ignoriert.

Nur ein Null-Erwartungswert oder eine Abweichung löst einen hinterlegten Plan aus. Ein
passendes Datum/Zeit-Paar benötigt keinen Download, sofern kein begonnener Download dieses
Plans unvollständig ist. Ein fehlender Plan bleibt
`ConfigurationRequired`, ein Uploadfehler bleibt `ConfigurationReadFailed`.

Der Update-Ablauf hält den Peer-SDO-Kanal über folgende Schritte:

1. Gebundene EDS/DCF-Beschreibung erfassen; sämtliche Schreibziele und gegebenenfalls
   1011h-Restore prüfen. Keine Zustandsänderung oder Wiederherstellung vor dieser Vorprüfung.
2. Individuelles NMT Pre-operational senden und dessen lokale Sendebestätigung abwarten.
3. Wenn 1F81h Bit 7 gesetzt und 1F8Ah ungleich null ist: `load` nach 1011h am ausgewählten
   Subindex schreiben und die SDO-Bestätigung abwarten; anschließend Reset Node senden,
   dessen Bestätigung und eine gültige Boot-up-Meldung abwarten. Deren Host-Empfangszeit
   muss mindestens der Übergabezeit dieses Resets an den Bus-Service entsprechen. Der
   Boot-up-Wartezeitraum beginnt nach der Sendebestätigung und ist durch SdoTimeout
   begrenzt; die Pflicht-Bootfrist kann ihn früher beenden.
4. DCF-Einträge in Reihenfolge schreiben, jeweils nach vorheriger Bestätigung.
5. Erst nach Erfolg folgen Safety-Prüfung und individueller Start. Auch eine verzögerte
   Operational-Meldung überspringt diese Schritte nach dem angeforderten Pre-operational nicht.

`ConfigurationDownloadFailed` erhält die ursprüngliche Ausnahme und bei SDO-/Zugriffsfehlern
das Objektpaar. Bestätigte Schreibzugriffe bleiben bestehen. Pflichtfehler halten den Boot an;
optionale Fehler folgen der gewählten Ankündigungs-/Wiederholungsstrategie. Rollenverlust,
Reset, Deadline und Disposal brechen den bisherigen Boot ab. Ein abgebrochener, noch wartender
Konfigurations-NMT-Auftrag trägt den CancellationToken auch bis zur tatsächlichen Sendung.

## Normumfang und Grenzen

Teil 2 §3.5 erlaubt DCF-Daten aus einem lokalen Dateisystem oder anderen Quellen. Die
Registrierung ist diese Anwendungsschnittstelle; sie implementiert keinen verwalteten
1F22h-Speicher, keine Dateisuche und keine automatische Auswahl nach Geräteidentität.
Reset Node ist die gewählte der beiden in §3.5 erlaubten Reset-Varianten. Pre-operational,
die vollständige Schreibvorprüfung, der Beschreibungssnapshot und SdoTimeout als
Boot-up-Wartefrist sind Bibliotheksentscheidungen.

Ein erfolgreicher Download genügt gemäß §3.5 Schritt e. Es gibt keinen zusätzlichen,
impliziten 1020h-Schreibzugriff, Store-Auftrag oder erneuten Signaturvergleich. Falls benötigt,
liegen diese Schreibzugriffe im gelieferten DCF. Die Sperre koordiniert nur den lokalen
Knoten; sie ist keine netzweite Transaktion. Safety-Kommissionierung bleibt gesondert.

Nicht enthalten sind insbesondere automatische Error-Control-Einrichtung nach §4.1,
Software-Download nach anderen Teilen der CiA-302-Reihe und vollständiges Management
sämtlicher Konfigurationsobjekte. Dies ist daher kein Anspruch auf vollständige DS302-Konformität.

## Gezielter Mutationsnachweis

Die folgenden Produktionsfehler wurden einzeln eingebaut und danach jeweils zurückgenommen.
Die Tests wurden gegen kompilierende Mutanten ausgeführt; der betroffene Test schlug jeweils
fehl. Dies ist eine gezielte Stichprobe, kein vollständiger Mutationslauf.

| Eingebauter Fehler | Nachweis in CanOpenBootIdentityTests |
| --- | --- |
| Automatischen Download auslassen | Boot_Download_Updates_Before_Individual_Start |
| Restore unabhängig von 1F81h Bit 7 wählen | Boot_Restore_Is_Acknowledged_Before_Reset_And_Download |
| Nach Reset nicht auf Boot-up warten | Restore_Reset_Requires_BootUp_And_Respects_The_Mandatory_Deadline |
| Schreibschutz nicht vorab prüfen | Boot_Download_Preflight_Prevents_Restore_For_An_Invalid_Target |
| DCF-Einträge rückwärts übertragen | Failed_Boot_Write_Reports_Original_Abort_And_Stops_The_Prefix |
| Abgewiesene NMT-Sendung als Erfolg behandeln | Rejected_Configuration_Nmt_Blocks_Download |
| Boot-CancellationToken ignorieren | Boot_Update_Pins_Description_And_Cancels_Between_Acknowledged_Writes |
| Eigenen statt gemeinsamen Peer-SDO-Kanal verwenden | Boot_Update_Pins_Description_And_Cancels_Between_Acknowledged_Writes |

Beim Nachweis der letzten beiden Fälle wurde die Wartefreigabe des Test-Witness an den
tatsächlich erreichten Haltepunkt nach der ersten SDO-Bestätigung gebunden. Eine vorab erteilte
Freigabe konnte virtuelle Zeit fortschreiten lassen, obwohl der Worker noch nicht am Haltepunkt
war. Die beiden Mutationen wurden mit der korrigierten Barriere erneut ausgeführt.

## Nachweis für teilweise geschriebene Versionswerte

Die abschließende Durchsicht fand einen weiteren Fehler im zunächst implementierten Ablauf:
Ein DCF konnte zuerst 1020h aktualisieren und später abbrechen. Der nächste optionale Versuch
akzeptierte dann die passende Signatur und startete trotz des fehlenden Parameters.
`A_Partial_Download_With_Updated_Signature_Must_Replay_Before_Start` wurde zuerst gegen diesen
Zwischenstand ausgeführt und scheiterte am leeren Zielparameter nach dem Start.

Jeder registrierte Plan trägt jetzt ab Beginn der Zustandsänderung eine Unvollständig-Markierung.
Nur seine vollständig bestätigte Übertragung löscht sie. Passende Versionswerte allein reichen
bei dieser Markierung nicht aus; sie überlebt auch Rollenwechsel und lokale NMT-Resets. Ein
expliziter Ersatz oder das Entfernen des Plans verwirft ihn samt Markierung. Die Prüfung umfasst
sowohl den optionalen Wiederholungsversuch als auch einen neu gestarteten Master.

## Lokale Abschlussprüfung am 21.11.2026

Release-Build mit `CI=true`: keine Warnungen oder Fehler. Vollständige net10.0-Suite:
5.752 Tests erfolgreich. `dotnet format --verify-no-changes`, frischer Pack-Lauf mit
`eng/verify-packages.py` (neun Pakete), net48-Kompilierung mit `CI=true` und
Anforderungs-Traceability sind erfolgreich.

Die Cobertura-Auswertung des vollständigen Laufs wurde gegen den Diff zu `b3d2bd96`
geschnitten, einschließlich sämtlicher Klassen und generierter Zustandsautomaten:
192 geänderte ausführbare Einträge, keine unbesetzte Zeile und keine teilweise abgedeckte
Verzweigung. Der API-Abgleich wurde aus der generierten `.received.txt` übernommen.

## Review-Nachtrag

Das Codex-Review von `75798b38` fand zwei weitere Fehler, die im selben PR korrigiert wurden:

- Die Boot-up-Frist lief bereits, während Reset Node noch auf seine Sendung wartete. Der
  Listener wird weiterhin vorher installiert; die Frist beginnt erst nach der bestätigten
  Sendung. Eine während der Sendebestätigung bereits eingetroffene Boot-up-Meldung bleibt gültig.
- Der maskierte NMT-Zustand unterschied 0x80 nicht von echtem Boot-up. Ein gemeinsamer Beobachter
  prüft vor der Heartbeat-/Guarding-Verarbeitung den unveränderten Frame: klassischer
  Datenframe mit genau einem Byte, Wert 0x00.

`Reset_Send_Delay_Does_Not_Spend_The_Post_Reset_BootUp_Timeout` und die vier Varianten von
`Only_A_One_Byte_Zero_Announcement_Completes_The_Restore_Reset` schlugen vor den Korrekturen
fehl und bestanden danach. Der virtuelle Witness unterscheidet zudem die asynchrone
Sendungs-/Timer-Vorbereitung vom tatsächlichen Warten auf Remote-Boot-up.

CodeQL-Hinweise zu Interface-Casts wurden durch statische Aufrufe der öffentlichen
Erweiterungsmethode behoben. Die erst am tatsächlichen Haltepunkt erworbene Wartefreigabe
bleibt bewusst im `finally`: vorherige Freigabe würde den geprüften Testfehler zurückbringen;
ein `using` auf dem anfangs noch leeren Slot würde die später erworbene Freigabe nicht erfassen.
Der allgemeine Catch bleibt die dokumentierte Grenze zwischen dem Hintergrund-Worker und
dem auf dem Actor ausgewerteten Boot-Ergebnis; die ursprüngliche Ausnahme bleibt erhalten.

Bugbot fand anschließend eine zu frühe Freigabe durch eine alte Boot-up-Meldung, während
der neue Reset noch auf seine Sendung wartete.
`A_BootUp_Before_Reset_Transmission_Does_Not_Release_The_Download` schlug gegen diesen
Zwischenstand fehl. Der Empfang behält jetzt `HostArrivalTimestamp`; die bestätigte Sendung
übergibt `HostHandoffTimestamp` an genau den vor der Sendung erfassten Reset-Versuch.
Früh gepufferte Antworten bleiben erhalten, zählen aber nur ab dieser Übergabezeit. Der
Test trennt die beiden Ereignisse ausdrücklich auf der virtuellen Uhr. Weitere Prüfungen
behandeln schnelle Antworten vor verzögerter Sendebestätigung und fehlende Übergabe-Metadaten.

Restore verlangt dafür vergleichbare, positive Host-Zeitstempel vom Bus-Service.
`CanBusService` liefert diese; fehlende TX-Metadaten brechen das Update ab, eine RX-Meldung
ohne Zeitstempel gibt es nicht frei. Die Aussage bleibt die der bestehenden
`TxConfirmation`: bei approximierter Bestätigung gilt die Annahme durch den Treiber,
nicht eine zusätzliche Garantie der physikalischen Übertragung.

Die eigene Prüfung des gemeinsam verwendeten Sendepfads fand danach eine Regression für
Extended-PDO-ID 0 mit Nutzbyte 0x81 sowie manuelle Reset-Node-Ziele oberhalb 127. Die
Beobachtung interpretierte diese als indizierbaren Slave-Reset und verhinderte die Sendung.
Die erweiterte PDO-Prüfung und beide manuellen Zielprüfungen schlugen vor dem Fix fehl.
Extended-Frames und Ziele außerhalb der Slave-Tabelle werden nun von der Reset-Beobachtung
ausgenommen; ihr bisheriger Sendepfad bleibt erhalten.
