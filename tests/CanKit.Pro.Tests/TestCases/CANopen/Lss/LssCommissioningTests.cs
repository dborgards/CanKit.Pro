using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// FR-CO-049: commissioning on the LSS master. Fastscan lists unconfigured slaves, a batch
/// assigns node-ids, and one slave can be configured, stored and inquired by its LSS address.
/// Configured nodes are read through SDO <c>1018h</c>.
/// </summary>
public class LssCommissioningTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly LssAddress First = new(1, 1, 1, 1);
    private static readonly LssAddress Second = new(1, 1, 1, 2);
    private static readonly LssAddress Third = new(1, 1, 1, 3);

    private static LssMasterOptions Quick() => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(300),
        FastscanTimeout = TimeSpan.FromMilliseconds(200),
        FastscanCycleTime = TimeSpan.FromMilliseconds(1),
        IdentificationTimeout = TimeSpan.FromMilliseconds(80),
    };

    private static LssFastscanRequest KnownVendor() => new()
    {
        VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
        ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
        RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
    };

    [Fact]
    public async Task Scan_Lists_Unconfigured_Slaves_Then_Releases_Them()
    {
        using var bench = new Bench();
        var slaves = bench.Unconfigured(First, Second, Third);
        var tool = new LssCommissioning(bench.Master);

        var listed = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            Limit = 2,
        });
        listed.Stop.Should().Be(LssScanStop.LimitReached);
        listed.Slaves.Should().HaveCount(2);
        listed.Slaves[0].Address.Should().Be(First);
        listed.Slaves[1].Address.Should().Be(Second);
        listed.Slaves[0].Succeeded.Should().BeTrue();
        foreach (var slave in slaves)
        {
            slave.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
            slave.LssState().Should().Be(LssState.Waiting);
        }

        var again = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { Fastscan = KnownVendor() });
        again.Stop.Should().Be(LssScanStop.NoUnconfiguredSlave);
        again.Slaves.Select(slave => slave.Address.SerialNumber).Should().Equal(1u, 2u, 3u);
    }

    [Fact]
    public async Task Assign_Gives_The_Next_Node_Id_And_Stops_When_The_Slave_Rejects_The_Store()
    {
        using var bench = new Bench();
        var store = new MemoryStore();
        var first = bench.Unconfigured(null, First)[0];
        bench.Unconfigured(store, Second);
        var tool = new LssCommissioning(bench.Master);

        var assigned = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            AssignFromNodeId = 0x21,
            Store = true,
        });

        assigned.Stop.Should().Be(LssScanStop.SlaveRejected);
        assigned.Slaves.Should().ContainSingle();
        assigned.Slaves[0].Address.Should().Be(First);
        assigned.Slaves[0].NodeId.Should().Be(0x21);
        assigned.Slaves[0].NodeIdResult!.Value.Succeeded.Should().BeTrue();
        assigned.Slaves[0].StoreResult!.Value.ErrorCode.Should().Be(LssCommand.StoreNotSupported);
        first.NodeId.Should().Be(0x21);
        await WaitUntilAsync(() => first.LssState() == LssState.Waiting);

        var withStore = bench.Unconfigured(store, Third)[0];
        var stored = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            AssignFromNodeId = 0x22,
            Store = true,
        });
        stored.Stop.Should().Be(LssScanStop.NoUnconfiguredSlave);
        stored.Slaves.Select(slave => slave.Address).Should().Equal(Second, Third);
        stored.Slaves[0].NodeId.Should().Be(0x22);
        stored.Slaves[1].NodeId.Should().Be(0x23);
        stored.Slaves.Should().OnlyContain(slave => slave.Succeeded);
        store.NodeId.Should().Be(0x23);
        withStore.NodeId.Should().Be(0x23);
    }

    [Fact]
    public async Task Configure_And_Inquire_One_Slave_By_Its_Address()
    {
        int rate = 0;
        var store = new MemoryStore();
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            Store = store,
            BitRateSwitch = (bitsPerSecond, _) =>
            {
                store.Stored.Should().BeTrue();
                rate = bitsPerSecond;
                return Task.CompletedTask;
            },
        });
        SetIdentity(slave, First);
        var tool = new LssCommissioning(bench.Master);

        var missed = await tool.ConfigureSlaveAsync(Second, new LssSlaveConfiguration { NodeId = 0x30 });
        missed.Selected.Should().BeFalse();
        slave.LssState().Should().Be(LssState.Waiting);

        var configured = await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            NodeId = 0x31,
            BitTimingSelector = LssBitTiming.Cia301Table,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
            ActivateBitRate = 250_000,
            Store = true,
        });
        configured.Succeeded.Should().BeTrue();
        configured.NodeId.Should().Be(0x31);
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting && rate == 250_000);

        var inquired = await tool.InquireSlaveAsync(First);
        inquired.Succeeded.Should().BeTrue();
        inquired.Address.Should().Be(First);
        inquired.NodeId.Should().Be(5);
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);
    }

    [Fact]
    public async Task ReadIdentities_Skips_A_Node_That_Does_Not_Answer()
    {
        using var bench = new Bench();
        var present = bench.Slave(1, 5, new LssSlaveOptions { Enabled = false });
        SetIdentity(present, First);
        var client = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        var tool = new LssCommissioning(bench.Master);

        var read = await tool.ReadIdentitiesAsync(client, new byte[] { 5, 9, 2, CanOpenCobId.UnconfiguredNodeId });
        read.Should().ContainSingle();
        read[0].Address.Should().Be(First);
        read[0].NodeId.Should().Be(5);
    }

    [Fact]
    public async Task An_Empty_Scan_Leaves_A_Configured_Slave_In_Configuration()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions());
        SetIdentity(slave, First);
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => slave.LssState() == LssState.Configuration);

        var scan = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync();
        scan.Stop.Should().Be(LssScanStop.NoUnconfiguredSlave);
        scan.Slaves.Should().BeEmpty();
        slave.LssState().Should().Be(LssState.Configuration);

        // Opened after the global switch, so it is the only slave still in waiting.
        var unconfigured = bench.Unconfigured(First)[0];
        var missed = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = new LssFastscanRequest
            {
                VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 99 },
            },
        });
        missed.Stop.Should().Be(LssScanStop.NoMatch);
        missed.Slaves.Should().BeEmpty();
        slave.LssState().Should().Be(LssState.Configuration);
        unconfigured.LssState().Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task A_Cancelled_Listing_Scan_Releases_The_Slaves_It_Parked()
    {
        using var bench = new Bench();
        var first = bench.Unconfigured(First, Second)[0];
        var tool = new LssCommissioning(bench.Master);
        using var cancel = new CancellationTokenSource();
        var scan = tool.ScanUnconfiguredAsync(cancellationToken: cancel.Token);
        await WaitUntilAsync(() => first.LssState() == LssState.Configuration);
        cancel.Cancel();
        var act = async () => await scan;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilAsync(() => first.LssState() == LssState.Waiting);
    }

    [Fact]
    public async Task The_Scan_Refuses_A_Change_That_Would_Hit_Every_Slave()
    {
        using var bench = new Bench();
        var tool = new LssCommissioning(bench.Master);
        var act = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { Store = true });
        await act.Should().ThrowAsync<ArgumentException>();

        var timing = async () => await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            BitTimingSelector = 0,
        });
        await timing.Should().ThrowAsync<ArgumentException>();

        var missingMaster = () => new LssCommissioning(null!);
        missingMaster.Should().Throw<ArgumentNullException>().WithParameterName("master");
        var limit = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { Limit = 0 });
        await limit.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var firstId = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { AssignFromNodeId = 0 });
        await firstId.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var nodeId = async () => await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration { NodeId = 0 });
        await nodeId.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var activate = async () => await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            ActivateDelayMilliseconds = 10,
        });
        await activate.Should().ThrowAsync<ArgumentException>();
        var rate = async () => await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration { ActivateBitRate = 250_000 });
        await rate.Should().ThrowAsync<ArgumentException>();
        var configuration = async () => await tool.ConfigureSlaveAsync(First, null!);
        await configuration.Should().ThrowAsync<ArgumentNullException>();
        var client = bench.Slave(1, 2, new LssSlaveOptions { Enabled = false });
        var noClient = async () => await tool.ReadIdentitiesAsync(null!, new byte[] { 1 });
        await noClient.Should().ThrowAsync<ArgumentNullException>();
        var noIds = async () => await tool.ReadIdentitiesAsync(client, null!);
        await noIds.Should().ThrowAsync<ArgumentNullException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = async () => await tool.ReadIdentitiesAsync(client, new byte[] { 4, 0xFF }, cancelled.Token);
        await stopped.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Assign_Stops_When_The_Next_Node_Id_Would_Not_Fit()
    {
        using var bench = new Bench();
        bench.Unconfigured(First, Second);
        var scan = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            AssignFromNodeId = 127,
        });
        scan.Stop.Should().Be(LssScanStop.NodeIdsExhausted);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].NodeId.Should().Be(127);
        scan.Slaves[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Configure_Can_Leave_The_Slave_In_Configuration_And_Report_A_Rejected_Table()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions());
        SetIdentity(slave, First);
        var tool = new LssCommissioning(bench.Master);

        var rejected = await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            BitTimingSelector = 0,
            BitTimingIndex = LssBitTiming.AutomaticIndex,
            Store = true,
        });
        rejected.Succeeded.Should().BeFalse();
        rejected.BitTimingResult!.Value.ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        rejected.StoreResult.Should().BeNull();
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);

        var held = await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            NodeId = 0x31,
            ReleaseToWaiting = false,
        });
        held.Succeeded.Should().BeTrue();
        slave.LssState().Should().Be(LssState.Configuration);

        int switches = 0;
        var rejecting = bench.Slave(2, 6, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                switches++;
                return Task.CompletedTask;
            },
        });
        SetIdentity(rejecting, Second);
        var notStored = await tool.ConfigureSlaveAsync(Second, new LssSlaveConfiguration
        {
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
            ActivateBitRate = 250_000,
            Store = true,
        });
        notStored.Succeeded.Should().BeFalse();
        notStored.StoreResult!.Value.ErrorCode.Should().Be(LssCommand.StoreNotSupported);
        await Task.Delay(40);
        switches.Should().Be(0);
    }

    [Fact]
    public async Task A_Scan_Of_More_Than_One_Slave_Refuses_To_Activate_Bit_Timing()
    {
        using var bench = new Bench();
        var tool = new LssCommissioning(bench.Master);
        var open = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            AssignFromNodeId = 0x10,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
        });
        await open.Should().ThrowAsync<ArgumentException>();
        var two = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            AssignFromNodeId = 0x10,
            Limit = 2,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
        });
        await two.Should().ThrowAsync<ArgumentException>();

        int switches = 0;
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        SetIdentity(slave, First);
        var one = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            AssignFromNodeId = 0x10,
            Limit = 1,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
            ActivateBitRate = 250_000,
        });
        one.Stop.Should().Be(LssScanStop.LimitReached);
        one.Slaves.Should().ContainSingle();
        one.Slaves[0].Succeeded.Should().BeTrue();
        one.Slaves[0].NodeId.Should().Be(0x10);
        await WaitUntilAsync(() => switches == 1 && slave.LssState() == LssState.Waiting);
        slave.NodeId.Should().Be(0x10);
    }

    [Fact]
    public async Task Inquire_Can_Miss_And_A_Partial_Identity_Counts_The_Rest_As_Zero()
    {
        using var bench = new Bench();
        var tool = new LssCommissioning(bench.Master);
        tool.Master.Should().BeSameAs(bench.Master);
        var missing = () => new LssUnconfiguredScanResult(null!, LssScanStop.NoUnconfiguredSlave);
        missing.Should().Throw<ArgumentNullException>().WithParameterName("slaves");

        var slave = bench.Slave(1, 5, new LssSlaveOptions());
        SetIdentity(slave, First);
        var missed = await tool.InquireSlaveAsync(Second);
        missed.Selected.Should().BeFalse();
        missed.Succeeded.Should().BeFalse();
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);

        var partial = bench.Slave(2, 6, new LssSlaveOptions { Enabled = false });
        partial.ObjectDictionary.WriteUnsigned(0x1018, 0x00, 1);
        partial.ObjectDictionary.WriteUnsigned(0x1018, 0x01, 9);
        var client = bench.Slave(3, 2, new LssSlaveOptions { Enabled = false });
        var read = await tool.ReadIdentitiesAsync(client, new byte[] { 6 });
        read.Should().ContainSingle();
        read[0].Address.Should().Be(new LssAddress(9, 0, 0, 0));
        read[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_Disposed_Master_Lets_The_Scan_End()
    {
        using var bench = new Bench();
        var first = bench.Unconfigured(First)[0];
        var tool = new LssCommissioning(bench.Master);
        using var cancel = new CancellationTokenSource();
        var scan = tool.ScanUnconfiguredAsync(cancellationToken: cancel.Token);
        await WaitUntilAsync(() => first.LssState() == LssState.Configuration);
        bench.Master.Dispose();
        cancel.Cancel();
        var act = async () => await scan;
        var thrown = await act.Should().ThrowAsync<Exception>();
        (thrown.Which is ObjectDisposedException or OperationCanceledException).Should().BeTrue();
    }

    private static void SetIdentity(ICanOpenNode node, LssAddress address)
    {
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1018, 0x00, 4);
        od.WriteUnsigned(0x1018, 0x01, address.VendorId);
        Write(od, 0x02, address.ProductCode);
        Write(od, 0x03, address.RevisionNumber);
        Write(od, 0x04, address.SerialNumber);
    }

    private static void Write(ObjectDictionary od, byte subindex, uint value)
    {
        if (!od.TryGet(0x1018, subindex, out _)) od.AddU32(0x1018, subindex, value);
        else od.WriteUnsigned(0x1018, subindex, value);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The LSS slave did not reach the expected state.");
            await Task.Delay(1);
        }
    }

    private sealed class MemoryStore : ILssConfigurationStore
    {
        public byte NodeId { get; private set; }

        public bool Stored { get; private set; }

        public bool TryLoad(out byte nodeId, out byte tableSelector, out byte tableIndex)
        {
            nodeId = 0;
            tableSelector = 0;
            tableIndex = 0;
            return false;
        }

        public bool TryStore(byte nodeId, byte tableSelector, byte tableIndex, out byte specError)
        {
            NodeId = nodeId;
            Stored = true;
            specError = 0;
            return true;
        }
    }

    private sealed class Bench : IDisposable
    {
        private readonly List<IDisposable> _disposables = new();
        private int _nextChannel = 1;

        public Bench()
        {
            Session = VirtualAdapterFixture.NewSession("lss-tool");
            var bus = Track(VirtualAdapterFixture.Open(Session, 0));
            Master = Track(CanOpenLss.OpenMaster(bus, Quick()));
        }

        public string Session { get; }

        public ILssMaster Master { get; }

        public ICanOpenNode[] Unconfigured(params LssAddress[] addresses)
            => Unconfigured(store: null, addresses);

        public ICanOpenNode[] Unconfigured(ILssConfigurationStore? store, params LssAddress[] addresses)
        {
            var nodes = new ICanOpenNode[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
            {
                nodes[i] = Slave(_nextChannel++, CanOpenCobId.UnconfiguredNodeId, new LssSlaveOptions { Store = store });
                SetIdentity(nodes[i], addresses[i]);
            }

            return nodes;
        }

        public ICanOpenNode Slave(int channel, byte nodeId, LssSlaveOptions lss)
        {
            var bus = Track(VirtualAdapterFixture.Open(Session, channel));
            return Track(CanOpen.OpenNode(bus, nodeId, new CanOpenNodeOptions
            {
                Lss = lss,
                SdoTimeout = TimeSpan.FromMilliseconds(200),
            }));
        }

        public void Dispose()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
        }

        private T Track<T>(T item) where T : IDisposable
        {
            _disposables.Add(item);
            return item;
        }
    }
}
