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

## Which branch this came from

`develop`, not `main`. The legacy repository keeps `main` as its release branch and `develop` as
its integration branch, and CanKit.Pro's development lives on `develop` — 486 commits ahead of
`main` at the time of the migration, and the difference is most of the product: `main` had only the
four L2 packages, while the entire transport and application stack is on `develop`.

## What moved

| | |
| --- | --- |
| `src/core/CanKit.Pro.{Actor,Addressing,RawCan,Reliability}/` | → `src/CanKit.Pro.*/` — **source unchanged** apart from comments referring to fork-internal files |
| `src/transports/CanKit.Pro.{IsoTp,J1939Tp}/` | → `src/CanKit.Pro.*/` |
| `src/protocols/CanKit.Pro.{CANopen,J1939,Uds}/` | → `src/CanKit.Pro.*/` |
| The Pro test suites in `tests/CanKit.Tests/TestCases/` | → `tests/CanKit.Pro.Tests/TestCases/`, adapted (below) |
| The five Pro quickstart samples | → `samples/CanKit.Pro.Sample.*/` |
| `docs/architecture/`, `docs/requirements/`, `docs/reviews/` | → `docs/`, with headers that mark CanKit as external context |

The flat `src/CanKit.Pro.<Name>/` layout replaces the legacy `core/transports/protocols` split: in
a repository whose every project is a `CanKit.Pro.*` package, the extra directory level encoded a
layer that the package name already states.

`CanKit.Pro.Vendor` (the generic extension framework for a customer's confidential private
protocol) moved over with everything else initially, but was later removed from this public
repository entirely and continues as a separate, internal-only repo — its presence here was never
more than the generic SPI extension point, and even that is confidential enough not to belong in
a public source history.

### Publishing

All `CanKit.Pro.*` packages built from this repo publish to nuget.org. The four L2 packages
(`Actor`, `Addressing`, `RawCan`, `Reliability`) were first; `CanKit.Pro.{IsoTp,J1939Tp,CANopen,
J1939,Uds}` initially kept `IsPackable=false` — the legacy repository's own assessment
(`publish: false` in its `eng/packages.json`), since they were pre-release and this migration was
not the moment to overrule that — until their APIs settled enough to ship. They were built and
tested on every CI run throughout, so they could not rot silently while unpublished.

## What did not move

**Everything that is CanKit.** `CanKit.Abstractions`, `CanKit.Core`, the adapters and
`CanKit.Transport.IsoTp` stay in the legacy repository under Apache-2.0 — including the fork's own
improvements to them:

- the frame-ownership / lifetime contract (`CanFrame.Duplicate`, `Dispose` honouring `OwnMemory`,
  per-recipient copies in `VirtualBusHub.Broadcast`),
- `VirtualBusHub`'s registry rework (atomic `Join`, hubs removed when the last member leaves),
- `IsEcho` flagging on the Virtual adapter's self-echo, and declaring `CanFeature.Echo` in its
  `StaticFeatures` (every vendor adapter declares it; the loopback adapter has the capability via
  `ChannelWorkMode.Echo` but never said so),
- four `CanKitErrorCode` members for protocol failures (see below),
- `IPeriodicTx.Faulted`, `AsyncFramePipe`, `SoftwarePeriodicTx`, `PreciseDelay`, `CanRegistry` and
  `CanEndpoint` improvements, and a series of adapter fixes (SocketCAN `ctrlmode`, ZLG and
  ControlCAN periodic-TX index handling),
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

### The four protocol error codes

`CanKitErrorCode` reserves the 6000 range for transport and protocol errors, and published CanKit
defines exactly one of them: `TransportOperationFailed = 6001`. The fork added four more —
`ProtocolTimeout`, `ProtocolPeerAbort`, `ProtocolNegativeResponse`, `AddressClaimFailed` — and five
packages report them. This is the only place where the whole 18k-line stack needed something from
CanKit that nuget.org does not have.

It could not simply be dropped: NFR-006 (arc42 ADR-12) requires every L3/L4 failure to be a
`CanKitException` whose `ErrorCode` says which kind of protocol failure it was, and there is a test
asserting the exact mapping for all five packages. Collapsing the four onto
`TransportOperationFailed` would have deleted a documented requirement, not a nicety.

`ProtocolErrorCodes` in `CanKit.Pro.RawCan` declares them as constants of the enum type carrying
the fork's own numeric values (6002-6005). The numbers a caller sees are byte-for-byte what the
fork produced, so nothing downstream changes; and if CanKit adopts these codes upstream,
`ErrorCode == CanKitErrorCode.ProtocolTimeout` starts being true for already-compiled callers and
this file just goes away. The cost until then is that `ErrorCode.ToString()` renders the number
instead of a name — the exception type is more specific than the code anyway.

Upstreaming those four members is the clean fix and is tracked in
[upstream-candidates.md](upstream-candidates.md).

### Tests: adapter internals → in-repo doubles

Three test classes depended on the fork's private changes to `CanKit.Adapter.Virtual`. Against the
*published* adapter they would have failed, or worse, passed while testing nothing:

| Test | Depended on | Now |
| --- | --- | --- |
| `TxConfirmTests` (echo paths) | Two fork changes to the Virtual adapter: `IsEcho = true` on its self-echo (upstream echoes the frame but does not flag it, so no echo would ever have matched) and `CanFeature.Echo` in its declared `StaticFeatures` (without it `SendConfirmed` takes the approximated path and the echo assertions fail). | `ControllableBus` — the test states which echo arrives, and "no echo ever arrives" is a setting rather than a never-matching software filter. `EchoCapableOptions` supplies the declared capability, since whether an adapter declares one is the adapter's business, not ours. |
| `BusStateMonitorTests` | Reflection into `VirtualBusHub._hubs` to reach `SetBusState`. | `ControllableBus.BusState` is settable. No reflection, no dependency on another package's private statics. |
| `RawCanSubscriptionTests.Buffered_Frame_Survives_…` | The fork's per-recipient `Duplicate` in `Broadcast`, which made RX frames allocator-owned so the poisoning allocator could prove aliasing. Upstream RX frames are not owned, so the test would have passed without exercising anything. | The test hands out an owned, pooled frame through `ControllableBus` and disposes it at an exact point. |
| `IsoTpBusOffTests`, `TxConfirmTests` bus-off case | A `VirtualBusControl` reflection helper reaching into `VirtualBusHub._hubs`, `_hubsGate` (a field the fork added) and `VirtualBus._exceptions`. | `ControllableBus.BusState` and `RaiseFault`. The helper is gone. |

The rest — `AddressingTests`, `ProtocolActorTests`, `DeadlineTests`, `CanIdFilterOverlapTests`,
and the non-echo half of `TxConfirmTests` — moved essentially as they were, with
`TestCaseProvider` swapped for `VirtualAdapterFixture`.

This is a strict improvement independent of the fork question: a test that reaches into another
package's internals is testing that package, and a test whose premise silently evaporates when a
dependency changes is worse than no test.

### Timing tests: one runner → three

The legacy pipeline built on Linux. This one builds on Linux, Windows and macOS, and the first
three-OS run failed five assertions that had never been wrong before — all of them wall-clock
measurements of protocol timing.

The shared cause was xUnit's default: test classes run in parallel, so roughly thirty of them —
each with its own actor loop and timer queue — competed for three cores while measuring
milliseconds. `tests/CanKit.Pro.Tests/xunit.runner.json` turns collection parallelism off. It
costs about a minute of wall clock per run and makes every timing assertion mean what it says.

Four tests needed more than that:

| Test | Was | Now |
| --- | --- | --- |
| `StartPeriodicSend_SingleFrame_FiresAtConfiguredPeriod` | Mean inter-arrival over 8 samples against an 80 ms period. One 500 ms stall on macOS moved the mean to 232 ms. | Median over 10 samples against a 120 ms period — above the 15.6 ms Windows timer granularity, and a single stall no longer decides the result. |
| `StartPeriodicSend_MultiFrame_KeepsFixedRate_Without_SendTime_Drift` | Total span of 8 emissions ≤ 1.3 × the 7 × 200 ms grid. | Every gap between announces must be a whole number of periods. `PeriodicSchedule` *drops* a tick whose previous emission is still in flight rather than queueing it, so a slow runner legitimately produces 2 × period gaps — on the grid, but far outside a total-span bound. The period is now derived from a measured BAM emission (twice its cost), which puts the send-then-delay hypothesis exactly half a period off the grid on whatever runner this is; on Windows the measured cost is ~140 ms rather than Linux's ~90 ms, because the timer granularity stretches every Th pause. |
| `ClaimAddressAsync_CancelAtArbitrationDeadline_NeverLeavesStateStuckInClaiming` | `Task.Delay(50)`, then assert the state is no longer `Claiming`. | Poll until it settles, bounded by the test timeout. The invariant is that the state leaves `Claiming`, not that it does so within fifty milliseconds. |
| `Functional_Collect_Does_Not_Accept_Frames_After_Window_Expiry` | 40 ms collection window, inject the late frame at 80 ms. | 200 ms window, inject at 400 ms. `CollectResponsesAsync` arms its window on a continuation, so under load the window could still be open at 80 ms and admit the frame the test says must be rejected. |

None of these weakened an assertion: three restate the same property in a way that does not depend
on how busy the runner is, and the fourth replaces a threshold that could not distinguish a
dropped tick from the drift it was written to catch.

## For consumers of the legacy packages

There are none: the `CanKit.Pro.*` package IDs were never published from the legacy repository —
`eng/packages.json` did not list them. The first release from this repository is the first release
of these packages, so there is no upgrade path to document and no compatibility to preserve.

Namespaces and public API are unchanged. Code written against the libraries inside the fork
compiles against the published packages as-is.
