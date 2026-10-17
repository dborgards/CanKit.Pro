using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;
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
        FastscanTimeout = TimeSpan.FromMilliseconds(500),
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
        await WaitUntilAsync(() => slaves.All(slave => slave.LssState() == LssState.Waiting));
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
        // Vendor, product and revision are known, so only the serial is scanned. Every
        // Fastscan step occupies FastscanTimeout, so the answering bits take the whole window.
        var scan = tool.ScanUnconfiguredAsync(
            new LssUnconfiguredScan { Fastscan = KnownVendor() }, cancel.Token);
        await WaitUntilAsync(() => first.LssState() == LssState.Configuration, seconds: 30);
        cancel.Cancel();
        var act = async () => await scan;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilAsync(() => first.LssState() == LssState.Waiting, seconds: 10);
    }

    [Fact]
    public async Task Configure_Does_Not_Reach_A_Slave_Already_In_Configuration()
    {
        using var bench = new Bench();
        var parked = bench.Unconfigured(First)[0];
        var target = bench.Unconfigured(Second)[0];
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => parked.LssState() == LssState.Configuration
            && target.LssState() == LssState.Configuration);

        var result = await new LssCommissioning(bench.Master).ConfigureSlaveAsync(
            Second, new LssSlaveConfiguration { NodeId = 0x30 });
        result.Succeeded.Should().BeTrue();
        target.NodeId.Should().Be(0x30);
        parked.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        await WaitUntilAsync(() => parked.LssState() == LssState.Waiting && target.LssState() == LssState.Waiting);
    }

    [Fact]
    public async Task Inquire_Reads_The_Addressed_Slave_When_Another_Is_In_Configuration()
    {
        using var bench = new Bench();
        var first = bench.Slave(1, 5, new LssSlaveOptions());
        var second = bench.Slave(2, 6, new LssSlaveOptions());
        SetIdentity(first, First);
        SetIdentity(second, Second);
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => first.LssState() == LssState.Configuration);

        var result = await new LssCommissioning(bench.Master).InquireSlaveAsync(Second);
        result.Succeeded.Should().BeTrue();
        result.Address.Should().Be(Second);
        result.NodeId.Should().Be(6);
    }

    [Fact]
    public async Task An_Assigning_Scan_Does_Not_Rewrite_A_Slave_Already_In_Configuration()
    {
        using var bench = new Bench();
        var parked = bench.Slave(1, 5, new LssSlaveOptions());
        SetIdentity(parked, new LssAddress(8, 8, 8, 8));
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => parked.LssState() == LssState.Configuration);
        var free = bench.Unconfigured(First)[0];
        var peer = bench.Slave(3, 2, new LssSlaveOptions { Enabled = false });

        var scan = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
            AssignFromNodeId = 0x40,
            Limit = 1,
        });
        scan.Stop.Should().Be(LssScanStop.LimitReached);
        free.NodeId.Should().Be(0x40);
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => parked.State == NmtState.PreOperational);
        parked.NodeId.Should().Be(5);
    }

    [Fact]
    public async Task An_Assigning_Scan_Assigns_When_Fastscan_Skips_The_Serial_Number()
    {
        using var bench = new Bench();
        var parked = bench.Slave(1, 5, new LssSlaveOptions());
        SetIdentity(parked, new LssAddress(9, 9, 9, 9));
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => parked.LssState() == LssState.Configuration);
        var free = bench.Unconfigured(new LssAddress(1, 1, 1, 7))[0];

        var scan = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = new LssFastscanRequest
            {
                VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            },
            AssignFromNodeId = 0x41,
            Limit = 1,
        });

        scan.Stop.Should().Be(LssScanStop.LimitReached);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].Succeeded.Should().BeTrue();
        scan.Slaves[0].NodeId.Should().Be(0x41);
        free.NodeId.Should().Be(0x41);
        parked.NodeId.Should().Be(5);
    }

    [Fact]
    public async Task A_Scan_That_Misses_After_Parking_One_Slave_Releases_It()
    {
        using var bench = new Bench();
        var slaves = bench.Unconfigured(First, new LssAddress(9, 1, 1, 2));
        var scan = await new LssCommissioning(bench.Master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = KnownVendor(),
        });
        scan.Stop.Should().Be(LssScanStop.NoMatch);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].Address.Should().Be(First);
        await WaitUntilAsync(() => slaves.All(slave => slave.LssState() == LssState.Waiting));
    }

    [Fact]
    public async Task A_Rejected_Service_Is_Not_A_Success_And_Is_Not_Activated()
    {
        var rejectedId = new ServiceMaster { NodeId = new LssServiceResult(LssCommand.NodeIdOutOfRange, 0) };
        var nodeId = await new LssCommissioning(rejectedId).ConfigureSlaveAsync(
            First, new LssSlaveConfiguration { NodeId = 4 });
        nodeId.Selected.Should().BeTrue();
        nodeId.Succeeded.Should().BeFalse();
        nodeId.NodeIdResult!.Value.ErrorCode.Should().Be(LssCommand.NodeIdOutOfRange);

        var rejectedTiming = new ServiceMaster { Timing = new LssServiceResult(LssCommand.BitTimingNotSupported, 0) };
        var timing = await new LssCommissioning(rejectedTiming).ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 10,
            ActivateBitRate = 250_000,
        });
        timing.Succeeded.Should().BeFalse();
        rejectedTiming.ActivateCalled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Selective_Switch_That_Throws_Releases_The_Slave()
    {
        var master = new SelectiveThrowMaster();
        var tool = new LssCommissioning(master);

        var configure = async () => await tool.ConfigureSlaveAsync(First, new LssSlaveConfiguration { NodeId = 4 });
        await configure.Should().ThrowAsync<InvalidOperationException>();
        var inquire = async () => await tool.InquireSlaveAsync(First);
        await inquire.Should().ThrowAsync<InvalidOperationException>();
        master.Releases.Should().Be(4);
    }

    [Fact]
    public async Task The_Scan_Refuses_A_Change_That_Would_Hit_Every_Slave()
    {
        using var bench = new Bench();
        var tool = new LssCommissioning(bench.Master);
        var act = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { Store = true });
        await act.Should().ThrowAsync<ArgumentException>();
        var selector = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { BitTimingSelector = 0 });
        await selector.Should().ThrowAsync<ArgumentException>();
        var index = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan { BitTimingIndex = 0 });
        await index.Should().ThrowAsync<ArgumentException>();
        var both = async () => await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            BitTimingSelector = 0,
            BitTimingIndex = 1,
        });
        await both.Should().ThrowAsync<ArgumentException>();

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
    public async Task An_Assigning_Scan_Configures_The_Slave_Fastscan_Left_In_Configuration()
    {
        var master = new FoundMaster { SelectiveHit = false };
        var scan = await new LssCommissioning(master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            AssignFromNodeId = 0x10,
            Limit = 1,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
        });

        scan.Stop.Should().Be(LssScanStop.LimitReached);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].Selected.Should().BeTrue();
        scan.Slaves[0].Succeeded.Should().BeTrue();
        scan.Slaves[0].NodeId.Should().Be(0x10);
        master.SelectiveCalls.Should().Be(0);
        master.ConfigureCalls.Should().Be(1);
        master.GlobalSwitches.Should().Be(1);
    }

    [Fact]
    public async Task A_Timeout_Keeps_The_Slaves_Already_Given_A_Node_Id()
    {
        var master = new FoundMaster { ThrowOnSecondConfigure = true };
        var scan = await new LssCommissioning(master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            AssignFromNodeId = 0x10,
            Limit = 2,
        });

        scan.Stop.Should().Be(LssScanStop.TimedOut);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].NodeId.Should().Be(0x10);
        scan.Slaves[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_Timeout_After_A_Confirmed_Node_Id_Keeps_That_Id()
    {
        var master = new FoundMaster { ThrowOnBitTiming = true };
        var scan = await new LssCommissioning(master).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            AssignFromNodeId = 0x10,
            Limit = 2,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
        });

        scan.Stop.Should().Be(LssScanStop.TimedOut);
        scan.Slaves.Should().ContainSingle();
        scan.Slaves[0].NodeId.Should().Be(0x10);
        scan.Slaves[0].NodeIdResult!.Value.Succeeded.Should().BeTrue();
        scan.Slaves[0].BitTimingResult.Should().BeNull();
        master.ConfigureCalls.Should().Be(1);

        var again = async () => await new LssCommissioning(master).ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            NodeId = 0x11,
            BitTimingSelector = 0,
            BitTimingIndex = 3,
        });
        await again.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task Configure_Does_Not_Send_A_Second_Waiting_Frame_After_Activation()
    {
        var master = new FoundMaster();
        var configured = await new LssCommissioning(master).ConfigureSlaveAsync(First, new LssSlaveConfiguration
        {
            BitTimingSelector = 0,
            BitTimingIndex = 3,
            ActivateDelayMilliseconds = 20,
        });

        configured.Succeeded.Should().BeTrue();
        master.SelectiveCalls.Should().Be(1);
        master.GlobalSwitches.Should().Be(1);
    }

    [Fact]
    public async Task An_Assigning_Scan_Configures_In_Place_When_Fastscan_Skips_A_Middle_Number()
    {
        var product = new FoundMaster();
        var skippedProduct = await new LssCommissioning(product).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = new LssFastscanRequest
            {
                VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            },
            AssignFromNodeId = 0x10,
            Limit = 1,
        });
        skippedProduct.Stop.Should().Be(LssScanStop.LimitReached);
        skippedProduct.Slaves[0].Succeeded.Should().BeTrue();
        skippedProduct.Slaves[0].NodeId.Should().Be(0x10);
        product.SelectiveCalls.Should().Be(0);
        product.ConfigureCalls.Should().Be(1);

        var revision = new FoundMaster();
        var skippedRevision = await new LssCommissioning(revision).ScanUnconfiguredAsync(new LssUnconfiguredScan
        {
            Fastscan = new LssFastscanRequest
            {
                VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
                RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            },
            AssignFromNodeId = 0x11,
            Limit = 1,
        });
        skippedRevision.Stop.Should().Be(LssScanStop.LimitReached);
        skippedRevision.Slaves[0].Succeeded.Should().BeTrue();
        skippedRevision.Slaves[0].NodeId.Should().Be(0x11);
        revision.SelectiveCalls.Should().Be(0);
        revision.ConfigureCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_Cleanup_Failure_Does_Not_Hide_A_Successful_Configure()
    {
        var cancelled = new ServiceMaster { OnRelease = new OperationCanceledException() };
        var configured = await new LssCommissioning(cancelled).ConfigureSlaveAsync(
            First, new LssSlaveConfiguration { NodeId = 0x11 });
        configured.Succeeded.Should().BeTrue();
        configured.NodeId.Should().Be(0x11);

        var transport = new ServiceMaster { OnRelease = new CanOpenTransportException("the waiting frame was late") };
        var again = await new LssCommissioning(transport).ConfigureSlaveAsync(
            First, new LssSlaveConfiguration { NodeId = 0x12 });
        again.Succeeded.Should().BeTrue();
        again.NodeId.Should().Be(0x12);
    }

#if NET
    [Fact]
    public async Task ReadIdentities_Does_Not_Treat_A_Timeout_As_A_Zero()
    {
        var proxy = DispatchProxy.Create<ICanOpenNode, UploadStub>();
        var stub = (UploadStub)proxy;
        stub.NodeId = 2;
        stub.Upload = sub =>
        {
            if (sub == 0x01) return Task.FromResult(new byte[] { 9, 0, 0, 0 });
            throw new SdoAbortException(0x1018, sub, SdoAbortCode.SdoProtocolTimedOut, SdoAbortOrigin.Local);
        };

        var act = async () => await new LssCommissioning(new ServiceMaster()).ReadIdentitiesAsync(proxy, new byte[] { 6 });
        var thrown = await act.Should().ThrowAsync<SdoAbortException>();
        thrown.Which.AbortCode.Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);
    }
#endif

    [Fact]
    public async Task A_Disposed_Master_Lets_The_Scan_End()
    {
        using var bench = new Bench();
        var first = bench.Unconfigured(First)[0];
        var tool = new LssCommissioning(bench.Master);
        using var cancel = new CancellationTokenSource();
        var scan = tool.ScanUnconfiguredAsync(
            new LssUnconfiguredScan { Fastscan = KnownVendor() }, cancel.Token);
        await WaitUntilAsync(() => first.LssState() == LssState.Configuration, seconds: 30);
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

    private static async Task WaitUntilAsync(Func<bool> ready, int seconds = 3)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The LSS slave did not reach the expected state.");
            await Task.Delay(1);
        }
    }

#if NET
    private class UploadStub : DispatchProxy
    {
        public byte NodeId { get; set; }

        public Func<byte, Task<byte[]>>? Upload { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == "get_" + nameof(ICanOpenNode.NodeId)) return NodeId;
            if (targetMethod.Name == nameof(ICanOpenNode.SdoUploadAsync))
                return Upload!((byte)args![2]!);
            if (targetMethod.ReturnType == typeof(void)) return null;
            if (targetMethod.ReturnType == typeof(Task)) return Task.CompletedTask;
            throw new NotSupportedException(targetMethod.Name);
        }
    }
#endif

    /// <summary>Confirmed services return the scripted results. Selective switch always hits.</summary>
    private sealed class ServiceMaster : ILssMaster
    {
        public LssServiceResult NodeId { get; init; } = LssServiceResult.Success;

        public LssServiceResult Timing { get; init; } = LssServiceResult.Success;

        public bool ActivateCalled { get; private set; }

        public LssMasterOptions Options { get; } = new();

        public Exception? OnRelease { get; init; }

        private int _globals;

        public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
        {
            if (++_globals > 1 && OnRelease is not null) throw OnRelease;
            return Task.CompletedTask;
        }

        public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
            => Task.FromResult(NodeId);

        public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Timing);

        public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
            bool returnToWaiting = false, CancellationToken cancellationToken = default)
        {
            ActivateCalled = true;
            return Task.CompletedTask;
        }

        public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LssServiceResult.Success);

        public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LssAddress(0, 0, 0, 0));

        public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default) => Task.FromResult((byte)0);

        public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    /// Fastscan reports one slave and then stops. Selective switch and configure node-id are
    /// counted so a test can see whether a skipped LSS number stayed in configuration.
    /// </summary>
    private sealed class FoundMaster : ILssMaster
    {
        public bool SelectiveHit { get; init; } = true;

        public int SelectiveCalls { get; private set; }

        public int ConfigureCalls { get; private set; }

        public int GlobalSwitches { get; private set; }

        public bool ThrowOnSecondConfigure { get; init; }

        public bool ThrowOnBitTiming { get; init; }

        public LssMasterOptions Options { get; } = new();

        public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
        {
            GlobalSwitches++;
            return Task.CompletedTask;
        }

        public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
        {
            SelectiveCalls++;
            return Task.FromResult(SelectiveHit);
        }

        public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
        {
            ConfigureCalls++;
            if (ThrowOnSecondConfigure && ConfigureCalls == 2)
                throw new LssTimeoutException(LssCommand.ConfigureNodeId, TimeSpan.FromMilliseconds(300));
            return Task.FromResult(LssServiceResult.Success);
        }

        public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnBitTiming)
                throw new LssTimeoutException(LssCommand.ConfigureBitTiming, TimeSpan.FromMilliseconds(300));
            return Task.FromResult(LssServiceResult.Success);
        }

        public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
            bool returnToWaiting = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LssServiceResult.Success);

        public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult(0u);

        public Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LssAddress(0, 0, 0, 0));

        public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default) => Task.FromResult((byte)0);

        public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(LssFastscanResult.Found(First));

        public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    /// Selective switch fails after the slave may already be in configuration. The other
    /// services are never reached; the global switch is the release.
    /// </summary>
    private sealed class SelectiveThrowMaster : ILssMaster
    {
        public int Releases { get; private set; }

        public LssMasterOptions Options { get; } = new();

        public Task SwitchStateGlobalAsync(LssState state, CancellationToken cancellationToken = default)
        {
            Releases++;
            return Task.CompletedTask;
        }

        public Task<bool> SwitchStateSelectiveAsync(LssAddress address, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("selective switch failed");

        public Task<LssServiceResult> ConfigureNodeIdAsync(byte nodeId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LssServiceResult> ConfigureBitTimingAsync(byte tableSelector, byte tableIndex,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ActivateBitTimingAsync(ushort switchDelayMilliseconds, int? bitsPerSecond = null,
            bool returnToWaiting = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LssServiceResult> StoreConfigurationAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint> InquireVendorIdAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint> InquireProductCodeAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint> InquireRevisionNumberAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint> InquireSerialNumberAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LssAddress> InquireAddressAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte> InquireNodeIdAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IdentifyRemoteSlavesAsync(LssIdentityRange range, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> IdentifyNonConfiguredSlavesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LssFastscanResult> FastscanAsync(LssFastscanRequest? request = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<byte[]?> TransceiveManufacturerAsync(byte commandSpecifier, ReadOnlyMemory<byte> parameters,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
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
