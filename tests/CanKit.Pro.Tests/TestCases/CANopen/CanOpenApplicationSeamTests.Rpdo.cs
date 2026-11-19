using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// RPDO reception and TPDO transmission by number (#558): an application hears of every RPDO of its
// logical devices that the node took, and can send one of its TPDOs by number, as CiA 408 v2.2.0
// §8.4.1 asks a type-255 TPDO to answer each corresponding RPDO.
public partial class CanOpenApplicationSeamTests
{
    private const uint SyncId = 0x080;

    /// <summary>A profile of logical device <paramref name="logicalDevice"/>: control word
    /// <c>6040h</c> (a command entry), a set point <c>6000h:01</c>, a status word <c>6041h</c>; its
    /// default RPDO 1 maps control word and set point, its default TPDO 1 the status word, both of
    /// transmission type <c>FFh</c>, numbered within the logical device. <c>2101h</c> outside the
    /// claim is the witness TPDO's object.</summary>
    private static RecordingApplication Responder(string name = "responder", int logicalDevice = 0)
    {
        ushort Index(ushort inDevice0) => CanOpenLogicalDeviceAddressing.Index(logicalDevice, inDevice0);
        int pdo = CanOpenLogicalDeviceAddressing.PdoNumber(logicalDevice, 1);
        return new RecordingApplication(name, CanOpenIndexRange.ForLogicalDevice(logicalDevice))
        {
            Requirements = new CanOpenApplicationRequirements { PdoCount = pdo },
            OnAttach = host =>
            {
                var od = host.ObjectDictionary;
                od.AddU16(Index(0x6040), 0x00, 0x0001);
                od.AddU8(Index(0x6000), 0x01, 0);
                od.AddU16(Index(0x6041), 0x00, 0x0237);
                if (logicalDevice == 0) od.AddU8(0x2101, 0x00, 0);
                host.SetDeviceType(logicalDevice, new CanOpenDeviceType(0x0000_0198));
                host.SetDefaultRpdo(pdo, new PdoMapping().Add(Index(0x6040), 0x00, 16).Add(Index(0x6000), 0x01, 8));
                host.SetDefaultTpdo(pdo, new PdoMapping().Add(Index(0x6041), 0x00, 16));
            },
            CommandEntries = new[] { new CanOpenObjectAddress(Index(0x6040), 0x00) },
        };
    }

    private static IEnumerable<string> Receptions(RecordingApplication app)
        => app.Log.Where(e => e.StartsWith("rpdo", StringComparison.Ordinal));

    /// <summary>Counts the node's own RPDO events, which come through the public event queue: once one
    /// has arrived, the frame was taken and its job has run.</summary>
    private sealed class RpdoEvents
    {
        private int _count;

        internal RpdoEvents(CanOpenNode node) => node.RpdoReceived += (_, _) => Interlocked.Increment(ref _count);

        internal Task WaitForAsync(int count)
            => WaitUntilAsync(() => Volatile.Read(ref _count) >= count, $"{count} RPDO event(s)");
    }

    // -----------------------------------------------------------------------------------------
    // Reception.
    // -----------------------------------------------------------------------------------------

    [Fact] // Review Focus 2, #558
    public async Task Two_Identical_Rpdos_Are_Two_Receptions()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        var events = new RpdoEvents(rig.Node);
        await rig.StartAsync();

        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        await events.WaitForAsync(2);
        Settle(rig.Node);

        Receptions(app).Should().Equal(new[] { "rpdo 1", "rpdo 1" }, "the second frame carries the same data and is a reception all the same");
    }

    [Fact] // #558
    public async Task A_Write_Of_The_Same_Entries_Is_No_Reception()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        await rig.StartAsync();

        var response = await rig.Wire.DownloadU8Async(0x6000, 0x01, 0x42);
        await rig.Wire.SdoAsync(new byte[] { 0x2B, 0x40, 0x60, 0x00, 0x0F, 0x00, 0, 0 });
        rig.Od.WriteUnsigned(0x6000, 0x01, 0x43);
        Settle(rig.Node);

        response[0].Should().Be(0x60);
        Written(app).Should().NotBeEmpty("the writes were told as writes");
        Receptions(app).Should().BeEmpty("an SDO download or a local write is no reception of the RPDO");
    }

    [Fact]
    public async Task The_Reception_Is_Told_After_The_Rpdos_Command_And_Entry_Writes()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        var events = new RpdoEvents(rig.Node);
        await rig.StartAsync();
        int mark = app.Log.Count;

        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        await events.WaitForAsync(1);
        Settle(rig.Node);

        app.Log.Skip(mark).Should().Equal(Command(0x000F), "written 6000:01", "rpdo 1");
    }

    [Fact]
    public async Task An_Rpdo_Tells_The_Application_Of_Its_Logical_Device_Only()
    {
        var zero = Responder("zero", logicalDevice: 0);
        var one = Responder("one", logicalDevice: 1);
        var other = new RecordingApplication("other", new CanOpenIndexRange(0x2000, 0x20FF));
        using var rig = new EntryRig(With(zero, one, other).With(pdoCount: 65));
        var events = new RpdoEvents(rig.Node);
        // RPDO 65 is logical device 1's RPDO 1; above RPDO 4 the master assigns its COB-ID.
        rig.Od.WriteUnsigned(0x1440, 0x01, 0x23F);
        await rig.StartAsync();

        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        rig.Wire.Transmit(0x23F, new byte[] { 0x0F, 0x00, 0x43 });
        await events.WaitForAsync(2);
        Settle(rig.Node);

        Receptions(zero).Should().Equal("rpdo 1");
        Receptions(one).Should().Equal("rpdo 65");
        Receptions(other).Should().BeEmpty("an application that set no device type has no RPDOs");
    }

    [Fact]
    public async Task An_Rpdo_Outside_Operational_Is_No_Reception()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);

        await WhileActorHeldAsync(rig.Node, () => QueueFramesAsync(rig, (Rpdo1, new byte[] { 0x0F, 0x00, 0x42 })));
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(0u, "PDOs exist only in Operational");
        Receptions(app).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Synchronous_Rpdo_Is_Told_When_The_Sync_Applies_Its_Data()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        var events = new RpdoEvents(rig.Node);
        // Transmission type 01h: the data waits for the next SYNC (CiA 301 v4.2.0 §7.2.2.2).
        rig.Od.WriteUnsigned(0x1400, 0x02, 0x01);
        await rig.StartAsync();

        await WhileActorHeldAsync(rig.Node, () => QueueFramesAsync(rig,
            (Rpdo1, new byte[] { 0x0F, 0x00, 0x41 }), (Rpdo1, new byte[] { 0x0F, 0x00, 0x42 })));
        Settle(rig.Node);
        var beforeSync = Receptions(app).ToArray();
        rig.Wire.Transmit(SyncId, Array.Empty<byte>());
        await events.WaitForAsync(1);
        Settle(rig.Node);

        beforeSync.Should().BeEmpty("the data is passed on with the SYNC");
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(0x42u, "the SYNC applies the last frame received before it");
        Receptions(app).Should().Equal(new[] { "rpdo 1" }, "one frame's data was applied, so one reception is told");
    }

    [Fact]
    public async Task A_Reset_Drops_The_Receptions_Not_Yet_Told()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        await rig.StartAsync();
        Task? reset = null;

        await WhileActorHeldAsync(rig.Node, async () =>
        {
            await QueueFramesAsync(rig, (Rpdo1, new byte[] { 0x0F, 0x00, 0x42 }));
            reset = rig.Node.ActorForTests.PostAsync(() => typeof(CanOpenNode)
                .GetMethod("HandleIncoming", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, ResetCommand(NmtCommand.ResetNode), false }));
        });
        await reset!;
        Settle(rig.Node);

        app.Log.Should().Contain("reset ResetNode");
        Receptions(app).Should().BeEmpty("the reset came before the reception was told");
        CommandCalls(app).Should().BeEmpty("and the RPDO's command write with it");
    }

    // -----------------------------------------------------------------------------------------
    // A TPDO by number.
    // -----------------------------------------------------------------------------------------

    [Fact] // Review Focus 2, #558
    public async Task Each_Rpdo_Answered_By_Number_Sends_Its_Tpdo()
    {
        var app = Responder();
        app.WhenRpdo = pdo => app.Host!.RequestTransmissionOfTpdo(pdo);
        using var rig = new EntryRig(With(app));
        var events = new RpdoEvents(rig.Node);
        rig.ConfigureWitness();
        await rig.StartAsync();

        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        rig.Wire.Transmit(Rpdo1, new byte[] { 0x0F, 0x00, 0x42 });
        await events.WaitForAsync(2);
        await rig.Wire.WaitForCountAsync(Tpdo1, 2);
        await rig.WitnessAsync();

        rig.Wire.Payloads(Tpdo1).Should().HaveCount(2).And.AllSatisfy(frame => frame.Should().Equal(0x37, 0x02));
    }

    [Fact]
    public async Task A_Tpdo_Outside_The_Applications_Logical_Devices_Is_Refused()
    {
        var app = Responder();
        var refused = new List<Exception?>();
        using var rig = new EntryRig(With(app).With(pdoCount: 65));
        await rig.StartAsync();

        await rig.Node.PostToActorAsync(() => { });
        app.Host!.Post(() =>
        {
            foreach (var number in new[] { 0, 65, -1 })
                refused.Add(Record.Exception(() => app.Host.RequestTransmissionOfTpdo(number)));
        });
        Settle(rig.Node);

        refused.Should().HaveCount(3).And.AllSatisfy(failure =>
            failure.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("pdoNumber"));
    }

    [Fact]
    public void A_Tpdo_Request_Off_The_Actor_Is_Refused()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));

        Action request = () => app.Host!.RequestTransmissionOfTpdo(1);

        request.Should().Throw<InvalidOperationException>().WithMessage("*actor loop*");
    }

    [Theory]
    [InlineData(0x00)] // synchronous acyclic: not latched for the SYNC
    [InlineData(0x01)] // synchronous cyclic
    [InlineData(0xFD)] // RTR-only, event-driven
    public async Task A_Tpdo_Of_A_Type_No_Event_Triggers_Is_Not_Sent(int type)
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        var syncHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.SyncReceived += (_, _) => syncHandled.TrySetResult(true);
        rig.ConfigureWitness();
        rig.Od.WriteUnsigned(0x1800, 0x02, (uint)type);
        await rig.StartAsync();

        await rig.Node.PostToActorAsync(() => { });
        app.Host!.Post(() => app.Host.RequestTransmissionOfTpdo(1));
        Settle(rig.Node);
        if (type == 0x00)
        {
            // A synchronous TPDO would go with the SYNC; type 00h only if something had latched it.
            // The node raises SyncReceived from the job that handles the SYNC, before that job sends
            // the synchronous TPDOs; the barrier behind the witness then waits for that job to end
            // and for what it sent to be on the wire (CanOpenTransmitBarrier).
            rig.Wire.Transmit(SyncId, Array.Empty<byte>());
            await syncHandled.Task.WithTimeoutAsync(ShortTimeout);
        }
        await rig.WitnessAsync();

        rig.Wire.Count(Tpdo1).Should().Be(0, $"transmission type {type:X2}h is not triggered by an event");
    }

    [Fact]
    public async Task A_Tpdo_Request_Outside_Operational_Sends_Nothing_Then_Or_Later()
    {
        var app = Responder();
        using var rig = new EntryRig(With(app));
        rig.ConfigureWitness();
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);

        await rig.Node.PostToActorAsync(() => { });
        app.Host!.Post(() => app.Host.RequestTransmissionOfTpdo(1));
        Settle(rig.Node);
        await rig.StartAsync();
        await rig.WitnessAsync();

        rig.Wire.Count(Tpdo1).Should().Be(0, "PDOs exist only in Operational, and the request is not kept");
    }

    [Fact]
    public async Task A_Tpdo_Requested_Within_Its_Inhibit_Time_Goes_Once_When_It_Has_Elapsed()
    {
        var clock = new ManualTimeSource();
        var app = Responder();
        using var rig = new EntryRig(With(app), clock);
        // The inhibit time is written while the TPDO does not exist (CiA 301 v4.2.0 §7.5.2.37).
        uint cobId = rig.Od.ReadUnsigned(0x1800, 0x01);
        rig.Od.WriteUnsigned(0x1800, 0x01, cobId | 0x8000_0000u);
        rig.Od.WriteUnsigned(0x1800, 0x03, 100); // 10 ms
        rig.Od.WriteUnsigned(0x1800, 0x01, cobId);
        await rig.StartAsync();

        await rig.Node.PostToActorAsync(() => { });
        app.Host!.Post(() =>
        {
            app.Host.RequestTransmissionOfTpdo(1);
            app.Host.RequestTransmissionOfTpdo(1);
            app.Host.RequestTransmissionOfTpdo(1);
        });
        await rig.Wire.WaitForCountAsync(Tpdo1, 1);
        await CanOpenTransmitBarrier.DrainAsync(rig.Node);
        int withinInhibit = rig.Wire.Count(Tpdo1);
        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(10));
        await rig.Wire.WaitForCountAsync(Tpdo1, 2);
        await CanOpenTransmitBarrier.DrainAsync(rig.Node);

        withinInhibit.Should().Be(1, "the first request sends, the others wait for the inhibit time");
        rig.Wire.Count(Tpdo1).Should().Be(2, "the requests made within the inhibit time add one transmission when it has elapsed");
    }
}
