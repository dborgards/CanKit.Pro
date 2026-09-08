## What does this change?

<!-- One or two sentences. The "why" matters more than the "what" — the diff already shows the what. -->

## Type of change

<!--
The PR title must be a Conventional Commit, because the merge commit is what semantic-release
reads to decide the next version and write the changelog. Examples:

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
