# Public API approvals

One `<PackageId>.approved.txt` per published package, holding a canonical rendering of its public
API surface. `PublicApiSurfaceTests` regenerates that rendering on every run and fails when it
differs — so a change to the public API of a shipped package cannot happen by accident, only by
updating the approval in the same pull request.

## What the files contain

C# declarations, produced by
[PublicApiGenerator](https://github.com/PublicApiGenerator/PublicApiGenerator). They read like a
reference assembly on purpose: base types and implemented interfaces, `sealed`/`abstract`/`static`,
`record`, parameter default values, `in`/`ref`/`out`, attributes, and nullable annotations are all
part of the promise a published package makes, so all of them are part of the approval. Sealing a
class or dropping an interface is a breaking change for consumers, and it has to show up here.

Two things are deliberately left out, because they change without the API changing: assembly-level
attributes (they carry the build's version, which is injected from outside the build) and the
compiler's own nullable/`CompilerGenerated` bookkeeping (already visible as `?` on the signatures).
`PublicApiSurfaceTests` documents both.

When the test fails it writes a `<PackageId>.received.txt` next to the approval. Review the diff;
if the change is intended, replace the `.approved.txt` with it. CI makes that file reachable two
ways: it is uploaded with the test results, and the *Show generated API approvals* step prints it
into the job log, so the new baseline can be read straight off a failed run without downloading
anything.

The `.received.txt` files are generated output and are git-ignored; only the approvals are checked
in.

All nine published packages are tracked, L2 and L3/L4 alike.
