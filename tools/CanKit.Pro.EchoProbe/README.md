# EchoProbe

Measures what a CAN adapter does with the frames it transmits itself. It exists to settle
[#249](https://github.com/dborgards/CanKit.Pro/issues/249): CanKit.Pro's contract says that an
adapter which declares `CanFeature.Echo` and runs in `WorkMode.Echo` delivers its own transmits
flagged `IsEcho`, and the review found that PCAN (unflagged) and Vector (dropped in Echo mode,
flagged in Normal mode) do not behave that way. That was read from the adapters' source and
not measured. This tool measures it.

For each work mode (Normal, Echo) it records

- whether the adapter reports its own frames at all, and with which `IsEcho` value, next to what a
  second interface on the same bus saw (the ground truth),
- whether `CanBusService.SendConfirmedAsync` is confirmed, whether approximated, and how long it takes,
- whether a J1939 node can claim an address (`ClaimAddressAsync`) with the adapter's own echo coming
  back at it, and how many claim frames the peer saw.

It is a diagnostic tool, not part of the packages: it is not packed and CI only builds it.

## Wiring

Two CAN interfaces on **one** bus, terminated with 120 ohm at both ends, the same bit rate.

- **A** is the adapter under test.
- **B** is any second interface. It acknowledges A's frames on the wire (a CAN transmitter without
  a second node gets no acknowledgement and cannot send) and records what the bus carried.

Run once per adapter you want to know about, with the other one as B. A PEAK interface and the
Kvaser against each other covers both ways round.

## Drivers

The tool brings the CanKit adapters; the vendor drivers must be installed on the machine
(`PCANBasic.dll` for PEAK, `canlib32.dll` for Kvaser, the XL driver for Vector).

## Running

On Windows from the published folder (no .NET needed, it is self-contained):

```powershell
.\CanKit.Pro.EchoProbe.exe --a pcan://PCAN_USBBUS1 --b kvaser://0 --label pcan-usb-classic
.\CanKit.Pro.EchoProbe.exe --a pcan://PCAN_USBBUS2 --b kvaser://0 --fd --label pcan-usb-fd
.\CanKit.Pro.EchoProbe.exe --a kvaser://0 --b pcan://PCAN_USBBUS1 --label kvaser
```

From source, with the .NET 10 SDK:

```bash
dotnet run --project tools/CanKit.Pro.EchoProbe -c Release -- --a pcan://PCAN_USBBUS1 --b kvaser://0
```

Without hardware, as a dry run on the virtual adapter:

```bash
dotnet run --project tools/CanKit.Pro.EchoProbe -c Release -- --a virtual://probe/0 --b virtual://probe/1
```

Options: `--bitrate`, `--fd` and `--dbit`, `--modes normal,echo`, `--frames`, `--wait-ms`, `--label`,
`--skip-claim`, `--out`. `--help` lists them. `--fd` skips the J1939 claim (J1939 is classic CAN).

## What comes out

A `.md` and a `.json` file named `echo-probe-<label>-<time>` in the current directory, and the same
on the console. The `.md` has one row per work mode and the list of frames A reported for its own
transmits. Send the `.md` back, or paste it into the issue.

## Publishing the Windows folder

```bash
dotnet publish tools/CanKit.Pro.EchoProbe -c Release -r win-x64 --self-contained \
  -p:DebugType=None -p:GenerateDocumentationFile=false -o artifacts/echo-probe-win
```
