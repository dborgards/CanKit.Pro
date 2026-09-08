# Security policy

## Supported versions

CanKit.Pro is released as one version across all four packages. Fixes go into the **latest**
release; there are no maintenance branches for older versions. Upgrading to the newest patch
release is the supported way to get a fix.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting — *Security* → *Report a vulnerability* on
[this repository](https://github.com/dborgards/CanKit.Pro/security/advisories/new) — or email
**dietmar@borgards.de**.

Helpful to include: the affected package and version, what an attacker can do, and a reproduction
(a `virtual://` repro is ideal, since it needs no hardware).

You can expect an acknowledgement within a week. Fixes are released as soon as they are ready and
credited in the advisory unless you prefer otherwise.

## Scope

This repository covers the four `CanKit.Pro.*` packages only.

Vulnerabilities in **CanKit** itself — adapters, `ICanBus`, frame handling, vendor SDK interop —
belong to [pkuyo/CanKit](https://github.com/pkuyo/CanKit). CanKit.Pro consumes it as a NuGet
package and cannot fix it. If you are unsure which side a problem is on, report it here and it
will be routed.

## A note on threat model

CanKit.Pro processes frames from a CAN bus, which is an untrusted input in most real deployments:
a CAN network has no authentication, and any node can send any identifier. Parsing and
demultiplexing code here is expected to be robust against malformed, hostile, or merely unexpected
traffic — an unhandled exception, an unbounded allocation or a hang triggered by a crafted frame
is a security issue, not just a bug.
