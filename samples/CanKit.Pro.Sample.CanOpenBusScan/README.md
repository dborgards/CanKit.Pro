# CANopen bus scan

Listens for CANopen heartbeats and boot-up, then prints the node IDs it heard.

The default is listen-only ([#131](https://github.com/dborgards/CanKit.Pro/issues/131) decision 3). `CanOpenDiscovery.ListenAsync` collects heartbeats and boot-ups for `--heartbeat-ms` (default 2000). Either one is enough for a node to count. No CANopen node is opened, so nothing is transmitted: no boot-up, and no answer to NMT, SDO, or node guarding. Silent node IDs are not probed.

`--active-scan` opens a node after the listen (`--client-node`, default 127) and calls `CanOpenDiscovery.ScanAsync`. That sends an SDO upload of `1000h:00` to each node ID that was not heard. The client's node ID must be unused and is excluded from discovery. The identity of every node found is then printed from `1018h:00`–`04`, which, like `1000h:00`, is allowed without a peer file. Pass `--peer-description` with an EDS or DCF to read only the objects that file lists. An EDS applies to every node; a DCF applies only to the node ID it was commissioned for.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --help
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --active-scan
```
