using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

/// <summary>
/// #379 (P-8), FR-CO-011: the error register <c>1001h</c> and the error reset across every EMCY
/// the node raises itself. CiA 301 §7.5.2.2 Table 54: bit 4 is "Communication error", and "The
/// generic error shall be signaled at any error situation" (bit 0). §7.2.7.1, Figure 37: an EMCY
/// once per error event, and "All errors are repaired. The CANopen device enters the error free
/// state and transmits an emergency object with the error code 'reset error / no error'".
/// </summary>
/// <remarks>
/// Every EMCY of the node leaves through one chain in the order the actor decided, and <c>1001h</c>
/// is written right before its frame, so the EMCY payloads on the wire are the sequence of
/// registers, and an EMCY that was wrongly sent (or wrongly not sent) shows up as the wrong next
/// payload: the next expected EMCY is the ordering witness. Frames are injected on the actor;
/// the deadline runs on a <see cref="ManualTimeSource"/>.
/// </remarks>
public partial class CanOpenPdoEngineTests
{
    private static readonly uint EmcyId = CanOpenCobId.Emcy(Device);
    private const ushort ErrorReset = 0x0000;
    private const byte Communication = 0x11; // bit 4 and the generic bit 0

    private static async Task ExpectEmcyAsync(Wire wire, int ordinal, ushort code, byte register)
    {
        await wire.WaitForCountAsync(EmcyId, ordinal);
        var p = wire.Payloads(EmcyId)[ordinal - 1];
        ((ushort)(p[0] | (p[1] << 8))).Should().Be(code, "EMCY {0} is the {1:X4}h", ordinal, code);
        p[2].Should().Be(register, "EMCY {0} carries the register {1:X2}h", ordinal, register);
    }

    private static async Task WaitForErrorRegisterAsync(ICanOpenNode node, byte expected)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (node.ObjectDictionary.ReadUnsigned(0x1001, 0) != expected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"1001h stayed {node.ObjectDictionary.ReadUnsigned(0x1001, 0):X2}h, expected {expected:X2}h.");
            await Task.Delay(5);
        }
    }

    private static ICanOpenNode OpenWithRpdo(string session, out Wire wire, out ManualTimeSource clock)
    {
        var bus = Open(session, 0);
        wire = new Wire(session, 2);
        clock = new ManualTimeSource();
        var node = OpenClocked(bus, Device, clock);
        node.ObjectDictionary.AddU16(0x2000, 0, 0);
        node.ObjectDictionary.AddU16(0x2001, 0, 0);
        node.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        return node;
    }

    // RPDO 8210h: the short frame sets bits 4 and 0, a second short frame is the same error
    // event, a frame that fits ends it with 0000h and an empty register.
    [Fact]
    public async Task Rpdo_Length_Error_Sets_The_Communication_Bit_Until_A_Frame_Fits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);

        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(Communication);
        await Inject(device, Rpdo1, 0x02);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x00);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0u);

        // Reconfiguring the RPDO ends the error as well.
        await Inject(device, Rpdo1, 0x03);
        await ExpectEmcyAsync(wire, 3, 0x8210, Communication);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0x00);
        wire.Count(EmcyId).Should().Be(4);
    }

    // RPDO 8250h: the deadline sets the bit, the next reception ends it.
    [Fact]
    public async Task Rpdo_Timeout_Sets_The_Communication_Bit_Until_The_Next_Reception()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out var clock);
        using var _w = wire;
        device.ObjectDictionary.WriteUnsigned(0x1400, 5, 100);
        await StartAsync(wire, device);

        await Inject(device, Rpdo1, 1, 0, 0, 0);
        Advance(clock, device, TimeSpan.FromMilliseconds(100));
        await ExpectEmcyAsync(wire, 1, 0x8250, Communication);
        await Inject(device, Rpdo1, 2, 0, 0, 0);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x00);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0u);
    }

    // SYNC 8240h: a SYNC of the wrong length sets the bit, one of the expected length ends it,
    // and so does a change of the SYNC configuration.
    [Fact]
    public async Task Sync_Length_Error_Sets_The_Communication_Bit_Until_A_Sync_Fits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);

        await Inject(device, CanOpenCobId.Sync, 7);
        await ExpectEmcyAsync(wire, 1, 0x8240, Communication);
        await Inject(device, CanOpenCobId.Sync, 8);
        await Inject(device, CanOpenCobId.Sync);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x00);

        await Inject(device, CanOpenCobId.Sync, 9);
        await ExpectEmcyAsync(wire, 3, 0x8240, Communication);
        device.ObjectDictionary.WriteUnsigned(0x1019, 0, 8);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0x00);
    }

    // MPDO 8210h and 8230h: a DAM frame that is not eight bytes, and a destination object that
    // is not available, are two errors of the same RPDO; each ends when its cause does — a
    // frame of eight bytes, a write that lands. The eight-byte frame that names a missing object
    // ends the first error (0000h) and starts the second.
    [Fact]
    public async Task Mpdo_Length_And_Destination_Errors_Set_The_Communication_Bit_Until_They_End()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(bus, Device, new ManualTimeSource());
        device.ObjectDictionary.AddU16(0x2000, 1, 0);
        device.Pdo().ConfigureMultiplexedRpdo(1, MpdoAddressMode.Destination, Rpdo1);
        await StartAsync(wire, device);

        await Inject(device, Rpdo1, 0x80, 0x00, 0x20, 1);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        await Inject(device, Rpdo1, 0x80, 0xFF, 0x2F, 0, 0, 0, 0, 0); // fits, but 2FFFh:00 is not there
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x00);
        await ExpectEmcyAsync(wire, 3, 0x8230, Communication);
        await Inject(device, Rpdo1, 0x80, 0xFF, 0x2F, 0, 0, 0, 0, 0);
        await Inject(device, Rpdo1, 0x80, 0x00, 0x20, 1, 0x34, 0x12, 0, 0);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0x00);
        device.ObjectDictionary.ReadUnsigned(0x2000, 1).Should().Be(0x1234u);
        wire.Count(EmcyId).Should().Be(4);
    }

    // SRDO 8210h, the node's half: the engine reports the start and the end of a run of short
    // pairs (SrdoEngineConsumerTests pins when), the node keeps the register and sends 0000h.
    [Fact]
    public async Task Srdo_Length_Error_Sets_The_Communication_Bit_Until_The_Engine_Ends_It()
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(bus, Device, new ManualTimeSource());
        await WaitForStateAsync(device, NmtState.PreOperational);
        var node = (CanOpenNode)device;
        await node.PostToActorAsync(() => ((ISrdoEngineHost)node).EmitEmcy(1, 0x8210));
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        await node.PostToActorAsync(() => ((ISrdoEngineHost)node).EmitEmcy(1, 0x8210));
        await node.PostToActorAsync(() => ((ISrdoEngineHost)node).EmcyCauseEnded(1));
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x00);
        await node.PostToActorAsync(() => ((ISrdoEngineHost)node).EmcyCauseEnded(1));
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 3, 0xFF00, 0x00);
    }

    // The rule across sources and the application: each new error is its own EMCY, the register
    // keeps bit 4 while any one is active, the application's bits are ORed in and never cleared by
    // the node, and the 0000h comes when the last communication error ends — with the
    // application's remaining bits (Figure 37, transition 2), and an application 0000h while a
    // communication error is active still carries it.
    [Fact]
    public async Task The_Error_Reset_Waits_For_The_Last_Communication_Error_And_Keeps_The_Applications_Bits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);

        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        await Inject(device, CanOpenCobId.Sync, 7);
        await ExpectEmcyAsync(wire, 2, 0x8240, Communication);
        await device.SendEmcyAsync(0xFF00, 0x81);
        await ExpectEmcyAsync(wire, 3, 0xFF00, 0x91);
        await Inject(device, Rpdo1, 1, 2, 3, 4); // one of two ends: no EMCY
        await Inject(device, CanOpenCobId.Sync);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0x81);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x81u);

        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 5, 0x8210, 0x91);
        await device.SendEmcyAsync(ErrorReset, 0x00);
        await ExpectEmcyAsync(wire, 6, ErrorReset, Communication);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 7, ErrorReset, 0x00);
        wire.Count(EmcyId).Should().Be(7);
    }

    // A reset is transitions 5 and 0 of Figure 37: the node is error free, "No error message is
    // sent". The communication bits leave 1001h, the application's stay (1001h is not restored).
    [Fact]
    public async Task A_Reset_Ends_The_Communication_Errors_Without_An_Emcy()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await device.SendEmcyAsync(0xFF00, 0x81);
        await ExpectEmcyAsync(wire, 1, 0xFF00, 0x81);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 2, 0x8210, 0x91);

        await Inject(device, CanOpenCobId.NmtCommand, (byte)NmtCommand.ResetCommunication, Device);
        await WaitForErrorRegisterAsync(device, 0x81);
        // The reset restored the power-on RPDO records: configure RPDO1 again.
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 3, 0x8210, 0x91);
        wire.Count(EmcyId).Should().Be(3, "the reset sent no 0000h, and the short frame after it is a new error");
    }

    // With the EMCY switched off there is no frame for the 0000h, but the register still drops
    // the bit. While it is off, a new error is not raised (FR-CO-024 as before).
    [Fact]
    public async Task An_Error_Ending_While_The_Emcy_Is_Off_Still_Clears_The_Register()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);

        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(device);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await WaitForErrorRegisterAsync(device, 0x00);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(device);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 2, 0xFF00, 0x00);
    }

    // In Stopped an EMCY is held (§7.3.2.2.4), the node's as the application's, and the one that
    // goes out after Stopped carries the register of that moment.
    [Fact]
    public async Task A_Held_Emcy_Carries_The_Register_Of_Its_Transmission()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);

        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await StartAsync(wire, device);
        await ExpectEmcyAsync(wire, 2, 0xFF00, Communication);

        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        Settle(device);
        await StartAsync(wire, device);
        await ExpectEmcyAsync(wire, 3, ErrorReset, 0x00);
        wire.Count(EmcyId).Should().Be(3);
    }
}
