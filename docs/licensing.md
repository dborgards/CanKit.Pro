# Licensing: why CanKit.Pro is MIT

CanKit.Pro is licensed under the [MIT License](../LICENSE). Its predecessor,
[CanKit.Pro.legacy](https://github.com/dborgards/CanKit.Pro.legacy), was Apache-2.0. This document
records why the change is sound, because "we relicensed" is the kind of claim a downstream user
should be able to check rather than take on trust.

> This is engineering reasoning, not legal advice. If CanKit.Pro is going into a product whose
> legal review cares about provenance, have that review read this page against the two
> repositories' git histories.

## What changed

CanKit.Pro.legacy was a **fork** of [CanKit](https://github.com/pkuyo/CanKit). A fork contains the
upstream source, so the whole repository necessarily carried upstream's license: Apache-2.0. That
was correct there, and it remains correct there.

CanKit.Pro is **not** a fork. It is a separate product that consumes CanKit the way any other
consumer does — as NuGet packages resolved at build time:

```xml
<PackageReference Include="CanKit.Abstractions" />
```

No CanKit source file exists in this repository, and none is redistributed in the packages this
repository publishes.

## Why that permits MIT

Two independent points, both of which have to hold:

**1. Apache-2.0 imposes nothing on a consumer's own code.** It is a permissive license, not a
copyleft one. Using an Apache-2.0 library — calling its API, depending on its package, shipping
alongside it — does not make the calling code a derivative work that must be Apache-2.0. Only the
Apache-2.0 material itself stays under Apache-2.0, wherever it goes, with its attribution and
NOTICE obligations attached. Those obligations travel with the CanKit packages, not with
CanKit.Pro; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

**2. Every file migrated here was written for the fork, not inherited from upstream.** That is the
part that actually had to be verified, and it was, file by file, against the legacy repository's
git history. What moved:

| Migrated | First added in the legacy repo |
| --- | --- |
| `src/CanKit.Pro.Actor/`, `.Addressing/`, `.RawCan/`, `.Reliability/` | 2026-07-14 … 2026-07-15, by the fork |
| The tests for those four libraries | 2026-07-14 … 2026-07-15, by the fork |
| `docs/architecture/`, `docs/requirements/`, `docs/reviews/` | 2026-07-14, by the fork |

What deliberately did **not** move, because it is upstream's Apache-2.0 code or a modification of
it, and would have had to keep its license:

- `CanKit.Abstractions`, `CanKit.Core`, every `CanKit.Adapter.*` and `CanKit.Transport.IsoTp`,
  including the fork's own changes to them (the frame-ownership contract, `CanFrame.Duplicate`,
  the `VirtualBusHub` rework). Those live on in CanKit.Pro.legacy under Apache-2.0, and belong
  upstream as pull requests rather than here.
- The upstream test harness (`TestCaseProvider`, `TestHelpers`, `TestMatrix`,
  `EmptyTestDataProvider`). CanKit.Pro has its own, purpose-built fixtures in
  `tests/CanKit.Pro.Tests/Infrastructure/`, written from scratch — they had to be rewritten anyway,
  since a standalone product has no vendor-hardware test matrix to orchestrate.
- The legacy `.github/` workflows and `eng/` release scripts, which were built around
  per-package version bumps inside a fork of upstream's release pipeline.

Copyright in the migrated work is held by Dietmar Borgards, who directed and reviewed all of it;
the AI assistants that appear as commit authors in the legacy history (`Claude`, `Cursor Agent`)
are tools operated under that direction, not independent contributors with their own claim.

## Why bother

Apache-2.0 would have worked too — the fork was never in a bad position. MIT is a better fit for
what CanKit.Pro now is:

- **It matches the actual relationship.** An Apache-2.0 license file at the root of a repository
  that contains no Apache-2.0 code invites the reasonable but wrong conclusion that upstream code
  is in here somewhere.
- **It is the lowest-friction license for a library.** MIT is on nearly every corporate
  pre-approved list and needs no NOTICE handling from consumers.
- **The attribution that matters is preserved anyway**, in
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md), in every package README, and in the
  packages' own dependency metadata — where a tool can actually find it.

One thing Apache-2.0 gives that MIT does not: an express patent grant (and its retaliation
clause). For a library of scheduling, addressing and demultiplexing helpers, that was not judged
to be worth the added friction. If CanKit.Pro later grows a package where patents are a realistic
concern, revisiting the license for that package is a legitimate decision — a license is a
technical choice like any other, and it is versioned here in git.

## Contributions

Contributions are accepted under MIT (see [CONTRIBUTING.md](../CONTRIBUTING.md)). There is no CLA:
opening a pull request licenses the contribution under the repository's license, which is the
GitHub default and standard practice for a project this size.
