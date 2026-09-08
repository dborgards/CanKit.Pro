# Third-party notices

CanKit.Pro itself is licensed under the [MIT License](LICENSE). It contains no third-party source
code: everything it builds on is resolved from nuget.org at build time and keeps its own license.

This file lists those dependencies so that anyone shipping CanKit.Pro knows what else ends up in
their application. It is informational — each package carries its own license metadata, which is
what governs.

## Runtime dependencies

### CanKit — Apache License 2.0

CanKit.Pro is built **on top of** [CanKit](https://github.com/pkuyo/CanKit) by pkuyo and
contributors, consumed as ordinary NuGet packages (`CanKit.Abstractions`, and for applications
`CanKit.Core` plus an adapter). CanKit is not forked, vendored, or redistributed here; no CanKit
source file is part of this repository.

    Copyright (c) 2025 pkuyo and contributors
    Licensed under the Apache License, Version 2.0
    https://github.com/pkuyo/CanKit/blob/master/LICENSE

An application that references both CanKit.Pro and CanKit is distributing Apache-2.0 licensed
binaries and must satisfy the Apache-2.0 attribution terms for them, independently of CanKit.Pro's
MIT license.

### .NET platform packages — MIT

`Microsoft.Bcl.AsyncInterfaces`, `System.Memory` and `System.Threading.Channels` are referenced by
`CanKit.Pro.RawCan` on `netstandard2.0` only; on `net8.0` those APIs are in the framework and no
package is pulled in.

    Copyright (c) .NET Foundation and Contributors
    Licensed under the MIT License

## Build- and test-only dependencies

These never reach a consumer's application: they are `PrivateAssets`/test-project references.

| Package | License |
| --- | --- |
| [GitVersion.MsBuild](https://github.com/GitTools/GitVersion) | MIT |
| [xunit](https://github.com/xunit/xunit), xunit.runner.visualstudio | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |
| [FluentAssertions](https://github.com/fluentassertions/fluentassertions) (6.x) | Apache-2.0 |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | MIT |
| [semantic-release](https://github.com/semantic-release/semantic-release) and plugins | MIT |

> FluentAssertions is pinned to the 6.x line on purpose: from 8.0.0 it is published under the
> Xceed commercial license. See the comment in `Directory.Packages.props`.

## Relationship to CanKit.Pro.legacy

The four packages in this repository were originally developed inside
[CanKit.Pro.legacy](https://github.com/dborgards/CanKit.Pro.legacy), a fork of CanKit that was
therefore Apache-2.0 in its entirety. Only code originally written in that fork — the four
`CanKit.Pro.*` libraries, their tests, and their documentation — was migrated here; no forked
CanKit source came along. See [docs/licensing.md](docs/licensing.md) for the reasoning behind the
change of license.
