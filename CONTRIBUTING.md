# Contributing to CanKit.Pro

Thanks for helping. This page covers the three things that are specific to this repository:
how branches and commits work (they decide releases), how to run the tests, and where a change
belongs — here or upstream in CanKit.

## Does your change belong here?

CanKit.Pro sits **on top of** [CanKit](https://github.com/pkuyo/CanKit), which it consumes as a
NuGet package. It does not fork it.

- **Adapters, `ICanBus`, `CanFrame`, timing, the registry** → upstream, at
  [pkuyo/CanKit](https://github.com/pkuyo/CanKit). We cannot fix those here, and a workaround
  layered on top usually makes the eventual upstream fix harder.
- **Demultiplexing, actors and scheduling, deadlines and bus state, CAN-ID arithmetic, and the
  protocol stacks above them** → here.

Moving to a new CanKit version is a single change: bump `CanKitVersion` in
`eng/Dependencies.props` and let CI tell you whether anything broke.

## Getting set up

```bash
git clone https://github.com/dborgards/CanKit.Pro.git
cd CanKit.Pro
dotnet build CanKit.Pro.sln -c Release
dotnet test  CanKit.Pro.sln -c Release
```

You need the **.NET 10 SDK or newer** (`global.json` pins `10.0.100` and rolls forward to a later
major if that is what you have installed). No CAN hardware is needed — the whole suite runs on CanKit's `virtual://` loopback
adapter and on in-repo test doubles.

A local build produces version `0.0.0`, deliberately: nothing in the build computes a version, it
only receives one from the pipeline or from semantic-release. To see what GitVersion makes of your
working copy, ask it directly:

```bash
dotnet tool restore
dotnet gitversion
```

Optional but useful before pushing:

```bash
dotnet format CanKit.Pro.sln    # applies .editorconfig
```

## Branching

Trunk-based, one long-lived branch:

| Branch | Purpose |
| --- | --- |
| `main` | Always releasable. Every merge is analysed by semantic-release and may publish a release. |
| `feat/…`, `fix/…`, `docs/…` | Short-lived. Branch from `main`, open a pull request into `main`. |

There is no `develop` and no release branch: the version is computed from the commits, so a
staging branch would only add a place for the two to disagree.

## Commits and pull-request titles decide the release

The pull request is squash-merged, so **its title becomes the commit that semantic-release
reads**. It must be a [Conventional Commit](https://www.conventionalcommits.org/):

```
feat(rawcan): expose per-subscription drop counters      -> minor release
fix(actor): stop Dispose deadlocking on the loop thread  -> patch release
feat(addressing)!: rename ComposePgn parameters          -> major release
docs: explain the deadline rearm semantics               -> no release
```

Scopes match the packages: `rawcan`, `actor`, `addressing`, `reliability`, plus `docs`, `ci`,
`build`, `deps`.

A breaking change needs both the `!` marker and a footer that says what to do about it:

```
feat(rawcan)!: ISubscription.Frames requires a CancellationToken

BREAKING CHANGE: `Frames` is now `Frames(CancellationToken)`. Callers using
`await foreach (var f in sub.Frames.WithCancellation(token))` become
`await foreach (var f in sub.Frames(token))`.
```

Types that never release on their own: `docs`, `test`, `chore`, `ci`, `style`, `refactor`. Use
them honestly — labelling a behaviour change as `refactor` means it ships with no changelog entry
and no version bump, which is worse than a noisy changelog.

## Tests

Every behavioural change needs a test, and tests here are expected to be deterministic:

- **No hardware, no timing luck.** Use the `virtual://` loopback adapter for anything about real
  bus behaviour, and `ControllableBus` (`tests/CanKit.Pro.Tests/Infrastructure/`) when the test
  needs to control what the bus does — echo frames, bus state, whether a transmit is accepted.
  `ControllableBus.DeferredEchoCapable(...)` parks each TX echo in a `DeferredEchoQueue` instead
  of raising it inside `Transmit`, which is the only way to have two sends pending at once: a
  synchronous echo re-enters `CanBusService`'s pending-send lock on the transmitting thread, so
  the pending list never holds more than that thread's own entry. Reach for it whenever the
  behaviour under test is about how several in-flight sends relate to each other.
- **Do not test through an adapter's internals.** If a test needs reflection into another
  package's private state, it is testing that package, not ours; drive the scenario through the
  double instead.
- **Cite the requirement.** Tests reference `FR-RAW-*` IDs from
  [the SRS](https://github.com/dborgards/CanKit.Pro/blob/main/docs/requirements/SRS-CanKit.Pro.md) so a reader can tell intended behaviour from
  incidental behaviour. Keep that up.

Run a single class while iterating:

```bash
dotnet test CanKit.Pro.sln --filter "FullyQualifiedName~TxConfirmTests"
```

## Public API

These are published libraries, so the public surface is a promise:

- XML documentation on every public type and member. The existing code documents in English with
  a Chinese translation, inherited from CanKit's own style — English alone is fine for new code.
  Those comments are published verbatim: `eng/build-api-docs.sh` turns them into the
  [API reference](https://dborgards.github.io/CanKit.Pro/api/) on every website build, so what you
  write there is what readers see, and the `FR-…`/`ADR-…` ids you cite become links into the SRS
  and the arc42 document.
- Prefer adding an overload to changing a signature. If a break is genuinely right, mark it `!`
  and write the migration into the footer.
- New public types need a matching section in the package's `README.md`, which ships inside
  the `.nupkg` and is what people actually read on nuget.org.

## Reporting bugs

Use the issue templates. A reproduction on `virtual://` endpoints is worth a great deal: it can go
into the test suite as-is, which usually means the fix ships in the next release rather than the
one after.

## License

Contributions are accepted under the [MIT License](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE). There is no CLA — opening a pull
request licenses your contribution under the repository's license. See
[docs/licensing.md](https://github.com/dborgards/CanKit.Pro/blob/main/docs/licensing.md).
