## What does this change?

<!-- One or two sentences. The "why" matters more than the "what" — the diff already shows the what. -->

## Type of change

<!--
Every commit on the branch must be a Conventional Commit, and so must the PR title.

Pull requests land as merge commits and every branch commit is retained, so semantic-release
analyses all of them -- a `docs:` title does not stop a `feat:` commit inside from publishing a
release. A breaking change needs its `BREAKING CHANGE:` footer in the *commit message*, where the
analyser reads it; the PR body is not analysed. See CONTRIBUTING.md. Examples:

  feat(rawcan): expose per-subscription drop counters
  fix(actor): stop Dispose from deadlocking on the loop thread
  feat(addressing)!: rename ComposePgn parameters      <- `!` = breaking, bumps the major
  docs: document the release process
-->

- [ ] `feat` — new behaviour (minor release)
- [ ] `fix` / `perf` — bug or performance fix (patch release)
- [ ] `docs` / `test` / `refactor` / `chore` / `ci` — no release
- [ ] Breaking change (`!` in the title, plus a `BREAKING CHANGE:` footer explaining the migration)

## Checklist

- [ ] `dotnet build CanKit.Pro.sln -c Release` succeeds
- [ ] `dotnet test CanKit.Pro.sln -c Release` passes
- [ ] Public API changes are documented with XML comments
- [ ] New behaviour is covered by a test
- [ ] The requirement or ADR this relates to is referenced (e.g. `FR-RAW-031`, `ADR-7`), if any
