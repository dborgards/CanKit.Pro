# CanKit.Pro

**Higher CAN protocol layers for .NET, built on [CanKit](https://github.com/pkuyo/CanKit).**

[![CI](https://github.com/dborgards/CanKit.Pro/actions/workflows/ci.yml/badge.svg)](https://github.com/dborgards/CanKit.Pro/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-dborgards.github.io%2FCanKit.Pro-10233f)](https://dborgards.github.io/CanKit.Pro/)

CanKit gives .NET a single, fast, vendor-neutral API for raw CAN and CAN FD frames. CanKit.Pro
adds the layer above it — the plumbing every real protocol stack (ISO-TP, J1939, CANopen, UDS)
needs and that people otherwise rebuild, slightly differently and slightly wrong, in each stack:

- several protocol instances sharing **one** bus without fighting over `ReceiveAsync`,
- a documented threading model instead of ad-hoc locks and busy loops,
- timeouts that are actually checked rather than merely stored,
- validated CAN-ID and J1939 field arithmetic,
- one answer to "was this frame really sent?", whatever the adapter does about TX echo.

> **CanKit.Pro is not a fork of CanKit.** It consumes CanKit from nuget.org exactly like your own
> application does. Adapters, `ICanBus`, frames and timing stay upstream, where they belong;
> this repository ships only the layers above.

## Packages

Published to nuget.org, versioned and released together.

**L2 — the raw-CAN service layer.**

| Package | What it gives you | Depends on |
| --- | --- | --- |
| [`CanKit.Pro.RawCan`](src/CanKit.Pro.RawCan) | Multi-protocol demultiplexing: N independent, filtered, read-only views of one `ICanBus`, reconfigurable at runtime. Plus `SendConfirmed`, a uniform TX-confirmation over hardware echo. | `CanKit.Abstractions` |
| [`CanKit.Pro.Actor`](src/CanKit.Pro.Actor) | `ProtocolActor`: single-mailbox, single-writer execution with an event-driven timer queue and one background-exception channel. | — |
| [`CanKit.Pro.Addressing`](src/CanKit.Pro.Addressing) | Validated 11/29-bit CAN IDs, J1939 PGN/priority/PDU/source-address composition, J1939 NAME and PGN catalogues. | — |
| [`CanKit.Pro.Reliability`](src/CanKit.Pro.Reliability) | Deadlines whose expiry is guaranteed to be checked, and a `BusStateMonitor` that pushes `ErrWarning`/`ErrPassive`/`BusOff` transitions and recovery. | `CanKit.Abstractions`, `CanKit.Pro.Actor` |

**L3/L4 — transports and application protocols.**

| Package | What it gives you |
| --- | --- |
| [`CanKit.Pro.IsoTp`](src/CanKit.Pro.IsoTp) | ISO 15765-2: SF/FF/CF/FC codec, bounds-checked PCI parsing, STmin handling, and an actor-driven `IIsoTpChannel` over CAN and CAN FD. |
| [`CanKit.Pro.J1939Tp`](src/CanKit.Pro.J1939Tp) | SAE J1939-21 transport: TP.BAM broadcast and TP.CM connection mode (RTS/CTS/EndOfMsgAck), multi-session. |
| [`CanKit.Pro.CANopen`](src/CanKit.Pro.CANopen) | CiA 301: SDO client/server incl. block transfer, static and dynamic PDO mapping, NMT, heartbeat and node guarding, EMCY, object dictionary. |
| [`CanKit.Pro.J1939`](src/CanKit.Pro.J1939) | J1939 node: address claim with arbitrary-address fallback, fixed-rate periodic send, SPN catalogue over J1939-71. |
| [`CanKit.Pro.Uds`](src/CanKit.Pro.Uds) | ISO 14229-1 client over ISO-TP: session control, security access, read/write by identifier, routine control, upload/download, P2/P2\* timing and 0x78 response-pending. |

Everything targets `netstandard2.0` and `net10.0`.

## Install

```bash
dotnet add package CanKit.Pro.RawCan

# CanKit itself: the core, plus the adapter for the hardware you talk to
dotnet add package CanKit.Core
dotnet add package CanKit.Adapter.Virtual    # loopback, no hardware
# dotnet add package CanKit.Adapter.PCAN     # or Kvaser, Vector, SocketCAN, ZLG, ControlCAN
```

## Two minutes

```csharp
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.RawCan;
using CanKit.Pro.Reliability;

using var bus = CanBus.Open("virtual://demo/0",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

using var service = new CanBusService(bus);

// One bus, two protocol instances, two disjoint ID ranges — neither starves the other,
// and a slow consumer cannot block the fast one.
using var isoTp = service.Subscribe(CanIdFilter.Range(0x700, 0x7FF));
using var j1939 = service.Subscribe(view => view.IsExtendedFrame);

await foreach (var frame in isoTp.Frames.WithCancellation(token))
{
    // A read-only CanFrameView: no ownership, no disposal, no aliasing surprises.
}

// "Did it actually go out?" — a real echo match where the bus provides one, driver acceptance
// otherwise, and flagged so you can tell which you got.
var tx = await service.SendConfirmed(CanFrame.Classic(0x123, new byte[] { 1, 2, 3 }));
if (!tx.Confirmed) Console.WriteLine(tx.FailureReason);  // Timeout, BusOff or Rejected — never a hang

// Timeouts and bus health, on the protocol instance's own single-threaded loop.
using var actor = new ProtocolActor();
using var monitor = new BusStateMonitor(bus, actor);
monitor.StateChanged += (_, e) => { if (e.Current.IsTransmitBlocked()) AbortActiveTransfer(); };

var deadline = new DeadlineScheduler(actor).Arm(TimeSpan.FromMilliseconds(150), OnTimeout);
```

A runnable version, needing no hardware:

```bash
dotnet run --project samples/CanKit.Pro.Sample.Demux
```

## Documentation

The project website, [dborgards.github.io/CanKit.Pro](https://dborgards.github.io/CanKit.Pro/),
renders everything below plus the per-package READMEs with navigation and search.

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | Install, open a bus, the packages in context |
| [Architecture (arc42)](docs/architecture/arc42-CanKit.Pro.md) | Layer model L0–L4, building blocks, runtime views, ADRs — German |
| [Requirements (SRS)](docs/requirements/SRS-CanKit.Pro.md) | The `FR-RAW-*` requirements the code and tests cite — German |
| [Release process](docs/release-process.md) | GitVersion + semantic-release, how a commit becomes a NuGet package |
| [Licensing](docs/licensing.md) | Why this is MIT although it grew out of an Apache-2.0 fork |
| [Migration from CanKit.Pro.legacy](docs/migration-from-legacy.md) | What moved, what stayed, what changed on the way |
| [Contributing](CONTRIBUTING.md) | Branching, Conventional Commits, running the tests |

## Roadmap

The layer model these packages implement (L2, "Raw-CAN service layer") exists to carry the layers
above it. In rough order:

- **Validate the L3/L4 packages against real hardware and foreign stacks.** They are published,
  implemented and tested, but so far only against this repository's own implementation and the
  loopback adapter — not against conformance testers or third-party ECUs.
- Source generators for object dictionaries and PGN definitions; DBC and EDS import.
- XCP, DeviceNet, CANopen Safety.

Architecture and requirements for all of these are in
[docs/](docs/architecture/arc42-CanKit.Pro.md).

## Versioning

Every package shares one version, derived from the commit history:
[Conventional Commits](https://www.conventionalcommits.org/) on `main` drive
[semantic-release](https://semantic-release.gitbook.io/), which decides the number, writes the
changelog, tags, and publishes to nuget.org. Builds that are not releases are stamped by a
[GitVersion](https://gitversion.net/) step in the pipeline, so a CI artifact is identifiable
without being a release. See [docs/release-process.md](docs/release-process.md).

## License

[MIT](LICENSE). CanKit is a separate project under Apache-2.0 — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [docs/licensing.md](docs/licensing.md).

Not affiliated with the CanKit project.
