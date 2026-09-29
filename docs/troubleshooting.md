# Troubleshooting

## Which build am I running?

A released package carries its version in the file name and in `nuget.org`: `1.4.2` means the
`v1.4.2` tag. A build made from a working copy does not — a plain `dotnet build` or `dotnet pack`
produces version `0.0.0`, deliberately (see [Release process](release-process.md)). Two `0.0.0`
builds can be different code, so the package version alone does not identify one.

| Version you see | What it is |
| --- | --- |
| `X.Y.Z` | A release. The package's embedded commit (below) is the code it was built from; the `vX.Y.Z` tag points at that commit or a later one, because the release's own changelog commit is made after packing. |
| `0.0.0` | A local build (or a build from a source archive) with no version passed in. |
| `0.0.0-unversioned.<n>` | A CI build where GitVersion could not name the commit; `<n>` is the workflow run number. |

Both `0.0.0` forms built from a git checkout still identify their commit: it is recorded in the
assembly and in the package. A build from a downloaded source archive has no `.git` directory, so
neither carries a commit (the informational version is a bare `0.0.0`); say where the archive came
from instead, for example the release tag or commit it was downloaded for.

### Read the commit from the assembly

The assembly's informational version is `<version>+<commit sha>`. For a local build it looks
like `0.0.0+a92dce82f5b0b598f24d2b20934ccf1da59c5e61`.

```powershell
# PowerShell
[System.Diagnostics.FileVersionInfo]::GetVersionInfo("CanKit.Pro.RawCan.dll").ProductVersion
```

```csharp
// In your own code (needs: using System.Reflection;)
// Any type from the assembly in question will do.
string? version = typeof(CanKit.Pro.RawCan.CanBusServiceExtensions).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion;
```

On Windows, the file's Properties dialog shows the same string as *Product version*.

### Read the commit from the package

A `.nupkg` is a zip file. Its `.nuspec` names the commit the package was packed from:

```xml
<repository type="git" url="https://github.com/dborgards/CanKit.Pro" commit="a92dce82…" />
```

### What to put in a bug report

For a release, the package version is enough. For any `0.0.0` build, also give the informational
version (the `+<sha>` part) from above, and say whether the build was your own or a CI artifact.
The commit decides which code the report is about; without it a `0.0.0` report cannot be
reproduced. The commit is the checkout's `HEAD`, not the working tree: if the build had
uncommitted changes, say so and attach the diff (or push a commit containing them), because two
different local builds of one `HEAD` carry the same string.
