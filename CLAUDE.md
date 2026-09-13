# Working agreement

## Pull requests

- **Every change lands through a pull request.** Nothing goes to `main` directly.
- **Group tickets into one PR where they overlap, split them where they don't.** Issues in the
  same package that touch the same files belong in one PR — two PRs racing over
  `CanBusService.cs` cost more review than they save, and the second one inherits a conflict.
  Issues in different packages get their own PR.
- **One pull request open at a time.** Not because parallel work is wrong in principle, but
  because of what merging does to everything else in flight: each merge to `main` obliges every
  open branch to take a base merge, and each base merge is a fresh full-gate run, a fresh CI
  cycle and a fresh review pass over code that did not change. Two green pull requests in flight
  cost exactly that, plus a manual base merge when GitHub's *Update branch* button failed.
  Finishing one and merging it before opening the next is cheaper than either.

  The exception is a pull request that comes *out of* the one in flight — a follow-up split off
  during review, or a fix the review made necessary elsewhere. Those may overlap, because the
  alternative is holding the finding until the first one lands.
- Branch from `main` as `feat/…`, `fix/…`, `docs/…` (see `CONTRIBUTING.md` § Branching).
- **Every commit on the branch decides the release, not the pull-request title.** As practised,
  pull requests land as two-parent merge commits (`git log --first-parent main` shows
  `Merge pull request …` throughout) and every branch commit is retained, so semantic-release
  analyses all of them. Consequences worth internalising:
  - Write *each* commit as a Conventional Commit, not just the title. A `docs:`-titled pull
    request containing one `feat:` commit publishes a minor release.
  - A breaking change needs the `!` and a `BREAKING CHANGE:` footer **in the commit message**,
    where the analyser reads it. Putting it only in the pull-request body does nothing.
  - Keep the title a valid Conventional Commit anyway: it is what reviewers read, and it is what
    a squash merge would use if the strategy is ever changed.

  `CONTRIBUTING.md` claimed the opposite until this file was written; both now describe the
  history. If the intent is squash merging, that belongs in the repository settings, and both
  texts want revisiting.
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
out=$(mktemp -d) && dotnet pack CanKit.Pro.sln -c Release -o "$out" \
  && python3 eng/verify-packages.py "$out"    # fresh dir: a stale one can hide a missing package
```

Do not report a build or test result that was not run.

API approval baselines are taken from the generated `*.received.txt`, never hand-written.

After editing XML (`.props`, `.targets`, `.csproj`) or YAML, check well-formedness. A `--` inside
an XML comment is illegal and once made every project fail to load, i.e. every CI job red.

## Before claiming something is true

The gate above catches broken code. These catch a different failure: a claim that was reasoned
into existence instead of measured, which the gate cannot see because the build is green either
way. Every rule here is written from getting it wrong, and in each case the check was cheap and
available at the time.

- **A claim about a test needs a mutation, not an argument.** Before writing that a test catches
  something, break that something in the source and watch the test fail. A test reworked for
  flakiness lost its regression detection entirely and still passed three runs out of three
  against the exact bug it existed for; the mutation check that would have caught it in a minute
  had been run on its sibling and skipped on that one. Review caught it instead.
- **When a tool disagrees with your own measurement, distrust the instrument first.** Two rounds
  of a coverage finding were explained away as bot lag when the fault was local both times:
  first a diff intersection using stale line numbers, then a script de-duplicating Cobertura
  entries by filename — and Cobertura emits one `<class>` per *type*, so every nested type was
  silently dropped. A tool that keeps reporting the same thing after a fix is evidence about the
  measurement.
- **Remove the broken half, not the whole assertion.** An assertion pairing a robust statistic
  with a wrong bound was replaced wholesale; the replacements were themselves unsound twice over,
  and three rounds later the fix was the original statistic with the wrong bound deleted. Name
  precisely which part is false before touching anything.
- **For anything asserted against a clock, name the quantity the host can perturb and check the
  margin against it.** Not against timings observed so far — that is how a tolerance gets widened
  again next time. If the margin is not large compared with the perturbation, the property is not
  measurable there: use a signal, a virtual clock, or stop gating on it. Five distinct tests in
  three packages have gone red on a loaded CI runner for want of this (#92).
- **A fallback does not finish the task.** When an API call fails and the work goes somewhere
  else — an answer posted as a plain comment because the review thread rejected the reply — the
  task is done only once the original path is retried. A resolved review thread containing no
  answer looks, to everyone else, like the finding was clicked away.

## Decisions that belong to the maintainer

When something genuinely needs a decision, ask it as a question with selectable options and then
stop until it is answered. Do not carry on working and reporting around it: a decision put in the
middle of a long status message gets missed, which has happened, and the cost is a wrong
assumption baked into everything that followed it.

The same restraint applies to offering. "Say the word and I will do it" is a stall when the right
answer is already clear — either it is correct, in which case do it, or it is not, in which case
say so. Twice in one wave that phrasing turned an obvious fix into a wait.

## Versioning

Until the `v1.3.0` tag, `docs/decisions/0001-versioning-and-api-stability.md` governs: breaking
changes are allowed, they map to a **minor** bump, and no `[Obsolete]` shim is introduced to dodge
one. `eng/verify-release-config.mjs` enforces both ends of that and fails the build if the rule
and the changelog disagree.

## Upstream

L0/L1 is [`pkuyo/CanKit`](https://github.com/pkuyo/CanKit), consumed as a NuGet package, not
forked. When the shape of a CanKit type matters, read it instead of assuming — clone the repo and
check out the tag pinned in `eng/Dependencies.props` (currently `v0.5.6`).
