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

### EdsDcfNet — MIT

`CanKit.Pro.CANopen` reads CiA 306 device descriptions (EDS, DCF) with
[EdsDcfNet](https://github.com/dborgards/eds-dcf-net), the maintainer's own library, consumed as
an ordinary NuGet package. It has no runtime dependencies of its own.

    Copyright (c) 2025 Dietmar Borgards
    Licensed under the MIT License
    https://github.com/dborgards/eds-dcf-net/blob/main/LICENSE

### .NET platform packages — MIT

`Microsoft.Bcl.AsyncInterfaces`, `System.Memory` and `System.Threading.Channels` are referenced by
the six packages that use them (`RawCan`, `IsoTp`, `J1939`, `J1939Tp`, `Uds`, `CanOpen`) on
`netstandard2.0` only; on `net10.0` those APIs are in the framework and no
package is pulled in.

    Copyright (c) .NET Foundation and Contributors
    Licensed under the MIT License

## Build- and test-only dependencies

These never reach a consumer's application: they are `PrivateAssets`/test-project references,
local dotnet tools, or part of the website and release tooling.

| Package | License |
| --- | --- |
| [GitVersion](https://github.com/GitTools/GitVersion) (`gitversion.tool`, a local dotnet tool) | MIT |
| [xunit](https://github.com/xunit/xunit), xunit.runner.visualstudio | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |
| [AwesomeAssertions](https://github.com/AwesomeAssertions/AwesomeAssertions) | Apache-2.0 |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | MIT |
| [Polyfill](https://github.com/SimonCropp/Polyfill) (source-only; BCL APIs for `netstandard2.0`/`net48`) | MIT |
| [PublicApiGenerator](https://github.com/PublicApiGenerator/PublicApiGenerator) (public-API approval tests) | MIT |
| [CycloneDX](https://github.com/CycloneDX/cyclonedx-dotnet) (`cyclonedx`, a local dotnet tool; generates the SBOMs) | Apache-2.0 |
| [DefaultDocumentation](https://github.com/Doraku/DefaultDocumentation) (`defaultdocumentation.console`, a local dotnet tool; renders the API reference) | MIT-0 |
| [semantic-release](https://github.com/semantic-release/semantic-release) and plugins | MIT |
| [MkDocs](https://github.com/mkdocs/mkdocs) | BSD-2-Clause |
| [mkdocs-material](https://github.com/squidfunk/mkdocs-material), [mkdocs-literate-nav](https://github.com/oprypin/mkdocs-literate-nav) (the project website) | MIT |
| [PyYAML](https://github.com/yaml/pyyaml) (label sync) | MIT |

> AwesomeAssertions is used instead of FluentAssertions on purpose: from 8.0.0 FluentAssertions is
> published under the Xceed commercial license. See the comment in `Directory.Packages.props`.

## Relationship to CanKit.Pro.legacy

The packages in this repository were originally developed inside
[CanKit.Pro.legacy](https://github.com/dborgards/CanKit.Pro.legacy), a fork of CanKit that was
therefore Apache-2.0 in its entirety. Only code originally written in that fork — the four
`CanKit.Pro.*` libraries, their tests, and their documentation — was migrated here; no forked
CanKit source came along. See [docs/licensing.md](docs/licensing.md) for the reasoning behind the
change of license.
