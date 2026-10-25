using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Read-only acquisition and diagnostics (FR-CO-060; #403).</summary>
public class CanOpenSafetyInspectionTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private static CanOpenDeviceDescription Description() => CanOpenDeviceDescription.ParseDcf(CanOpenFixtures.Text("safety.dcf"));
    private static SrdoCommunicationParameter Parameter => new(SrdoDirection.Transmit,
        TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
    private static PeerSafetyConfiguration Expected() => new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
        .Add(1, Parameter, new SrdoMapping().Add(0x2000, 0, 16));

    private static Dictionary<(ushort, byte), byte[]> Image()
    {
        var p = Parameter;
        ushort crc = SrdoCrc.Compute(p, new SrdoMapping().Add(0x2000, 0, 16));
        return new()
        {
            [(0x1018, 1)] = ObjectDictionary.EncodeU32(0x100),
            [(0x1018, 2)] = ObjectDictionary.EncodeU32(0x1001),
            [(0x1018, 3)] = ObjectDictionary.EncodeU32(0x10000),
            [(0x1018, 4)] = ObjectDictionary.EncodeU32(123),
            [(0x1300, 0)] = new byte[] { 1 },
            [(0x13FE, 0)] = new byte[] { 0xA5 },
            [(0x13FF, 0)] = new byte[] { 1 },
            [(0x1301, 0)] = new byte[] { 6 },
            [(0x1301, 1)] = new byte[] { 1 },
            [(0x1301, 2)] = new byte[] { 30, 0 },
            [(0x1301, 3)] = new byte[] { 20 },
            [(0x1301, 4)] = new byte[] { 254 },
            [(0x1301, 5)] = ObjectDictionary.EncodeU32(0x109),
            [(0x1301, 6)] = ObjectDictionary.EncodeU32(0x10A),
            [(0x1381, 0)] = new byte[] { 2 },
            [(0x1381, 1)] = ObjectDictionary.EncodeU32(0x20000010),
            [(0x1381, 2)] = ObjectDictionary.EncodeU32(0x20000010),
            [(0x13FF, 1)] = new[] { (byte)crc, (byte)(crc >> 8) },
        };
    }

    private sealed class Rig : IDisposable
    {
        public readonly Dictionary<(ushort, byte), byte[]> Values = Image();
        public readonly Dictionary<(ushort, byte), uint> Aborts = new();
        public readonly ConcurrentQueue<CanFrame> Sent = new();
        public readonly ConcurrentDictionary<(ushort, byte), int> Reads = new();
        public readonly ManualTimeSource Clock = new();
        public readonly ControllableBus Bus;
        public readonly CanOpenNode Master;
        public Action<ushort, byte, int>? BeforeReply;
        public (ushort, byte)? Hold;
        public readonly TaskCompletionSource<bool> Holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _boot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _reply;
        private bool _recording;

        public Rig()
        {
            Bus = ControllableBus.Plain($"inspection-{Guid.NewGuid():N}");
            Bus.OnTransmitting = frame =>
            {
                if (frame.ID == 0x701) { _boot.TrySetResult(true); return; }
                if (_recording) Sent.Enqueue(frame);
                if (frame.ID != 0x605 || frame.Data.Span[0] != 0x40) return;
                var d = frame.Data.ToArray();
                ushort index = (ushort)(d[1] | d[2] << 8);
                byte sub = d[3];
                int read = Reads.AddOrUpdate((index, sub), 1, (_, n) => n + 1);
                BeforeReply?.Invoke(index, sub, read);
                void Reply()
                {
                    var response = new byte[8];
                    response[1] = d[1]; response[2] = d[2]; response[3] = sub;
                    uint abort = Aborts.TryGetValue((index, sub), out var configured) ? configured : Values.ContainsKey((index, sub)) ? 0u : 0x06090011u;
                    if (abort != 0)
                    {
                        response[0] = 0x80;
                        ObjectDictionary.EncodeU32(abort).CopyTo(response, 4);
                    }
                    else
                    {
                        var value = Values[(index, sub)];
                        response[0] = (byte)(0x43 | ((4 - value.Length) << 2));
                        value.CopyTo(response, 4);
                    }
                    Bus.RaiseObserved(CanFrame.Classic(0x585, response), isEcho: false);
                }
                if (Hold == (index, sub)) { Hold = null; _reply = Reply; Holding.TrySetResult(true); }
                else Reply();
            };
            Master = new CanOpenNode(new CanBusService(Bus), 1, new CanOpenNodeOptions(), ownsService: true, Clock);
            Master.BindPeerDeviceDescription(5, Description());
        }
        public async Task Start()
        {
            await _boot.Task.WaitAsync(Watchdog);
            await Master.PostToActorAsync(() => { }).WaitAsync(Watchdog);
            _recording = true;
        }
        public void Release() { var reply = _reply; _reply = null; reply?.Invoke(); }
        public Task<PeerSafetySnapshot> Read(PeerSafetyConfiguration? expected = null, CancellationToken ct = default)
            => Master.Safety().ReadPeerSafetyConfigurationAsync(5, expected, ct).WaitAsync(Watchdog);
        public void Dispose() { Master.Dispose(); Bus.Dispose(); }
    }

    [Fact]
    public async Task Reads_Without_An_Expected_Dcf_And_Separates_The_Flag_Signature_And_Expectation()
    {
        using var r = new Rig(); await r.Start();
        var snapshot = await r.Read();
        snapshot.IsComplete.Should().BeTrue();
        snapshot.DeviceConfigurationValid.Should().BeTrue();
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.NotRequested);
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.Unconfirmed);
        var srdo = snapshot.Srdos.Single();
        srdo.Parameter.Should().Be(Parameter);
        srdo.SignatureComparison.Should().Be(SafetyComparison.Match);
        srdo.EligibleForMonitoring.Should().BeTrue();
        srdo.Mapping.Single().Index.Should().Be(0x2000);
        snapshot.PeerNodeId.Should().Be(5);
        snapshot.StartedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        snapshot.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.NormativeUncertainty);
        snapshot.Entries.Single(e => e.Index == 0x1018 && e.Subindex == 1).UnsignedValue.Should().Be(0x100);
        r.Sent.Should().NotBeEmpty().And.OnlyContain(f => f.ID == 0x605 && f.Data.ToArray()[0] == 0x40,
            "inspection may only send SDO uploads, no downloads, NMT, SRDO, GFC or store commands");
        r.Reads.Values.Should().OnlyContain(count => count == 2);
        var second = await r.Read(Expected());
        second.ExpectedComparison.Should().Be(SafetyComparison.Match);
        second.ConfigurationId.Should().NotBe(snapshot.ConfigurationId);
        ((IList<byte>)snapshot.Entries[0].RawValue).Invoking(v => v[0] = 99).Should().Throw<NotSupportedException>();
        r.Values[(0x1018, 1)][0] = 99;
        snapshot.Entries[0].UnsignedValue.Should().Be(0x100);
    }

    [Fact]
    public async Task A_True_Device_Flag_Does_Not_Hide_A_Bad_Signature_Or_Expected_Difference()
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FF, 1)] = new byte[] { 0, 0 };
        var snapshot = await r.Read(Expected());
        snapshot.DeviceConfigurationValid.Should().BeTrue();
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Mismatch);
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Mismatch);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.SignatureMismatch);
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.ExpectedMismatch);
    }

    [Fact]
    public async Task Partial_Reads_Retain_File_Provenance_And_Abort_Without_Verifying_A_Mixed_Image()
    {
        using var r = new Rig(); await r.Start();
        r.Aborts[(0x1301, 2)] = 0x08000022;
        var snapshot = await r.Read(Expected());
        var entry = snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 2);
        entry.Origin.Should().Be(SafetyValueOrigin.DeviceDescription);
        entry.AbortCode.Should().Be(0x08000022);
        entry.AbortOrigin.Should().Be(SdoAbortOrigin.Peer);
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Srdos[0].ComputedSignature.Should().BeNull();
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Unavailable);
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Unavailable);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.SdoAbort && f.Index == 0x1301 && f.Subindex == 2);
        r.Sent.Should().OnlyContain(f => f.ID == 0x605 && f.Data.ToArray()[0] == 0x40);
    }

    [Fact]
    public async Task Changed_Live_Values_Are_Retained_And_Disqualify_The_Whole_Evaluation_Period()
    {
        using var r = new Rig(); await r.Start();
        r.BeforeReply = (index, sub, read) => { if ((index, sub, read) == (0x1301, 2, 2)) r.Values[(index, sub)] = new byte[] { 31, 0 }; };
        var snapshot = await r.Read();
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.ChangeObserved);
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 2).UnsignedValue.Should().Be(30);
        snapshot.RecheckEntries.Single(e => e.Index == 0x1301 && e.Subindex == 2).UnsignedValue.Should().Be(31);
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.ConfigurationChanged);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Fact]
    public async Task Holds_The_Peer_Channel_Snapshots_The_Binding_And_Releases_On_Cancellation()
    {
        using var r = new Rig(); await r.Start();
        r.Hold = (0x13FF, 0);
        using var cancel = new CancellationTokenSource();
        var pending = r.Read(ct: cancel.Token);
        await r.Holding.Task.WaitAsync(Watchdog);
        r.Master.PeerSdoChannelIsFreeForTests(5).Should().BeFalse("the acquisition reserves the peer channel");
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Master.SdoUploadAsync(5, 0x1018, 1));
        r.Master.UnbindPeerDeviceDescription(5);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        r.Master.PeerSdoChannelIsFreeForTests(5).Should().BeTrue();
        r.Release();
        r.Master.BindPeerDeviceDescription(5, Description());
        (await r.Read()).IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task A_Rebind_During_Acquisition_Does_Not_Change_Its_Access_Gate()
    {
        using var r = new Rig(); await r.Start();
        r.Hold = (0x13FF, 0);
        var expected = Expected();
        var pending = r.Read(expected);
        await r.Holding.Task.WaitAsync(Watchdog);
        r.Master.UnbindPeerDeviceDescription(5);
        expected.Add(1, Parameter with { RefreshOrSafeguardCycleTime = TimeSpan.FromMilliseconds(40) }, new SrdoMapping().Add(0x2000, 0, 16));
        r.Release();
        var snapshot = await pending;
        snapshot.IsComplete.Should().BeTrue();
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Match, "the expectation was captured before the pause");
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => r.Read());
    }

    [Fact]
    public async Task No_Bound_Description_Is_Refused_Before_Any_Traffic()
    {
        using var r = new Rig(); await r.Start();
        r.Master.UnbindPeerDeviceDescription(5);
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => r.Read());
        r.Sent.Should().BeEmpty();
        r.Master.PeerSdoChannelIsFreeForTests(5).Should().BeTrue();
    }
    public static IEnumerable<object[]> InvalidRecords()
    {
        yield return new object[] { (ushort)0x1301, (byte)0, new byte[] { 7 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)1, new byte[] { 3 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)2, new byte[] { 0, 0 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)3, new byte[] { 0 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)4, new byte[] { 0 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)5, ObjectDictionary.EncodeU32(0), PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)6, ObjectDictionary.EncodeU32(0), PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1301, (byte)2, new byte[] { 1, 2, 3 }, PeerSafetyFindingKind.InvalidValue };
        yield return new object[] { (ushort)0x1381, (byte)0, new byte[] { 0 }, PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)0, new byte[] { 1 }, PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)0, new byte[] { 17 }, PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)0, new byte[] { 129 }, PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)0, new byte[] { 2, 0 }, PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)1, ObjectDictionary.EncodeU32(0), PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x1381, (byte)2, ObjectDictionary.EncodeU32(0), PeerSafetyFindingKind.InvalidMapping };
        yield return new object[] { (ushort)0x13FE, (byte)0, new byte[] { 1 }, PeerSafetyFindingKind.InvalidValue };
    }

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public async Task Invalid_Raw_Records_Are_Retained_And_Are_Not_Monitor_Input(ushort index, byte sub, byte[] raw, PeerSafetyFindingKind finding)
    {
        using var r = new Rig(); await r.Start();
        r.Values[(index, sub)] = raw;
        var snapshot = await r.Read();
        snapshot.Entries.Single(e => e.Index == index && e.Subindex == sub).RawValue.Should().Equal(raw);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == finding);
        if (index == 0x1381 && sub == 0 && (raw.Length != 1 || raw[0] > 128)) snapshot.IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData(0x1301, 0)]
    [InlineData(0x1301, 1)]
    [InlineData(0x1301, 2)]
    [InlineData(0x1301, 3)]
    [InlineData(0x1301, 4)]
    [InlineData(0x1301, 5)]
    [InlineData(0x1301, 6)]
    [InlineData(0x1381, 0)]
    [InlineData(0x1381, 1)]
    [InlineData(0x1381, 2)]
    [InlineData(0x13FF, 1)]
    [InlineData(0x13FE, 0)]
    public async Task Any_Missing_Required_Entry_Prevents_Eligibility(int index, int sub)
    {
        using var r = new Rig(); await r.Start();
        r.Aborts[((ushort)index, (byte)sub)] = 0x06090011;
        var snapshot = await r.Read();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Srdos.Should().OnlyContain(s => !s.EligibleForMonitoring);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(3)]
    public async Task Peer_Counts_Do_Not_Allow_An_Unbounded_Scan(int count)
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FF, 0)] = new[] { (byte)count };
        var snapshot = await r.Read(Expected());
        snapshot.Srdos.Count.Should().Be(count == 0 ? 0 : 2, "the bound file declares at most two SRDOs");
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Unavailable);
        if (count > 2) snapshot.IsComplete.Should().BeFalse();
        r.Reads.Keys.Should().NotContain(k => k.Item1 == 0x1303 || k.Item1 == 0x1383);
    }

    private static CanOpenDeviceDescription WithoutSrvt()
    {
        string text = CanOpenFixtures.Text("safety.dcf").Replace("\r\n", "\n");
        int start = text.IndexOf("[1301sub3]", StringComparison.Ordinal);
        int end = text.IndexOf("[1301sub4]", StringComparison.Ordinal);
        return CanOpenDeviceDescription.ParseDcf(text[..start] + text[end..]);
    }

    [Theory]
    [InlineData(1, 0x06090011u, SafetyValueOrigin.CiA319Default)]
    [InlineData(1, 0x06010000u, SafetyValueOrigin.Unavailable)]
    [InlineData(2, 0x06090011u, SafetyValueOrigin.Unavailable)]
    public async Task Only_An_Unimplemented_Undeclared_Producer_Srvt_Uses_The_Explicit_CiA319_Rule(int direction, uint abort, SafetyValueOrigin origin)
    {
        using var r = new Rig(); await r.Start();
        r.Master.BindPeerDeviceDescription(5, WithoutSrvt());
        r.Values[(0x1301, 1)] = new[] { (byte)direction };
        r.Aborts[(0x1301, 3)] = abort;
        var snapshot = await r.Read();
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 3).Origin.Should().Be(origin);
        if (origin == SafetyValueOrigin.CiA319Default)
        {
            snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Match);
            snapshot.Srdos[0].EligibleForMonitoring.Should().BeTrue();
        }
        else snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Fact]
    public async Task A_Recheck_That_Becomes_Unavailable_Does_Not_Permit_A_Complete_Image()
    {
        using var r = new Rig(); await r.Start();
        r.BeforeReply = (index, sub, read) => { if ((index, sub, read) == (0x1301, 2, 2)) r.Aborts[(index, sub)] = 0x08000022; };
        var snapshot = await r.Read();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.Unconfirmed);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleted_Records_Do_Not_Report_A_Stale_Checksum_As_A_Failure(bool clearMapping)
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x1301, 1)] = new byte[] { 0 };
        if (clearMapping) r.Values[(0x1381, 0)] = new byte[] { 0 };
        r.Values[(0x13FF, 1)] = new byte[] { 0x12, 0x34 };
        var snapshot = await r.Read(new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true });
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Match);
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.NotRequested);
        snapshot.Srdos[0].Mapping.Should().BeEmpty();
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().NotContain(f => f.Kind == PeerSafetyFindingKind.SignatureMismatch);
    }

    [Fact]
    public async Task Shared_CobIds_Disqualify_Both_Records()
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FF, 0)] = new byte[] { 2 };
        r.Values[(0x13FF, 2)] = Image()[(0x13FF, 1)];
        foreach (var entry in Image().Where(e => e.Key.Item1 is 0x1301 or 0x1381))
            r.Values[((ushort)(entry.Key.Item1 + 1), entry.Key.Item2)] = entry.Value;
        var snapshot = await r.Read();
        snapshot.Srdos.Should().OnlyContain(s => !s.EligibleForMonitoring);
        snapshot.Findings.Should().Contain(f => f.Message.Contains("also used"));
    }

    [Theory]
    [InlineData(0x20000000u)]
    [InlineData(0x2000000Cu)]
    [InlineData(0x00020008u)]
    public async Task Paired_But_Unusable_Mapping_Words_Are_Diagnosed(uint word)
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x1381, 1)] = r.Values[(0x1381, 2)] = ObjectDictionary.EncodeU32(word);
        var snapshot = await r.Read();
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.InvalidMapping);
    }

    [Fact]
    public async Task A_Valid_Signature_With_A_Clear_Device_Flag_Is_Not_Eligible()
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FE, 0)] = new byte[] { 0 };
        var snapshot = await r.Read();
        snapshot.DeviceConfigurationValid.Should().BeFalse();
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Match);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Fact]
    public async Task A_Malformed_Count_Uses_Only_The_Description_Bound_And_Reports_Partial_Data()
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FF, 0)] = new byte[] { 1, 0 };
        var snapshot = await r.Read();
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Srdos.Count.Should().Be(2);
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.InvalidValue && f.Index == 0x13FF);
    }

    [Fact]
    public async Task A_Local_Timeout_Is_Retained_And_Does_Not_Apply_The_Peer_Srvt_Default_Rule()
    {
        using var r = new Rig(); await r.Start();
        r.Master.BindPeerDeviceDescription(5, WithoutSrvt());
        r.Hold = (0x1301, 3);
        var pending = r.Read();
        await r.Holding.Task.WaitAsync(Watchdog);
        await r.Master.PostToActorAsync(() => { }).WaitAsync(Watchdog);
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await r.Master.PostToActorAsync(() => { }).WaitAsync(Watchdog);
        var snapshot = await pending;
        var entry = snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 3);
        entry.AbortOrigin.Should().Be(SdoAbortOrigin.Local);
        entry.Origin.Should().Be(SafetyValueOrigin.Unavailable);
        snapshot.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_Transmit_Refusal_Produces_An_Unavailable_Finding_And_Keeps_File_Provenance()
    {
        using var r = new Rig(); await r.Start();
        bool fail = true;
        r.BeforeReply = (index, sub, _) =>
        {
            if ((index, sub) == (0x1301, 5) && fail) { fail = false; throw new InvalidOperationException("injected transmit refusal"); }
        };
        var snapshot = await r.Read();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.Unavailable && f.Index == 0x1301 && f.Subindex == 5);
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 5).Origin.Should().Be(SafetyValueOrigin.DeviceDescription);
        snapshot.IsComplete.Should().BeFalse();
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("65536")]
    public async Task Unusable_File_Values_Are_Not_Truncated_Or_Manufactured(string textValue)
    {
        using var r = new Rig(); await r.Start();
        string text = CanOpenFixtures.Text("safety.dcf").Replace("\r\n", "\n");
        int a = text.IndexOf("[1301sub2]", StringComparison.Ordinal), b = text.IndexOf("[1301sub3]", StringComparison.Ordinal);
        text = text[..a] + $"[1301sub2]\nParameterName=Cycle\nObjectType=0x7\nDataType=0x0006\nAccessType=rw\nParameterValue={textValue}\nPDOMapping=0\n\n" + text[b..];
        r.Master.BindPeerDeviceDescription(5, CanOpenDeviceDescription.ParseDcf(text));
        r.Aborts[(0x1301, 2)] = 0x08000022;
        var snapshot = await r.Read();
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 2).Origin.Should().Be(SafetyValueOrigin.Unavailable);
        snapshot.Srdos[0].ComputedSignature.Should().BeNull();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 1u)]
    [InlineData(2, 513u)]
    [InlineData(3, null)]
    [InlineData(4, 67305985u)]
    public void Raw_Value_Interpretation_Is_Width_Checked_And_Little_Endian(int length, uint? expected)
    {
        var entry = new PeerSafetyEntry(0x1301, 2, Enumerable.Range(1, length).Select(n => (byte)n).ToArray(), SafetyValueOrigin.Live);
        entry.UnsignedValue.Should().Be(expected);
    }

    [Fact]
    public void A_Missing_Recheck_Cannot_Become_A_Complete_Image()
    {
        var entries = Image().Select(e => new PeerSafetyEntry(e.Key.Item1, e.Key.Item2, e.Value, SafetyValueOrigin.Live)).ToArray();
        var rechecks = entries.Where(e => e.Index != 0x1301 || e.Subindex != 2).ToArray();
        var snapshot = PeerSafetyDiagnostics.Build(5, DateTimeOffset.UtcNow, TimeSpan.Zero, entries, rechecks,
            new List<PeerSafetyFinding>(), 1, true, false, new Dictionary<(ushort, byte), byte[]>());
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Fact]
    public void Inspection_Extension_Rejects_A_Null_Implementation()
        => Assert.Throws<ArgumentNullException>(() => { _ = CanOpenSafetyExtensions.ReadPeerSafetyConfigurationAsync(null!, 5); });

#if NET5_0_OR_GREATER
    [Fact]
    public void Existing_Safety_Implementations_Need_Not_Add_Inspection()
    {
        var foreign = System.Reflection.DispatchProxy.Create<ICanOpenSafety, ForeignSafety>();
        Assert.Throws<NotSupportedException>(() => { _ = foreign.ReadPeerSafetyConfigurationAsync(5); });
    }
    private class ForeignSafety : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => throw new NotSupportedException();
    }
#endif

    [Fact]
    public async Task A_Checksummed_Mapping_Still_Has_To_Match_The_Bound_Object_Width()
    {
        using var r = new Rig(); await r.Start();
        var map = new SrdoMapping().Add(0x2000, 0, 8);
        r.Values[(0x1381, 1)] = r.Values[(0x1381, 2)] = ObjectDictionary.EncodeU32(0x20000008);
        ushort crc = SrdoCrc.Compute(Parameter, map);
        r.Values[(0x13FF, 1)] = new[] { (byte)crc, (byte)(crc >> 8) };
        var snapshot = await r.Read();
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Match);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.InvalidMapping && f.Message.Contains("not as wide"));
    }

    [Fact]
    public async Task A_Readonly_Producer_Srvt_Outside_The_Existing_CiA319_Rule_Is_Reported_Without_Rewriting_It()
    {
        using var r = new Rig(); await r.Start();
        string text = CanOpenFixtures.Text("safety.dcf").Replace("\r\n", "\n");
        int a = text.IndexOf("[1301sub3]", StringComparison.Ordinal), b = text.IndexOf("[1301sub4]", StringComparison.Ordinal);
        text = text[..a] + text[a..b].Replace("AccessType=rw", "AccessType=ro") + text[b..];
        r.Master.BindPeerDeviceDescription(5, CanOpenDeviceDescription.ParseDcf(text));
        r.Values[(0x1301, 3)] = new byte[] { 30 };
        ushort crc = SrdoCrc.Compute(Parameter with { ValidationTime = TimeSpan.FromMilliseconds(30) }, new SrdoMapping().Add(0x2000, 0, 16));
        r.Values[(0x13FF, 1)] = new[] { (byte)crc, (byte)(crc >> 8) };
        var snapshot = await r.Read();
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 3).UnsignedValue.Should().Be(30);
        snapshot.Srdos[0].SignatureComparison.Should().Be(SafetyComparison.Match);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.InvalidValue && f.Message.Contains("20 ms"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Consumer_Record_Requires_A_Writable_Mapped_Object(bool writable)
    {
        using var r = new Rig(); await r.Start();
        if (writable)
        {
            string text = CanOpenFixtures.Text("safety.dcf").Replace("\r\n", "\n");
            int at = text.IndexOf("[2000]", StringComparison.Ordinal);
            text = text[..at] + text[at..].Replace("AccessType=ro", "AccessType=rw");
            r.Master.BindPeerDeviceDescription(5, CanOpenDeviceDescription.ParseDcf(text));
        }
        r.Values[(0x1301, 1)] = new byte[] { 2 };
        ushort crc = SrdoCrc.Compute(Parameter with { Direction = SrdoDirection.Receive }, new SrdoMapping().Add(0x2000, 0, 16));
        r.Values[(0x13FF, 1)] = new[] { (byte)crc, (byte)(crc >> 8) };
        var snapshot = await r.Read();
        snapshot.Srdos[0].Parameter!.Value.Direction.Should().Be(SrdoDirection.Receive);
        snapshot.Srdos[0].EligibleForMonitoring.Should().Be(writable);
    }

    [Theory]
    [InlineData(0x1301, 2, false)]
    [InlineData(0x1018, 1, true)]
    [InlineData(0x1300, 0, true)]
    public async Task A_Malformed_Recheck_Is_Retained_And_Required_Data_Cannot_Remain_Complete(int index, int sub, bool complete)
    {
        using var r = new Rig(); await r.Start();
        r.BeforeReply = (i, s, read) => { if (i == index && s == sub && read == 2) r.Values[(i, s)] = new byte[] { 30, 0, 0 }; };
        var snapshot = await r.Read();
        snapshot.IsComplete.Should().Be(complete);
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.ChangeObserved);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.InvalidValue && f.Index == index && f.Subindex == sub);
        snapshot.RecheckEntries.Single(e => e.Index == index && e.Subindex == sub).RawValue.Should().Equal(30, 0, 0);
    }

    [Fact]
    public async Task A_Change_In_Srvt_Evidence_Also_Begins_A_New_Evaluation_Period()
    {
        using var r = new Rig(); await r.Start();
        r.Master.BindPeerDeviceDescription(5, WithoutSrvt());
        r.BeforeReply = (index, sub, read) => { if ((index, sub, read) == (0x1301, 3, 2)) r.Aborts[(index, sub)] = 0x06090011; };
        var snapshot = await r.Read();
        snapshot.Entries.Single(e => e.Index == 0x1301 && e.Subindex == 3).Origin.Should().Be(SafetyValueOrigin.Live);
        snapshot.RecheckEntries.Single(e => e.Index == 0x1301 && e.Subindex == 3).Origin.Should().Be(SafetyValueOrigin.CiA319Default);
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.ChangeObserved);
        snapshot.Srdos[0].EligibleForMonitoring.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_Unconfirmed_Peer_Count_Cannot_Establish_A_Matching_Configuration(bool abortCount, bool different)
    {
        using var r = new Rig(); await r.Start();
        r.Values[(0x13FF, 0)] = new byte[] { 3 };
        if (abortCount) r.Aborts[(0x13FF, 0)] = 0x06090011;
        foreach (var entry in Image().Where(e => e.Key.Item1 is 0x1301 or 0x1381))
            r.Values[((ushort)(entry.Key.Item1 + 1), entry.Key.Item2)] = entry.Value;
        r.Values[(0x1302, 1)] = new byte[] { 0 };
        r.Values[(0x1382, 0)] = new byte[] { 0 };
        r.Values[(0x13FF, 2)] = new byte[] { 0, 0 };
        var expected = different ? new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, Parameter with { RefreshOrSafeguardCycleTime = TimeSpan.FromMilliseconds(40) }, new SrdoMapping().Add(0x2000, 0, 16)) : Expected();
        var snapshot = await r.Read(expected);
        snapshot.IsComplete.Should().BeFalse();
        snapshot.ExpectedComparison.Should().Be(different ? SafetyComparison.Mismatch : SafetyComparison.Unavailable);
    }

    [Fact]
    public async Task An_Image_That_Changes_During_The_Read_Cannot_Establish_A_Matching_Expectation()
    {
        using var r = new Rig(); await r.Start();
        r.BeforeReply = (index, sub, read) => { if ((index, sub, read) == (0x13FE, 0, 2)) r.Values[(index, sub)] = new byte[] { 0 }; };
        var snapshot = await r.Read(Expected());
        snapshot.Consistency.Should().Be(PeerSafetySnapshotConsistency.ChangeObserved);
        snapshot.ExpectedComparison.Should().Be(SafetyComparison.Unavailable);
        snapshot.Findings.Should().Contain(f => f.Kind == PeerSafetyFindingKind.ExpectedUnavailable);
    }

}
