# CANopen bus scan

Listens for CANopen heartbeats and boot-up, then prints the node IDs it heard.

Every CANopen device has a node ID from 1 to 127. Devices that are switched on announce themselves
once (*boot-up*) and many then repeat an "I'm alive" message (*heartbeat*). This tool listens for
those; it is the CANopen equivalent of asking "who is on this cable?". On its own it never sends
a thing, so it is safe to point at a machine that is running.

## Try it without hardware

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --demo
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --demo --active-scan
```

`--demo` starts two simulated devices on a virtual bus and scans that: node 5 sends heartbeats,
node 9 stays silent. The first run finds only node 5 — listening cannot see a device that says
nothing. The second run adds `--active-scan`, which asks the silent node IDs directly and finds
node 9 too, then prints what each device says about itself. Without `--demo` the tool scans
`virtual://canopen-scan/0`, where nobody is, and reports "No CANopen devices detected".

## Against a real bus

Pass the adapter's endpoint and bit rate, for example
`--endpoint socketcan://can0 --bitrate 500000` (and reference that adapter's NuGet package from
the `.csproj`). Leave `--active-scan` off on a running machine unless you are sure that sending
an SDO read to every free node ID is acceptable there.

## Details

The default is listen-only ([#131](https://github.com/dborgards/CanKit.Pro/issues/131) decision 3). `CanOpenDiscovery.ListenAsync` collects heartbeats and boot-ups for `--heartbeat-ms` (default 2000). Either one is enough for a node to count. No CANopen node is opened, so nothing is transmitted: no boot-up, and no answer to NMT, SDO, or node guarding. Silent node IDs are not probed.

`--active-scan` opens a node after the listen (`--client-node`, default 127) and calls `CanOpenDiscovery.ScanAsync`. That sends an SDO upload of `1000h:00` to each node ID that was not heard. The client's node ID must be unused and is excluded from discovery. The identity of every node found is then printed from `1018h:00`–`04`, which, like `1000h:00`, is allowed without a peer file. Pass `--peer-description` with an EDS or DCF to read only the objects that file lists. An EDS applies to every node; a DCF applies only to the node ID it was commissioned for.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --help
dotnet run --project samples/CanKit.Pro.Sample.CanOpenBusScan -- --active-scan
```
