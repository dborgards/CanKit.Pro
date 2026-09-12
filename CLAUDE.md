# Working agreement

## Pull requests

- **Every change lands through a pull request.** Nothing goes to `main` directly.
- **Group tickets into one PR where they overlap, split them where they don't.** Issues in the
  same package that touch the same files belong in one PR — two PRs racing over
  `CanBusService.cs` cost more review than they save, and the second one inherits a conflict.
  Issues in different packages get their own PR and can run in parallel.
- Branch from `main` as `feat/…`, `fix/…`, `docs/…` (see `CONTRIBUTING.md` § Branching).
- The repository **squash-merges**, so the pull-request *title* becomes the commit
  semantic-release reads. It must be a Conventional Commit. A breaking change needs the `!` in
  the title **and** a `BREAKING CHANGE:` footer — and the footer has to survive the squash, so
  put it in the pull-request body, not only in a commit message.
- Fill in `.github/PULL_REQUEST_TEMPLATE.md`. Tick a checklist box only for something actually
  run.

## Verify locally before pushing

A .NET 10 SDK **is** installable in this container, even though
`builds.dotnet.microsoft.com` is blocked by the proxy — it is in the Ubuntu archive:

```bash
apt-get install -y --no-install-recommends dotnet-sdk-10.0   # noble-updates, 10.0.112
```

So there is no reason to push a guess and let CI be the compiler. The gate:

```bash
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build  CanKit.Pro.sln -c Release -p:CI=true          # CI=true turns warnings into errors
dotnet test   CanKit.Pro.sln -c Release --no-build --framework net10.0
dotnet format CanKit.Pro.sln --verify-no-changes            # the single arbiter of code style
dotnet pack   CanKit.Pro.sln -c Release -o /tmp/nupkgs && python3 eng/verify-packages.py /tmp/nupkgs
```

Do not report a build or test result that was not run.

API approval baselines are taken from the generated `*.received.txt`, never hand-written.

After editing XML (`.props`, `.targets`, `.csproj`) or YAML, check well-formedness. A `--` inside
an XML comment is illegal and once made every project fail to load, i.e. every CI job red.

## Versioning

Until the `v1.3.0` tag, `docs/decisions/0001-versioning-and-api-stability.md` governs: breaking
changes are allowed, they map to a **minor** bump, and no `[Obsolete]` shim is introduced to dodge
one. `eng/verify-release-config.mjs` enforces both ends of that and fails the build if the rule
and the changelog disagree.

## Upstream

L0/L1 is [`pkuyo/CanKit`](https://github.com/pkuyo/CanKit), consumed as a NuGet package, not
forked. When the shape of a CanKit type matters, read it instead of assuming — clone the repo and
check out the tag pinned in `eng/Dependencies.props` (currently `v0.5.6`).
