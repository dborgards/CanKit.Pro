# CANopen bus scan

Listens for CANopen heartbeats and boot-up, then prints the node IDs it heard.

The default is listen-only ([#131](https://github.com/dborgards/CanKit.Pro/issues/131) decision 3). It subscribes to heartbeat and boot-up frames and does not open a CANopen node, so it transmits nothing: no boot-up, and no answer to NMT, SDO, or node guarding. Silent node IDs are not probed.

`--active-scan` is what opens a node (`--client-node`, default 127) and probes node IDs that did not heartbeat. That node ID must be unused and is excluded from discovery. `1000h:00` and `1018h:00`–`04` are allowed on that path without a peer file. Pass `--peer-description` with an EDS or DCF to read only the objects that file lists. An EDS applies to every node; a DCF applies only to the node-id it was commissioned for.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --help
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --active-scan
```
