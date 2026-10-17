using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

/// <summary>
/// FR-CO-048: the LSS slave inside <see cref="CanOpenNode"/>, driven by the LSS master on the
/// virtual bus. Node-id changes take effect on NMT reset communication, an unconfigured slave
/// does not boot, and a device description's <c>LSS_Supported</c> turns the slave on.
/// </summary>
public class LssSlaveTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly LssAddress Address = new(0x10, 0x20, 0x30, 0x40);

    private static LssMasterOptions Quick() => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(300),
        FastscanTimeout = TimeSpan.FromMilliseconds(500),
        FastscanCycleTime = TimeSpan.FromMilliseconds(1),
        IdentificationTimeout = TimeSpan.FromMilliseconds(80),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The LSS slave did not reach the expected state.");
            await Task.Delay(1);
        }
    }

    private static async Task EnterConfiguration(ILssMaster master, ICanOpenNode slave)
    {
        await master.SwitchStateGlobalAsync(LssState.Configuration);
        await WaitUntilAsync(() => slave.LssState() == LssState.Configuration);
    }

    private static void SetIdentity(ICanOpenNode node, LssAddress address)
    {
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1018, 0x00, 4);
        od.WriteUnsigned(0x1018, 0x01, address.VendorId);
        WriteIdentity(od, 0x02, address.ProductCode);
        WriteIdentity(od, 0x03, address.RevisionNumber);
        WriteIdentity(od, 0x04, address.SerialNumber);
    }

    private static void WriteIdentity(ObjectDictionary od, byte subindex, uint value)
    {
        if (!od.TryGet(0x1018, subindex, out _)) od.AddU32(0x1018, subindex, value);
        else od.WriteUnsigned(0x1018, subindex, value);
    }

    private static void Transmit(ICanBus bus, byte[] payload)
        => TransmitCob(bus, CanOpenCobId.LssMasterMessage, payload);

    private static void TransmitCob(ICanBus bus, uint cobId, byte[] payload)
    {
        using var frame = CanFrame.Classic(unchecked((int)cobId), payload, isExtendedFrame: false);
        bus.Transmit(frame);
    }

    [Fact]
    public void Open_Rejects_An_Unconfigured_Node_Id_Unless_Lss_Is_On()
    {
        var session = VirtualAdapterFixture.NewSession("lss-reject");
        using var bus = VirtualAdapterFixture.Open(session, 0);
        var rejected = () => CanOpen.OpenNode(bus, CanOpenCobId.UnconfiguredNodeId);
        rejected.Should().Throw<ArgumentOutOfRangeException>();

        var kept = new CanOpenNodeOptions { Lss = new LssSlaveOptions { Enabled = false } };
        kept.With(sdoTimeout: TimeSpan.FromSeconds(2)).Lss!.Enabled.Should().BeFalse();
        kept.WithLss(new LssSlaveOptions()).Lss!.Enabled.Should().BeTrue();
        kept.WithLss(null).Lss.Should().BeNull();
    }

    [Fact]
    public async Task Confirmed_Services_Run_Only_In_Configuration_And_Report_The_Slave_Codes()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5);
        SetIdentity(slave, Address);
        var silent = async () => await bench.Master.ConfigureNodeIdAsync(0x11);
        await silent.Should().ThrowAsync<LssTimeoutException>();
        slave.LssState().Should().Be(LssState.Waiting);
        slave.NodeId.Should().Be(5);

        (await bench.Master.SwitchStateSelectiveAsync(new LssAddress(1, 1, 1, 1))).Should().BeFalse();
        (await bench.Master.SwitchStateSelectiveAsync(Address)).Should().BeTrue();
        await bench.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);

        // A vendor command that is not followed by the other three leaves the comparison
        // mid-sequence. The next complete switch must still select the slave.
        using (var poke = VirtualAdapterFixture.Open(bench.Session, 9))
        using (var heard = bench.Listen())
        {
            Transmit(poke, LssFrames.WithU32(LssCommand.SwitchSelectiveVendor, Address.VendorId));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteVendor, Address.VendorId));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteProduct, Address.ProductCode));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionLow, Address.RevisionNumber));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionHigh, Address.RevisionNumber));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteSerialLow, Address.SerialNumber));
            Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteSerialHigh, Address.SerialNumber));
            await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.LssSlaveMessage));
        }

        (await bench.Master.SwitchStateSelectiveAsync(Address)).Should().BeTrue();
        slave.LssState().Should().Be(LssState.Configuration);
        (await bench.Master.InquireAddressAsync()).Should().Be(Address);
        (await bench.Master.InquireNodeIdAsync()).Should().Be(5);

        (await bench.Master.ConfigureNodeIdAsync(0)).ErrorCode.Should().Be(LssCommand.NodeIdOutOfRange);
        (await bench.Master.ConfigureNodeIdAsync(0x80)).ErrorCode.Should().Be(LssCommand.NodeIdOutOfRange);
        (await bench.Master.ConfigureBitTimingAsync(0, 5)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.ConfigureBitTimingAsync(0, LssBitTiming.AutomaticIndex)).ErrorCode
            .Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.ConfigureBitTimingAsync(1, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.StoreConfigurationAsync()).ErrorCode.Should().Be(LssCommand.StoreNotSupported);

        (await bench.Master.ConfigureNodeIdAsync(0x11)).Succeeded.Should().BeTrue();
        (await bench.Master.InquireNodeIdAsync()).Should().Be(5);
        await bench.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);
        var missed = async () => await bench.Master.InquireNodeIdAsync();
        await missed.Should().ThrowAsync<LssTimeoutException>();
    }

    [Fact]
    public async Task Node_Id_Is_Applied_On_Reset_Communication_And_Reset_Node_Reloads_The_Persistent_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5);
        var client = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, slave);

        (await bench.Master.ConfigureNodeIdAsync(0x11)).Succeeded.Should().BeTrue();
        (await bench.Master.InquireNodeIdAsync()).Should().Be(5);
        await client.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x11 && slave.State == NmtState.PreOperational);
        (await client.SdoUploadAsync(0x11, 0x1000, 0x00)).Should().NotBeEmpty();
        var oldId = async () => await client.SdoUploadAsync(5, 0x1000, 0x00);
        (await oldId.Should().ThrowAsync<SdoAbortException>()).Which.AbortCode
            .Should().Be((uint)SdoAbortCode.SdoProtocolTimedOut);

        (await bench.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        (await bench.Master.InquireNodeIdAsync()).Should().Be(0x11);
        await client.SendNmtCommandAsync(NmtCommand.ResetCommunication, 0x11);
        await WaitUntilAsync(() => slave.NodeId == CanOpenCobId.UnconfiguredNodeId
            && slave.State == NmtState.Initializing);

        await client.SendNmtCommandAsync(NmtCommand.ResetNode, 0);
        await WaitUntilAsync(() => slave.NodeId == 5 && slave.State == NmtState.PreOperational);
        (await client.SdoUploadAsync(5, 0x1000, 0x00)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_Unconfigured_Slave_Does_Not_Boot_Until_It_Is_Given_A_Node_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, Address);
        slave.LssState().Should().Be(LssState.Waiting);
        slave.State.Should().Be(NmtState.Initializing);
        slave.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);

        using var heard = bench.Listen();
        await Task.Delay(50);
        heard.Ids.Should().NotContain(id => id >= CanOpenCobId.HeartbeatBase);

        (await bench.Master.IdentifyNonConfiguredSlavesAsync()).Should().BeTrue();
        // A node does not observe the NMT it sends, so the start comes from the unconfigured
        // slave and the peer is only the receiver. Start, stop and enter pre-operational need
        // an active node-id, so the sender stays in reset-communication waiting.
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await WaitUntilAsync(() => peer.State == NmtState.PreOperational);
        await slave.SendNmtCommandAsync(NmtCommand.Start, 0);
        await WaitUntilAsync(() => peer.State == NmtState.Operational);
        slave.State.Should().Be(NmtState.Initializing);

        var found = await bench.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.VendorId },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.ProductCode },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.RevisionNumber },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.SerialNumber },
        });
        found.Status.Should().Be(LssFastscanStatus.Found);
        found.Address.Should().Be(Address);
        slave.LssState().Should().Be(LssState.Configuration);

        (await bench.Master.ConfigureNodeIdAsync(0x15)).Succeeded.Should().BeTrue();
        await WaitUntilAsync(() => slave.NodeId == 0x15 && slave.State == NmtState.PreOperational);
        slave.LssState().Should().Be(LssState.Configuration);
        (await bench.Master.IdentifyNonConfiguredSlavesAsync()).Should().BeFalse();
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.Heartbeat(0x15)));
    }

    [Fact]
    public async Task Fastscan_Finds_Unconfigured_Slaves_In_Numeric_Order()
    {
        using var bench = new Bench();
        var first = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        var second = bench.Slave(2, CanOpenCobId.UnconfiguredNodeId);
        var third = bench.Slave(3, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(first, new LssAddress(1, 1, 1, 1));
        SetIdentity(second, new LssAddress(1, 1, 1, 2));
        SetIdentity(third, new LssAddress(1, 1, 1, 3));
        var request = new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = 1 },
        };

        var firstFound = await bench.Master.FastscanAsync(request);
        firstFound.Status.Should().Be(LssFastscanStatus.Found);
        firstFound.Address.SerialNumber.Should().Be(1);
        (await bench.Master.ConfigureNodeIdAsync(0x21)).Succeeded.Should().BeTrue();
        (await bench.Master.FastscanAsync(request)).Address.SerialNumber.Should().Be(2);
        (await bench.Master.ConfigureNodeIdAsync(0x22)).Succeeded.Should().BeTrue();
        (await bench.Master.FastscanAsync(request)).Address.SerialNumber.Should().Be(3);
        (await bench.Master.ConfigureNodeIdAsync(0x23)).Succeeded.Should().BeTrue();
        (await bench.Master.FastscanAsync(request)).Status.Should().Be(LssFastscanStatus.NoUnconfiguredSlave);

        first.NodeId.Should().Be(0x21);
        second.NodeId.Should().Be(0x22);
        third.NodeId.Should().Be(0x23);
    }

    [Fact]
    public async Task Full_Fastscan_Reads_The_Identity_From_The_Object_Dictionary()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, new LssAddress(1, 0, 0, 1));
        var found = await bench.Master.FastscanAsync();
        found.Status.Should().Be(LssFastscanStatus.Found);
        found.Address.Should().Be(new LssAddress(1, 0, 0, 1));
        slave.LssState().Should().Be(LssState.Configuration);
    }

    [Fact]
    public async Task Full_Fastscan_Finds_An_Address_Whose_Every_Bit_Is_Zero()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, new LssAddress(0, 0, 0, 0));
        var found = await bench.Master.FastscanAsync();
        found.Status.Should().Be(LssFastscanStatus.Found);
        found.Address.Should().Be(new LssAddress(0, 0, 0, 0));
        slave.LssState().Should().Be(LssState.Configuration);
    }

    [Fact]
    public async Task Fastscan_Accepts_LssNext_Four_As_The_Completion()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, Address);
        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        Transmit(poke, LssFrames.Fastscan(0, LssCommand.FastscanReset, 0, 0));
        Transmit(poke, LssFrames.Fastscan(Address.VendorId, 0, 0, 1));
        Transmit(poke, LssFrames.Fastscan(Address.ProductCode, 0, 1, 2));
        Transmit(poke, LssFrames.Fastscan(Address.RevisionNumber, 0, 2, 3));
        Transmit(poke, LssFrames.Fastscan(Address.SerialNumber, 0, 3, 4));
        await WaitUntilAsync(() => slave.LssState() == LssState.Configuration);
    }

    [Fact]
    public async Task Fastscan_LssNext_Four_Does_Not_Finish_An_Earlier_Number()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, Address);
        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        using var heard = bench.Listen();
        Transmit(poke, LssFrames.Fastscan(0, LssCommand.FastscanReset, 0, 0));
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.LssSlaveMessage));
        int replies = heard.Ids.Count(id => id == CanOpenCobId.LssSlaveMessage);
        Transmit(poke, LssFrames.Fastscan(Address.VendorId, 0, 0, 4));
        await WaitUntilAsync(() => heard.Ids.Count(id => id == CanOpenCobId.LssSlaveMessage) > replies);
        slave.LssState().Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task Store_Persists_The_Pending_Node_Id_And_Bit_Timing_And_The_Next_Open_Loads_Them()
    {
        var store = new MemoryStore();
        using (var bench = new Bench())
        {
            var slave = bench.Slave(1, 5, new LssSlaveOptions
            {
                Store = store,
                ManufacturerRates = new Dictionary<(byte, byte), int> { [(0x80, 1)] = 100_000 },
            });
            await EnterConfiguration(bench.Master, slave);
            (await bench.Master.ConfigureNodeIdAsync(0x12)).Succeeded.Should().BeTrue();
            (await bench.Master.ConfigureBitTimingAsync(0x80, 1)).Succeeded.Should().BeTrue();

            store.Fail = true;
            (await bench.Master.StoreConfigurationAsync()).ErrorCode.Should().Be(LssCommand.StoreMediaError);
            store.Spec = 0x2A;
            var specific = await bench.Master.StoreConfigurationAsync();
            specific.ErrorCode.Should().Be(LssCommand.ManufacturerSpecific);
            specific.SpecError.Should().Be(0x2A);
            store.Has.Should().BeFalse();

            store.Fail = false;
            (await bench.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
            store.NodeId.Should().Be(0x12);
            store.Selector.Should().Be(0x80);
            store.Index.Should().Be(1);
        }

        using var again = new Bench();
        var reloaded = again.Slave(1, 5, new LssSlaveOptions { Store = store });
        reloaded.LssState().Should().Be(LssState.Waiting);
        reloaded.NodeId.Should().Be(0x12);
        await EnterConfiguration(again.Master, reloaded);
        (await again.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
        store.Selector.Should().Be(0x80);
        store.Index.Should().Be(1);
    }

    [Fact]
    public async Task Activate_Suppresses_Transmission_For_The_Second_Delay_And_Reports_The_Rate()
    {
        int switches = 0;
        int rate = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (bitsPerSecond, _) =>
            {
                Interlocked.Increment(ref switches);
                Volatile.Write(ref rate, bitsPerSecond);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(LssBitTiming.Cia301Table, 3)).Succeeded.Should().BeTrue();

        using var heard = bench.Listen();
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        Volatile.Read(ref rate).Should().Be(250_000);
        int before = heard.Ids.Count;
        var held = slave.SendNmtCommandAsync(NmtCommand.Start, 0);
        await Task.Delay(40);
        held.IsCompleted.Should().BeFalse();
        heard.Ids.Skip(before).Should().NotContain(CanOpenCobId.NmtCommand);
        await activate;
        await held;
        await WaitUntilAsync(() => heard.Ids.Skip(before).Contains(CanOpenCobId.NmtCommand));
    }

    [Fact]
    public async Task Disposing_The_Slave_Drops_A_Held_Nmt()
    {
        int switches = 0;
        using var bench = new Bench();
        using var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        using var heard = bench.Listen();
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        var held = slave.SendNmtCommandAsync(NmtCommand.Start, 0);
        await Task.Delay(30);
        held.IsCompleted.Should().BeFalse();
        slave.Dispose();
        var finished = await Task.WhenAny(held, Task.Delay(1000));
        finished.Should().BeSameAs(held);
        held.Status.Should().Be(TaskStatus.RanToCompletion);
        heard.Ids.Should().NotContain(CanOpenCobId.NmtCommand);
        await activate;
    }

    [Fact]
    public async Task A_Driver_Fault_Completes_A_Held_Nmt_And_The_Heartbeat_Behind_It_Still_Goes()
    {
        await AssertHeldNmtFailure(cancel: false);
    }

    [Fact]
    public async Task A_Cancelled_Held_Nmt_Completes_And_The_Heartbeat_Behind_It_Still_Goes()
    {
        await AssertHeldNmtFailure(cancel: true);
    }

    private static async Task AssertHeldNmtFailure(bool cancel)
    {
        int switches = 0;
        using var bench = new Bench();
        using var bus = VirtualAdapterFixture.Open(bench.Session, 1);
        var service = new FaultingService(new CanBusService(bus));
        using var slave = new CanOpenNode(service, 5, new CanOpenNodeOptions
        {
            Lss = new LssSlaveOptions
            {
                BitRateSwitch = (_, _) =>
                {
                    Interlocked.Increment(ref switches);
                    return Task.CompletedTask;
                },
            },
            SdoTimeout = TimeSpan.FromMilliseconds(250),
        }, ownsService: true);
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        using var heard = bench.Listen();
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        var held = slave.SendNmtCommandAsync(NmtCommand.Start, 0);
        service.FaultNextNmt(cancel);
        slave.StartHeartbeatProducer(TimeSpan.FromMilliseconds(20));
        await Task.Delay(200);
        slave.StopHeartbeatProducer();
        held.IsCompleted.Should().BeFalse();
        await activate;
        var act = async () => await held;
        if (cancel) await act.Should().ThrowAsync<OperationCanceledException>();
        else await act.Should().ThrowAsync<TimeoutException>();
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.Heartbeat(5)));
    }

    [Fact]
    public async Task The_Switch_Silence_Drops_An_Sdo_Upload_From_The_Slave()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        using var heard = bench.Listen();
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        using var cancel = new CancellationTokenSource();
        var upload = slave.SdoUploadAsync(2, 0x1000, 0x00, cancel.Token);
        await Task.Delay(40);
        heard.Ids.Should().NotContain(CanOpenCobId.SdoRx(2));
        cancel.Cancel();
        var act = async () => await upload;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await activate;
    }

    [Fact]
    public void A_Superseded_Switch_Delay_Does_Not_Start_Silence()
    {
        using var bench = new Bench();
        using var slave = (CanOpenNode)bench.Slave(1, 5);
        slave.BeginLssSilence(generation: -1, TimeSpan.FromMilliseconds(10));
        slave.CompleteLssSilence(generation: -1);
        slave.LssState().Should().Be(LssState.Waiting);
        slave.Dispose();
        slave.BeginLssSilence(generation: 0, TimeSpan.FromMilliseconds(10));
        slave.CompleteLssSilence(generation: -1);
    }

    [Fact]
    public async Task A_Store_During_The_Second_Delay_Is_Answered_When_The_Slave_May_Transmit()
    {
        var store = new MemoryStore();
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            Store = store,
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x12)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();

        using var heard = bench.Listen();
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        using (var poke = VirtualAdapterFixture.Open(bench.Session, 9))
            Transmit(poke, LssFrames.Command(LssCommand.StoreConfiguration));
        await Task.Delay(40);
        heard.Ids.Should().NotContain(CanOpenCobId.LssSlaveMessage);
        store.NodeId.Should().Be(0x12);
        await activate;
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.LssSlaveMessage));
    }

    [Fact]
    public async Task The_Second_Delay_Suppresses_A_Node_Guarding_Rtr()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        slave.StartNodeGuardingConsumer(2, TimeSpan.FromMilliseconds(30), 2);

        using var heard = bench.Listen();
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.Heartbeat(2)));
        // The second delay is the silence. A poll that passed the gate before it started can
        // still be on its way to the wire, so the quiet window begins only after that drain.
        var activate = bench.Master.ActivateBitTimingAsync(800, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        await Task.Delay(400);
        int duringSilence = heard.Ids.Count;
        await Task.Delay(200);
        heard.Ids.Skip(duringSilence).Should().NotContain(CanOpenCobId.Heartbeat(2));
        await activate;
        await WaitUntilAsync(() => heard.Ids.Skip(duringSilence).Contains(CanOpenCobId.Heartbeat(2)));
    }

    [Fact]
    public async Task Predefined_Cob_Ids_Follow_The_New_Node_Id_And_A_Custom_One_Does_Not()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions(), srdoCount: 1);
        var od = slave.ObjectDictionary;
        od.WriteUnsigned(0x1014, 0x00, CanOpenCobId.InvalidBit | 0x090);
        uint customTpdo = (od.ReadUnsigned(0x1801, 0x01) & ~CanOpenCobId.CanIdMask) | 0x289;
        od.WriteUnsigned(0x1801, 0x01, customTpdo);
        slave.StoreParameters();
        _ = slave.State;
        uint tpdo1Before = od.ReadUnsigned(0x1800, 0x01);
        (tpdo1Before & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.TpdoDefault(5, 1));

        var master = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();
        await master.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x20);
        _ = slave.State;

        od.ReadUnsigned(0x1014, 0x00).Should().Be(CanOpenCobId.InvalidBit | 0x090);
        od.ReadUnsigned(0x1801, 0x01).Should().Be(customTpdo);
        uint tpdo1 = od.ReadUnsigned(0x1800, 0x01);
        (tpdo1 & ~CanOpenCobId.CanIdMask).Should().Be(tpdo1Before & ~CanOpenCobId.CanIdMask);
        (tpdo1 & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.TpdoDefault(0x20, 1));
        od.ReadUnsigned(0x1200, 0x01).Should().Be(CanOpenCobId.SdoRx(0x20));
        od.ReadUnsigned(0x1200, 0x02).Should().Be(CanOpenCobId.SdoTx(0x20));
        od.ReadUnsigned(0x1F90, 0x06).Should().Be((uint)(4000 + 10 * 0x20));
        od.ReadUnsigned(0x1301, 0x05).Should().Be(CanOpenCobId.SrdoDefaultCobId1(0x20));
        od.ReadUnsigned(0x1301, 0x06).Should().Be(CanOpenCobId.SrdoDefaultCobId2(0x20));
    }

    [Fact]
    public async Task Srdo_Defaults_Appear_When_An_Unconfigured_Slave_Gets_A_Node_Id_And_Clear_Above_64()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId, srdoCount: 1);
        var od = slave.ObjectDictionary;
        od.ReadUnsigned(0x1301, 0x05).Should().Be(0);
        od.ReadUnsigned(0x1301, 0x06).Should().Be(0);

        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(5)).Succeeded.Should().BeTrue();
        await WaitUntilAsync(() => slave.NodeId == 5);
        od.ReadUnsigned(0x1301, 0x05).Should().Be(CanOpenCobId.SrdoDefaultCobId1(5));
        od.ReadUnsigned(0x1301, 0x06).Should().Be(CanOpenCobId.SrdoDefaultCobId2(5));

        var configured = bench.Slave(2, 5, new LssSlaveOptions(), srdoCount: 1);
        var peer = bench.Slave(3, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, configured);
        (await bench.Master.ConfigureNodeIdAsync(70)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => configured.NodeId == 70
            && configured.ObjectDictionary.ReadUnsigned(0x1301, 0x05) == 0);
        configured.ObjectDictionary.ReadUnsigned(0x1301, 0x06).Should().Be(0);
    }

    [Fact]
    public async Task A_Committed_Srdo_Stays_Valid_When_The_Node_Id_Stays_In_The_Predefined_Range()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions(), srdoCount: 1);
        var od = slave.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0x1234);
        od.AddU8(0x2001, 0x00, 0x5A);
        var safety = slave.Safety();
        safety.ConfigureSrdoProducer(1,
            new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8),
            TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        slave.StoreParameters();

        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        using var heard = bench.Listen();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x20 && slave.State == NmtState.PreOperational);

        od.ReadUnsigned(0x1301, 0x05).Should().Be(CanOpenCobId.SrdoDefaultCobId1(0x20));
        od.ReadUnsigned(0x1301, 0x06).Should().Be(CanOpenCobId.SrdoDefaultCobId2(0x20));
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.TryReadCommunication(od, 1, out var parameter).Should().BeTrue();
        ushort crc = SrdoCrc.Compute(parameter, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, 1)));
        od.ReadUnsigned(0x13FF, 1).Should().Be(crc);

        await peer.SendNmtCommandAsync(NmtCommand.Start, 0x20);
        await WaitUntilAsync(() => slave.State == NmtState.Operational);
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.SrdoDefaultCobId1(0x20)));
    }

    [Fact]
    public async Task A_Committed_Srdo_Loses_Its_Signature_When_The_Node_Id_Leaves_1_To_64()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions(), srdoCount: 1);
        var od = slave.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0x1234);
        od.AddU8(0x2001, 0x00, 0x5A);
        slave.Safety().ConfigureSrdoProducer(1,
            new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8),
            TimeSpan.FromMilliseconds(25));
        slave.Safety().CommitSafetyConfiguration();
        slave.StoreParameters();

        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(70)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 70 && slave.State == NmtState.PreOperational);
        od.ReadUnsigned(0x1301, 0x05).Should().Be(0);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0);
    }

    [Fact]
    public async Task An_Inactive_Srdo_Keeps_Its_Signature_When_The_Node_Id_Changes()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions(), srdoCount: 1);
        var od = slave.ObjectDictionary;
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        slave.StoreParameters();
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x20);
        od.ReadUnsigned(0x1301, 0x05).Should().Be(CanOpenCobId.SrdoDefaultCobId1(0x20));
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
    }

    [Fact]
    public async Task A_Custom_Flying_Master_Slot_Is_Not_Rewritten()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        // Smaller than the priority time divided by 127, so the write is accepted, and not the
        // pre-defined slot, so a node-id change has to leave it.
        slave.ObjectDictionary.WriteUnsigned(0x1F90, 0x06, 11);
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x15)).Succeeded.Should().BeTrue();
        await WaitUntilAsync(() => slave.NodeId == 0x15);
        slave.ObjectDictionary.ReadUnsigned(0x1F90, 0x06).Should().Be(11u);
    }

    [Fact]
    public void Storing_Parameters_Without_Lss_Leaves_The_Layer_Node_Id_Unset()
    {
        using var bench = new Bench();
        var node = bench.Slave(1, 5, new LssSlaveOptions { Enabled = false });
        node.StoreParameters();
        node.NodeId.Should().Be(5);
    }

    [Fact]
    public void A_Missing_Cob_Id_Is_Left_Alone()
    {
        using var bench = new Bench();
        var slave = (CanOpenNode)bench.Slave(1, 5);
        slave.RetargetCobId(0x2000, 0x01, 0x180, 0x181).Should().BeFalse();
    }

    [Fact]
    public async Task An_Unconfigured_Flying_Master_Claims_After_It_Receives_A_Node_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        slave.StartFlyingMaster(0, TimeSpan.FromMilliseconds(100));
        using var heard = bench.Listen();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x15)).Succeeded.Should().BeTrue();
        await WaitUntilAsync(() => slave.NodeId == 0x15 && heard.Ids.Contains(CanOpenCobId.FlyingMasterDetect));
    }

    [Fact]
    public async Task A_Device_Description_Turns_Lss_On_And_Restricts_Table_0()
    {
        var supported = CanOpenDeviceDescription.ParseEds(Eds(lss: true, baud250: true, baud500: true));
        using var bench = new Bench();
        var slave = bench.Described(1, 7, supported);
        slave.LssState().Should().Be(LssState.Waiting);
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.TransceiveManufacturerAsync(0x80, new byte[] { 1 })).Should().BeNull();
        await bench.Master.ActivateBitTimingAsync(0);
        (await bench.Master.ConfigureBitTimingAsync(0, 2)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.ConfigureBitTimingAsync(0x80, 1)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.StoreConfigurationAsync()).ErrorCode.Should().Be(LssCommand.StoreNotSupported);

        var silent = CanOpenDeviceDescription.ParseEds(Eds(lss: false, baud250: false, baud500: true));
        var off = bench.Described(2, 8, silent, lss: null);
        off.LssState().Should().Be(LssState.Waiting);
        var missed = async () => await bench.Master.SwitchStateSelectiveAsync(new LssAddress(0, 0, 0, 0));
        (await missed()).Should().BeFalse();

        var forcedOff = bench.Described(3, 9, supported, lss: new LssSlaveOptions { Enabled = false });
        await bench.Master.SwitchStateGlobalAsync(LssState.Configuration);
        await Task.Delay(30);
        forcedOff.LssState().Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task Manufacturer_Commands_Are_Answered_Only_In_Configuration()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            Manufacturer = (cs, parameters) =>
            {
                var response = new byte[8];
                response[0] = cs;
                response[1] = parameters.Span[0];
                return response;
            },
        });
        (await bench.Master.TransceiveManufacturerAsync(0x80, new byte[] { 0x3C })).Should().BeNull();
        await EnterConfiguration(bench.Master, slave);
        var answer = await bench.Master.TransceiveManufacturerAsync(0x80, new byte[] { 0x3C });
        answer.Should().NotBeNull();
        answer![0].Should().Be(0x80);
        answer[1].Should().Be(0x3C);
    }

    [Fact]
    public async Task Commands_Outside_Configuration_And_A_Missing_Handler_Draw_No_Reply()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        var waiting = async () => await bench.Master.StoreConfigurationAsync();
        await waiting.Should().ThrowAsync<LssTimeoutException>();
        var inquire = async () => await bench.Master.InquireVendorIdAsync();
        await inquire.Should().ThrowAsync<LssTimeoutException>();
        await bench.Master.ActivateBitTimingAsync(0);

        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.InquireNodeIdAsync()).Should().Be(CanOpenCobId.UnconfiguredNodeId);
        (await bench.Master.InquireSerialNumberAsync()).Should().Be(0);
        (await bench.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        slave.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        (await bench.Master.TransceiveManufacturerAsync(0x80, new byte[] { 1 })).Should().BeNull();
        using (var poke = VirtualAdapterFixture.Open(bench.Session, 9))
            Transmit(poke, new byte[] { 0x70, 0, 0, 0, 0, 0, 0, 0 });
        await Task.Delay(20);
        slave.LssState().Should().Be(LssState.Configuration);
        await bench.Master.ActivateBitTimingAsync(0);
    }

    [Fact]
    public async Task An_Empty_Manufacturer_Reply_Is_Not_A_Response()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            Manufacturer = (_, _) => Array.Empty<byte>(),
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.TransceiveManufacturerAsync(0x80, new byte[] { 1 })).Should().BeNull();

        var silent = bench.Slave(2, 6, new LssSlaveOptions { Manufacturer = (_, _) => null });
        await EnterConfiguration(bench.Master, silent);
        (await bench.Master.TransceiveManufacturerAsync(0x81, Array.Empty<byte>())).Should().BeNull();
    }

    [Fact]
    public async Task Activate_Does_Not_Call_The_Hook_Without_A_Rate()
    {
        int calls = 0;
        using var bench = new Bench();
        var plain = bench.Slave(1, 5);
        await EnterConfiguration(bench.Master, plain);
        await bench.Master.ActivateBitTimingAsync(0);
        (await bench.Master.ConfigureBitTimingAsync(0x80, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);

        var stored = new MemoryStore { Has = true, NodeId = 6, Selector = 0x80, Index = 0 };
        var slave = bench.Slave(2, 6, new LssSlaveOptions
        {
            Store = stored,
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        await bench.Master.ActivateBitTimingAsync(0);
        await Task.Delay(40);
        calls.Should().Be(0);

        var rates = new Dictionary<(byte Selector, byte Index), int> { [(0x80, 2)] = 125_000 };
        int seen = 0;
        using var alone = new Bench();
        var rated = alone.Slave(1, 7, new LssSlaveOptions
        {
            ManufacturerRates = rates,
            BitRateSwitch = (rate, _) =>
            {
                seen = rate;
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(alone.Master, rated);
        (await alone.Master.ConfigureBitTimingAsync(0x80, 2)).Succeeded.Should().BeTrue();
        await alone.Master.ActivateBitTimingAsync(0);
        await WaitUntilAsync(() => seen == 125_000);
        rates.Clear();
        await alone.Master.ActivateBitTimingAsync(0);
        await Task.Delay(40);
        seen.Should().Be(125_000);

        (await alone.Master.ConfigureBitTimingAsync(0x81, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
    }

    [Fact]
    public async Task A_Second_Activate_During_The_Silence_Drops_The_First()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        var first = bench.Master.ActivateBitTimingAsync(300);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        await bench.Master.ActivateBitTimingAsync(0);
        await first;
        await Task.Delay(80);
        Volatile.Read(ref switches).Should().Be(2);
        slave.LssState().Should().Be(LssState.Configuration);
    }

    [Fact]
    public async Task Identify_And_Fastscan_Ignore_A_Step_That_Is_Out_Of_Order()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, Address);
        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteProduct, Address.ProductCode));
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteVendor, Address.VendorId));
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteProduct, Address.ProductCode + 1));
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionLow, Address.RevisionNumber));
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteRevisionHigh, Address.RevisionNumber));
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteSerialLow, Address.SerialNumber));
        Transmit(poke, LssFrames.Fastscan(0, 32, 0, 0));
        Transmit(poke, LssFrames.Fastscan(0, 0, 1, 0));
        Transmit(poke, LssFrames.Fastscan(0, LssCommand.FastscanReset, 0, 0));
        Transmit(poke, LssFrames.Fastscan(Address.VendorId, 0, 0, 1));
        Transmit(poke, LssFrames.Fastscan(Address.ProductCode, 0, 1, 2));
        Transmit(poke, LssFrames.Fastscan(Address.RevisionNumber, 0, 2, 3));
        Transmit(poke, LssFrames.Fastscan(Address.SerialNumber, 0, 3, 4));
        await WaitUntilAsync(() => slave.LssState() == LssState.Configuration);
        await bench.Master.SwitchStateGlobalAsync(LssState.Waiting);
        await WaitUntilAsync(() => slave.LssState() == LssState.Waiting);

        (await bench.Master.IdentifyRemoteSlavesAsync(new LssIdentityRange(
            Address.VendorId, Address.ProductCode, Address.RevisionNumber, Address.RevisionNumber,
            Address.SerialNumber, Address.SerialNumber))).Should().BeTrue();
        var found = await bench.Master.FastscanAsync(new LssFastscanRequest
        {
            VendorId = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.VendorId },
            ProductCode = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.ProductCode },
            RevisionNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.RevisionNumber },
            SerialNumber = new LssFastscanField { Mode = LssFastscanFieldMode.Match, Value = Address.SerialNumber },
        });
        found.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_Switch_Silence_Holds_A_Flying_Master_Reset_Until_The_Slave_May_Transmit()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        var reset = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        slave.ApplicationReset += (_, _) => reset.TrySetResult(true);
        using var heard = bench.Listen();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        var activate = bench.Master.ActivateBitTimingAsync(800);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);

        slave.ObjectDictionary.WriteUnsigned(0x1F90, 0x01, 80);
        slave.ObjectDictionary.WriteUnsigned(0x1F90, 0x02, 20);
        slave.StartFlyingMaster(0, TimeSpan.FromMilliseconds(200));
        int atSilence = heard.Ids.Count;
        await Task.Delay(250);
        reset.Task.IsCompleted.Should().BeFalse();
        heard.Ids.Skip(atSilence).Should().NotContain(CanOpenCobId.NmtCommand);

        await activate;
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(3));
        heard.Ids.Skip(atSilence).Should().Contain(CanOpenCobId.NmtCommand);
        await WaitUntilAsync(() => heard.Ids.Skip(atSilence).Contains(CanOpenCobId.Heartbeat(5)));
    }

    [Fact]
    public async Task The_Switch_Silence_Sends_A_Heartbeat_After_The_Delay()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        slave.StartHeartbeatProducer(TimeSpan.FromMilliseconds(40));
        using var heard = bench.Listen();
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.Heartbeat(5)));
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        var activate = bench.Master.ActivateBitTimingAsync(800);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        await Task.Delay(80);
        int duringSilence = heard.Ids.Count;
        await Task.Delay(160);
        heard.Ids.Skip(duringSilence).Should().NotContain(CanOpenCobId.Heartbeat(5));
        await activate;
        await WaitUntilAsync(() => heard.Ids.Skip(duringSilence).Contains(CanOpenCobId.Heartbeat(5)));
    }

    [Fact]
    public async Task The_Switch_Silence_Drops_A_Block_Upload()
    {
        int switches = 0;
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) =>
            {
                Interlocked.Increment(ref switches);
                return Task.CompletedTask;
            },
        });
        using var heard = bench.Listen();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        var activate = bench.Master.ActivateBitTimingAsync(400);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        int duringSilence = heard.Ids.Count;
        using (var poke = VirtualAdapterFixture.Open(bench.Session, 9))
        {
            TransmitCob(poke, CanOpenCobId.SdoRx(5), SdoBlockFrames.BuildBlockUploadInit(
                0x1018, 0x01, clientCrcSupported: false, blockSize: 4, pst: 0));
            TransmitCob(poke, CanOpenCobId.SdoRx(5), new byte[] { SdoBlockFrames.CcsBlockUploadStart, 0, 0, 0, 0, 0, 0, 0 });
            await Task.Delay(80);
        }

        heard.Ids.Skip(duringSilence).Should().NotContain(CanOpenCobId.SdoTx(5));
        await activate;
    }

    [Fact]
    public async Task Identify_Remote_Slave_Matches_An_Inclusive_Range_In_Either_State()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5);
        SetIdentity(slave, Address);
        var hit = new LssIdentityRange(Address.VendorId, Address.ProductCode,
            Address.RevisionNumber - 1, Address.RevisionNumber,
            Address.SerialNumber, Address.SerialNumber + 1);
        (await bench.Master.IdentifyRemoteSlavesAsync(hit)).Should().BeTrue();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.IdentifyRemoteSlavesAsync(hit)).Should().BeTrue();
        var miss = new LssIdentityRange(Address.VendorId, Address.ProductCode,
            Address.RevisionNumber, Address.RevisionNumber,
            Address.SerialNumber + 1, Address.SerialNumber + 2);
        (await bench.Master.IdentifyRemoteSlavesAsync(miss)).Should().BeFalse();
    }

    [Fact]
    public void A_Stored_Node_Id_Outside_The_Layer_Setting_Range_Is_Rejected()
    {
        using var bench = new Bench();
        var store = new MemoryStore { Has = true, NodeId = 0 };
        var open = () => bench.Slave(1, 5, new LssSlaveOptions { Store = store });
        open.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("nodeId");
    }

    [Fact]
    public async Task Rejected_Commands_And_A_Zero_Switch_Delay_Stay_Inside_The_Rules()
    {
        int rate = 0;
        Exception? background = null;
        using var bench = new Bench();
        var rates = new Dictionary<(byte, byte), int>
        {
            [(0x80, 1)] = 125_000,
            [(0x80, 3)] = 10_000,
        };
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            ManufacturerRates = rates,
            BitRateSwitch = (bitsPerSecond, _) =>
            {
                rate = bitsPerSecond;
                if (bitsPerSecond == 10_000) throw new InvalidOperationException("rate hook");
                return Task.CompletedTask;
            },
            Manufacturer = (cs, parameters) =>
            {
                if (parameters.Span[0] == 1) return new byte[] { cs, 0x11 };
                if (parameters.Span[0] == 2)
                {
                    var longReply = new byte[12];
                    longReply[0] = cs;
                    return longReply;
                }
                return null;
            },
        });
        slave.BackgroundExceptionOccurred += (_, ex) => background = ex;
        SetIdentity(slave, Address);
        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        using var heard = bench.Listen();

        Transmit(poke, LssFrames.SwitchGlobal(LssState.Waiting));
        var ignored = new byte[LssFrames.Length];
        ignored[0] = LssCommand.SwitchStateGlobal;
        ignored[1] = 0x02;
        Transmit(poke, ignored);
        Transmit(poke, new byte[] { LssCommand.ConfigureNodeId, 0x11 });
        Transmit(poke, LssFrames.WithU32(LssCommand.IdentifyRemoteSerialHigh, Address.SerialNumber));
        Transmit(poke, LssFrames.ConfigureBitTiming(0, 2));
        Transmit(poke, LssFrames.ActivateBitTiming(0));
        poke.Transmit(CanFrame.Classic(
            unchecked((int)CanOpenCobId.LssMasterMessage), ReadOnlyMemory<byte>.Empty, isRemoteFrame: true));
        await Task.Delay(40);
        slave.LssState().Should().Be(LssState.Waiting);
        heard.Ids.Should().NotContain(CanOpenCobId.LssSlaveMessage);

        await EnterConfiguration(bench.Master, slave);
        Transmit(poke, LssFrames.WithU32(LssCommand.SwitchSelectiveVendor, Address.VendorId));
        await Task.Delay(30);
        slave.LssState().Should().Be(LssState.Configuration);
        (await bench.Master.ConfigureBitTimingAsync(0x80, 1)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0x80, 2)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        (await bench.Master.ConfigureBitTimingAsync(0x81, 1)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
        await bench.Master.ActivateBitTimingAsync(0, 125_000);
        await WaitUntilAsync(() => rate == 125_000);

        (await bench.Master.TransceiveManufacturerAsync(0x90, new byte[] { 1 }))![1].Should().Be(0x11);
        var padded = await bench.Master.TransceiveManufacturerAsync(0x90, new byte[] { 2 });
        padded.Should().NotBeNull();
        padded!.Length.Should().Be(LssFrames.Length);

        (await bench.Master.ConfigureBitTimingAsync(0x80, 3)).Succeeded.Should().BeTrue();
        await bench.Master.ActivateBitTimingAsync(0, null);
        await WaitUntilAsync(() => background is InvalidOperationException);
    }

    [Fact]
    public async Task A_Slave_That_Does_Not_Support_The_Service_Stays_Silent()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 6, new LssSlaveOptions
        {
            NodeIdConfigurationSupported = false,
            BitTimingSupported = false,
        });
        await EnterConfiguration(bench.Master, slave);
        var nodeId = async () => await bench.Master.ConfigureNodeIdAsync(0x12);
        await nodeId.Should().ThrowAsync<LssTimeoutException>();
        var timing = async () => await bench.Master.ConfigureBitTimingAsync(0, 2);
        await timing.Should().ThrowAsync<LssTimeoutException>();
        await bench.Master.ActivateBitTimingAsync(0, null);
        slave.NodeId.Should().Be(6);
    }

    [Fact]
    public async Task Fastscan_Rejects_A_Bad_Position_And_Stays_Waiting_On_Vendor_LssNext_Zero()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        SetIdentity(slave, Address);
        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        using var heard = bench.Listen();
        Transmit(poke, LssFrames.Fastscan(0, LssCommand.FastscanReset, 0, 0));
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.LssSlaveMessage));
        int afterReset = heard.Ids.Count;
        Transmit(poke, LssFrames.Fastscan(Address.VendorId, 0, 0, 0));
        await WaitUntilAsync(() => heard.Ids.Count > afterReset);
        slave.LssState().Should().Be(LssState.Waiting);

        int before = heard.Ids.Count(id => id == CanOpenCobId.LssSlaveMessage);
        Transmit(poke, LssFrames.Fastscan(0, 0, 4, 0));
        Transmit(poke, LssFrames.Fastscan(0, 0, 0, 5));
        await Task.Delay(40);
        heard.Ids.Count(id => id == CanOpenCobId.LssSlaveMessage).Should().Be(before);
        slave.LssState().Should().Be(LssState.Waiting);
    }

    [Fact]
    public async Task An_Unconfigured_Slave_Ignores_Selective_Nmt_And_Stays_Quiet()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, CanOpenCobId.UnconfiguredNodeId);
        var seen = false;
        slave.ApplicationReset += (_, _) => seen = slave.LssState() == LssState.Waiting;
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        using var heard = bench.Listen();
        slave.StartHeartbeatProducer(TimeSpan.FromMilliseconds(50));
        await peer.SendNmtCommandAsync(NmtCommand.Start, 0);
        await peer.SendNmtCommandAsync(NmtCommand.Stop, 0);
        await peer.SendNmtCommandAsync(NmtCommand.EnterPreOperational, 0);
        await peer.SendNmtCommandAsync(NmtCommand.Start, 5);
        await Task.Delay(80);
        slave.State.Should().Be(NmtState.Initializing);
        slave.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
        heard.Ids.Should().NotContain(CanOpenCobId.Heartbeat(CanOpenCobId.UnconfiguredNodeId));
        var emcy = async () => await slave.SendEmcyAsync(0x1000, 0x01);
        await emcy.Should().NotThrowAsync();
        heard.Ids.Should().NotContain(CanOpenCobId.Emcy(CanOpenCobId.UnconfiguredNodeId));

        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 0);
        await Task.Delay(30);
        seen.Should().BeTrue();
        slave.NodeId.Should().Be(CanOpenCobId.UnconfiguredNodeId);
    }

    [Fact]
    public async Task A_Flying_Master_Stops_Claiming_When_Reset_Node_Clears_The_Node_Id()
    {
        using var bench = new Bench();
        var store = new MemoryStore();
        var slave = bench.Slave(1, 5, new LssSlaveOptions { Store = store });
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        slave.StartFlyingMaster(0, TimeSpan.FromMilliseconds(200));
        using var heard = bench.Listen();
        await WaitUntilAsync(() => heard.Ids.Contains(CanOpenCobId.FlyingMasterDetect));

        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        (await bench.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
        int before = heard.Ids.Count;
        await peer.SendNmtCommandAsync(NmtCommand.ResetNode, 5);
        await WaitUntilAsync(() => slave.NodeId == CanOpenCobId.UnconfiguredNodeId
            && slave.State == NmtState.Initializing);
        await Task.Delay(80);
        heard.Ids.Skip(before).Should().NotContain(CanOpenCobId.FlyingMasterDetect);
    }

    [Fact]
    public async Task Store_And_Restore_Keep_One_Node_Id_On_Every_Predefined_Cob_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions(), srdoCount: 1);
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        slave.ObjectDictionary.WriteUnsigned(0x1F90, 0x06, 1);
        slave.StoreParameters();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x20);
        slave.ObjectDictionary.ReadUnsigned(0x1F90, 0x06).Should().Be(1);
        (slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.TpdoDefault(0x20, 1));
        slave.StoreParameters();

        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 0x20);
        await WaitUntilAsync(() => slave.State == NmtState.PreOperational && slave.NodeId == 0x20);
        (slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.TpdoDefault(0x20, 1));

        slave.RestoreDefaultParameters();
        await peer.SendNmtCommandAsync(NmtCommand.ResetNode, 0x20);
        await WaitUntilAsync(() => slave.NodeId == 5 && slave.State == NmtState.PreOperational);
        (slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.TpdoDefault(5, 1));
    }

    [Fact]
    public async Task Reset_Publishes_The_Pending_Node_Id_Before_The_Application_Hook()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions());
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        slave.StoreParameters();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x20)).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 5);
        await WaitUntilAsync(() => slave.NodeId == 0x20 && slave.State == NmtState.PreOperational);

        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(0x30)).Succeeded.Should().BeTrue();
        byte seenId = 0;
        uint seenTpdo = 0;
        uint seenEmcy = 0;
        slave.ApplicationReset += (_, _) =>
        {
            seenId = slave.NodeId;
            seenTpdo = slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01);
            seenEmcy = slave.ObjectDictionary.ReadUnsigned(0x1014, 0x00);
            // The save must record the COB-IDs together with the node-id they were retargeted to.
            slave.StoreParameters();
        };

        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 0x20);
        await WaitUntilAsync(() => slave.NodeId == 0x30 && slave.State == NmtState.PreOperational);
        seenId.Should().Be(0x30);
        (seenTpdo & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.TpdoDefault(0x30, 1));
        (seenEmcy & CanOpenCobId.CanIdMask).Should().Be(CanOpenCobId.Emcy(0x30));
        (slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.TpdoDefault(0x30, 1));

        await peer.SendNmtCommandAsync(NmtCommand.ResetCommunication, 0x30);
        await WaitUntilAsync(() => slave.NodeId == 0x30 && slave.State == NmtState.PreOperational);
        (slave.ObjectDictionary.ReadUnsigned(0x1800, 0x01) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.TpdoDefault(0x30, 1));
        (slave.ObjectDictionary.ReadUnsigned(0x1014, 0x00) & CanOpenCobId.CanIdMask)
            .Should().Be(CanOpenCobId.Emcy(0x30));
    }

    [Fact]
    public async Task An_Unconfigured_Reset_Does_Not_Hide_A_Peer_On_The_Freed_Node_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions { Store = new MemoryStore() });
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        slave.EmcyReceived += (_, _) => seen.TrySetResult(true);
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        (await bench.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetNode, 5);
        await WaitUntilAsync(() => slave.NodeId == CanOpenCobId.UnconfiguredNodeId
            && slave.State == NmtState.Initializing);

        using var poke = VirtualAdapterFixture.Open(bench.Session, 9);
        using var frame = CanFrame.Classic(
            unchecked((int)CanOpenCobId.Emcy(5)),
            new byte[] { 0x00, 0x10, 0x01, 0, 0, 0, 0, 0 },
            isExtendedFrame: false);
        poke.Transmit(frame);
        var arrived = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        arrived.Should().Be(seen.Task);
    }

    [Fact]
    public async Task Assigning_The_Same_Node_Id_Again_Sends_Emcy_On_That_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions { Store = new MemoryStore() });
        var peer = bench.Slave(2, 2, new LssSlaveOptions { Enabled = false });
        using var heard = bench.Listen();
        await EnterConfiguration(bench.Master, slave);
        (await bench.Master.ConfigureNodeIdAsync(CanOpenCobId.UnconfiguredNodeId)).Succeeded.Should().BeTrue();
        (await bench.Master.StoreConfigurationAsync()).Succeeded.Should().BeTrue();
        await peer.SendNmtCommandAsync(NmtCommand.ResetNode, 5);
        await WaitUntilAsync(() => slave.NodeId == CanOpenCobId.UnconfiguredNodeId
            && slave.State == NmtState.Initializing);

        (await bench.Master.ConfigureNodeIdAsync(5)).Succeeded.Should().BeTrue();
        await WaitUntilAsync(() => slave.NodeId == 5 && slave.State == NmtState.PreOperational);

        int before = heard.Ids.Count;
        await slave.SendEmcyAsync(0x1000, 0x01);
        await WaitUntilAsync(() => heard.Ids.Count > before);
        var sent = heard.Ids.Skip(before).ToArray();
        sent.Should().NotContain(CanOpenCobId.NmtCommand);
        sent.Should().Contain(CanOpenCobId.Emcy(5));
    }

    [Fact]
    public async Task Starting_The_Flying_Master_During_The_First_Switch_Delay_Keeps_The_Node_Id()
    {
        using var bench = new Bench();
        var slave = bench.Slave(1, 5, new LssSlaveOptions
        {
            BitRateSwitch = (_, _) => Task.CompletedTask,
        });
        await EnterConfiguration(bench.Master, slave);
        var activate = bench.Master.ActivateBitTimingAsync(200, 500_000);
        await Task.Delay(40);
        slave.StartFlyingMaster(0, TimeSpan.FromMilliseconds(200));
        await activate;
        slave.NodeId.Should().Be(5);
    }

    [Fact]
    public async Task Table_0_Follows_Each_Named_Baud_Flag()
    {
        var named = new (string Flag, byte Index)[]
        {
            ("BaudRate_1000", 0),
            ("BaudRate_800", 1),
            ("BaudRate_500", 2),
            ("BaudRate_250", 3),
            ("BaudRate_125", 4),
            ("BaudRate_50", 6),
            ("BaudRate_20", 7),
            ("BaudRate_10", 8),
        };
        byte nodeId = 10;
        foreach (var (flag, index) in named)
        {
            using var bench = new Bench();
            var description = CanOpenDeviceDescription.ParseEds(EdsWithBauds(true, flag));
            var slave = bench.Described(1, nodeId, description);
            await EnterConfiguration(bench.Master, slave);
            (await bench.Master.ConfigureBitTimingAsync(0, index)).Succeeded.Should().BeTrue();
            byte other = index == 0 ? (byte)8 : (byte)0;
            (await bench.Master.ConfigureBitTimingAsync(0, other)).ErrorCode
                .Should().Be(LssCommand.BitTimingNotSupported);
            nodeId++;
        }

        using var open = new Bench();
        var any = CanOpenDeviceDescription.ParseEds(EdsWithBauds(true));
        var wide = open.Described(1, 30, any);
        await EnterConfiguration(open.Master, wide);
        (await open.Master.ConfigureBitTimingAsync(0, 8)).Succeeded.Should().BeTrue();
        (await open.Master.ConfigureBitTimingAsync(0, LssBitTiming.AutomaticIndex)).ErrorCode
            .Should().Be(LssCommand.BitTimingNotSupported);
    }

    [Fact]
    public void LssState_Rejects_A_Null_Node_And_Reads_Waiting_For_A_Foreign_One()
    {
        var act = () => CanOpenLssNodeExtensions.LssState(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("node");
        new ForeignNode().LssState().Should().Be(LssState.Waiting);
    }

    private static string EdsWithBauds(bool lss, params string[] enabled)
    {
        var baud = new StringBuilder();
        foreach (var name in enabled)
            baud.Append(name).Append("=1\n");
        return "[FileInfo]\n" +
            "FileName=lss.eds\n" +
            "FileVersion=1\n" +
            "FileRevision=0\n" +
            "EDSVersion=4.0\n" +
            "Description=LSS slave\n" +
            "CreationTime=10:00AM\n" +
            "CreationDate=10-17-2026\n" +
            "CreatedBy=CanKit.Pro tests\n" +
            "[DeviceInfo]\n" +
            "VendorName=CanKit.Pro\n" +
            "VendorNumber=0\n" +
            "ProductName=LSS\n" +
            "ProductNumber=0\n" +
            "RevisionNumber=0\n" +
            baud +
            "SimpleBootUpSlave=1\n" +
            "Granularity=8\n" +
            "NrOfRXPDO=0\n" +
            "NrOfTXPDO=0\n" +
            "LSS_Supported=" + (lss ? "1" : "0") + "\n" +
            "[MandatoryObjects]\n" +
            "SupportedObjects=1\n" +
            "1=0x1000\n" +
            "[1000]\n" +
            "ParameterName=Device type\n" +
            "ObjectType=0x7\n" +
            "DataType=0x0007\n" +
            "AccessType=ro\n" +
            "DefaultValue=0\n" +
            "PDOMapping=0\n";
    }

    private static string Eds(bool lss, bool baud250, bool baud500) =>
        "[FileInfo]\n" +
        "FileName=lss.eds\n" +
        "FileVersion=1\n" +
        "FileRevision=0\n" +
        "EDSVersion=4.0\n" +
        "Description=LSS slave\n" +
        "CreationTime=10:00AM\n" +
        "CreationDate=10-17-2026\n" +
        "CreatedBy=CanKit.Pro tests\n" +
        "[DeviceInfo]\n" +
        "VendorName=CanKit.Pro\n" +
        "VendorNumber=0\n" +
        "ProductName=LSS\n" +
        "ProductNumber=0\n" +
        "RevisionNumber=0\n" +
        "BaudRate_250=" + (baud250 ? "1" : "0") + "\n" +
        "BaudRate_500=" + (baud500 ? "1" : "0") + "\n" +
        "SimpleBootUpSlave=1\n" +
        "Granularity=8\n" +
        "NrOfRXPDO=0\n" +
        "NrOfTXPDO=0\n" +
        "LSS_Supported=" + (lss ? "1" : "0") + "\n" +
        "[MandatoryObjects]\n" +
        "SupportedObjects=1\n" +
        "1=0x1000\n" +
        "[1000]\n" +
        "ParameterName=Device type\n" +
        "ObjectType=0x7\n" +
        "DataType=0x0007\n" +
        "AccessType=ro\n" +
        "DefaultValue=0\n" +
        "PDOMapping=0\n";

    private sealed class MemoryStore : ILssConfigurationStore
    {
        public bool Has;
        public byte NodeId;
        public byte Selector;
        public byte Index;
        public bool Fail;
        public byte Spec;

        public bool TryLoad(out byte nodeId, out byte tableSelector, out byte tableIndex)
        {
            nodeId = NodeId;
            tableSelector = Selector;
            tableIndex = Index;
            return Has;
        }

        public bool TryStore(byte nodeId, byte tableSelector, byte tableIndex, out byte specError)
        {
            if (Fail)
            {
                specError = Spec;
                return false;
            }
            Has = true;
            NodeId = nodeId;
            Selector = tableSelector;
            Index = tableIndex;
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
            Session = VirtualAdapterFixture.NewSession("lss-slave");
            var bus = Track(VirtualAdapterFixture.Open(Session, 0));
            Master = Track(CanOpenLss.OpenMaster(bus, Quick()));
        }

        public string Session { get; }

        public ILssMaster Master { get; }

        public ICanOpenNode Slave(int channel, byte nodeId, LssSlaveOptions? lss = null, int srdoCount = 0)
        {
            var bus = Track(VirtualAdapterFixture.Open(Session, channel));
            var options = new CanOpenNodeOptions
            {
                Lss = lss ?? new LssSlaveOptions(),
                SdoTimeout = TimeSpan.FromMilliseconds(250),
                SrdoCount = srdoCount,
            };
            return Track(CanOpen.OpenNode(bus, nodeId, options));
        }

        public ICanOpenNode Described(int channel, byte nodeId, CanOpenDeviceDescription description, LssSlaveOptions? lss = null)
        {
            var bus = Track(VirtualAdapterFixture.Open(Session, channel));
            var options = new CanOpenNodeOptions
            {
                Lss = lss,
                SdoTimeout = TimeSpan.FromMilliseconds(250),
            };
            return Track(CanOpen.OpenNode(bus, nodeId, description, options));
        }

        public Listener Listen()
        {
            var bus = Track(VirtualAdapterFixture.Open(Session, Interlocked.Increment(ref _nextChannel) + 20));
            return Track(new Listener(bus));
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

    /// <summary>The next NMT confirmation fails, so a held flush can be seen to finish that send.</summary>
    private sealed class FaultingService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private int _fault;

        public FaultingService(ICanBusService inner) => _inner = inner;

        public void FaultNextNmt(bool cancel) => Volatile.Write(ref _fault, cancel ? 2 : 1);

        public ICanBus Bus => _inner.Bus;

        public int SubscriptionCount => _inner.SubscriptionCount;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add => _inner.BackgroundExceptionOccurred += value;
            remove => _inner.BackgroundExceptionOccurred -= value;
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(predicate, bufferCapacity, includeEcho);

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => _inner.Subscribe(filter, bufferCapacity, includeEcho);

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
            => _inner.FindOverlappingFilterSubscriptions();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (frame.ID == 0)
            {
                int fault = Interlocked.Exchange(ref _fault, 0);
                if (fault == 1)
                    return Task.FromException<TxConfirmation>(new TimeoutException("driver rejected the frame"));
                if (fault == 2)
                    return Task.FromException<TxConfirmation>(new OperationCanceledException());
            }

            return _inner.SendConfirmedAsync(frame, timeout, cancellationToken);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class Listener : IDisposable
    {
        private readonly CanBusService _service;
        private readonly ISubscription _subscription;
        private readonly List<uint> _ids = new();

        public Listener(CanKit.Abstractions.API.Can.ICanBus bus)
        {
            _service = new CanBusService(bus);
            _subscription = _service.Subscribe(_ => true);
            _ = Task.Run(ReadAsync);
        }

        public IReadOnlyList<uint> Ids
        {
            get { lock (_ids) return _ids.ToArray(); }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _service.Dispose();
        }

        private async Task ReadAsync()
        {
            try
            {
                await foreach (var frame in _subscription.Frames.ConfigureAwait(false))
                {
                    lock (_ids) _ids.Add((uint)frame.Frame.ID);
                }
            }
            catch (ObjectDisposedException)
            {
                // The listener was closed.
            }
        }
    }

    /// <summary>An <see cref="ICanOpenNode"/> this library did not create.</summary>
    private sealed class ForeignNode : ICanOpenNode
    {
        public byte NodeId => 1;
        public CanOpenNodeOptions Options => throw new NotSupportedException();
        public ObjectDictionary ObjectDictionary => throw new NotSupportedException();
        public NmtState State => NmtState.PreOperational;
        public DeviceDescriptionReport? DeviceDescription => null;
        public FlyingMasterRole FlyingMasterRole => FlyingMasterRole.Inactive;
        public byte? ActiveFlyingMasterNodeId => null;
        public ushort? ActiveFlyingMasterPriority => null;
        public CanOpenDeviceDescription? GetPeerDeviceDescription(byte nodeId) => null;
        public void Dispose()
        {
        }

        public event EventHandler<HeartbeatReceivedEventArgs>? HeartbeatReceived { add { } remove { } }
        public event EventHandler<HeartbeatTimeoutEventArgs>? HeartbeatTimeout { add { } remove { } }
        public event EventHandler<EmcyReceivedEventArgs>? EmcyReceived { add { } remove { } }
        public event EventHandler<SyncReceivedEventArgs>? SyncReceived { add { } remove { } }
        public event EventHandler<RpdoReceivedEventArgs>? RpdoReceived { add { } remove { } }
        public event EventHandler<NmtCommandReceivedEventArgs>? NmtCommandReceived { add { } remove { } }
        public event EventHandler<NmtResetEventArgs>? ApplicationReset { add { } remove { } }
        public event EventHandler<NodeGuardingReceivedEventArgs>? NodeGuardingReceived { add { } remove { } }
        public event EventHandler<NodeGuardingTimeoutEventArgs>? NodeGuardingTimeout { add { } remove { } }
        public event EventHandler<LifeGuardingEventArgs>? LifeGuardingEvent { add { } remove { } }
        public event EventHandler<Exception>? BackgroundExceptionOccurred { add { } remove { } }
        public event EventHandler<FlyingMasterChangedEventArgs>? FlyingMasterChanged { add { } remove { } }

        public Task SendNmtCommandAsync(NmtCommand command, byte targetNodeId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void StartFlyingMaster(ushort priorityLevel, TimeSpan activeMasterHeartbeatTimeout) => throw new NotSupportedException();
        public void StopFlyingMaster() => throw new NotSupportedException();
        public void StartHeartbeatProducer(TimeSpan interval) => throw new NotSupportedException();
        public void StopHeartbeatProducer() => throw new NotSupportedException();
        public void AddHeartbeatConsumer(byte producerNodeId, TimeSpan timeout) => throw new NotSupportedException();
        public void RemoveHeartbeatConsumer(byte producerNodeId) => throw new NotSupportedException();
        public void StartSyncProducer(TimeSpan interval) => throw new NotSupportedException();
        public void StopSyncProducer() => throw new NotSupportedException();
        public Task SendSyncAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendEmcyAsync(ushort errorCode, byte errorRegister, ReadOnlyMemory<byte> manufacturerSpecific = default, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void BindPeerDeviceDescription(byte nodeId, CanOpenDeviceDescription description) => throw new NotSupportedException();
        public void UnbindPeerDeviceDescription(byte nodeId) => throw new NotSupportedException();
        public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex, SdoTransferMode mode = SdoTransferMode.Auto, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex, ReadOnlyMemory<byte> data, SdoTransferMode mode = SdoTransferMode.Auto, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void ConfigureTpdo(int pdoIndex, PdoMapping mapping, TpdoTransmission transmission = TpdoTransmission.EventDriven, uint? cobId = null, TimeSpan? eventTimerInterval = null, TimeSpan? inhibitTime = null)
            => throw new NotSupportedException();
        public void ConfigureRpdo(int pdoIndex, PdoMapping mapping, uint? cobId = null, RpdoTransmission transmission = RpdoTransmission.EventDriven)
            => throw new NotSupportedException();
        public Task TriggerTpdoAsync(int pdoIndex, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ForeignPdoObserveResult> ObserveForeignPdoAsync(byte peerNodeId, uint cobId, ReadOnlyMemory<byte> payload, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void StoreParameters() => throw new NotSupportedException();
        public void RestoreDefaultParameters() => throw new NotSupportedException();
        public void StartNodeGuardingConsumer(byte producerNodeId, TimeSpan guardTime, byte lifeTimeFactor) => throw new NotSupportedException();
        public void StopNodeGuardingConsumer(byte producerNodeId) => throw new NotSupportedException();
    }
}
