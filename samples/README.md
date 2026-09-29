# Samples

Runnable programs that show each CanKit.Pro layer working. None of them needs CAN hardware or a
second computer: they run on CanKit's **virtual** adapter, where CAN buses opened with the same
session name are wired together inside your process — like two devices on one cable.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repository root:

```bash
dotnet run --project samples/CanKit.Pro.Sample.IsoTpQuickstart
```

## Where to start

New to CAN? Read the samples in this order; each builds on the one before.

| # | Sample | What you learn | Level |
| - | ------ | -------------- | ----- |
| 1 | [`Demux`](CanKit.Pro.Sample.Demux) | Several parts of a program reading one bus without stealing frames from each other; "was that frame really sent?"; building J1939 IDs | first steps |
| 2 | [`IsoTpQuickstart`](CanKit.Pro.Sample.IsoTpQuickstart) | Sending messages longer than one CAN frame (ISO 15765-2) | first steps |
| 3 | [`UdsQuickstart`](CanKit.Pro.Sample.UdsQuickstart) | Talking to a car ECU as a diagnostic tester: sessions, reading values, refusals | needs 2 |
| 4 | [`J1939Quickstart`](CanKit.Pro.Sample.J1939Quickstart) | The protocol of trucks and engines: addresses, parameter groups, signals | first steps |
| 5 | [`CanOpenQuickstart`](CanKit.Pro.Sample.CanOpenQuickstart) | Configuring a device over SDO and streaming its data as a PDO (CANopen) | first steps |
| 6 | [`CanOpenBusScan`](CanKit.Pro.Sample.CanOpenBusScan) | A small command-line tool: which CANopen devices are on this bus? | tool, not a tutorial |

The tutorials 1–5 share one shape: a comment block at the top says what the program does and
defines the words it uses, then numbered steps print what they are doing, so the output reads as a
narration of the code. Read `Program.cs` from the top; each step is only a few lines. Where a
sample needs a stand-in for something real (the ECU in `UdsQuickstart`), it lives in a clearly
marked section at the bottom that you can skip.

## Extras for professionals: `--pro`

Every quickstart runs its beginner path by default. Add `--pro` (after `--`, so that `dotnet`
passes it to the program) for the extra section, which goes past the happy path:

```bash
dotnet run --project samples/CanKit.Pro.Sample.UdsQuickstart -- --pro
```

| Sample | What `--pro` adds |
| ------ | ----------------- |
| `Demux` | Predicate filters and runtime `Reconfigure`; detecting overlapping subscriptions; bounded queues (a slow reader loses the oldest frames, never blocks the bus); `ProtocolActor`, deadlines that are actually checked, and bus-state monitoring |
| `IsoTpQuickstart` | Receiver-side flow control (block size, STmin); `IsoTpTimeoutException` and its timers; several channels on one bus service; CAN FD with 64-byte frames |
| `UdsQuickstart` | NRC 0x78 "response pending" and the P2/P2\* timers; `UdsTimeoutException`; security access (seed/key); writing values; TesterPresent keep-alive |
| `J1939Quickstart` | Periodic sending; requesting a parameter group; an address conflict and the fallback to an arbitrary address; composing and decomposing 29-bit IDs |
| `CanOpenQuickstart` | Heartbeat producer/consumer and timeouts; emergency (EMCY) messages; SDO aborts |

`CanOpenBusScan` has its own switches; `--help` lists them. `--demo` starts two simulated
devices so the scan has something to find without hardware — run it plain, then again with
`--active-scan`, and compare.

## From loopback to real hardware

The samples open buses with endpoint strings like `virtual://my-session/0`. To use a real
adapter you change that string and add the adapter's NuGet package to the sample's `.csproj`:

- `CanBus.Open("socketcan://can0", ...)` for Linux SocketCAN (package `CanKit.Adapter.SocketCAN`),
  `zlg://USBCANFD-200U?index=0#ch1` for a ZLG device, and analogous strings for PCAN, Kvaser,
  Vector and ControlCAN — the [CanKit](https://github.com/pkuyo/CanKit) documentation gives the
  syntax for each adapter.
- Match the bit rate to your bus (the samples use 500 kbit/s, classic CAN 2.0).
- Drop the stand-ins. The samples open *two* buses because one plays "the rest of the world";
  on a real bus that is your car, your machine or your test bench.

CanKit.Pro itself does not change: everything above the bus (ISO-TP, UDS, J1939, CANopen,
demultiplexing) works the same on every adapter.

## Reading the output

- Lines starting with a number or letter (`3.`, `B.`) are steps; the indented lines below
  them are results.
- A sample that ends with an unhandled exception is a bug in the sample or an environment
  problem — please open an issue. Exceptions that the sample *catches on purpose* (a refused
  request, a timeout) are printed as part of the story.
- Every wait in the quickstarts has a timeout, so one never hangs silently; the worst case is a
  clear error after 5–20 seconds.
