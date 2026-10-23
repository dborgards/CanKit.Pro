using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
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
/// <para>
/// On the <c>net48</c> leg of the suite this same test approves the <c>netstandard2.0</c> build,
/// because <see cref="Assembly.Load(AssemblyName)"/> resolves whichever asset that target
/// framework was given. One approval file therefore covers both shipped assets: the two surfaces
/// are expected to be identical — the sources are the same and PublicApiGenerator renders from
/// metadata rather than from the running framework's reflection — so a divergence is a finding,
/// not a baseline that needs splitting per target framework.
/// </para>
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

    /// <summary>
    /// <see cref="Tracked"/> is a hand-kept copy of "the packable projects", and a theory over a
    /// list can only check the entries it was given: dropping a package from the list, or adding
    /// a package without listing it, left every test green. This pins the list to
    /// <c>src/*/*.csproj</c>, where each project is packable by <c>src/Directory.Build.props</c>
    /// unless it opts out.
    /// </summary>
    [Fact]
    public void Every_Packable_Project_Is_Tracked()
    {
        var packable = PackableProjectIds(FindRepositoryRoot());

        Tracked.Select(t => t.PackageId).OrderBy(id => id, StringComparer.Ordinal)
            .Should().Equal(packable,
                "every project under src/ ships as a package and needs a Tracked entry here and an " +
                "approved file — and the count lives in eng/verify-packages.py, eng/verify-sboms.py and " +
                "eng/build-api-docs.sh too (eng/test_package_registry.py checks those)");
    }

    [Fact]
    public void Every_Approved_File_Belongs_To_A_Tracked_Package()
    {
        var approved = Directory.EnumerateFiles(FindApprovalsDir(), "*.approved.txt")
            .Select(path => Path.GetFileName(path)![..^".approved.txt".Length])
            .OrderBy(id => id, StringComparer.Ordinal);

        approved.Should().Equal(Tracked.Select(t => t.PackageId).OrderBy(id => id, StringComparer.Ordinal),
            "an approved file nobody tests guards nothing, and a package without one is " +
            "created from the first received file");
    }

    // The package id is the project's file name; a project opts out of packing explicitly.
    private static string[] PackableProjectIds(string repositoryRoot)
        => Directory.EnumerateFiles(Under(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !File.ReadAllText(path).Contains("<IsPackable>false</IsPackable>", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    // Fixed relative segments under a known root. Spelled out rather than Path.Combine, which drops
    // everything before a later argument that happens to be rooted (CodeQL cs/path-combine) --
    // a hazard these literals do not have, and not worth a suppression.
    private static string Under(string root, params string[] segments)
        => string.Join(Path.DirectorySeparatorChar.ToString(), new[] { root }.Concat(segments));

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").Trim();

    private static string FindApprovalsDir()
        => Under(FindRepositoryRoot(), "tests", "CanKit.Pro.Tests", "ApiApprovals");

    private static string FindRepositoryRoot()
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
        return dir.FullName;
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

        // The generator defaults to printing a record as a plain class. Three of the shipped
        // types are records — J1939SpnDefinition, J1939SpnValue and TxConfirmation — and
        // record-ness is part of what
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
