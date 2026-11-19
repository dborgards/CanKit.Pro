using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Cia401;
using CanKit.Pro.CANopen.Cia408;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Cia408;

/// <summary>
/// The one test per profile that the scope record asks for (<c>docs/reviews/2026-11-17-canopen-device-profiles-scope.md</c>,
/// step 4): a <see cref="Cia408Device"/> at one node and a <see cref="Cia408Peer"/> at another, both
/// built from <see cref="Cia408Objects"/>, on one virtual bus, found by a tool that knows CiA 401 and
/// CiA 408; and one node carrying a CiA 401 device at logical device 0 beside a CiA 408 device at
/// logical device 1. A contradiction between the two sides on what an object is, where it lives or
/// what it carries fails here, which neither side's own tests can show.
/// </summary>
/// <remarks>
/// <para>The tool is a real <see cref="CanOpenNode"/>, so every access passes its SDO client and peer
/// gate. The test writes no profile index: objects are reached through the peer's typed methods and
/// <see cref="Cia408Objects"/> members; the literals are node-ids, COB-IDs, control words and expected
/// values. Nothing is asserted against a wall clock: the device's process model steps on its manual
/// clock, and the peer's pauses run on the rig's virtual one.</para>
/// </remarks>
public class Cia408EndToEndTests
{
    private const byte DeviceId = CanOpenProfileRig.DeviceId;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>CiA 408 v2.2.0 §6.3, Table 1: input current too high.</summary>
    private const ushort InputCurrentTooHigh = 0x2110;

    /// <summary>The catalogue a tool that knows both profiles holds.</summary>
    private static CanOpenProfileCatalog Catalogue(Cia408ToolRig rig)
        => new(new Cia401Profile(), new Cia408Profile(rig.Options()));

    /// <summary>The tool reads the logical devices of the device and asks the catalogue for its peers.</summary>
    private static async Task<Cia408Peer> ConnectAsync(Cia408ToolRig rig)
    {
        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = Catalogue(rig).CreatePeers(rig.Tool, DeviceId, devices);
        return peers.Should().ContainSingle().Which.Should().BeOfType<Cia408Peer>().Which;
    }

    /// <summary>A plant that follows the setpoints at once while the valve is ACTIVE: the spool
    /// position the q setpoint asks for, the pressure the p setpoint asks for.</summary>
    private sealed class FollowingValve : ICia408Process
    {
        public void Step(Cia408ProcessContext context)
        {
            if (context.State != Cia408State.Active) return;
            context.SetActual(Cia408Objects.SpoolPositionActualValue, context.Setpoint(Cia408Objects.QSetpoint));
            context.SetActual(Cia408Objects.VprcActualValue, context.Setpoint(Cia408Objects.VprcSetpoint));
        }
    }

    // ---- one valve, one tool --------------------------------------------------------------------------

    [Fact]
    public async Task A_Tool_That_Knows_Both_Profiles_Finds_The_Pq_Valve()
    {
        using var rig = new Cia408ToolRig();

        var peer = await ConnectAsync(rig);

        peer.Profile.Should().BeOfType<Cia408Profile>();
        peer.LogicalDevice.DeviceType.Should().Be(Cia408ToolRig.FixtureConfiguration.DeviceType,
            "the device type the tool read is the one the device was configured to declare");
        peer.SupportedControlModes.Should().Equal(Cia408ToolRig.FixtureConfiguration.ControlModes);
        peer.Resolution.Should().Be(Cia408ToolRig.FixtureConfiguration.Resolution);
        (await peer.ReadControlModeAsync().WithTimeoutAsync(HangGuard)).Should().Be(Cia408ControlMode.PQControl);
    }

    [Fact]
    public async Task Enabled_Through_The_Peer_The_Valve_Follows_Setpoints_By_Rpdo_And_The_Tool_Names_What_It_Answers()
    {
        using var rig = new Cia408ToolRig(
            new[] { new Cia408Device(Cia408ToolRig.FixtureConfiguration, new FollowingValve()) }, Cia408ToolRig.FixtureDescription);
        var peer = await ConnectAsync(rig);
        await rig.Rig.StartAsync();

        await peer.EnableAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.State.Should().Be(Cia408State.Active);

        // RPDO 2 (the default of mapping 4, Table 32): the control word, the q setpoint, the p setpoint.
        await rig.Rig.SendRpdo2Async(CanOpenProfileRig.Words(0x0007, 1000, -500));
        rig.Rig.Advance(Cia408Configuration.DefaultProcessInterval);
        await rig.Rig.DrainAsync();

        var observed = await peer.ObserveAsync(CanOpenProfileRig.Tpdo2, rig.Rig.Payloads(CanOpenProfileRig.Tpdo2).Last(),
            rig.Description!).WithTimeoutAsync(HangGuard);
        observed.Result.Decoded.Should().BeTrue();
        observed.Values.Select(v => (v.Definition, v.Value)).Should().Equal(
            (Cia408Objects.DeviceStatusWord16, 0x000FL),
            (Cia408Objects.SpoolPositionActualValue, 1000L),
            (Cia408Objects.VprcActualValue, -500L));
        (await peer.ReadValueAsync(Cia408Objects.QSetpoint).WithTimeoutAsync(HangGuard)).Value.Should().Be(1000);
        (await peer.ReadValueAsync(Cia408Objects.VprcActualValue).WithTimeoutAsync(HangGuard)).Value.Should().Be(-500);
    }

    [Fact]
    public async Task A_Fault_The_Application_Reports_Is_Read_Reset_And_Left_Through_The_Peer()
    {
        using var rig = new Cia408ToolRig();
        var peer = await ConnectAsync(rig);
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Rig.Device.ReportFault(InputCurrentTooHigh, Cia408FaultState.FaultHold);
        rig.Rig.Settle();

        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).State.Should().Be(Cia408State.FaultHold);
        (await peer.ReadErrorCodeAsync().WithTimeoutAsync(HangGuard)).Should().Be(InputCurrentTooHigh);
        Func<Task> early = () => peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);
        (await early.Should().ThrowExactlyAsync<Cia408CommandException>()).Which.Reason
            .Should().Be(Cia408CommandFailure.NotReached, "the device refuses the reset while the cause lasts");

        rig.Rig.Device.ClearFaultCause(InputCurrentTooHigh);
        rig.Rig.Settle();
        await peer.ResetFaultAsync().WithTimeoutAsync(HangGuard);
        rig.Rig.Device.State.Should().Be(Cia408State.Hold, "D11 from FAULT_HOLD");
        await peer.EnableAsync().WithTimeoutAsync(HangGuard);

        rig.Rig.Device.State.Should().Be(Cia408State.Active);
        (await peer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).State.Should().Be(Cia408State.Active);
    }

    // ---- one node, two profiles -------------------------------------------------------------------------

    [Fact]
    public async Task A_Node_With_Cia401_At_Logical_Device_0_And_Cia408_At_1_Serves_Both()
    {
        var io = new Cia401Device(new Cia401Configuration { DigitalInputs = 8 });
        var valve = new Cia408Device(new Cia408Configuration
        {
            Resolution = Cia408Resolution.Valve16,
            ControlModes = new[] { Cia408ControlMode.PQControl },
            LogicalDevice = 1,
        });
        using var rig = new MixedRig(io, valve);

        // 1000h announces the chain: profile 401 of logical device 0 with FFFFh above it; each
        // device's own word is 67FFh + n·800h (CiA 301 v4.2.0 §7.5.2.1).
        rig.Od.ReadUnsigned(0x1000, 0x00).Should().Be(0xFFFF_0191u);
        rig.Od.ReadUnsigned(CanOpenLogicalDevices.DeviceTypeIndex(0), 0x00).Should().Be(0x0001_0191u);
        rig.Od.ReadUnsigned(CanOpenLogicalDevices.DeviceTypeIndex(1), 0x00).Should().Be(0x2200_0198u);

        // 1029h: CiA 401 asks for no layout and CiA 408 for its own, so the record is CiA 408's
        // (CanOpenApplicationRequirements.ErrorBehavior: only two applications asking for different
        // layouts conflict). Its communication error defaults to 01h, no change, for both devices.
        rig.Od.ReadRaw(0x1029, 0x00).Should().Equal(new byte[] { 0x02 });
        rig.Od.ReadRaw(0x1029, 0x01).Should().Equal(new byte[] { 0x01 });
        rig.Od.ReadRaw(0x1029, 0x02).Should().Equal(new byte[] { 0x01 });

        var devices = await CanOpenLogicalDevices.ReadAsync(rig.Tool, DeviceId).WithTimeoutAsync(HangGuard);
        var peers = new CanOpenProfileCatalog(new Cia401Profile(), new Cia408Profile(rig.Options))
            .CreatePeers(rig.Tool, DeviceId, devices);
        peers.Should().HaveCount(2);
        var ioPeer = peers[0].Should().BeOfType<Cia401Peer>().Which;
        var valvePeer = peers[1].Should().BeOfType<Cia408Peer>().Which;

        io.SetDigitalInputs(1, 0x5A);
        rig.Settle();
        (await ioPeer.DigitalInputs!.ReadAsync(1).WithTimeoutAsync(HangGuard)).Should().Be(0x5A);
        await valvePeer.EnableAsync().WithTimeoutAsync(HangGuard);
        valve.State.Should().Be(Cia408State.Active);
        (await valvePeer.ReadStatusAsync().WithTimeoutAsync(HangGuard)).State.Should().Be(Cia408State.Active);
    }

    /// <summary>One device node with a <see cref="Cia401Device"/> and a <see cref="Cia408Device"/>
    /// attached in that order, and a tool node with a description of the objects the test reaches.</summary>
    private sealed class MixedRig : CanOpenProfileRig, IDisposable
    {
        private readonly ICanBus _toolBus;

        internal MixedRig(Cia401Device io, Cia408Device valve)
            : base("cia401-408", Cia408Device.AddTo(Cia401Device.AddTo(new CanOpenNodeOptions(), io), valve), description: null)
        {
            _toolBus = VirtualAdapterFixture.Open(Session, 2);
            Tool = new CanOpenNode(new CanBusService(_toolBus), 0x01, new CanOpenNodeOptions(), ownsService: true, new ManualTimeSource());
            Tool.BindPeerDeviceDescription(DeviceId, Description());
        }

        internal CanOpenNode Tool { get; }

        /// <summary>Peer options whose pauses complete at once; the device answers within a read.</summary>
        internal Cia408PeerOptions Options { get; } = new() { Delay = (_, _) => Task.CompletedTask };

        // Re-implemented, so that a using of the rig closes the tool too before the device node.
        void IDisposable.Dispose()
        {
            Tool.Dispose();
            _toolBus.Dispose();
            Dispose();
        }

        private static CanOpenDeviceDescription Description()
        {
            var text = new StringBuilder();
            text.Append("[FileInfo]\nFileName=mixed.eds\nFileVersion=1\nFileRevision=0\nEDSVersion=4.0\n")
                .Append("Description=CiA 401 and CiA 408\nCreationTime=10:00AM\nCreationDate=11-19-2026\nCreatedBy=CanKit.Pro tests\n")
                .Append("[DeviceInfo]\nVendorName=CanKit.Pro\nVendorNumber=0\nProductName=Mixed\nProductNumber=0\nRevisionNumber=0\n")
                .Append("OrderCode=MIXED\nBaudRate_500=1\nSimpleBootUpMaster=0\nSimpleBootUpSlave=1\nGranularity=8\n")
                .Append("DynamicChannelsSupported=0\nGroupMessaging=0\nNrOfRXPDO=0\nNrOfTXPDO=0\nLSS_Supported=0\n")
                .Append("[MandatoryObjects]\nSupportedObjects=1\n1=0x1000\n")
                .Append("[1000]\nParameterName=Device type\nObjectType=0x7\nDataType=0x0007\nAccessType=ro\nDefaultValue=0xFFFF0191\nPDOMapping=0\n")
                .Append("[OptionalObjects]\nSupportedObjects=5\n1=0x6000\n2=0x67FF\n3=0x6840\n4=0x6841\n5=0x6FFF\n")
                .Append("[6000]\nParameterName=Read digital input 8-bit\nSubNumber=2\nObjectType=0x8\n")
                .Append("[6000sub0]\nParameterName=Highest sub-index supported\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=1\nPDOMapping=0\n")
                .Append("[6000sub1]\nParameterName=Inputs 1 to 8\nObjectType=0x7\nDataType=0x0005\nAccessType=ro\nDefaultValue=0\nPDOMapping=1\n");
            void Variable(ushort index, string type, string access)
                => text.Append(string.Format(CultureInfo.InvariantCulture,
                    "[{0:X4}]\nParameterName=V{0:X4}\nObjectType=0x7\nDataType={1}\nAccessType={2}\nDefaultValue=0\nPDOMapping=0\n",
                    index, type, access));

            Variable(0x67FF, "0x0007", "ro");
            Variable(0x6840, "0x0006", "rw");
            Variable(0x6841, "0x0006", "ro");
            Variable(0x6FFF, "0x0007", "ro");
            return CanOpenDeviceDescription.ParseEds(text.ToString());
        }
    }
}
