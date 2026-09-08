# Release process

One version for all four packages, computed from the commit history. Nobody edits a version
number, and nobody decides by hand what the next one is.

```
 conventional commits on main
            │
            ▼
   semantic-release ─── decides X.Y.Z ──┬── writes CHANGELOG.md, commits it back to main
   (.releaserc.json)                    ├── tags vX.Y.Z, opens the GitHub Release
                                        └── dotnet pack -p:Version=X.Y.Z ─► nuget.org

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

**GitVersion** answers *"what version is this commit?"* — for every build that is not a release.
A pull-request build, a local `dotnet pack`, a CI artifact from `main` between releases: each gets
a real, ordered SemVer derived from the tags semantic-release already wrote, instead of `0.0.0` or
a hand-maintained placeholder. It is wired in as `GitVersion.MsBuild`, a
`GlobalPackageReference` in `Directory.Packages.props`, so it applies to every project without
per-project setup.

They meet at the tag: semantic-release writes `vX.Y.Z`, GitVersion reads it back.

During a release, `Directory.Build.props` switches GitVersion off entirely — the version passed by
semantic-release is authoritative and must not be second-guessed:

```xml
<DisableGitVersionTask Condition="'$(DisableGitVersionTask)' == '' AND '$(Version)' != ''">true</DisableGitVersionTask>
```

## What a release run does

`.github/workflows/release.yml` runs on every push to `main`:

1. Checkout with `fetch-depth: 0` — both tools need real history.
2. Build, then **test**. This is the gate: nothing is tagged or published if the tests fail on
   this exact commit.
3. `npx semantic-release`, which:
   - analyses the commits and stops right there if none of them warrants a release;
   - regenerates `CHANGELOG.md`;
   - runs `dotnet pack -p:Version=X.Y.Z` (`@semantic-release/exec`);
   - runs `dotnet nuget push … --skip-duplicate`;
   - commits the changelog back to `main` as `chore(release): X.Y.Z [skip ci]`;
   - creates the `vX.Y.Z` tag and the GitHub Release, with the `.nupkg` files attached.

Ordering matters here: packing and pushing happen in `prepare`/`publish`, *before* the tag is
created. A failed `dotnet nuget push` therefore aborts the run without leaving a tag that claims a
release nobody can install.

## Setup checklist

Needed once, in repository settings:

- **`NUGET_API_KEY`** — a repository secret. On nuget.org, scope the key to the glob
  `CanKit.Pro.*` and enable *Push new packages and package versions*. The four IDs are unclaimed
  at the time of writing, so the first successful release also reserves them.
- **Actions may write to the repository** — Settings → Actions → General → Workflow permissions.
  `@semantic-release/git` pushes the changelog commit.
- **Branch protection on `main`, if enabled, must let that push through.** Either allow the
  `github-actions[bot]` to bypass the rule, or drop `@semantic-release/git` from
  `.releaserc.json` and accept that the changelog lives only in the GitHub Releases.
- **The first release is `1.0.0`**, because semantic-release starts there when it finds no tags.
  If you would rather stay pre-1.0 while the API settles, push a starting tag *before* the first
  release run:

  ```bash
  git tag v0.1.0 && git push origin v0.1.0
  ```

  Subsequent releases then continue from `0.1.x`. Note that under SemVer a breaking change still
  moves `0.x` to `1.0.0`.

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

There isn't one, on purpose. If a release is stuck, fix the workflow rather than packing from a
laptop: a manually pushed package has a version nothing in the history explains, and the next
automated run will disagree with it.
