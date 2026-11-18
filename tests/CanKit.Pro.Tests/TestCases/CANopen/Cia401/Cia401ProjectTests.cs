using AwesomeAssertions;
using CanKit.Pro.CANopen.Cia401;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia401;

/// <summary>
/// The CiA 401 profile project is built but not packaged yet: it sees the application seam of the
/// core package through <c>InternalsVisibleTo</c> and offers nothing to a user until the
/// publication step (ADR-14).
/// </summary>
public class Cia401ProjectTests
{
    [Fact]
    public void The_Descriptor_Answers_To_Profile_401()
    {
        var profile = new Cia401Profile();

        profile.ProfileNumber.Should().Be(401);
        profile.Name.Should().Be("CiA 401");
    }

    [Fact]
    public void The_Assembly_Exports_No_Public_Type()
    {
        // The guard that the project stays invisible until it is published: a public type here
        // would be API of a package that does not exist yet, with no approval file to hold it.
        typeof(Cia401Profile).Assembly.GetExportedTypes().Should().BeEmpty();
    }
}
