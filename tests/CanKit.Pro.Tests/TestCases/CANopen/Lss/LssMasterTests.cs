using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// FR-CO-047: the LSS master against a scripted slave on the virtual bus. Every confirmed
/// service, the unconfirmed ones, Fastscan across several slaves, and the error paths.
/// </summary>
public class LssMasterTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly LssAddress SlaveA = new(1, 1, 1, 1);
    private static readonly LssAddress SlaveB = new(1, 1, 1, 2);
    private static readonly LssAddress SlaveC = new(1, 1, 1, 3);

    private static LssMasterOptions Quick() => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(300),
        // Fastscan tests drive this window on a virtual clock.
        FastscanTimeout = TimeSpan.FromMilliseconds(50),
        FastscanCycleTime = TimeSpan.FromMilliseconds(1),
        IdentificationTimeout = TimeSpan.FromMilliseconds(80),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The LSS slave did not reach the expected state.");
            await Task.Delay(1);
        }
    }

    [Fact]
    public void OpenMaster_Rejects_A_Null_Bus_And_A_Bad_Timeout()
    {
        var bus = () => CanOpenLss.OpenMaster((ICanBus)null!);
        bus.Should().Throw<ArgumentNullException>();
        var service = () => CanOpenLss.OpenMaster((ICanBusService)null!);
        service.Should().Throw<ArgumentNullException>();

        var session = VirtualAdapterFixture.NewSession("lss-opt");
        using var real = VirtualAdapterFixture.Open(session, 0);
        var zero = () => CanOpenLss.OpenMaster(real, new LssMasterOptions { ResponseTimeout = TimeSpan.Zero });
        zero.Should().Throw<ArgumentOutOfRangeException>();
        var negative = () => CanOpenLss.OpenMaster(real,
            new LssMasterOptions { FastscanCycleTime = TimeSpan.FromMilliseconds(-1) });
        negative.Should().Throw<ArgumentOutOfRangeException>();
        var identify = () => CanOpenLss.OpenMaster(real, new LssMasterOptions { IdentificationTimeout = TimeSpan.Zero });
        identify.Should().Throw<ArgumentOutOfRangeException>();
        var fastscan = () => CanOpenLss.OpenMaster(real, new LssMasterOptions { FastscanTimeout = TimeSpan.Zero });
        fastscan.Should().Throw<ArgumentOutOfRangeException>();
        var huge = () => CanOpenLss.OpenMaster(real, new LssMasterOptions { ResponseTimeout = TimeSpan.MaxValue });
        huge.Should().Throw<ArgumentOutOfRangeException>();
        var hugeGap = () => CanOpenLss.OpenMaster(real, new LssMasterOptions { FastscanCycleTime = TimeSpan.MaxValue });
        hugeGap.Should().Throw<ArgumentOutOfRangeException>();

        using var defaults = CanOpenLss.OpenMaster(real);
        defaults.Options.ResponseTimeout.Should().Be(TimeSpan.FromSeconds(1));
        defaults.Options.FastscanTimeout.Should().Be(TimeSpan.FromMilliseconds(50));
        using var sharedService = new CanBusService(real);
        using var shared = CanOpenLss.OpenMaster(sharedService);
        shared.Options.IdentificationTimeout.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task A_Subscribe_Failure_Disposes_An_Owned_Service_And_Not_A_Shared_One()
    {
        int loops = ProtocolActor.RunningLoopCount;
        var owned = new RejectingSubscribeService();
        var openOwned = () => CanOpenLss.OpenMaster(owned, Quick(), leaveOpen: false);
        openOwned.Should().Throw<InvalidOperationException>();
        owned.Disposals.Should().Be(1);
        ProtocolActor.RunningLoopCount.Should().Be(loops);

        var shared = new RejectingSubscribeService();
        var openShared = () => CanOpenLss.OpenMaster(shared, Quick());
        openShared.Should().Throw<InvalidOperationException>();
        shared.Disposals.Should().Be(0);
        using var clock = new VirtualClock();
        var timers = clock.NewActor();
        var injected = () => new LssMaster(shared, Quick(), ownsService: false, timers);
        injected.Should().Throw<InvalidOperationException>();
        (await timers.PostAsync(() => true)).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_Observes_A_Reader_That_Fails_Immediately()
    {
        var session = VirtualAdapterFixture.NewSession("lss-fault");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var raw = new CanBusService(bus);
        var service = new FaultingReadService(raw, faultImmediately: true);
        using var master = CanOpenLss.OpenMaster(service, Quick());
        await WaitUntilAsync(() => service.FramesOpened > 0);
        master.Dispose();
        await using var asyncMaster = CanOpenLss.OpenMaster(service, Quick());
        await WaitUntilAsync(() => service.FramesOpened > 1);
    }

    [Fact]
    public async Task A_Drained_Identify_Keeps_The_Match_When_The_Reader_Fails()
    {
        var session = VirtualAdapterFixture.NewSession("lss-fault-match");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var raw = new CanBusService(bus);
        using var slave = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 1), SlaveA);
        var service = new FaultingReadService(raw, faultImmediately: false);
        await using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            IdentificationTimeout = TimeSpan.FromSeconds(5),
        });

        (await master.IdentifyNonConfiguredSlavesAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Switch_Global_Is_Unconfirmed_And_Selects_Configuration()
    {
        using var rig = Rig.Open(SlaveA);
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => rig.Slave.State == LssState.Configuration);
        rig.Slave.Commands.Should().Contain(LssCommand.SwitchStateGlobal);

        var invalid = async () => await rig.Master.SwitchStateGlobalAsync((LssState)9);
        await invalid.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Selective_Switch_Matches_One_Address_And_Then_Inquire_Works()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 5);
        (await rig.Master.SwitchStateSelectiveAsync(new LssAddress(1, 1, 1, 9))).Should().BeFalse();
        rig.Slave.State.Should().Be(LssState.Waiting);

        (await rig.Master.SwitchStateSelectiveAsync(SlaveA)).Should().BeTrue();
        rig.Slave.State.Should().Be(LssState.Configuration);
        (await rig.Master.SwitchStateSelectiveAsync(SlaveA)).Should().BeFalse();

        (await rig.Master.InquireAddressAsync()).Should().Be(SlaveA);
        (await rig.Master.InquireNodeIdAsync()).Should().Be(5);
    }

    [Fact]
    public async Task Configure_And_Store_Report_The_Slave_Error_Codes()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 5);
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => rig.Slave.State == LssState.Configuration);

        var outOfRange = await rig.Master.ConfigureNodeIdAsync(0);
        outOfRange.ErrorCode.Should().Be(LssCommand.NodeIdOutOfRange);
        (await rig.Master.ConfigureNodeIdAsync(0x80)).ErrorCode.Should().Be(LssCommand.NodeIdOutOfRange);

        (await rig.Master.ConfigureNodeIdAsync(0x11)).Succeeded.Should().BeTrue();
        rig.Slave.PendingNodeId.Should().Be(0x11);
        (await rig.Master.InquireNodeIdAsync()).Should().Be(5, "the active node-id changes on the next reset communication");

        (await rig.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        rig.Slave.PendingNodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        (await rig.Master.InquireNodeIdAsync()).Should().Be(5);

        (await rig.Master.ConfigureBitTimingAsync(0, 5)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await rig.Master.ConfigureBitTimingAsync(0, LssBitTiming.AutomaticIndex)).ErrorCode
            .Should().Be(LssCommand.BitTimingNotSupported);
        (await rig.Master.ConfigureBitTimingAsync(1, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await rig.Master.ConfigureBitTimingAsync(0xFF, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);

        rig.Slave.ManufacturerRates[(0x80, 1)] = 250_000;
        (await rig.Master.ConfigureBitTimingAsync(0x80, 1)).Succeeded.Should().BeTrue();
        (await rig.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();

        (await rig.Master.ConfigureNodeIdAsync(0x12)).Succeeded.Should().BeTrue();
        (await rig.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
        rig.Slave.PersistentNodeId.Should().Be(0x12);
        rig.Slave.PersistentTableSelector.Should().Be(0);
        rig.Slave.PersistentTableIndex.Should().Be(3);
    }

    [Fact]
    public async Task Store_Reports_Media_And_Manufacturer_Errors_And_Does_Not_Copy()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 5);
        await EnterConfigurationAsync(rig);
        (await rig.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();

        rig.Slave.StoreSupported = false;
        var unsupported = await rig.Master.StoreConfigurationAsync();
        unsupported.ErrorCode.Should().Be(LssCommand.StoreNotSupported);
        rig.Slave.PersistentNodeId.Should().Be(5);

        rig.Slave.StoreSupported = true;
        rig.Slave.StoreMediaFails = true;
        (await rig.Master.StoreConfigurationAsync()).ErrorCode.Should().Be(LssCommand.StoreMediaError);
        rig.Slave.PersistentNodeId.Should().Be(5);

        rig.Slave.StoreMediaFails = false;
        rig.Slave.StoreSpecError = 0x2A;
        var specific = await rig.Master.StoreConfigurationAsync();
        specific.IsManufacturerSpecific.Should().BeTrue();
        specific.SpecError.Should().Be(0x2A);
        rig.Slave.PersistentNodeId.Should().Be(5);
    }

    [Fact]
    public async Task NodeId_Manufacturer_Error_Is_Passed_Through()
    {
        using var rig = Rig.Open(SlaveA);
        await EnterConfigurationAsync(rig);
        rig.Slave.NodeIdSpecError = 0x07;
        var result = await rig.Master.ConfigureNodeIdAsync(0x10);
        result.IsManufacturerSpecific.Should().BeTrue();
        result.SpecError.Should().Be(0x07);
        rig.Slave.ActiveNodeId.Should().BeNull();
    }

    [Fact]
    public async Task Unconfigured_Slave_Takes_A_NodeId_Immediately_And_Answers_NonConfigured_Only_Before_That()
    {
        using var rig = Rig.Open(SlaveA);
        (await rig.Master.IdentifyNonConfiguredSlavesAsync()).Should().BeTrue();
        await EnterConfigurationAsync(rig);
        (await rig.Master.InquireNodeIdAsync()).Should().Be(CanOpenCobId.UnconfiguredNodeId);
        (await rig.Master.ConfigureNodeIdAsync(0x15)).Succeeded.Should().BeTrue();
        rig.Slave.ActiveNodeId.Should().Be(0x15);
        (await rig.Master.InquireNodeIdAsync()).Should().Be(0x15);
        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => rig.Slave.State == LssState.Waiting);
        (await rig.Master.IdentifyNonConfiguredSlavesAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Confirmed_Service_Times_Out_When_The_Slave_Is_Waiting_Or_Silent()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 4);
        var act = async () => await rig.Master.ConfigureNodeIdAsync(4);
        var timeout = await act.Should().ThrowAsync<LssTimeoutException>();
        timeout.Which.CommandSpecifier.Should().Be(LssCommand.ConfigureNodeId);
        timeout.Which.Timeout.Should().Be(rig.Master.Options.ResponseTimeout);

        await EnterConfigurationAsync(rig);
        rig.Slave.NodeIdConfigurationSupported = false;
        var ignored = async () => await rig.Master.ConfigureNodeIdAsync(4);
        await ignored.Should().ThrowAsync<LssTimeoutException>();

        rig.Slave.BitTimingSupported = false;
        var timing = async () => await rig.Master.ConfigureBitTimingAsync(0, 2);
        await timing.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task A_Short_Slave_Frame_Is_Not_A_Confirmation()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 4);
        await EnterConfigurationAsync(rig);
        rig.Slave.ReplyWithLength(7);
        var act = async () => await rig.Master.InquireVendorIdAsync();
        await act.Should().ThrowAsync<LssTimeoutException>();
        (await rig.Master.InquireVendorIdAsync()).Should().Be(SlaveA.VendorId);
        (await rig.Master.InquireRevisionNumberAsync()).Should().Be(SlaveA.RevisionNumber);
    }

    [Fact]
    public async Task Cancelling_And_Disposing_Unblock_A_Wait()
    {
        var session = VirtualAdapterFixture.NewSession("lss-cancel");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        await using var master = CanOpenLss.OpenMaster(bus, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromSeconds(5),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        var cancel = async () => await master.InquireVendorIdAsync(cts.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        var hanging = master.InquireProductCodeAsync();
        await Task.Delay(20);
        await master.DisposeAsync();
        var disposed = async () => await hanging;
        await disposed.Should().ThrowAsync<ObjectDisposedException>();
        var after = async () => await master.InquireVendorIdAsync();
        await after.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task An_Unconfirmed_Send_Is_A_Transport_Error()
    {
        var session = VirtualAdapterFixture.NewSession("lss-reject");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var inner = new CanBusService(bus);
        using var rejecting = new RejectingSendService(inner);
        using var master = CanOpenLss.OpenMaster(rejecting, Quick());
        var act = async () => await master.SwitchStateGlobalAsync(LssState.Waiting);
        await act.Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task Activate_Waits_Both_Delays_And_The_Slave_Stays_Silent_For_The_Second()
    {
        int switches = 0;
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            BitRateSwitch = (rate, _) =>
            {
                switches++;
                rate.Should().Be(250_000);
                return Task.CompletedTask;
            },
        });
        var slave = rig.Add(SlaveA, nodeId: 4);
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        (await rig.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();

        var activating = rig.Master.ActivateBitTimingAsync(80, 250_000);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(80));
        switches.Should().Be(0);
        activating.IsCompleted.Should().BeFalse();
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(79));
        switches.Should().Be(0);
        slave.ActiveTableIndex.Should().Be(2);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await slave.WaitForSilenceAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => switches == 1);
        slave.IsSilent.Should().BeTrue();
        slave.ActiveTableIndex.Should().Be(3);

        // The clock is frozen inside d2: no host scheduling delay can move this poke out of it.
        var poke = slave.Process(LssFrames.Command(LssCommand.InquireVendorId));
        poke.Should().BeNull();
        slave.SuppressedResponses.Should().Be(1);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(80));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(79));
        activating.IsCompleted.Should().BeFalse();
        slave.IsSilent.Should().BeTrue();
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !slave.IsSilent);
        (await rig.Master.InquireVendorIdAsync()).Should().Be(SlaveA.VendorId);
    }

    [Fact]
    public async Task Activate_With_No_Delay_Still_Applies_And_Can_Skip_The_Rate_Switch()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 4);
        await EnterConfigurationAsync(rig);
        (await rig.Master.ConfigureBitTimingAsync(0, 6)).Succeeded.Should().BeTrue();
        await rig.Master.ActivateBitTimingAsync(0);
        await WaitUntilAsync(() => rig.Slave.ActiveTableIndex == 6);
        rig.Slave.ActiveTableIndex.Should().Be(6);

        int switches = 0;
        var withSwitch = Rig.ReplaceOptions(rig, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            IdentificationTimeout = TimeSpan.FromMilliseconds(80),
            FastscanTimeout = TimeSpan.FromMilliseconds(40),
            BitRateSwitch = (_, _) =>
            {
                switches++;
                return Task.CompletedTask;
            },
        });
        await withSwitch.ActivateBitTimingAsync(0, bitsPerSecond: null);
        switches.Should().Be(0);
    }

    [Fact]
    public async Task Identify_Remote_Matches_The_Inclusive_Range_In_Either_State()
    {
        var session = VirtualAdapterFixture.NewSession("lss-id");
        using var masterBus = VirtualAdapterFixture.Open(session, 0);
        using var master = CanOpenLss.OpenMaster(masterBus, Quick());
        using var inRange = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 1), SlaveB, 8);
        using var outOfRange = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 2), SlaveC, 9);

        var hit = new LssIdentityRange(1, 1, 1, 1, 2, 2);
        (await master.IdentifyRemoteSlavesAsync(hit)).Should().BeTrue();
        var miss = new LssIdentityRange(1, 1, 1, 1, 9, 9);
        (await master.IdentifyRemoteSlavesAsync(miss)).Should().BeFalse();

        await master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => inRange.State == LssState.Configuration);
        (await master.IdentifyRemoteSlavesAsync(hit)).Should().BeTrue();
    }

    [Fact]
    public async Task Fastscan_Finds_The_Smallest_Unconfigured_Slave_Then_The_Next()
    {
        using var rig = new ClockedLssRig(Quick());
        var master = rig.Master;
        var a = rig.Add(SlaveA);
        var b = rig.Add(SlaveB);
        var c = rig.Add(SlaveC);

        var request = new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
        };
        var first = await rig.RunAsync(master.FastscanAsync(request));
        first.Succeeded.Should().BeTrue();
        first.Address.Should().Be(SlaveA);
        a.State.Should().Be(LssState.Configuration);
        (await master.InquireSerialNumberAsync()).Should().Be(1);
        (await master.ConfigureNodeIdAsync(0x21)).Succeeded.Should().BeTrue();
        await master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => a.State == LssState.Waiting);

        var second = await rig.RunAsync(master.FastscanAsync(request));
        second.Address.SerialNumber.Should().Be(2);
        (await master.ConfigureNodeIdAsync(0x22)).Succeeded.Should().BeTrue();
        await master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => b.State == LssState.Waiting);

        var third = await rig.RunAsync(master.FastscanAsync(request));
        third.Address.Should().Be(SlaveC);
        (await master.ConfigureNodeIdAsync(0x23)).Succeeded.Should().BeTrue();
        await master.SwitchStateGlobalAsync(LssState.Waiting);

        (await rig.RunAsync(master.FastscanAsync(request))).Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);
        c.ActiveNodeId.Should().Be(0x23);
    }

    [Fact]
    public async Task Fastscan_Scans_Every_Bit_And_Honours_Skip_And_A_Known_Prefix()
    {
        using var rig = new ClockedLssRig(Quick());
        var slave = rig.Add(new LssAddress(1, 0, 0, 1));
        var full = await rig.RunAsync(rig.Master.FastscanAsync());
        full.Succeeded.Should().BeTrue();
        full.Address.Should().Be(new LssAddress(1, 0, 0, 1));

        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => slave.State == LssState.Waiting);
        // The scan above gave it no node-id, so it is in configuration and not unconfigured
        // until it is back in waiting. It is still unconfigured: pending is FFh.
        slave.PendingNodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);

        var skipped = await rig.RunAsync(rig.Master.FastscanAsync(new LssFastscanRequest
        {
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
        }));
        skipped.Succeeded.Should().BeTrue();
        skipped.Address.VendorId.Should().Be(1);
        skipped.Address.ProductCode.Should().Be(0);
        skipped.Address.SerialNumber.Should().Be(1);

        await rig.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => slave.State == LssState.Waiting);
        var prefixed = await rig.RunAsync(rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
            SerialNumber = new LssFastscanField { KnownHighBitCount = 31, Value = 0 },
        }));
        prefixed.Address.SerialNumber.Should().Be(1);
    }

    [Fact]
    public async Task Fastscan_Waits_Out_The_Window_So_A_Late_Reply_Is_Not_The_Next_Bit()
    {
        // Both slaves have vendor-id 1, so bit 0 is 1 and the step must hear silence. The
        // second slave answers after the cycle gap and inside the timeout. Without draining
        // the window, that late 4Fh is taken as "bit 0 is 0" and the vendor comes back 0.
        using var rig = new ClockedLssRig(new LssMasterOptions
        {
            FastscanTimeout = TimeSpan.FromMilliseconds(100),
            FastscanCycleTime = TimeSpan.FromMilliseconds(1),
        });
        var master = rig.Master;
        var first = rig.Add(new LssAddress(1, 0, 0, 0));
        var second = rig.Add(new LssAddress(1, 0, 0, 0));
        second.ResponseDelay = TimeSpan.FromMilliseconds(40);

        var found = await rig.RunAsync(master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { KnownHighBitCount = 30, Value = 0 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 0 },
        }));

        found.Status.Should().Be(LssFastscanStatus.Found);
        found.Address.VendorId.Should().Be(1u);
        first.State.Should().Be(LssState.Configuration);
        second.State.Should().Be(LssState.Configuration);
    }

    [Fact]
    public async Task Fastscan_Reports_No_Slave_And_No_Match()
    {
        using var rig = new ClockedLssRig(Quick());
        var master = rig.Master;
        (await rig.RunAsync(master.FastscanAsync())).Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);

        rig.Add(SlaveA, nodeId: 7);
        (await rig.RunAsync(master.FastscanAsync())).Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);

        var slave = rig.Add(SlaveA);
        var missed = await rig.RunAsync(master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 99 },
        }));
        missed.Status.Should().Be(LssFastscanStatus.NoMatch);
        missed.Address.Should().Be(default(LssAddress));
        slave.State.Should().Be(LssState.Waiting);

        var productMiss = await rig.RunAsync(master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 99 },
        }));
        productMiss.Status.Should().Be(LssFastscanStatus.NoMatch);
        productMiss.Address.Should().Be(new LssAddress(1, 0, 0, 0));

        // A known prefix the slave does not have ends the scan before any later bit.
        var prefixMiss = await rig.RunAsync(master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { KnownHighBitCount = 8, Value = 0xFF00_0000 },
        }));
        prefixMiss.Status.Should().Be(LssFastscanStatus.NoMatch);
        slave.State.Should().Be(LssState.Waiting);

        var known = await rig.RunAsync(master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { KnownHighBitCount = 32, Value = SlaveA.VendorId },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = SlaveA.ProductCode },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = SlaveA.RevisionNumber },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = SlaveA.SerialNumber },
        }));
        known.Status.Should().Be(LssFastscanStatus.Found);
        known.Address.Should().Be(SlaveA);
    }

    [Fact]
    public async Task Fastscan_Rejects_A_Selection_That_Cannot_Be_Unique()
    {
        using var rig = new ClockedLssRig(Quick());
        rig.Add(SlaveA);
        var skipVendor = async () => await rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
        });
        await skipVendor.Should().ThrowAsync<ArgumentException>();

        var skipThree = async () => await rig.Master.FastscanAsync(new LssFastscanRequest
        {
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Skip },
        });
        await skipThree.Should().ThrowAsync<ArgumentException>();

        var prefix = async () => await rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { KnownHighBitCount = -1 },
        });
        await prefix.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var prefixOnMatch = async () => await rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, KnownHighBitCount = 8, Value = 1 },
        });
        await prefixOnMatch.Should().ThrowAsync<ArgumentException>();

        var tooWide = async () => await rig.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { KnownHighBitCount = 33 },
        });
        await tooWide.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var missing = async () => await rig.Master.FastscanAsync(new LssFastscanRequest { VendorId = null! });
        await missing.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_Short_Frame_And_An_Extended_Frame_Are_Not_An_Lss_Response()
    {
        using var rig = new ClockedLssRig(Quick());
        rig.Add(SlaveA);
        await rig.InjectAsync(new byte[] { 1, 2 });
        await rig.InjectAsync(new byte[LssFrames.Length], extended: true);

        var found = await rig.RunAsync(rig.Master.FastscanAsync());
        found.Succeeded.Should().BeTrue();
        found.Address.Should().Be(SlaveA);
    }

    [Fact]
    public async Task A_Frame_From_Another_Node_Is_Not_An_Lss_Response()
    {
        using var rig = Rig.Open(SlaveA);
        using var peer = VirtualAdapterFixture.Open(rig.Session, 2);
        using (var other = CanFrame.Classic(0x123, new byte[LssFrames.Length], isExtendedFrame: false))
            peer.Transmit(other);
        using (var extended = CanFrame.Classic(
            unchecked((int)CanOpenCobId.LssSlaveMessage), new byte[LssFrames.Length], isExtendedFrame: true))
            peer.Transmit(extended);

        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => rig.Slave.State == LssState.Configuration);
        (await rig.Master.InquireVendorIdAsync()).Should().Be(SlaveA.VendorId);
    }

    [Fact]
    public async Task A_Timeout_Shorter_Than_The_Response_Misses_It()
    {
        using var rig = new ClockedLssRig(new LssMasterOptions { ResponseTimeout = TimeSpan.FromTicks(1) });
        var slave = rig.Add(SlaveA);
        slave.ResponseDelay = TimeSpan.FromTicks(2);
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        var inquire = rig.Master.InquireVendorIdAsync();
        await rig.ArmedAsync(TimeSpan.FromTicks(1));
        await rig.Clock.AdvanceAsync(TimeSpan.FromTicks(1));
        var act = async () => await inquire.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task Closing_The_Service_Ends_An_Inquire_As_A_Timeout()
    {
        var session = VirtualAdapterFixture.NewSession("lss-close");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var service = new CanBusService(bus);
        using var slave = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 1), SlaveA);
        await using var master = CanOpenLss.OpenMaster(service, Quick());
        var inquire = master.InquireVendorIdAsync();
        await WaitUntilAsync(() => slave.Commands.Contains(LssCommand.InquireVendorId));
        service.Dispose();
        var act = async () => await inquire;
        await act.Should().ThrowAsync<CanOpenTransportException>();
    }

    [Fact]
    public async Task A_Drained_Identify_Keeps_The_Match_When_The_Reader_Ends()
    {
        var session = VirtualAdapterFixture.NewSession("lss-drain");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var service = new CanBusService(bus);
        using var slave = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 1), SlaveA);
        await using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            FastscanTimeout = TimeSpan.FromMilliseconds(40),
            IdentificationTimeout = TimeSpan.FromSeconds(5),
        });
        using var listenBus = VirtualAdapterFixture.Open(session, 2);
        using var listen = new CanBusService(listenBus);
        var answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = listen.Subscribe(frame =>
        {
            if (!frame.Frame.IsExtendedFrame && (uint)frame.Frame.ID == CanOpenCobId.LssSlaveMessage)
                answered.TrySetResult(true);
            return false;
        });

        var identify = master.IdentifyNonConfiguredSlavesAsync();
        await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();

        (await identify).Should().BeTrue();
    }

    [Fact]
    public async Task Activate_Can_Return_The_Slave_To_Waiting_Before_The_Switch()
    {
        using var rig = new ClockedLssRig();
        var slave = rig.Add(SlaveA, nodeId: 5);
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        (await rig.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        var activating = rig.Master.ActivateBitTimingAsync(20, returnToWaiting: true);
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(20));
        slave.State.Should().Be(LssState.Waiting);
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        await rig.ArmedAsync(TimeSpan.FromMilliseconds(20));
        await rig.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        await activating.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_Zero_Delay_Returns_To_Waiting_After_The_Master_Switches()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 5);
        await EnterConfigurationAsync(rig);
        bool waitingDuringSwitch = true;
        var master = Rig.ReplaceOptions(rig, new LssMasterOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(300),
            FastscanTimeout = TimeSpan.FromMilliseconds(40),
            IdentificationTimeout = TimeSpan.FromMilliseconds(80),
            BitRateSwitch = (_, _) =>
            {
                waitingDuringSwitch = rig.Slave.State == LssState.Waiting;
                return Task.CompletedTask;
            },
        });

        await master.ActivateBitTimingAsync(0, bitsPerSecond: 250_000, returnToWaiting: true);

        waitingDuringSwitch.Should().BeFalse();
        await WaitUntilAsync(() => rig.Slave.State == LssState.Waiting);
        rig.Slave.State.Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task Manufacturer_Command_Round_Trips_And_Times_Out_Without_A_Handler()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 4);
        (await rig.Master.TransceiveManufacturerAsync(0x90, new byte[] { 1, 2 })).Should().BeNull();

        await EnterConfigurationAsync(rig);
        rig.Slave.Manufacturer = (cs, data) =>
        {
            cs.Should().Be(0x90);
            data[1].Should().Be(1);
            var reply = new byte[8];
            reply[0] = cs;
            reply[1] = 0x55;
            return reply;
        };
        var response = await rig.Master.TransceiveManufacturerAsync(0x90, new byte[] { 1, 2 });
        response.Should().NotBeNull();
        response![1].Should().Be(0x55);

        var low = async () => await rig.Master.TransceiveManufacturerAsync(0x11, ReadOnlyMemory<byte>.Empty);
        await low.Should().ThrowAsync<ArgumentOutOfRangeException>();
        var longPayload = async () => await rig.Master.TransceiveManufacturerAsync(0x90, new byte[8]);
        await longPayload.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_Shared_Service_Stays_Open_When_The_Master_Does_Not_Own_It()
    {
        var session = VirtualAdapterFixture.NewSession("lss-share");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var service = new CanBusService(bus);
        var master = CanOpenLss.OpenMaster(service, Quick(), leaveOpen: true);
        master.Dispose();
        master.Dispose();
        using (var frame = CanFrame.Classic(0x100, new byte[] { 1 }, isExtendedFrame: false))
        {
            (await service.SendConfirmedAsync(frame)).Confirmed.Should().BeTrue();
        }

        var owning = CanOpenLss.OpenMaster(service, Quick(), leaveOpen: false);
        owning.Dispose();
        using (var again = CanFrame.Classic(0x100, new byte[] { 1 }, isExtendedFrame: false))
        {
            var act = async () => await service.SendConfirmedAsync(again);
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }
    }

    [Fact]
    public async Task Commands_Run_One_At_A_Time()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 4);
        await EnterConfigurationAsync(rig);
        var vendor = rig.Master.InquireVendorIdAsync();
        var product = rig.Master.InquireProductCodeAsync();
        (await vendor).Should().Be(1);
        (await product).Should().Be(1);
    }

    [Fact]
    public async Task Dispose_Completes_A_Command_Queued_On_The_Gate()
    {
        using var rig = new ClockedLssRig();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.Send = async frame =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return new TxConfirmation { Confirmed = true };
        };
        var first = rig.Master.InquireVendorIdAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = rig.Master.InquireProductCodeAsync();
        try
        {
            // Keep the active send blocked until the queued waiter has observed disposal.
            // Its gate cannot be released first by a competing cancellation continuation.
            rig.Master.Dispose();
            var secondAct = async () => await second.WaitAsync(TimeSpan.FromSeconds(5));
            await secondAct.Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            release.TrySetResult(true);
            var firstAct = async () => await first.WaitAsync(TimeSpan.FromSeconds(5));
            await firstAct.Should().ThrowAsync<ObjectDisposedException>();
        }
    }

    [Fact]
    public async Task Dispose_Cancels_An_Activate_That_Is_Still_Waiting()
    {
        using var rig = Rig.Open(SlaveA, nodeId: 5);
        await EnterConfigurationAsync(rig);
        var pending = rig.Master.ActivateBitTimingAsync(60_000);
        await WaitUntilAsync(() => rig.Slave.Commands.Contains(LssCommand.ActivateBitTiming));
        rig.Master.Dispose();
        var act = async () => await pending;
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_Confirmation_Without_Timestamps_Still_Waits_For_The_Slave()
    {
        var session = VirtualAdapterFixture.NewSession("lss-stamp");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        using var service = new CanBusService(bus);
        using var slave = new ScriptedLssSlave(VirtualAdapterFixture.Open(session, 1), SlaveA, persistentNodeId: 5);
        await using var master = CanOpenLss.OpenMaster(new UnstampedSendService(service), Quick());
        await master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => slave.State == LssState.Configuration);
        (await master.InquireVendorIdAsync()).Should().Be(SlaveA.VendorId);
    }

    [Fact]
    public async Task Fastscan_Finds_An_Address_Whose_Every_Field_Has_Bit_Zero_Clear()
    {
        // BitCheck 0 and LSSNext 0 is the switch into configuration. The vendor-id's own bit 0
        // must not be probed with that pair, including when every field really is zero.
        using var rig = new ClockedLssRig(Quick());
        var slave = rig.Add(new LssAddress(0, 0, 0, 0));
        var found = await rig.RunAsync(rig.Master.FastscanAsync());
        found.Succeeded.Should().BeTrue();
        found.Address.Should().Be(new LssAddress(0, 0, 0, 0));
        slave.State.Should().Be(LssState.Configuration);
    }

    private static async Task EnterConfigurationAsync(Rig rig)
    {
        await rig.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => rig.Slave.State == LssState.Configuration);
    }

    private sealed class Rig : IDisposable
    {
        private Rig(string session, ICanBus masterBus, ILssMaster master, ScriptedLssSlave slave)
        {
            Session = session;
            MasterBus = masterBus;
            Master = master;
            Slave = slave;
        }

        public string Session { get; }
        public ICanBus MasterBus { get; }
        public ILssMaster Master { get; private set; }
        public ScriptedLssSlave Slave { get; }

        public static Rig Open(LssAddress address, byte nodeId = CanOpenCobId.UnconfiguredNodeId)
        {
            var session = VirtualAdapterFixture.NewSession("lss");
            var masterBus = VirtualAdapterFixture.Open(session, 0);
            var slaveBus = VirtualAdapterFixture.Open(session, 1);
            var slave = new ScriptedLssSlave(slaveBus, address, nodeId);
            var master = CanOpenLss.OpenMaster(masterBus, Quick());
            return new Rig(session, masterBus, master, slave);
        }

        public static ILssMaster ReplaceOptions(Rig rig, LssMasterOptions options)
        {
            rig.Master.Dispose();
            rig.Master = CanOpenLss.OpenMaster(rig.MasterBus, options);
            return rig.Master;
        }

        public void Dispose()
        {
            Master.Dispose();
            Slave.Dispose();
            MasterBus.Dispose();
        }
    }

    private sealed class RejectingSendService : ICanBusService
    {
        private readonly ICanBusService _inner;

        public RejectingSendService(ICanBusService inner) => _inner = inner;

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TxConfirmation
            {
                Confirmed = false,
                FailureReason = TxConfirmFailureReason.Rejected,
                Timestamp = DateTime.UtcNow,
            });

        public void Dispose() => _inner.Dispose();
    }

    private sealed class UnstampedSendService : ICanBusService
    {
        private readonly ICanBusService _inner;

        public UnstampedSendService(ICanBusService inner) => _inner = inner;

        public ICanBus Bus => _inner.Bus;
        public int SubscriptionCount => _inner.SubscriptionCount;
        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var confirmation = await _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
            return confirmation with { HostHandoffTimestamp = 0, HostTransmitTimestamp = 0 };
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Subscribe fails, so opening the master never starts a reader.</summary>
    private sealed class RejectingSubscribeService : ICanBusService
    {
        public int Disposals { get; private set; }

        public ICanBus Bus => throw new NotSupportedException();

        public int SubscriptionCount => 0;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add { }
            remove { }
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false)
            => throw new InvalidOperationException("subscribe failed");

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => throw new InvalidOperationException("subscribe failed");

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => Array.Empty<FilterOverlap>();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() => Disposals++;
    }

    /// <summary>
    /// The reader's frame stream fails. Immediately, so disposal observes a faulted reader, or
    /// after the first frame, so a drain that already matched can still return that match.
    /// </summary>
    private sealed class FaultingReadService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly bool _faultImmediately;
        private int _framesOpened;

        public FaultingReadService(ICanBusService inner, bool faultImmediately)
        {
            _inner = inner;
            _faultImmediately = faultImmediately;
        }

        public int FramesOpened => Volatile.Read(ref _framesOpened);

        public ICanBus Bus => _inner.Bus;

        public int SubscriptionCount => _inner.SubscriptionCount;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false)
            => new FaultingSubscription(_inner.Subscribe(predicate, bufferCapacity, includeEcho), this);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => new FaultingSubscription(_inner.Subscribe(filter, bufferCapacity, includeEcho), this);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => _inner.SendConfirmedAsync(frame, timeout, cancellationToken);

        public void Dispose() => _inner.Dispose();

        private void Opened() => Interlocked.Increment(ref _framesOpened);

        private sealed class FaultingSubscription : ISubscription
        {
            private readonly ISubscription _inner;
            private readonly FaultingReadService _owner;
            private bool _opened;
            private bool _delivered;

            public FaultingSubscription(ISubscription inner, FaultingReadService owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public IAsyncEnumerable<CanFrameEvent> Frames
            {
                get
                {
                    Opened();
                    if (_owner._faultImmediately)
                        throw new InvalidOperationException("reader failed");
                    return FaultAfter(_inner.Frames);
                }
            }

            public bool TryRead(out CanFrameEvent frameEvent)
            {
                if (!_inner.TryRead(out frameEvent)) return false;
                _delivered = true;
                return true;
            }

            public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                Opened();
                if (_owner._faultImmediately || _delivered)
                    throw new InvalidOperationException("reader failed");
                return _inner.WaitToReadAsync(cancellationToken);
            }

            private void Opened()
            {
                if (_opened) return;
                _opened = true;
                _owner.Opened();
            }

            public void Reconfigure(CanIdFilter filter) => _inner.Reconfigure(filter);

            public void Reconfigure(Func<CanFrameEvent, bool>? predicate) => _inner.Reconfigure(predicate);

            public void Dispose() => _inner.Dispose();

            private static async IAsyncEnumerable<CanFrameEvent> FaultAfter(IAsyncEnumerable<CanFrameEvent> frames)
            {
                await foreach (var frame in frames.ConfigureAwait(false))
                {
                    yield return frame;
                    throw new InvalidOperationException("reader failed");
                }
            }
        }
    }
}
