# CanOpenLss

A small commissioning tool for CANopen Layer Setting Services (CiA 305). It drives
`LssCommissioning` in `CanKit.Pro.CANopen`: Fastscan for unconfigured slaves, a selective
configure or inquire of one slave by its LSS address, and an SDO read of `1018h` for slaves
that already have a node-id.

No hardware is required for `--demo`. It starts three unconfigured LSS slaves and one
configured node on the virtual bus.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- scan --demo
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- scan --demo --all --client-node 126
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- assign-unconfigured --demo --first-node-id 16 --store
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- inquire --demo --vendor 1 --product 1 --revision 1 --serial 1
```

On a real bus, drop `--demo` and pass the adapter endpoint. The bit rate has to match the
slaves. Activating a new bit timing waits both switch delays and does not reconfigure the
open channel; reopen the adapter afterwards if the slaves actually changed rate.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- scan --endpoint socketcan://can0 --bitrate 500000
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- assign \
  --endpoint socketcan://can0 \
  --vendor 0x10 --product 0x20 --revision 0x30 --serial 0x40 \
  --node-id 0x11 --selector 0 --index 2 --activate-ms 100 --store
```

`--selector` and `--index` are the LSS bit-timing table. Selector 0 is the CiA 301 table
(index 2 is 500 kbit/s, index 3 is 250 kbit/s). `--store` asks the slave to keep the pending
node-id and bit timing. The demo slaves keep that only in memory. `--node-id` writes the
pending node-id. A slave that already has an active node-id keeps it until NMT reset
communication; this tool does not send that reset.

`scan` without `--all` lists unconfigured slaves and leaves the network in LSS waiting.
`--all` listens for heartbeats and boot-up (`--heartbeat-ms`, default 400) and reads
`1018h:01`–`04` of those nodes through `--client-node`, which must be unused. `--probe-all`
asks every node-id from 1 to 127 at once and then reads `1018h` of the nodes that answer.
`assign` selects one slave. `assign-unconfigured`
gives each unconfigured slave the next node-id, starting at `--first-node-id`.
`--activate-ms` is accepted only together with `--count 1`, so one slave does not change rate
before the next is found.

`detect` is CiA 801 automatic bit-rate detection. It opens each candidate bit rate in
listen-only mode and reports the first that receives a frame, so it needs traffic on the bus
(a heartbeat, SYNC, or SDO reads). It transmits nothing.

```bash
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- detect --demo
dotnet run --project samples/CanKit.Pro.Sample.CanOpenLss -- detect \
  --endpoint socketcan://can0#netlink --candidates 500000,250000,125000 --window-ms 2000
```

`--candidates` is a comma-separated list in try order (default 500/250/125 kbit/s first, then
the rest of the CiA 301 table). `--window-ms` is the listen time per candidate, `--error-frames`
how many errors rule a rate out (default 2), `--passes` how many times to walk the list. On the
demo bus the loopback ignores the configured rate, so the first candidate wins; on real hardware
the reported rate is the one the bus actually runs. Reopen the adapter at that rate afterwards.

`--allow-non-listen-only` opens each probe in normal mode instead of listen-only, for an adapter
that rejects listen-only but is silent in normal mode. This is reactive: a wrong candidate may put
error frames on the bus, against the spirit of CiA 801 §5.2.1. Use it only where that is acceptable.

The adapter has to actually apply each candidate rate, or every probe listens at the interface's
existing rate and the first one with traffic is reported regardless of its value. On SocketCAN
that means the `#netlink` endpoint, as above: CanKit sets the bit rate and listen-only mode only
when netlink is enabled (it reconfigures the interface, which needs the privilege to bring it
down and up), otherwise the interface must already be at the right rate — at which point there is
nothing to detect. Adapters whose `Baud(...)` takes effect on open (PCAN, Kvaser, Vector) need no
such suffix.
