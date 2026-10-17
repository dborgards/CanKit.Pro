using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Nmt;
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
        FastscanTimeout = TimeSpan.FromMilliseconds(40),
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
    {
        using var frame = CanFrame.Classic(unchecked((int)CanOpenCobId.LssMasterMessage), payload, isExtendedFrame: false);
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

        (await bench.Master.FastscanAsync(request)).Address.SerialNumber.Should().Be(1);
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
        var activate = bench.Master.ActivateBitTimingAsync(200, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        Volatile.Read(ref rate).Should().Be(250_000);
        int before = heard.Ids.Count;
        await slave.SendNmtCommandAsync(NmtCommand.Start, 0);
        await Task.Delay(20);
        heard.Ids.Skip(before).Should().NotContain(CanOpenCobId.NmtCommand);
        await activate;
        await WaitUntilAsync(() =>
        {
            slave.SendNmtCommandAsync(NmtCommand.EnterPreOperational, 5).GetAwaiter().GetResult();
            return heard.Ids.Contains(CanOpenCobId.NmtCommand);
        });
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
        var activate = bench.Master.ActivateBitTimingAsync(150, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        using (var poke = VirtualAdapterFixture.Open(bench.Session, 9))
            Transmit(poke, LssFrames.Command(LssCommand.StoreConfiguration));
        await Task.Delay(20);
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
        var activate = bench.Master.ActivateBitTimingAsync(200, null);
        await WaitUntilAsync(() => Volatile.Read(ref switches) == 1);
        await Task.Delay(40);
        int duringSilence = heard.Ids.Count;
        await Task.Delay(90);
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
        await WaitUntilAsync(() => configured.NodeId == 70);
        configured.ObjectDictionary.ReadUnsigned(0x1301, 0x05).Should().Be(0);
        configured.ObjectDictionary.ReadUnsigned(0x1301, 0x06).Should().Be(0);
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
        (await bench.Master.ConfigureBitTimingAsync(0, 2)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0, 3)).Succeeded.Should().BeTrue();
        (await bench.Master.ConfigureBitTimingAsync(0, 0)).ErrorCode.Should().Be(LssCommand.BitTimingNotSupported);
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
}
