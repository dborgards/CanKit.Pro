# Migration from CanKit.Pro.legacy

[CanKit.Pro.legacy](https://github.com/dborgards/CanKit.Pro.legacy) is a fork of
[CanKit](https://github.com/pkuyo/CanKit) in which the four `CanKit.Pro.*` libraries were first
built. CanKit.Pro is those libraries, extracted into a product of their own that depends on CanKit
through NuGet.

## Why stop forking

A fork made sense while the work needed changes *inside* CanKit — the frame-ownership contract and
the `VirtualBusHub` rework, for instance, both live below the API surface. It stopped making sense
once the valuable part was the layer on top:

- **Every upstream release became merge work.** A fork pays that tax forever, for code it does not
  want to own.
- **The packages could not ship.** `CanKit.Pro.*` packages built from a fork carry a *modified*
  `CanKit.Core` — either vendored, or as a dependency on a package the fork does not publish.
  Neither is something to put on nuget.org.
- **Users had to choose.** A consumer cannot take upstream CanKit *and* forked CanKit; the fork
  turned "add a package" into "switch your CAN stack".
- **The licensing followed the fork.** Carrying upstream's source means carrying its license.
  Consuming a package does not. See [licensing.md](licensing.md).

Depending on CanKit as a package inverts all four: upstream releases are a version bump, the
packages are publishable, users add CanKit.Pro to the CanKit they already have, and CanKit.Pro is
MIT.

## What moved

| | |
| --- | --- |
| `src/core/CanKit.Pro.{Actor,Addressing,RawCan,Reliability}/` | → `src/CanKit.Pro.*/` — **source unchanged** apart from comments referring to fork-internal files |
| The seven Pro test classes in `tests/CanKit.Tests/TestCases/` | → `tests/CanKit.Pro.Tests/TestCases/`, adapted (below) |
| `docs/architecture/`, `docs/requirements/`, `docs/reviews/` | → `docs/`, with headers that mark CanKit as external context |

## What did not move

**Everything that is CanKit.** `CanKit.Abstractions`, `CanKit.Core`, the adapters and
`CanKit.Transport.IsoTp` stay in the legacy repository under Apache-2.0 — including the fork's own
improvements to them:

- the frame-ownership / lifetime contract (`CanFrame.Duplicate`, `Dispose` honouring `OwnMemory`,
  per-recipient copies in `VirtualBusHub.Broadcast`),
- `VirtualBusHub`'s registry rework (atomic `Join`, hubs removed when the last member leaves),
- `IsEcho` flagging on the Virtual adapter's self-echo,
- assorted `PreciseDelay` and `QueuedTxCanBus` fixes.

Those are genuine improvements, and the right home for them is an upstream pull request. Until
then they exist only in the legacy fork, and CanKit.Pro must work without them — which shaped the
test migration below.

**The upstream test harness.** `TestCaseProvider`, `TestHelpers`, `TestMatrix` and
`EmptyTestDataProvider` are upstream's Apache-2.0 code, built to orchestrate a vendor-hardware
matrix driven by `CANKIT_TEST_ADAPTERS`. CanKit.Pro has no such matrix, so
`tests/CanKit.Pro.Tests/Infrastructure/` replaces them with two small purpose-built pieces.

**The release pipeline.** The legacy repository has nine CI workflows and a PowerShell pipeline
that detects per-package version bumps from `eng/package-versions.props` — a sensible design for a
fork tracking upstream's many independently-versioned packages. CanKit.Pro versions all four
packages together from the commit history instead, so the whole thing collapses into
[one CI workflow and one release workflow](release-process.md).

## What changed on the way

### Dependencies: `CanKit.Core` → `CanKit.Abstractions`

The libraries only ever used types from `CanKit.Abstractions` (`ICanBus`, `CanFrame`,
`CanFrameView`, `BusState`); `CanKit.Core` was inherited from the fork's project layout. The
package dependency is now the smaller, correct one. Applications still need `CanKit.Core` and an
adapter — that is what opens a bus — but they were already referencing those directly.

### Package metadata

`Authors` and `RepositoryUrl` pointed at `Pkuyo` / `pkuyo/CanKit`, correct for a fork and wrong
for a separate product. They now name this repository and its author, and the license expression
is `MIT`.

### Versioning

Per-package `<CanKitProActorVersion>` properties in `eng/package-versions.props` are gone. All
four packages share one version, derived from the commit history — see
[release-process.md](release-process.md).

### Tests: adapter internals → in-repo doubles

Three test classes depended on the fork's private changes to `CanKit.Adapter.Virtual`. Against the
*published* adapter they would have failed, or worse, passed while testing nothing:

| Test | Depended on | Now |
| --- | --- | --- |
| `TxConfirmTests` (echo paths) | The fork's `IsEcho = true` on the Virtual adapter's self-echo. Upstream echoes the frame but does not flag it, so no echo would ever have matched. | `ControllableBus` — the test states which echo arrives, and "no echo ever arrives" is a setting rather than a never-matching software filter. |
| `BusStateMonitorTests` | Reflection into `VirtualBusHub._hubs` to reach `SetBusState`. | `ControllableBus.BusState` is settable. No reflection, no dependency on another package's private statics. |
| `RawCanSubscriptionTests.Buffered_Frame_Survives_…` | The fork's per-recipient `Duplicate` in `Broadcast`, which made RX frames allocator-owned so the poisoning allocator could prove aliasing. Upstream RX frames are not owned, so the test would have passed without exercising anything. | The test hands out an owned, pooled frame through `ControllableBus` and disposes it at an exact point. |

The rest — `AddressingTests`, `ProtocolActorTests`, `DeadlineTests`, `CanIdFilterOverlapTests`,
and the non-echo half of `TxConfirmTests` — moved essentially as they were, with
`TestCaseProvider` swapped for `VirtualAdapterFixture`.

This is a strict improvement independent of the fork question: a test that reaches into another
package's internals is testing that package, and a test whose premise silently evaporates when a
dependency changes is worse than no test.

## For consumers of the legacy packages

There are none: the `CanKit.Pro.*` package IDs were never published from the legacy repository —
`eng/packages.json` did not list them. The first release from this repository is the first release
of these packages, so there is no upgrade path to document and no compatibility to preserve.

Namespaces and public API are unchanged. Code written against the libraries inside the fork
compiles against the published packages as-is.
