# Release process

One version for every package the repository publishes, computed from the commit history. Nobody edits a version
number, and nobody decides by hand what the next one is.

```
 conventional commits on main
            │
            ▼
   the `verify` job ──── asks the commit analyser for X.Y.Z
   (no credentials)  └── builds, tests and packs at that version ──┐
            │                                                      │
            ▼                                                  .nupkg
   semantic-release ─── writes CHANGELOG.md, commits it to main    │
   (.releaserc.json)  ── tags vX.Y.Z                               │
   (the only job with ── pushes the packages it was handed ◄───────┘
    publishing rights)   and opens the GitHub Release ─► nuget.org

 every other build
            │
            ▼
      GitVersion ─── derives an ordered prerelease version from tags + branch
   (GitVersion.yml)   e.g. 1.4.0-pr.17.3, so CI artifacts are identifiable
```

## The two tools, and why both

They answer different questions, and neither can answer the other's.

**semantic-release** answers *"what should the next release be, and what changed?"* It reads the
Conventional Commits since the last tag: a `fix` bumps the patch, a `feat` the minor, a `!` or
`BREAKING CHANGE:` footer the major. It then writes the changelog, creates the tag, publishes the
GitHub Release and pushes the packages. It only runs on `main`.

!!! note "Until 1.3.0, a breaking change bumps the minor"

    `.releaserc.json` currently maps `breaking` to a **minor** release, so that the API
    corrections on the way to 1.3.0 cannot publish 2.0.0 by accident. This is temporary and is
    on the release checklist in
    [Versioning](decisions/0001-versioning-and-api-stability.md);
    `eng/verify-release-config.mjs` fails the build if the override outlives 1.3.0.

**GitVersion** answers *"what version is this commit?"* — for builds that are not releases. A
pull-request build or a CI artifact from `main` between releases gets a real, ordered SemVer
derived from the tags semantic-release already wrote, instead of `0.0.0` or a hand-maintained
placeholder.

They meet at the tag: semantic-release writes `vX.Y.Z`, GitVersion reads it back.

### GitVersion runs in the pipeline, not in the build

The obvious wiring — `GitVersion.MsBuild` as a `GlobalPackageReference`, so every project is
versioned automatically — was tried first and removed. It runs the tool once per project *and*
target framework, it makes `dotnet build` depend on git history it has no reason to need, and,
decisively, it turns "I cannot determine a version" into a failed compile. This repository hit
exactly that on its first CI run: with a single branch, no `main` and no tags, GitVersion
classified the branch as orphaned and aborted every project with
`No base versions determined on the current branch`. Neither `next-version` nor the `Fallback`
strategy changes that.

So the `version` job runs GitVersion once, and every other job is handed the result:

```yaml
- run: dotnet build CanKit.Pro.sln -c Release -p:Version=${{ needs.version.outputs.semver }}
```

That job is allowed to fail. If GitVersion cannot name the build, CI logs a warning and falls back
to `0.0.0-unversioned.<run number>` — an unnameable build is still worth compiling and testing.
Release versions never travel this path: semantic-release computes those itself.

Nothing in the build computes a version. `Directory.Build.props` only sets `VersionPrefix` as the
last-resort fallback for a plain `dotnet build` with no `-p:Version=`.

To see what GitVersion makes of your working copy:

```bash
dotnet tool restore
dotnet gitversion
```

## Which packages the release publishes

Nine projects build; all nine ship. The split used to hold four of them back is expressed once,
in the project files, so a project that becomes packable is always a one-line change: drop its
`IsPackable` property, and add it to `PublicApiSurfaceTests.Tracked` so its surface is tracked
from its first release.

| Package | Layer | Ships |
| --- | --- | --- |
| `CanKit.Pro.Actor` | L2 — single-threaded protocol actor | yes |
| `CanKit.Pro.Addressing` | L2 — CAN-ID addressing and filters | yes |
| `CanKit.Pro.RawCan` | L2 — raw-CAN demultiplex and TX confirmation | yes |
| `CanKit.Pro.Reliability` | L2 — deadlines, retries, bus-state monitoring | yes |
| `CanKit.Pro.IsoTp` | L3 — ISO 15765-2 | yes |
| `CanKit.Pro.J1939Tp` | L3 — SAE J1939-21 transport | yes |
| `CanKit.Pro.CANopen` | L4 — CiA 301 | yes |
| `CanKit.Pro.J1939` | L4 — J1939 node and address claim | yes |
| `CanKit.Pro.Uds` | L4 — ISO 14229-1 | yes |

`dotnet pack CanKit.Pro.sln` packs whichever projects don't set `IsPackable=false`, without
needing a list of project names anywhere, which means the CI pack job and the release
workflow's `verify` job cannot drift apart — they run the same command over the same
solution. The CI `pack` job prints `ls -l artifacts/nuget`, so a project that silently
becomes packable shows up as a new file in that listing on the pull request that did it.

`CanKit.Pro.Vendor`, the generic extension framework for a customer's confidential private
protocol, used to be the tenth project and stayed unpublished for a different reason: not
API-immaturity but confidentiality. It has since moved out of this public repository entirely
into a separate, internal-only one.

## What a release run does

`.github/workflows/release.yml` is **started by hand**, from the Actions tab: *Release → Run
workflow*. It has no push trigger. The *dry run* input defaults to **checked**, so the safe thing
is also the default — an accidental run analyses the commits and prints what it would do without
tagging or publishing anything. Uncheck it to release for real.

1. Checkout with `fetch-depth: 0` — semantic-release needs the full history to find the last
   tag and the commits since it. No credential is persisted into `.git/config`; see
   [Credentials](#credentials).
2. Resolve the next version, then **build, test and pack** in `verify`. That job has no OIDC
   grant and none of the publishing secrets: a bare `dotnet pack` restores and builds, so it
   cannot run in the job that holds `id-token: write`. This is the gate — nothing is tagged
   or published if the tests fail on this exact commit.
3. `NuGet/login` exchanges the run's OIDC token for a NuGet API key valid for one hour.
4. `npx semantic-release`, which runs the steps below in this order.

### The order the steps actually run in, and why it matters

Each plugin runs in the order it appears in `.releaserc.json`, within each lifecycle step:

```
analyze        →  nothing to release? stop here
verifyRelease  →  refuse a version below 1.3.0 while the ADR window is open  (exec)
prepare        →  CHANGELOG.md regenerated                     (@semantic-release/changelog)
               →  verify the nine packages `verify` packed     (@semantic-release/exec, prepareCmd)
               →  changelog committed and PUSHED to main       (@semantic-release/git)
   ↓
  TAG          →  vX.Y.Z created and pushed                    (semantic-release core)
   ↓
publish        →  dotnet nuget push … --skip-duplicate         (@semantic-release/exec, publishCmd)
               →  GitHub Release with the .nupkg attached      (@semantic-release/github)
```

Two boundaries matter here.

**Packing happens in `verify`, before any credential exists.** A packing failure therefore
aborts the run having changed nothing on the remote: no tag, no commit, nothing published.

What `prepare` does instead is re-check the artifact it was handed: `eng/verify-packages.py` runs
against the downloaded `.nupkg` files with the version about to be tagged, and fails unless all
nine are there, each carries a README, a license expression and a `.snupkg`, and every one of
them is stamped at exactly that version. It is the same script the CI pack job runs, so a package
that would be rejected on a pull request is rejected here too — this time with a version to check
against.

**The tag is created after `prepare` and before `publish`.** So a failed `dotnet nuget push`
leaves behind:

- the `chore(release): X.Y.Z [skip ci]` commit on `main`,
- the `vX.Y.Z` tag,
- **possibly some packages on nuget.org**, and no GitHub Release.

That third point is the awkward one: `dotnet nuget push` walks the `.nupkg` files one at a time
and is not atomic, so a failure part-way through can leave a subset of the nine packages
published at that version. Check which package IDs actually landed before choosing a recovery
route.

And because the next run computes the version from the newest tag, it moves on to the version
after that one. The failed release is skipped rather than retried. [Recovering a half-finished
release](#recovering-a-half-finished-release) is the way out.

The `[skip ci]` marker on the changelog commit suppresses every push-triggered workflow, so the
website (`.github/workflows/docs.yml`) additionally listens for the Release workflow's completion
and rebuilds right after a successful release. Without that, the changelog page would lag until
the next unrelated documentation change.

## Recovering a half-finished release

There is no manual *versioning* — the version always comes from the commit history, and a
hand-picked number is one nothing in the history explains. But a release that stopped between the
tag and nuget.org has to be finished or undone by hand, because no rerun will do it.

First establish what actually landed: does the `vX.Y.Z` tag exist, is the `chore(release)` commit
on `main`, are the packages on nuget.org, is there a GitHub Release?

**Option A — finish the release from the tag.** Right when the packages are the only thing
missing and the version is sound. The version is *read from the tag*, never invented:

```bash
git fetch --tags
git checkout vX.Y.Z
dotnet pack CanKit.Pro.sln --configuration Release -p:Version=X.Y.Z --output artifacts/nuget
dotnet nuget push "artifacts/nuget/*.nupkg" \
  --source https://api.nuget.org/v3/index.json --api-key "$KEY" --skip-duplicate
```

`--skip-duplicate` makes this safe to repeat when some packages made it and others did not. The
API key comes from a fresh `NuGet/login` run or a temporary key from nuget.org. Afterwards, create
the GitHub Release for the tag by hand and attach the `.nupkg` files.

**Option B — undo it and release again.** Right when the version is wrong, or the packages that
landed have to be abandoned. Delete the tag, then revert the release commit through a pull
request:

```bash
git push origin :refs/tags/vX.Y.Z          # remote tag
git tag -d vX.Y.Z                          # local tag

git switch -c revert/release-X.Y.Z origin/main
git revert <sha of the chore(release) commit>
git push -u origin revert/release-X.Y.Z    # then open and merge the pull request
```

The revert has to reach `origin/main` before the next run: the workflow builds from the remote
branch, so a revert sitting in a local clone changes nothing. And it goes through a pull request
because `main` is protected — the same ruleset that made `RELEASE_TOKEN` necessary in the first
place. Revert rather than force-push: rewriting `main` costs more than a commit that says what
happened.

semantic-release will then compute the same version again from the same commits — the changelog
entry it regenerates supersedes the reverted one. Packages already pushed at that version stay
published; `--skip-duplicate` lets the rerun past them, but they will not be rebuilt, so use
Option A instead if their content matters.

Note what Option B is *not* for: a failure in the build or the test step. Those run before
semantic-release is invoked, so nothing has been tagged, committed or published. Fix the failure
and start the workflow again — there is nothing to undo.

Do **not** delete a version from nuget.org to "try again": nuget.org does not allow it, and
unlisting leaves the version number consumed either way.

## Credentials

Two, and they do different jobs.

**nuget.org — no stored key.** The release job requests an OIDC token from GitHub and exchanges
it through `NuGet/login` for an API key that is valid for one hour (Trusted Publishing). Nothing
long-lived is stored in the repository. This needs `id-token: write` on the workflow, and the
nuget.org account name in the `NUGET_USER` secret. The trust relationship itself is configured on
nuget.org, under the account's *Trusted Publishing* settings, and names this repository and
workflow.

**`main` — `RELEASE_TOKEN`.** A fine-grained PAT (Contents: read and write, scoped to this
repository) from an account holding the repo-owner role. The default `GITHUB_TOKEN` cannot push
to `main`: the ruleset requires a pull request and only bypasses for that role, which the Actions
bot does not hold. `@semantic-release/git` needs the push for the changelog commit, and
`@semantic-release/github` uses the same token to create the Release.

The PAT is passed **only** in the `env:` of the release step. The checkout runs with
`persist-credentials: false`, so it never reaches `.git/config` while `npm ci` executes
third-party code. Restore, build, test and pack run in `verify`, which has neither the PAT
nor `id-token: write`. The release job's own `GITHUB_TOKEN` is restricted to `contents: read`
plus `id-token: write`.

A PAT expires. When a release run fails at the push or the Release step with a 403, check that
first.

### Setup, once

- **`NUGET_USER`** and **`RELEASE_TOKEN`** as repository secrets.
- **Trusted Publishing** configured on nuget.org for this repository and `release.yml`.
- **The ruleset on `main`** must let the `RELEASE_TOKEN` identity push. That is the repo-owner
  bypass, not a `github-actions[bot]` bypass — the bot is not what pushes.

Historical note: the first release landed on `1.0.0` because semantic-release starts there when
it finds no tag, and the `v0.1.0` seed tag this document used to recommend was never pushed. See
[Versioning](decisions/0001-versioning-and-api-stability.md) for what was decided about that.

## Dry runs

Before merging, from the Actions tab: **Release → Run workflow**, leaving *dry run* checked. It
prints the version it would pick and the release notes it would write, and touches nothing.

Locally, the same thing without needing a token for anything but the GitHub plugin:

```bash
npm ci
npx semantic-release --dry-run --no-ci
```

CI also runs `node eng/verify-release-config.mjs` on every pull request. That is not a dry run —
it loads `.releaserc.json` and resolves every plugin and preset against the pinned tooling, which
catches the failures a PR can actually introduce (a typo'd plugin, a dependency dropped from
`package.json`, an incompatible version pair) without needing a token that a fork PR cannot have.

## Changing the CanKit version

`eng/Dependencies.props` holds the single knob:

```xml
<CanKitVersion>0.5.6</CanKitVersion>
```

It sets the `PackageReference` version for `CanKit.Abstractions` (what the libraries build
against, and what ends up as the dependency floor in the `.nupkg`) and for `CanKit.Core` /
`CanKit.Adapter.Virtual` (tests and samples). Bump it, let CI run, and commit as
`build(deps): move to CanKit 0.5.7` — a `patch` release, so consumers get a package whose
dependency metadata matches what was actually tested.

## Manual release

There is no manual *versioning*, on purpose. The version comes from the commit history; a
hand-picked one is a number nothing explains, and the next automated run will disagree with it.
If a release will not start, fix the workflow rather than packing from a laptop.

That is not the same as having no way out of a release that stopped half-way. A tag that exists
with no packages behind it cannot be fixed by rerunning anything —
[Recovering a half-finished release](#recovering-a-half-finished-release) is the documented path,
and the version there is read from the tag rather than chosen.
