# CANopen conformance runner

Asks a CANopen device the questions CiA 301 and CiA 305 answer — and, where an EDS or DCF is
given, the questions that file answers — then writes up **what works, what does not, whose fault
that is, and what was never checked**. It runs on PEAK, Kvaser and Vector hardware, and with
`--simulate` on CanKit's virtual adapter with no hardware at all.

It is a diagnostic tool, not part of the packages: it is not packed, and CI only builds it.

## Why it is here and not in `tests/` or `samples/`

- `tests/CanKit.Pro.Tests` is in the solution, and CI runs `dotnet test CanKit.Pro.sln` on Linux,
  Windows and macOS with no CAN hardware. A hardware suite there would be a permanently skipped
  shell. SRS §7 puts hardware-in-the-loop tests outside the standard CI gate (assumption A-5) and
  names an "HIL sample" as the verification of `FR-CO-001..059`; §9 item 5 records that it has
  never existed. This is that.
- `samples/README.md` promises in writing that no sample needs hardware or a second computer.
- `tools/CanKit.Pro.EchoProbe` already has this shape — vendor adapter references, a markdown and
  JSON report, a self-contained Windows publish — and is the precedent followed here.

## Wiring

One CAN interface for this tool and the device under test on the same bus, terminated with
120 ohm at both ends, both at the same bit rate. **250 kbit/s classic CAN is the default**;
`--bitrate` changes it.

The read tier does not change anything the device holds, but it is not a listen-only tap: an SDO
upload has to transmit, and a listen-only interface acknowledges nothing, which on a two-node bus
stops the device transmitting at all. So the interface participates.

## Drivers

The tool brings the CanKit adapters; the vendor drivers must be installed on the machine
(`PCANBasic.dll` for PEAK, `canlib32.dll` for Kvaser, the XL driver for Vector). A missing driver
shows up as a failure to open the bus, which the report attributes to the transport and not to the
device.

## Running

On Windows from the published folder (no .NET needed, it is self-contained):

```powershell
.\CanKit.Pro.CanOpenConformance.exe --endpoint kvaser://0 --node 17 --eds .\device.eds
.\CanKit.Pro.CanOpenConformance.exe --endpoint pcan://PCAN_USBBUS1 --node 17 --eds .\device.eds --tier write
```

From source, with the .NET 10 SDK:

```bash
dotnet run --project tools/CanKit.Pro.CanOpenConformance -c Release -- \
  --endpoint pcan://PCAN_USBBUS1 --node 17 --eds path/to/device.eds --tier write
```

Without hardware — the tool's own smoke test, which also shows what a report looks like:

```bash
dotnet run --project tools/CanKit.Pro.CanOpenConformance -c Release -- \
  --endpoint virtual://conformance/0 --node 17 --simulate --tier commission
```

`--help` lists every option. Exit code 0 when no check failed, 1 when one did (or the bus could
not be opened), 2 on a usage error.

## Pass an EDS or DCF

`--eds` is not a nicety. CanKit.Pro's peer-SDO gate transfers only `1000h:00`, `1001h:00` and
`1018h:00`–`04` to a node with no device description bound, so without one the runner is *not
allowed* to ask about the heartbeat time, the PDO parameters, SYNC, node guarding or parameter
storage — those checks report themselves as skipped for want of the file. With the file bound,
every readable object it declares is uploaded from the device and compared against what the file
says: an object the file declares and the device does not have, or one whose length differs, is a
finding either way round, and the report says which of the two it cannot decide between.

The single most useful first run is therefore `--eds <the device's own file>`.

## Safety tiers

A conformance run against a commissioned machine must not reconfigure it by accident, so how deep
it may go is chosen on the command line and never inferred. Every check declares the tier it needs
and is reported as *not run* below it.

| `--tier` | What it may do |
| --- | --- |
| `read` (default) | Reads and passive observation. Nothing the device holds is changed. |
| `write` | Adds SDO downloads — each restored to the value read first — NMT state commands, SYNC produced by this tool, and the LSS state switch. Volatile: nothing reaches the device's non-volatile memory. The NMT state is put back where it was found, including after Ctrl+C. |
| `commission` | Adds what outlives a power cycle or changes the device's identity on the bus: LSS Fastscan selection and `1010h` store. **Do not point this at a working machine.** |

## What it checks

| Group | Checks |
| --- | --- |
| `bus` | The adapter's capabilities, and whether the interface is already error-passive or bus-off before anything is sent — which explains every later timeout if it is. |
| `discovery` | Passive presence (heartbeat, boot-up) and an active scan by SDO upload of `1000h:00` per node-id. |
| `identity` | `1000h` device type, `1001h` error register with its bits named, the whole `1018h` identity object, and `1008h`/`1009h`/`100Ah` — the last of which forces segmented SDO on the device's own data. |
| `sdo` | Expedited upload round-trip latency, the abort code for a non-existent object (CiA 301 asks for `0x06020000`), and the refusal of a write to a read-only object. |
| `eds` | The file parses; its `1018h` matches the device's; every readable object it declares exists on the device at the declared length. |
| `heartbeat` | `1017h`, the heartbeat actually observed on `700h`+node-id with its period and spread, and whether the stack's consumer reports the beats without spurious timeouts. |
| `guarding` | `100Ch`/`100Dh` node guarding parameters. |
| `nmt` | Pre-operational → operational → stopped → pre-operational, each step verified against the heartbeat's state byte, then the device returned to the state it was found in. |
| `sync` | `1005h`/`1006h`, and whether synchronous TPDOs follow a SYNC this tool produces. |
| `pdo` | TPDO and RPDO communication and mapping parameters read from the device (not assumed from the defaults, because a remapped device is the case a default gets wrong), TPDOs observed in Operational, and — the one that matters when a machine is switched off — that none are transmitted in Stopped. |
| `emcy` | Emergency objects on `80h`+node-id, decoded. Passive: nothing can make a conforming device raise one on request, and the report says so rather than reporting silence as a pass. |
| `lss` | Unconfigured slaves, selective addressing by the device's own `1018h` address with the inquired address and node-id compared back, and Fastscan. |
| `store` | `1010h`/`1011h` capability, the `save` signature, and that a wrong signature is refused. |

`--groups` selects a subset. What a run did **not** measure is a section of the report in its own
right, including the services this runner does not address at all (SDO block transfer, MPDO, CiA
304 safety/SRDO, the flying master, LSS bit-timing changes, boot-up after a power cycle) so that
nobody reads their absence as a pass.

## The report

A `.md` and a `.json` named `canopen-conformance-<label>-<time>` in the current directory (or
`--out`), and the findings on the console as they happen. The markdown has, in order: the run's
parameters, the verdict counts, **where the unresolved findings point**, what was tested (the
device's own identity, so the report can be filed against something), what does not work grouped
by attribution, what could not be concluded, what works, the coverage table, every error that
occurred, every check in order, and how to read it.

Each unresolved finding carries an attribution, which is the column the report exists for:

| Attribution | Meaning |
| --- | --- |
| Device | The device under test. Take it to whoever ships its firmware. |
| Tool | This runner or the CanKit.Pro stack. **Open an issue against this repository** — not a device defect. |
| Transport | The bus, the adapter or its vendor driver: wiring, termination, bit rate, a missing driver. |
| Setup | How the run was invoked. Change the arguments and run again. |
| Unknown | Not attributable from what was observed. Stated as open rather than guessed. |

Two distinctions it keeps that are easy to get wrong, and both were wrong here first:

- An SDO abort the peer sent is the device refusing; one the client raised never reached the bus.
  But a *local* abort of `SdoProtocolTimedOut` means the server said nothing — that is the device,
  not the runner, and an empty-bus run blamed the runner for it until the classification looked at
  the abort code instead of just the origin.
- Every period is measured on **this host's** clock, not the device's. A loaded host widens the
  range without the device changing, so periods are reported with their spread (median and
  inter-quartile range, which one descheduled thread does not move) and never gated on.

## Publishing the Windows folder

```bash
dotnet publish tools/CanKit.Pro.CanOpenConformance -c Release -r win-x64 --self-contained \
  -p:DebugType=None -p:GenerateDocumentationFile=false -o artifacts/canopen-conformance-win
```

`Fixtures/simulated-device.eds` travels with it, so `--simulate` works from the published folder.
