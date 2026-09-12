# Versioning: 1.3.0 is the first stable release

**Status:** accepted, 2026-09-12. Resolves
[#61](https://github.com/dborgards/CanKit.Pro/issues/61).

## Context

Nine packages went to nuget.org as 1.0.0, 1.1.0, 1.2.0, 1.2.1, 1.2.2 and 1.2.3 on 9 and 10
September 2026. Three of them — `CanKit.Pro.CANopen`, `CanKit.Pro.IsoTp` and `CanKit.Pro.Uds` —
described themselves as **Experimental** in the `<Description>` that nuget.org renders, while
carrying major version 1.

Both statements cannot hold. Major 1 is a promise that the public API will not break without a
major bump. "Experimental" asks nobody to rely on it yet. The two audiences — a package manager
resolving a version range, and a human reading the package page — were told opposite things.

The 1.x line was not chosen. semantic-release starts at 1.0.0 when it finds no tag, and the first
release run found none: the `v0.1.0` seed tag that the release runbook describes was never pushed.
The first release therefore claimed a stable API on a code base that had never been reviewed
against its own specifications.

The review of 2026-09-10 then found roughly 45 defects. Four of them cannot be fixed properly
without changing the public surface:

| Issue | The fix that is actually right |
| --- | --- |
| [#23](https://github.com/dborgards/CanKit.Pro/issues/23) | Subscription items carry `IsEcho` and the bus timestamp, so the three hand-rolled echo workarounds in J1939, CANopen and the TP channel can go |
| [#37](https://github.com/dborgards/CanKit.Pro/issues/37) | SPN extraction returns a value that can say "not available" or "error" instead of reporting `0xFFFF` as 8191.875 rpm |
| [#44](https://github.com/dborgards/CanKit.Pro/issues/44) | `SdoTransferMode.Expedited` and `.Segmented` are removed, because nothing enforces them |
| [#82](https://github.com/dborgards/CanKit.Pro/issues/82) | `FindOverlappingFilterSubscriptions()` is replaced by a named `FilterOverlap` type instead of a bare tuple pair |

Under a strict reading of SemVer, each of these either costs a major version or has to ship as a
new member with an `[Obsolete]` shadow on the old one. `src/` today contains **no `[Obsolete]`
member at all**. The additive route would introduce four of them into a clean surface — and there
is no major version scheduled that would ever remove them again.

## Decision

1. **1.0.0 – 1.2.3 are withdrawn.** All six versions of all nine packages are unlisted and
   deprecated on nuget.org (see the checklist below). They were published as stable by mistake.

2. **1.3.0 is the first release for which the SemVer promise holds.** Everything before it is
   treated as never having made that promise, because it was not in a position to.

3. **Until the `v1.3.0` tag, breaking API changes are allowed and expected.** This is the one
   window in which the surface can be corrected. It closes with the tag.

4. **Replace, do not shadow.** No `[Obsolete]` member is introduced in this window in order to
   dodge a break. After 1.3.0, `[Obsolete]` is the normal deprecation tool again — then with a
   major version in prospect that eventually removes it.

5. **No "Experimental" in package metadata.** The maturity statement that belongs in a README is
   what has been validated and what has not; it is not a stability claim, and it does not
   contradict the version.

6. **No intermediate releases during the review series.** One release at the end. Cutting 1.3.0,
   1.4.0 and 1.5.0 each with breaks in them would dissolve the very statement this document
   makes.

7. **The commit analyzer maps breaking changes to a minor bump for the duration.**
   `.releaserc.json` carries `{ "breaking": true, "release": "minor" }` so that a commit with a
   `!` or a `BREAKING CHANGE:` footer cannot silently publish 2.0.0. This is a temporary override
   and is listed on the release checklist below. `eng/verify-release-config.mjs` fails the build
   if the override is still in place once 1.3.0 has been released.

## Alternatives considered

**2.0.0.** Honest about the breaks and conventional. Rejected: the version number would be
spending a major on a mistake rather than on a product milestone, and the 1.x line would stand on
nuget.org forever as the "real" first version. The withdrawal achieves the same protection
without inflating the number.

**A pre-release line (`1.3.0-preview.N` or `2.0.0-preview.N`).** Rejected: NuGet does not resolve
pre-release versions by default, so `dotnet add package` would have continued to install the
mistaken 1.2.3 until it was withdrawn — and once it *is* withdrawn, the pre-release suffix protects
nobody who is left. It would have added a semantic-release channel, a `--prerelease` flag in
nineteen install snippets and a second transition (preview → final) for no gain.

**Going back below 1.2.3.** Not possible. SemVer has no reverse gear, and NuGet would never serve
a lower version as the latest.

## What this costs

The deviation is real and worth naming: 1.3.0 will break code compiled against 1.2.x, and it will
do so without a major bump.

What bounds it is the withdrawal. Unlisted packages disappear from search and stop resolving
through version ranges; only an exact pin still restores. Across all six releases nuget.org counts
30 to 68 downloads per package — the traffic profile of mirrors and vulnerability scanners, with no
evidence of a human consumer. The promise is being broken towards versions that can no longer be
discovered or installed by anything that was not already pinned to them.

nuget.org has no hard delete. Withdrawal is as close to undoing a publication as the platform
allows, and the badges in `docs/packages/index.md` and `docs/index.md` will show no version until
1.3.0 ships.

## Release checklist for 1.3.0

1. All `type: bug` issues from the review series closed.
2. [#52](https://github.com/dborgards/CanKit.Pro/issues/52) — the normative negative tests exist.
3. **[#23](https://github.com/dborgards/CanKit.Pro/issues/23),
   [#37](https://github.com/dborgards/CanKit.Pro/issues/37),
   [#44](https://github.com/dborgards/CanKit.Pro/issues/44) and
   [#82](https://github.com/dborgards/CanKit.Pro/issues/82) landed.** After the tag, none of them
   is possible without a major version.
4. The API approval baselines reviewed as a whole and frozen.
5. **`.releaserc.json` back to `{ "breaking": true, "release": "major" }`.** Without this, a real
   breaking change later becomes a silent minor. `eng/verify-release-config.mjs` enforces it once
   1.3.0 is in the changelog.
6. Each package README states what is validated and what is not (software doubles vs. hardware).

## Withdrawing 1.0.0 – 1.2.3

Manual work on nuget.org, outside this repository. For each of the nine `CanKit.Pro.*` packages,
for versions 1.0.0, 1.1.0, 1.2.0, 1.2.1, 1.2.2 and 1.2.3:

- **Deprecate** with reason *Other* and the message:

  > The 1.x releases were published as stable by mistake while the API was still under review.
  > They are withdrawn. Use 1.3.0 or later.

- **Unlist**, so the versions leave search results and stop resolving through version ranges.

Deprecate as well as unlist: unlisting is silent for someone who already depends on the package,
deprecation is what surfaces in their IDE and build log.

The git tags `v1.0.0` … `v1.2.3` stay. They are history, and GitVersion reads them to derive CI
versions.

## After 1.3.0

Normal SemVer. A breaking change costs a major version; a deprecation is an `[Obsolete]` member
that a later major removes. The API approval tests are what make an accidental break visible in
the pull request that causes it.
