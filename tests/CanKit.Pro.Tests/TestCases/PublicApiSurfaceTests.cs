using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using PublicApiGenerator;
using Xunit;

namespace CanKit.Pro.Tests.TestCases;

/// <summary>
/// Public-API tracking for the published CanKit.Pro packages: renders each assembly's public
/// surface as C# declarations and compares it against the checked-in approved file under
/// <c>tests/CanKit.Pro.Tests/ApiApprovals/&lt;PackageId&gt;.approved.txt</c>. Any API change
/// fails the test and drops a <c>.received.txt</c> next to the approval so the reviewer can
/// see exactly what changed — updating the approval is a deliberate act in the same PR.
/// </summary>
/// <remarks>
/// The rendering comes from <see href="https://github.com/PublicApiGenerator/PublicApiGenerator">
/// PublicApiGenerator</see> rather than from reflection walked by hand. The hand-rolled renderer
/// this replaces printed a type's kind, its name and its member signatures and nothing else, so
/// six kinds of breaking change slipped past it unchanged: sealing a type or dropping
/// <c>abstract</c>, removing a base type or an implemented interface, changing or deleting a
/// parameter default, flipping an <c>in</c>/<c>ref</c>/<c>out</c> modifier, adding or removing an
/// attribute (<c>[Obsolete]</c>, <c>[Flags]</c>), and tightening a nullable annotation.
/// PublicApiGenerator reports all six, because it emits compilable C# declarations rather than a
/// summary of them.
/// </remarks>
public class PublicApiSurfaceTests
{
    private static readonly (string PackageId, string AssemblyName)[] Tracked =
    {
        ("CanKit.Pro.Actor", "CanKit.Pro.Actor"),
        ("CanKit.Pro.Addressing", "CanKit.Pro.Addressing"),
        ("CanKit.Pro.RawCan", "CanKit.Pro.RawCan"),
        ("CanKit.Pro.Reliability", "CanKit.Pro.Reliability"),
        ("CanKit.Pro.IsoTp", "CanKit.Pro.IsoTp"),
        ("CanKit.Pro.J1939Tp", "CanKit.Pro.J1939Tp"),
        ("CanKit.Pro.CANopen", "CanKit.Pro.CANopen"),
        ("CanKit.Pro.J1939", "CanKit.Pro.J1939"),
        ("CanKit.Pro.Uds", "CanKit.Pro.Uds"),
    };

    [Theory]
    [MemberData(nameof(TrackedPackages))]
    public void Public_Api_Surface_Matches_The_Approved_File(string packageId, string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));
        var actual = Render(assembly);

        var approvalsDir = FindApprovalsDir();
        var approvedPath = Path.Combine(approvalsDir, $"{packageId}.approved.txt");
        if (!File.Exists(approvedPath))
        {
            Directory.CreateDirectory(approvalsDir);
            File.WriteAllText(Path.Combine(approvalsDir, $"{packageId}.received.txt"), actual);
            File.Exists(approvedPath).Should().BeTrue(
                $"no approved API file for {packageId} at {approvedPath} — create it from the .received.txt this test just dropped");
            return;
        }

        var approved = File.ReadAllText(approvedPath);
        if (!string.Equals(Normalize(approved), Normalize(actual), StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(approvalsDir, $"{packageId}.received.txt"), actual);
            approved.Should().Be(actual,
                $"public API of {packageId} changed — review {packageId}.received.txt vs .approved.txt " +
                "and update the approval in the same PR if the change is intended");
        }
    }

    public static IEnumerable<object[]> TrackedPackages
        => Tracked.Select(t => new object[] { t.PackageId, t.AssemblyName });

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").Trim();

    private static string FindApprovalsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CanKit.Pro.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (CanKit.Pro.sln).");
        }
        return Path.Combine(dir.FullName, "tests", "CanKit.Pro.Tests", "ApiApprovals");
    }

    // Canonical, diff-stable rendering of the assembly's public API.
    private static string Render(Assembly assembly)
        => assembly.GeneratePublicApi(RenderOptions);

    private static readonly ApiGeneratorOptions RenderOptions = new()
    {
        // Assembly-level attributes are left out on purpose. They carry the build's version
        // (AssemblyVersion, AssemblyInformationalVersion), and nothing in this repository computes
        // that inside the build — it is 0.0.0 locally, the GitVersion SemVer in CI, and whatever
        // semantic-release derived on a release (see Directory.Build.props). Including them would
        // fail the approval on every CI run for a reason that has nothing to do with the public
        // API. TargetFrameworkAttribute sits in the same set and would move with the TFMs.
        IncludeAssemblyAttributes = false,

        // The generator defaults to printing a record as a plain class. Two of the shipped types
        // are records — J1939SpnDefinition and TxConfirmation — and record-ness is part of what
        // they promise: value equality, `with` expressions, and for the positional one a
        // deconstruction that the class rendering would drop along with the primary constructor's
        // parameter list. Turning a record back into a class is a breaking change, so print it.
        TreatRecordsAsClasses = false,

        // Compiler bookkeeping that records *how* the assembly was built rather than what it
        // offers a consumer. The nullable attributes matter most: the generator already turns
        // them into `?` annotations on the signatures themselves, so leaving them listed would
        // print every annotation twice.
        ExcludeAttributes = new[]
        {
            "System.Diagnostics.DebuggerNonUserCodeAttribute",
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "System.Runtime.CompilerServices.NullableAttribute",
            "System.Runtime.CompilerServices.NullableContextAttribute",
        },
    };
}
