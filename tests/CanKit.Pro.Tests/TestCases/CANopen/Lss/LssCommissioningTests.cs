using System;
using System.Collections.Generic;
using System.Linq;
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
        FastscanTimeout = TimeSpan.FromMilliseconds(40),
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
        first.LssState().Should().Be(LssState.Waiting);

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
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            Store = new MemoryStore(),
            BitRateSwitch = (bitsPerSecond, _) =>
            {
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
        slave.LssState().Should().Be(LssState.Waiting);
        await WaitUntilAsync(() => rate == 250_000);

        var inquired = await tool.InquireSlaveAsync(First);
        inquired.Succeeded.Should().BeTrue();
        inquired.Address.Should().Be(First);
        inquired.NodeId.Should().Be(5);
        slave.LssState().Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task ReadIdentities_Skips_A_Node_That_Does_Not_Answer()
    {
        using var bench = new Bench();
        var present = bench.Slave(1, 5, new LssSlaveOptions { Enabled = false });
        SetIdentity(present, First);
        var client = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        var tool = new LssCommissioning(bench.Master);

        var read = await tool.ReadIdentitiesAsync(client, new byte[] { 5, 9, 2 });
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
