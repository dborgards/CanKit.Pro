# Public API approvals

One `<PackageId>.approved.txt` per published package, holding a canonical rendering of its public
API surface. `PublicApiSurfaceTests` regenerates that rendering on every run and fails when it
differs — so a change to the public API of a shipped package cannot happen by accident, only by
updating the approval in the same pull request.

When the test fails it writes a `<PackageId>.received.txt` next to the approval. Review the diff;
if the change is intended, replace the `.approved.txt` with it. CI makes that file reachable two
ways: it is uploaded with the test results, and the *Show generated API approvals* step prints it
into the job log, so the new baseline can be read straight off a failed run without downloading
anything.

The `.received.txt` files are generated output and are git-ignored; only the approvals are checked
in.

All nine published packages are tracked, L2 and L3/L4 alike.
