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

    // #434: the SRDO engine must not latch a disabled error before the node can report it.
    // RPDO is the existing contract; application EMCYs witness the exact wire sequence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Length_Error_While_Emcy_Is_Disabled_Is_Reported_After_Enabling_For_Pdo_And_Srdo(bool srdo)
    {
        var session = NewSession();
        using var bus = Open(session, 0);
        using var wire = new Wire(session, 2);
        using var device = OpenClocked(bus, Device, new ManualTimeSource(),
            new CanOpenNodeOptions { SrdoCount = 1, WritableCommunicationParameters = true });
        await WaitForStateAsync(device, NmtState.PreOperational);
        var od = device.ObjectDictionary;
        od.AddU16(0x2000, 0, 0); od.AddU16(0x2001, 0, 0);
        if (srdo)
        {
            device.Safety().ConfigureSrdoConsumer(1, new SrdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20), 0x123, 0x124);
            device.Safety().CommitSafetyConfiguration();
        }
        else device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        await StartAsync(wire, device);
        async Task Receive(bool shortFrame)
        {
            byte[] normal = shortFrame ? new byte[] { 1, 2 } : new byte[] { 1, 2, 3, 4 };
            await Inject(device, srdo ? 0x123 : Rpdo1, normal);
            if (srdo) await Inject(device, 0x124, SrdoFrames.Invert(normal));
        }
        od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        await Receive(shortFrame: true);
        await Receive(shortFrame: true);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        await device.SendEmcyAsync(0xFF00, 0);
        await ExpectEmcyAsync(wire, 1, 0xFF00, 0);
        await Receive(shortFrame: true);
        await device.SendEmcyAsync(0xFF01, 0);
        await ExpectEmcyAsync(wire, 2, 0x8210, Communication);
        await ExpectEmcyAsync(wire, 3, 0xFF01, Communication);
        od.ReadUnsigned(0x2000, 0).Should().Be(0, "short frames remain unprocessed");
        od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        await Receive(shortFrame: true);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        await Receive(shortFrame: true);
        await device.SendEmcyAsync(0xFF02, 0);
        await ExpectEmcyAsync(wire, 4, 0xFF02, Communication);
        await Receive(shortFrame: false);
        await device.SendEmcyAsync(0xFF03, 0);
        await ExpectEmcyAsync(wire, 5, ErrorReset, 0);
        await ExpectEmcyAsync(wire, 6, 0xFF03, 0);
        wire.Count(EmcyId).Should().Be(6, "enabling an already reported run does not report it twice");
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

    // A reset is transitions 5 and 0 of Figure 37: "After NMT state INITIALISATION the CANopen
    // device enters the error free state" (Corrigendum 3), with no EMCY. The communication bits
    // leave 1001h, the application's stay (1001h is not restored).
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

    // Bugbot on #417: a held 0000h that is discarded because the EMCY was switched off in Stopped
    // still takes the ended error out of 1001h.
    [Fact]
    public async Task A_Held_Error_Reset_Discarded_With_The_Emcy_Off_Still_Clears_The_Register()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);

        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16)); // 0000h held
        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(device);
        await StartAsync(wire, device);
        await WaitForErrorRegisterAsync(device, 0x00);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(device);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 2, 0xFF00, 0x00);
    }

    // Codex on #417: a held 0000h that an application EMCY replaces in Stopped, with the EMCY then
    // switched off, still takes the ended error out of 1001h: the application's EMCY sends and
    // writes nothing, and the node's register update does not ride on its payload.
    [Fact]
    public async Task A_Held_Error_Reset_Replaced_By_An_Application_Emcy_Still_Clears_The_Register()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);

        wire.SendNmt(NmtCommand.Stop, Device);
        await WaitForStateAsync(device, NmtState.Stopped);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16)); // 0000h held
        await device.SendEmcyAsync(0xFF00, 0x00); // replaces it
        var od = device.ObjectDictionary;
        od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit);
        Settle(device);
        await StartAsync(wire, device);
        await WaitForErrorRegisterAsync(device, 0x00);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(device);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 2, 0xFF00, 0x00);
    }

    // Codex on #417: the application's part of the register is the one its last transmitted EMCY
    // carried. A call cancelled before it is transmitted changes nothing.
    [Fact]
    public async Task A_Cancelled_Application_Emcy_Leaves_The_Applications_Bits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await device.SendEmcyAsync(0xFF00, 0x81);
        await ExpectEmcyAsync(wire, 1, 0xFF00, 0x81);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.SendEmcyAsync(0xFF01, 0x00, cancellationToken: cancelled.Token));
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 2, 0x8210, 0x91);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x91u);
    }

    // Codex on #417: the error reset was decided with the EMCY on, and the EMCY is switched off
    // before its turn on the chain comes. No frame goes out, but the register still drops the
    // ended error. The loop is held so that both run in one drain, ahead of the transmission.
    [Fact]
    public async Task An_Error_Reset_Whose_Emcy_Is_Switched_Off_Before_Its_Turn_Still_Clears_The_Register()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);

        var node = (CanOpenNode)device;
        var od = device.ObjectDictionary;
        using var release = new System.Threading.ManualResetEventSlim();
        var held = node.PostToActorAsync(() => release.Wait(ShortTimeout));
        var fits = Inject(device, Rpdo1, 1, 2, 3, 4);
        var off = node.PostToActorAsync(() => od.WriteUnsigned(0x1014, 0, EmcyId | CanOpenCobId.InvalidBit));
        release.Set();
        await held;
        await fits;
        await off;
        await WaitForErrorRegisterAsync(device, 0x00);
        od.WriteUnsigned(0x1014, 0, EmcyId);
        Settle(device);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 2, 0xFF00, 0x00);
    }

    // Reconfiguring an RPDO ends its own errors only; another active one keeps the bit, and the
    // error reset waits for it.
    [Fact]
    public async Task Reconfiguring_An_Rpdo_Leaves_Another_Error_Active()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, Communication);
        await Inject(device, CanOpenCobId.Sync, 7);
        await ExpectEmcyAsync(wire, 2, 0x8240, Communication);
        device.ConfigureRpdo(1, new PdoMapping().Add(0x2000, 0, 16).Add(0x2001, 0, 16));
        Settle(device);
        await Inject(device, CanOpenCobId.Sync);
        await ExpectEmcyAsync(wire, 3, ErrorReset, 0x00);
        wire.Count(EmcyId).Should().Be(3);
    }

    // Bugbot on #417: an error decided after an application EMCY was queued, but before that
    // EMCY is transmitted, still carries the application's new part — it follows it on the chain.
    // The loop is held so that both calls run in one drain, ahead of the queued transmission.
    [Fact]
    public async Task An_Error_Decided_Behind_A_Queued_Application_Emcy_Carries_Its_Bits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        var node = (CanOpenNode)device;
        using var release = new System.Threading.ManualResetEventSlim();
        var held = node.PostToActorAsync(() => release.Wait(ShortTimeout));
        var application = device.SendEmcyAsync(0xFF00, 0x81);
        var shortFrame = Inject(device, Rpdo1, 0x01);
        release.Set();
        await held;
        await shortFrame;
        await application;
        await ExpectEmcyAsync(wire, 1, 0xFF00, 0x81); // decided before the error
        await ExpectEmcyAsync(wire, 2, 0x8210, 0x91);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x91u);
    }

    // Review of #417: 1001h is in the dictionary, so the application can also set its bits there
    // without an EMCY, and a device description can give it a value. Those bits are the
    // application's too: a node EMCY ORs its own into them, and the error reset leaves them.
    [Fact]
    public async Task An_Error_Register_The_Application_Wrote_Is_Kept_By_The_Nodes_Emcys()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        device.ObjectDictionary.WriteUnsigned(0x1001, 0, 0x04);
        await StartAsync(wire, device);

        await Inject(device, Rpdo1, 0x01);
        await ExpectEmcyAsync(wire, 1, 0x8210, 0x15);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x04);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x04u);

        // A write after the node's own EMCYs is taken as well.
        device.ObjectDictionary.WriteUnsigned(0x1001, 0, 0x08);
        await Inject(device, Rpdo1, 0x02);
        await ExpectEmcyAsync(wire, 3, 0x8210, 0x19);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 4, ErrorReset, 0x08);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x08u);

        // Codex on #417: a write of exactly the value the node last wrote is the application's
        // too — its own communication error, which the node's error reset does not clear.
        await Inject(device, Rpdo1, 0x03);
        await ExpectEmcyAsync(wire, 5, 0x8210, 0x19);
        device.ObjectDictionary.WriteUnsigned(0x1001, 0, 0x19);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 6, ErrorReset, 0x19);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0x19u);

        // Codex and Bugbot on #417: an application EMCY after a dictionary write replaces that
        // write. The register it leaves in 1001h, the node's bits ORed in, is not the
        // application's part, and the error reset clears to what the EMCY passed.
        await Inject(device, Rpdo1, 0x04);
        await ExpectEmcyAsync(wire, 7, 0x8210, 0x19);
        device.ObjectDictionary.WriteUnsigned(0x1001, 0, 0x00);
        await device.SendEmcyAsync(0xFF00, 0x00);
        await ExpectEmcyAsync(wire, 8, 0xFF00, Communication);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 9, ErrorReset, 0x00);
        device.ObjectDictionary.ReadUnsigned(0x1001, 0).Should().Be(0u);
    }

    // Codex and Bugbot on #417: an application write of 1001h from another thread, racing the
    // node's step on the EMCY chain, is not overwritten and then mistaken for the composed value.
    // The test holds the dictionary's write gate until the node's step waits at it (#171 seam),
    // then writes 04h inside it: the step that follows takes 04h, whether or not it had already
    // started.
    [Fact]
    public async Task An_Application_Write_Racing_A_Node_Emcy_Keeps_Its_Bits()
    {
        using var device = OpenWithRpdo(NewSession(), out var wire, out _);
        using var _w = wire;
        await StartAsync(wire, device);
        var od = device.ObjectDictionary;
        using var inside = new System.Threading.ManualResetEventSlim();
        using var write = new System.Threading.ManualResetEventSlim();
        var holder = Task.Run(() => od.Transaction(() =>
        {
            inside.Set();
            write.Wait(ShortTimeout).Should().BeTrue("the test lets the write go");
            od.WriteUnsigned(0x1001, 0, 0x04);
        }));
        inside.Wait(ShortTimeout).Should().BeTrue();
        try
        {
            await Inject(device, Rpdo1, 0x01);
            var deadline = DateTime.UtcNow + ShortTimeout;
            while (od.WriteGateWaiters < 1)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The node's EMCY step did not reach the write gate.");
                await Task.Delay(1);
            }
        }
        finally
        {
            write.Set(); // a failed wait must not leave the gate held for the node's disposal
        }
        await holder;

        await ExpectEmcyAsync(wire, 1, 0x8210, 0x15);
        await Inject(device, Rpdo1, 1, 2, 3, 4);
        await ExpectEmcyAsync(wire, 2, ErrorReset, 0x04);
        od.ReadUnsigned(0x1001, 0).Should().Be(0x04u);
    }
}
