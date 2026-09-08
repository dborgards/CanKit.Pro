# Public API approvals

One `<PackageId>.approved.txt` per published package, holding a canonical rendering of its public
API surface. `PublicApiSurfaceTests` regenerates that rendering on every run and fails when it
differs — so a change to the public API of a shipped package cannot happen by accident, only by
updating the approval in the same pull request.

When the test fails it writes a `<PackageId>.received.txt` next to the approval (and CI uploads it
with the test results). Review the diff; if the change is intended, replace the `.approved.txt`
with it.

Only the four published L2 packages are tracked. The L3/L4 packages are still pre-release, and
pinning their API before it has settled would be ceremony rather than protection.
