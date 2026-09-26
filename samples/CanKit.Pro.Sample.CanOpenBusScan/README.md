# CANopen bus scan

Listens for CANopen heartbeats and boot-up, then prints the node IDs it heard.

The default is listen-only ([#131](https://github.com/dborgards/CanKit.Pro/issues/131) decision 3). After the listen window the sample does not SDO-read node IDs that stayed silent. A full active scan is explicit:

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --active-scan
```

`--active-scan` probes node IDs that did not heartbeat. The scanner's own node ID is excluded. `1000h:00` and `1018h:00`–`04` are allowed on that path without a peer file. Pass `--peer-description` with an EDS or DCF to read only the objects that file lists. An EDS applies to every node; a DCF applies only to the node-id it was commissioned for.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --help
```
