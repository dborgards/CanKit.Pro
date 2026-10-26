using System;
using System.Collections.Generic;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

public class ForeignSrdoConfigurationTests
{
    private static ForeignSrdoConfiguration Make(byte peer = 5, int number = 1, uint id1 = 0x101, uint id2 = 0x102,
        IEnumerable<PdoMappingEntry>? mapping = null, double srvt = 10, double sct = 100, string provenance = "DCF rev 3")
        => new(peer, number, id1, id2, mapping ?? Array.Empty<PdoMappingEntry>(), TimeSpan.FromMilliseconds(srvt), TimeSpan.FromMilliseconds(sct), provenance);

    [Fact]
    public void Mapping_Is_Copied_And_Configuration_Metadata_Is_Immutable()
    {
        var entries = new[] { new PdoMappingEntry(0x2000, 1, 8) };
        var config = Make(mapping: entries);
        entries[0] = new PdoMappingEntry(0x3000, 2, 16);
        config.Mapping[0].Index.Should().Be(0x2000); config.Mapping[0].Subindex.Should().Be(1);
        config.PeerNodeId.Should().Be(5); config.Number.Should().Be(1); config.CobId1.Should().Be(0x101); config.CobId2.Should().Be(0x102);
        config.ValidationTime.Should().Be(TimeSpan.FromMilliseconds(10)); config.SafeguardCycleTime.Should().Be(TimeSpan.FromMilliseconds(100));
        config.Provenance.Should().Be("DCF rev 3"); config.SourceSnapshot.Should().BeNull(); config.ConfigurationId.Should().NotBeEmpty();
        ((IList<PdoMappingEntry>)config.Mapping).Invoking(x => x.Clear()).Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData(0, 1, 0x101u, 0x102u, 10, 100, "ok")]
    [InlineData(128, 1, 0x101u, 0x102u, 10, 100, "ok")]
    [InlineData(1, 0, 0x101u, 0x102u, 10, 100, "ok")]
    [InlineData(1, 65, 0x101u, 0x102u, 10, 100, "ok")]
    [InlineData(1, 1, 0x102u, 0x103u, 10, 100, "ok")]
    [InlineData(1, 1, 0x101u, 0x104u, 10, 100, "ok")]
    [InlineData(1, 1, 0x101u, 0x102u, 0, 100, "ok")]
    [InlineData(1, 1, 0x101u, 0x102u, 256, 100, "ok")]
    [InlineData(1, 1, 0x101u, 0x102u, 10, 0, "ok")]
    [InlineData(1, 1, 0x101u, 0x102u, 10, 65536, "ok")]
    [InlineData(1, 1, 0x101u, 0x102u, 10, 100, " ")]
    public void Invalid_Prepared_Inputs_Are_Explicit_Exceptions(byte peer, int number, uint id1, uint id2, int srvt, int sct, string provenance)
    {
        Action construct = () => Make(peer, number, id1, id2, srvt: srvt, sct: sct, provenance: provenance);
        construct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Null_And_Invalid_Mapping_Are_Rejected_Before_Monitoring()
    {
        Action missing = () => new ForeignSrdoConfiguration(1, 1, 0x101, 0x102, null!, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), "DCF");
        missing.Should().Throw<ArgumentNullException>();
        Action invalid = () => Make(mapping: new[] { default(PdoMappingEntry) });
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static PeerSafetySnapshot Snapshot(SrdoDirection direction, bool eligible = true, bool parameterPresent = true)
    {
        var parameter = new SrdoCommunicationParameter(direction, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(20), 0x101, 0x102);
        var entry = new PeerSafetyEntry(0x1301, 3, new byte[] { 20 }, SafetyValueOrigin.CiA319Default);
        return new PeerSafetySnapshot(5, new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.Zero,
            new[] { entry }, Array.Empty<PeerSafetyEntry>(),
            new[] { new PeerSafetySrdoSnapshot(1, parameterPresent ? parameter : null,
                new[] { new PdoMappingEntry(0x2000, 1, 8) }, 0x1234, 0x1234, SafetyComparison.Match, eligible) },
            Array.Empty<PeerSafetyFinding>(), true, true, SafetyComparison.NotRequested, PeerSafetySnapshotConsistency.Unconfirmed);
    }

    [Fact]
    public void Producer_Snapshot_Requires_Both_Observer_Times_And_Preserves_Full_Provenance()
    {
        var snapshot = Snapshot(SrdoDirection.Transmit);
        Action absent = () => ForeignSrdoConfiguration.FromSnapshot(snapshot, 1);
        Action noSct = () => ForeignSrdoConfiguration.FromSnapshot(snapshot, 1, TimeSpan.FromMilliseconds(3));
        Action noSrvt = () => ForeignSrdoConfiguration.FromSnapshot(snapshot, 1, safeguardCycleTime: TimeSpan.FromMilliseconds(99));
        absent.Should().Throw<ArgumentException>(); noSct.Should().Throw<ArgumentException>(); noSrvt.Should().Throw<ArgumentException>();
        var config = ForeignSrdoConfiguration.FromSnapshot(snapshot, 1, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(99));
        config.SourceSnapshot.Should().BeSameAs(snapshot); config.ConfigurationId.Should().Be(snapshot.ConfigurationId);
        config.SourceSnapshot!.Entries[0].Origin.Should().Be(SafetyValueOrigin.CiA319Default);
        config.ValidationTime.Should().Be(TimeSpan.FromMilliseconds(3)); config.SafeguardCycleTime.Should().Be(TimeSpan.FromMilliseconds(99));
        config.Mapping[0].Index.Should().Be(0x2000);
    }

    [Fact]
    public void Receiver_Snapshot_Uses_Consumer_Times_And_Rejects_Ineligible_Missing_Or_Null_Input()
    {
        var config = ForeignSrdoConfiguration.FromSnapshot(Snapshot(SrdoDirection.Receive), 1);
        config.ValidationTime.Should().Be(TimeSpan.FromMilliseconds(20)); config.SafeguardCycleTime.Should().Be(TimeSpan.FromMilliseconds(250));
        Action ineligible = () => ForeignSrdoConfiguration.FromSnapshot(Snapshot(SrdoDirection.Receive, eligible: false), 1);
        Action missingParameter = () => ForeignSrdoConfiguration.FromSnapshot(Snapshot(SrdoDirection.Receive, parameterPresent: false), 1);
        Action missingRecord = () => ForeignSrdoConfiguration.FromSnapshot(Snapshot(SrdoDirection.Receive), 2);
        Action missing = () => ForeignSrdoConfiguration.FromSnapshot(null!, 1);
        ineligible.Should().Throw<ArgumentException>(); missingParameter.Should().Throw<ArgumentException>();
        missingRecord.Should().Throw<ArgumentException>(); missing.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Invalid_And_Ambiguous_Monitor_Configuration_Is_Refused_Atomically()
    {
        using var service = new CanKit.Pro.Tests.Infrastructure.StarvedReaderBusService();
        var sub = service.Subscribe();
        Action missingSub = () => new ForeignSrdoMonitor(null!, "CAN", new[] { Make() });
        Action missingConfig = () => new ForeignSrdoMonitor(sub, "CAN", null!);
        Action nullConfig = () => new ForeignSrdoMonitor(sub, "CAN", new ForeignSrdoConfiguration[] { null! });
        Action overlap = () => new ForeignSrdoMonitor(sub, "CAN", new[] { Make(), Make(6, 2) });
        Action duplicate = () => new ForeignSrdoMonitor(sub, "CAN", new[] { Make(), Make(id1: 0x103, id2: 0x104) });
        missingSub.Should().Throw<ArgumentNullException>(); missingConfig.Should().Throw<ArgumentNullException>();
        nullConfig.Should().Throw<ArgumentException>(); overlap.Should().Throw<ArgumentException>(); duplicate.Should().Throw<ArgumentException>();
    }
}
