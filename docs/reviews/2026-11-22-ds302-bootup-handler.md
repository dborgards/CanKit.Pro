# CiA 302: Boot-up-Handler

Stand: 22.11.2026. Basis ist `main` bei `84680df32e333e329f18f8513970996b33c935ec`,
Merge von [#589](https://github.com/dborgards/CanKit.Pro/pull/589).
FR-CO-072 ergänzt den unabhängigen Boot-up-Handler nach CiA 302-2 v4.1.0 §4.3,
Abbildung 13. Gelesen wurden zusätzlich §6.3, Tabelle 20 (1F81h, Boot-Bit 2).
Die gelieferten Normdateien bleiben außerhalb des Repositorys; Herkunft und Prüfsummen
stehen im [Boot-Nachtrag](2026-11-21-ds302-boot-followup.md).

## Verhalten und Grenzen

`EnableNmtBootupHandler` ist standardmäßig false und bleibt in Optionskopien erhalten.
Die Option schließt Error-Control-Start ein, aktiviert aber weder den Laufzeit-Fehlerhandler
noch weitere Identitäts-/Konfigurationsoptionen. Nur der aktive Master verarbeitet gültige
Peer-Boot-ups: klassische Standard-Datenframes auf 700h + Node-ID mit genau einem Nullbyte.
Er reiht `SlaveBootupReceived` auch für nicht zugeordnete Peers ein, ohne die eigene Node-ID.
Die öffentliche Zustellung ist asynchron und kein Vetopunkt.

Bei einem zugeordneten Peer verwirft der Handler alte Freigaben, wartenden Start,
Worker-Ergebnisse und ausstehenden Retry dieses Peers. Aktivierte Identität,
Konfiguration einschließlich Download, Error Control und Safety laufen erneut.
Ein zusätzliches Reset wird dafür nicht gesendet. Andere Peers behalten ihren Zustand.
Gelöschtes Boot-Bit oder unterdrückter Start verhindert Konfiguration und Start;
nicht zugeordnete Peers werden ausschließlich gemeldet.

Erwartete Boot-ups innerhalb eines Reset-/Restore-Handshakes bleiben dessen Verantwortung.
Insbesondere ersetzt die neue Beobachtung weder Sendebestätigung noch Frischeprüfung.
Ein ausstehender aktiver Reset Communication bleibt ebenfalls abzuwarten.
Abbruch-Token werden auch bei der späteren Actor-Verarbeitung von Worker-Ergebnissen
geprüft: Ein Ergebnis kann vor dem Boot-up eingereiht und erst danach verarbeitet werden,
obwohl die globale Boot-Generation unverändert ist.

Wiederholte initiale Boot-ups verlängern die initiale Frist nicht. Bereits fertig gebootete
Peers und laufende oder gesperrte Laufzeit-Recovery erhalten eine neue Peer-Frist aus
nichtnull 1F89h. Das ist Bibliothekspolitik für den gesamten erneuten Boot-Versuch.
Frisches Boot-up kann eine einzelne Recovery-Sperre aufheben, niemals den Netzwerk-Halt.
Wiederholte Boot-ups starten den Peer-Versuch erneut. Während eines gehaltenen Master-Resets
bleibt die Ankündigung erhalten; nach Verwerfen des Resets setzen frische Prüfungen fort.
Die neue Peer-Frist beginnt erst beim Fortsetzen, damit die gehaltene Ankündigung nicht
schon vor Beginn ihrer Prüfungen gesperrt wird. Rollenende, Neuzuordnung und Disposal
verwerfen alte Arbeit.

Dies ist keine vollständige DS302-Konformitätsbehauptung. Insbesondere bleibt der
Keep-alive-Ablauf nach §3.6 ein eigener offener Schritt. Die vorhandenen Boot-Gates behalten
ihren dokumentierten Umfang; der Handler erweitert ihre normative Abdeckung nicht.

## Prüfung

Die Tests in `CanOpenBootIdentityTests.NmtBootup.cs`, `.NmtBootupLifecycle.cs` und
`CanOpenSafetyBootUpTests` verwenden virtuellen Bus und virtuelle Protokollzeit sowie
Actor-/Send-Witness. Gehaltene Übertragungen und Downloads werden explizit freigegeben.
Reale Runner-Timeouts sind ausschließlich Grenzen gegen Hängenbleiben.
Die API-Baseline wurde aus der erzeugten `.received.txt` übernommen.

Während der Arbeit fiel der bereits auf der Basis vorhandene Broadcast-Abbruchtest
sporadisch aus. Die unveränderte Basisbibliothek reproduzierte den Fehler in einem
isolierten Runner mit dem unveränderten bestehenden Test. Ursache und Nacharbeit stehen
in [#590](https://github.com/dborgards/CanKit.Pro/issues/590); der Test wird in diesem
Inkrement nicht umgeschrieben.

Gezielte Produktionsmutationen wurden einzeln kompiliert, geprüft und zurückgenommen.
Folgende Mutationen ließen die jeweils genannten Tests fehlschlagen:

| Mutation | Test |
| --- | --- |
| Boot-up-Beobachtung aus dem Reader entfernt | `Bootup_Rechecks_Only_The_Rebooted_Peer_Without_Another_Reset` |
| Alten Peer-Token nicht abgebrochen | `Bootup_Cancels_Only_The_Rebooted_Peers_Queued_Start` |
| Alte Identitäts- beziehungsweise Konfigurationsfreigabe erhalten | `Bootup_Repeats_Identity_And_Configuration` |
| Alte Safety-Freigabe erhalten | `Bootup_Repeats_Safety_Verification` |
| Tokenprüfung im eingereihten Identitäts-/Konfigurationsergebnis entfernt | `Bootup_Cancels_Queued_Identity_Or_Configuration_Results` |
| Tokenprüfung im eingereihten Safety-Ergebnis entfernt | `Bootup_Discards_A_Queued_Safety_Result_From_The_Previous_Attempt` |
| Schon initiale Boot-ups als Laufzeit-Recovery behandelt | `Repeated_Initial_Bootups_Do_Not_Extend_The_Initial_Deadline` |
| Neue Peer-Frist nicht aktiviert | `Runtime_Bootup_Owns_A_Fresh_Deadline_Independent_Of_The_Initial_One` |
| Gesamte Abwehr für bestehende Reset-Handshakes entfernt | `Bootup_Observer_Cannot_Approve_A_Runtime_Reset_Before_Confirmation`, `Bootup_During_Active_Reset_Still_Waits_For_Transmission_Confirmation` |
| Mehrbyte-Boot-ups zugelassen | `Only_A_Valid_Peer_Bootup_Triggers_The_Handler` |
| Ausstehenden Retry nicht entsorgt | `Bootup_Cancels_An_Optional_Peers_Pending_Retry` |
| Resetting-Guard entfernt, bevor der Reset den Peer-Kanal erhält | `Bootup_Cannot_Replace_A_Runtime_Reset_Waiting_For_The_Peer_Channel` |
| Dispose-Catch beim Safety-Ergebnis umgangen | `A_Queued_Safety_Result_After_Disposal_Is_Ignored` |

Das Entfernen nur des FD-Guards wurde nicht erkannt: Der klassische Testbus liefert den
FD-Frame nicht an diesen Pfad. Daraus wird keine isolierte Testabdeckung dieses Guards
abgeleitet. Die zunächst überlebende Mutation des Resetting-Guards führte zum zusätzlichen
Fall mit belegtem Peer-SDO-Kanal: Dort ist noch kein Reset-Listener registriert, und das
Entfernen des Guards lässt den Test fehlschlagen.

Der [Codex-Reviewbefund](https://github.com/dborgards/CanKit.Pro/pull/591#discussion_r4656861800)
wurde gegen `641f3ca4` reproduziert: Bei 1F89h = 100 ms und 150 ms gehaltenem Master-Reset
wurde die neue Boot-up-Episode bereits während des Halts als ErrorRecoveryFailed gesperrt.
Die Peer-Frist wird nun erst in ResumeHeldBoot aktiviert. Ein bestehender Timer wird dabei
nicht verlängert und ein bereits gesperrter Fehler nicht wieder aktiviert. Der erweiterte
Test prüft deaktivierte Frist, erfolgreichen Start kurz vor Ablauf der frischen Frist und
deren Ablauf nach dem Fortsetzen. Sowohl vorzeitiges Aktivieren als auch Weglassen der
Frist beim Fortsetzen ließ den erweiterten Test in gezielten Mutationen fehlschlagen.

Lokale Prüfung des abschließenden Stands:

- Release-Build mit `CI=true`: keine Warnungen oder Fehler.
- Gesamtsuite net10.0: 5.892 bestanden, keine fehlgeschlagenen oder übersprungenen Tests.
- Cobertura gegen Basis `84680df3`, alle Klassen einschließlich generierter Zustandsautomaten:
  56/56 geänderte ausführbare Einträge und 59/60 Zweigbedingungen getroffen.
  Teilweise getroffen bleibt in ResumeHeldBoot die Abwehr einer bereits gesperrten Recovery;
  der normale Friststart und das Nichtverlängern einer laufenden Frist werden durchlaufen.
  Dies ist die lokale Diff-Auswertung, kein vorweggenommenes Codecov-Ergebnis.
- `dotnet format --verify-no-changes`: unverändert.
- Frisches Release-Pack und `eng/verify-packages.py`: alle neun Pakete geprüft.
- net48-Testprojekt mit `CanKitProTestNetFrameworkLeg=true` und `CI=true`: ohne Warnungen
  oder Fehler kompiliert; die Windows-Laufzeitprüfung bleibt der CI vorbehalten.
- Anforderungsnachverfolgung: keine neuen ungetraceten Must-Anforderungen.
