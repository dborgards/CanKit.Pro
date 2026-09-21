using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// The device-description loader (FR-CO-025 … FR-CO-028): an EDS or DCF shapes the node's object
/// dictionary and, through it, its runtime; what the node cannot take as written is corrected in
/// the dictionary and reported. The descriptions are files under <c>Fixtures/</c>, read from disk
/// as an application would read them.
/// </summary>
public class CanOpenDeviceDescriptionTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private const byte Device = 0x11;
    private const byte Master = 0x01;

    private static string NewSession() => VirtualAdapterFixture.NewSession("canopen-eds");

    private static ICanBus Open(string session, int channel) => VirtualAdapterFixture.Open(session, channel);

    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, Path.Combine("TestCases", "CANopen", "Fixtures"));

    private static string Fixture(string name) => Path.Combine(FixturesDirectory, name);

    private static CanOpenDeviceDescription DeviceEds() => CanOpenDeviceDescription.Load(Fixture("device.eds"));

    private static Task<byte[]> UploadAsync(ICanOpenNode client, ushort index, byte subindex)
        => client.SdoUploadAsync(Device, index, subindex).WithTimeoutAsync(ShortTimeout);

    private static async Task<uint> UploadUnsignedAsync(ICanOpenNode client, ushort index, byte subindex)
    {
        var raw = await UploadAsync(client, index, subindex);
        uint value = 0;
        for (int i = raw.Length - 1; i >= 0; i--) value = (value << 8) | raw[i];
        return value;
    }

    private static Task DownloadAsync(ICanOpenNode client, ushort index, byte subindex, byte[] data)
        => client.SdoDownloadAsync(Device, index, subindex, data).WithTimeoutAsync(ShortTimeout);

    private static byte[] U32(uint value) => new[]
    {
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
    };

    private static async Task WaitForStateAsync(ICanOpenNode node, NmtState state)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.State != state)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Node 0x{node.NodeId:X2} did not reach {state}; it is in {node.State}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Records the data frames a bus observes, per COB-ID.</summary>
    private sealed class FrameTap
    {
        private readonly object _gate = new();
        private readonly Dictionary<uint, List<byte[]>> _frames = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public FrameTap(ICanBus bus)
        {
            bus.FrameObserved += (_, e) =>
            {
                var frame = e.CanFrame;
                if (frame.IsExtendedFrame || frame.IsRemoteFrame) return;
                var data = frame.Data.ToArray();
                lock (_gate)
                {
                    if (!_frames.TryGetValue((uint)frame.ID, out var list)) _frames[(uint)frame.ID] = list = new List<byte[]>();
                    list.Add(data);
                }
                _arrived.Release();
            };
        }

        public int Count(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.Count : 0;
        }

        public byte[][] Payloads(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.ToArray() : Array.Empty<byte[]>();
        }

        public async Task WaitForCountAsync(uint cobId, int count)
        {
            using var deadline = new CancellationTokenSource(ShortTimeout);
            while (Count(cobId) < count)
            {
                if (!await _arrived.WaitAsync(ShortTimeout, deadline.Token).ConfigureAwait(false))
                    throw new TimeoutException($"Only {Count(cobId)} of {count} frames arrived on 0x{cobId:X3} within {ShortTimeout}.");
            }
        }
    }

    private sealed class BootupWatch
    {
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _third = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public BootupWatch(ICanOpenNode observer, byte producer)
        {
            observer.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId != producer || e.State != NmtState.Initializing) return;
                switch (Interlocked.Increment(ref _seen))
                {
                    case 1: _first.TrySetResult(true); break;
                    case 2: _second.TrySetResult(true); break;
                    case 3: _third.TrySetResult(true); break;
                }
            };
        }

        public Task First => _first.Task.WithTimeoutAsync(ShortTimeout);
        public Task Second => _second.Task.WithTimeoutAsync(ShortTimeout);
        public Task Third => _third.Task.WithTimeoutAsync(ShortTimeout);
    }

    // =========================================================================================
    // FR-CO-025 — the description shapes the dictionary and, through it, the runtime.
    // =========================================================================================

    [Fact]
    public async Task A_Description_Shapes_The_Dictionary_And_The_Runtime()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var busC = Open(session, 2);
        var tap = new FrameTap(busC);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, DeviceEds());
        var od = device.ObjectDictionary;

        var report = device.DeviceDescription!;
        report.Should().NotBeNull();
        report.IsExact.Should().BeTrue("every entry of device.eds is one the node implements as written:\n"
            + string.Join("\n", report.Findings.Select(f => f.ToString())));
        report.NodeId.Should().Be(Device);
        report.EntriesLoaded.Should().BeGreaterThan(20);

        // Mandatory and identity objects carry the file's values, not the placeholders.
        od.ReadUnsigned(0x1000, 0x00).Should().Be(0x00030191u);
        od.ReadUnsigned(0x1018, 0x00).Should().Be(4u);
        od.ReadUnsigned(0x1018, 0x01).Should().Be(0x100u);
        od.ReadUnsigned(0x1018, 0x04).Should().Be(0x2Au);

        // Application objects: type, access, mappability and value from the file.
        od.TryGet(0x2000, 0x00, out var processValue).Should().BeTrue();
        processValue.DataType.Should().Be(OdDataType.Unsigned16);
        processValue.Access.Should().Be(OdAccess.ReadWrite);
        processValue.PdoMappable.Should().BeTrue("PDOMapping=1");
        od.ReadUnsigned(0x2000, 0x00).Should().Be(0x1234u);
        od.TryGet(0x2002, 0x00, out var label).Should().BeTrue();
        label.DataType.Should().Be(OdDataType.VisibleString);
        label.Access.Should().Be(OdAccess.ReadOnly);
        label.PdoMappable.Should().BeFalse("PDOMapping=0");
        Encoding.ASCII.GetString(od.ReadRaw(0x2002, 0x00)).Should().Be("abc");
        Encoding.ASCII.GetString(od.ReadRaw(0x1008, 0x00)).Should().Be("Test device");

        // The managed communication objects took the file's values through the validated path.
        od.ReadUnsigned(0x1005, 0x00).Should().Be(0x4000_0080u);
        od.ReadUnsigned(0x1006, 0x00).Should().Be(100_000u);
        od.ReadUnsigned(0x1014, 0x00).Should().Be(0x080u + Device, "$NODEID+0x80");
        od.ReadUnsigned(0x1016, 0x00).Should().Be(2u);
        od.ReadUnsigned(0x1016, 0x01).Should().Be(0x0012_0064u);
        od.ReadUnsigned(0x1017, 0x00).Should().Be(200u);
        od.ReadUnsigned(0x1800, 0x00).Should().Be(5u);
        od.ReadUnsigned(0x1800, 0x01).Should().Be(0x180u + Device, "$NODEID+0x180, created (bit 31 clear)");
        od.ReadUnsigned(0x1800, 0x02).Should().Be(254u);
        od.ReadUnsigned(0x1800, 0x03).Should().Be(10u);
        od.ReadUnsigned(0x1800, 0x05).Should().Be(100u);
        od.ReadUnsigned(0x1A00, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1A00, 0x01).Should().Be(0x2000_0010u);
        od.ReadUnsigned(0x1400, 0x00).Should().Be(2u);
        od.ReadUnsigned(0x1400, 0x01).Should().Be(0x200u + Device);
        od.ReadUnsigned(0x1600, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1600, 0x01).Should().Be(0x2001_0008u);
        // A PDO the file does not declare does not exist (§7.3.3).
        od.ContainsIndex(0x1801).Should().BeFalse();
        od.ContainsIndex(0x1A01).Should().BeFalse();
        od.ContainsIndex(0x1401).Should().BeFalse();
        (await Assert.ThrowsAsync<SdoAbortException>(() => UploadAsync(master, 0x1801, 0x01)))
            .AbortCode.Should().Be((uint)SdoAbortCode.ObjectDoesNotExist);

        // The runtime follows: the heartbeat producer (1017h) and the SYNC producer
        // (1005h bit 30 + 1006h) are running; the TPDO and the RPDO the file created work.
        await tap.WaitForCountAsync(CanOpenCobId.Heartbeat(Device), 3);
        await tap.WaitForCountAsync(CanOpenCobId.Sync, 2);

        var actuated = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.RpdoReceived += (_, e) =>
        {
            if (e.CobId == 0x200u + Device) actuated.TrySetResult(e.Payload);
        };
        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        await WaitForStateAsync(device, NmtState.Operational);

        await device.TriggerTpdoAsync(1);
        await tap.WaitForCountAsync(0x180u + Device, 1);
        tap.Payloads(0x180u + Device)[0].Should().Equal(0x34, 0x12);

        busC.Transmit(CanFrame.Classic((int)(0x200u + Device), new byte[] { 0x09 }, isExtendedFrame: false));
        await actuated.Task.WithTimeoutAsync(ShortTimeout);
        od.ReadUnsigned(0x2001, 0x00).Should().Be(9u);
    }

    // =========================================================================================
    // FR-CO-026 — what the node cannot take as written is corrected in the dictionary and reported.
    // =========================================================================================

    [Fact]
    public void Degradations_Are_Corrected_In_The_Dictionary_And_Reported()
    {
        var session = NewSession();
        using var busB = Open(session, 1);
        using var device = CanOpen.OpenNode(busB, Device, CanOpenDeviceDescription.Load(Fixture("quirky.eds")));
        var od = device.ObjectDictionary;
        var report = device.DeviceDescription!;
        report.IsExact.Should().BeFalse();

        DeviceDescriptionFinding Finding(ushort index, byte subindex)
            => report.Findings.SingleOrDefault(f => f.Index == index && f.Subindex == subindex)
               ?? throw new Xunit.Sdk.XunitException($"no finding for 0x{index:X4}:{subindex:X2}; findings:\n"
                   + string.Join("\n", report.Findings.Select(f => f.ToString())));

        // The missing identity object keeps its placeholder.
        Finding(0x1018, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.SuppliedDefault);
        od.ReadUnsigned(0x1018, 0x00).Should().Be(1u);
        od.ReadUnsigned(0x1018, 0x01).Should().Be(0u);

        // A reserved transmission type: the rule an SDO download hits, the default kept.
        var reserved = Finding(0x1800, 0x02);
        reserved.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        reserved.AbortCode.Should().Be(SdoAbortCode.ValueRangeExceeded);
        reserved.DescribedValue.Should().Be("0xF5");
        od.ReadUnsigned(0x1800, 0x02).Should().Be(254u);
        od.ReadUnsigned(0x1800, 0x01).Should().Be(0x180u + Device, "TPDO1 itself is created; only its transmission type was corrected");

        // A 29-bit COB-ID: the PDO stays destroyed.
        var extended = Finding(0x1801, 0x01);
        extended.Outcome.Should().Be(DeviceDescriptionOutcome.PdoDisabled);
        extended.AbortCode.Should().Be(SdoAbortCode.ValueRangeExceeded);
        (od.ReadUnsigned(0x1801, 0x01) & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit);

        // A mapping onto an object that does not exist: the mapping stays disabled, the PDO destroyed.
        Finding(0x1A02, 0x01).AbortCode.Should().Be(SdoAbortCode.ObjectDoesNotExist);
        Finding(0x1802, 0x01).Outcome.Should().Be(DeviceDescriptionOutcome.PdoDisabled);
        od.ReadUnsigned(0x1A02, 0x00).Should().Be(0u);
        (od.ReadUnsigned(0x1802, 0x01) & CanOpenCobId.InvalidBit).Should().Be(CanOpenCobId.InvalidBit);

        // A fifth TPDO: not created.
        Finding(0x1804, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        Finding(0x1A04, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        od.ContainsIndex(0x1804).Should().BeFalse();

        // TIME: created as data, reported as behaviourless.
        Finding(0x1012, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.NotImplemented);
        od.ReadUnsigned(0x1012, 0x00).Should().Be(0x100u);

        // A 24-bit integer: not a type the dictionary represents.
        Finding(0x2003, 0x00).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        od.ContainsIndex(0x2003).Should().BeFalse();

        // A second consumer entry for the same producer: 0604 0043h, the slot stays unused.
        var duplicate = Finding(0x1016, 0x02);
        duplicate.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        duplicate.AbortCode.Should().Be(SdoAbortCode.GeneralParameterIncompatibility);
        od.ReadUnsigned(0x1016, 0x01).Should().Be(0x0012_0064u);
        od.ReadUnsigned(0x1016, 0x02).Should().Be(0u);

        // A sub-index above 7Fh: 1016h holds at most 127 entries, so it is not created and the
        // count stays what sub-index 00h announces (a larger count would be refused, and the
        // node's loops over the array count to it).
        Finding(0x1016, 0x80).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        od.TryGet(0x1016, 0x80, out _).Should().BeFalse();
        od.ReadUnsigned(0x1016, 0x00).Should().Be(2u);

        // 1200h is const at the default server's COB-IDs; a different described value is reported
        // and not exposed, the matching one is silently the same.
        Finding(0x1200, 0x01).Outcome.Should().Be(DeviceDescriptionOutcome.NotImplemented);
        od.ReadUnsigned(0x1200, 0x01).Should().Be(0x600u + Device);
        report.Findings.Should().NotContain(f => f.Index == 0x1200 && f.Subindex == 0x02);

        // 1010h beyond sub-index 01h: not implemented, not created.
        Finding(0x1010, 0x02).Outcome.Should().Be(DeviceDescriptionOutcome.Omitted);
        od.TryGet(0x1010, 0x02, out _).Should().BeFalse();
    }

    // =========================================================================================
    // FR-CO-028 — with a description the application objects have power-on values: Reset Node
    // restores them, Reset Communication restores the communication profile area only.
    // =========================================================================================

    [Fact]
    public async Task Reset_Node_Restores_Application_Objects_And_Reset_Communication_Does_Not()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        var bootups = new BootupWatch(master, Device);
        using var device = CanOpen.OpenNode(busB, Device, DeviceEds());
        var od = device.ObjectDictionary;
        await bootups.First;

        od.WriteUnsigned(0x2000, 0x00, 1);
        od.WriteUnsigned(0x1017, 0x00, 300);

        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Device);
        await bootups.Second;
        (await UploadUnsignedAsync(master, 0x1017, 0x00)).Should().Be(200u, "1017h is in the communication profile area");
        (await UploadUnsignedAsync(master, 0x2000, 0x00)).Should().Be(1u, "Reset Communication leaves the application objects alone");

        await master.SendNmtCommandAsync(NmtCommand.ResetNode, Device);
        await bootups.Third;
        (await UploadUnsignedAsync(master, 0x2000, 0x00)).Should().Be(0x1234u, "Reset Node restores the described value");
    }

    // FR-CO-028: the described values are the defaults a "load" returns to — not the node's
    // built-in ones.
    [Fact]
    public async Task The_Described_Values_Are_The_Defaults_A_Load_Restores()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        var bootups = new BootupWatch(master, Device);
        using var device = CanOpen.OpenNode(busB, Device, DeviceEds());
        await bootups.First;

        device.ObjectDictionary.WriteUnsigned(0x1017, 0x00, 300);
        device.StoreParameters();
        device.RestoreDefaultParameters();
        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, Device);
        await bootups.Second;

        (await UploadUnsignedAsync(master, 0x1017, 0x00)).Should().Be(200u, "the file's value, not the built-in 0");
    }

    // =========================================================================================
    // FR-CO-027 — the description's access rights govern the bus: records it declares rw are
    // writable by a master without the WritableCommunicationParameters option.
    // =========================================================================================

    [Fact]
    public async Task A_Master_May_Write_The_Pdo_Records_The_Description_Declares_Writable()
    {
        var session = NewSession();
        using var busA = Open(session, 0);
        using var busB = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = CanOpen.OpenNode(busB, Device, DeviceEds());
        device.Options.WritableCommunicationParameters.Should().BeFalse("the file decides, not the option");

        await DownloadAsync(master, 0x1800, 0x01, U32(CanOpenCobId.InvalidBit | (0x180u + Device)));
        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x00 });
        await DownloadAsync(master, 0x1A00, 0x01, U32(0x2001_0008u));
        await DownloadAsync(master, 0x1A00, 0x00, new byte[] { 0x01 });
        await DownloadAsync(master, 0x1800, 0x01, U32(0x180u + Device));
        (await UploadUnsignedAsync(master, 0x1A00, 0x01)).Should().Be(0x2001_0008u);

        // What the file declares read-only stays read-only: 2002h.
        (await Assert.ThrowsAsync<SdoAbortException>(() => DownloadAsync(master, 0x2002, 0x00, new byte[] { 0x41 })))
            .AbortCode.Should().Be((uint)SdoAbortCode.AttemptWriteReadOnly);
    }

    // =========================================================================================
    // FR-CO-025 — a DCF is a commissioned device: its NodeID is the node's, its ParameterValue
    // overrides the DefaultValue of the EDS it was made from.
    // =========================================================================================

    [Fact]
    public void A_Dcf_Supplies_The_NodeId_And_Its_Parameter_Values()
    {
        var dcf = CanOpenDeviceDescription.Load(Fixture("device.dcf"));
        dcf.IsConfigurationFile.Should().BeTrue();
        dcf.NodeId.Should().Be(5);

        var session = NewSession();
        using var busB = Open(session, 1);
        using var device = CanOpen.OpenNode(busB, dcf);
        device.NodeId.Should().Be(5);
        device.DeviceDescription!.NodeId.Should().Be(5);
        device.ObjectDictionary.ReadUnsigned(0x2000, 0x00).Should().Be(0x0777u, "ParameterValue takes precedence over DefaultValue");
        device.ObjectDictionary.ReadUnsigned(0x1800, 0x01).Should().Be(0x185u, "$NODEID resolved to the commissioned node-id");
        device.ObjectDictionary.ReadUnsigned(0x1014, 0x00).Should().Be(0x085u);

        Action edsWithoutNodeId = () => CanOpen.OpenNode(busB, DeviceEds());
        edsWithoutNodeId.Should().Throw<ArgumentException>("an EDS describes a device type and carries no node-id");
    }

    [Fact]
    public void Load_And_Parse_Read_A_Description_And_Record_The_Parsers_Diagnostics()
    {
        var eds = CanOpenDeviceDescription.Load(Fixture("device.eds"));
        eds.Eds.Should().NotBeNull();
        eds.Dcf.Should().BeNull();
        eds.IsConfigurationFile.Should().BeFalse();
        eds.NodeId.Should().BeNull();
        eds.DeviceInfo.VendorNumber.Should().Be(0x100u);
        eds.ParseDiagnostics.Should().BeEmpty("device.eds is a clean file");

        var parsed = CanOpenDeviceDescription.ParseEds(File.ReadAllText(Fixture("device.eds")));
        parsed.Objects.Objects.Keys.Should().BeEquivalentTo(eds.Objects.Objects.Keys);

        var dcf = CanOpenDeviceDescription.ParseDcf(File.ReadAllText(Fixture("device.dcf")));
        dcf.Dcf.Should().NotBeNull();
        dcf.NodeId.Should().Be(5);
    }
}
