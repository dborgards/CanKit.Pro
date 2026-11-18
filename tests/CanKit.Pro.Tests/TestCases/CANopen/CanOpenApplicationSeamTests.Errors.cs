using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// The errors of a profile (1001h and EMCY) and the communication failure it is told about.
public partial class CanOpenApplicationSeamTests
{
    private const uint SlaveEmcy = 0x080 + Slave;
    private const byte ProducerNode = 0x22;
    private const uint ProducerHeartbeat = 0x700 + ProducerNode;

    private static CanOpenNodeOptions WithErrorBehavior(params ICanOpenApplication[] applications)
        => applications.Aggregate(new CanOpenNodeOptions { EnableErrorBehavior = true },
            (options, application) => options.WithApplication(application));

    /// <summary>Returns once every EMCY ordered so far has been through the chain: the frame is out, or,
    /// with the EMCY switched off, the register is written. The barrier of every assertion here on
    /// what EMCYs were not sent, or exactly how many were: <see cref="CanOpenTransmitBarrier.DrainEmcyAsync"/>.</summary>
    private static Task EmcyChainSettledAsync(CanOpenNode node) => CanOpenTransmitBarrier.DrainEmcyAsync(node);

    private static byte ErrorRegister(CanOpenNode node) => (byte)node.ObjectDictionary.ReadUnsigned(0x1001, 0);

    private static Task OnActor(CanOpenNode node, Action work) => node.PostToActorAsync(work);

    private static async Task RaiseAsync(CanOpenNode node, RecordingApplication app, ushort code, byte bits,
        byte[]? manufacturer = null)
    {
        await OnActor(node, () => app.Host!.RaiseError(code, bits, manufacturer));
        await EmcyChainSettledAsync(node);
    }

    private static async Task ClearAsync(CanOpenNode node, RecordingApplication app, ushort code)
    {
        await OnActor(node, () => app.Host!.ClearError(code));
        await EmcyChainSettledAsync(node);
    }

    private static byte[] Emcy(ushort code, byte register, params byte[] manufacturer)
    {
        var frame = new byte[8];
        frame[0] = (byte)code;
        frame[1] = (byte)(code >> 8);
        frame[2] = register;
        manufacturer.CopyTo(frame, 3);
        return frame;
    }

    private static RecordingApplication Profile() => new("profile");

    // -----------------------------------------------------------------------------------------
    // Errors.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Profile_Error_Is_One_Emcy_With_Its_Bits()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));

        await RaiseAsync(rig.Node, app, 0x5000, 0x04, new byte[] { 1, 2 });

        rig.Wire.Payloads(SlaveEmcy).Should().HaveCount(1);
        rig.Wire.Payloads(SlaveEmcy)[0].Should().Equal(Emcy(0x5000, 0x05, 1, 2));
        ErrorRegister(rig.Node).Should().Be(0x05, "the generic bit comes with any error (§7.5.2.2)");
    }

    [Fact]
    public async Task The_Same_Error_Twice_Is_One_Emcy()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));

        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        // Other bits than the first raise's (bit 6, which an earlier version used here, is reserved, #554).
        await RaiseAsync(rig.Node, app, 0x5000, 0x20, new byte[] { 9 });

        rig.Wire.Payloads(SlaveEmcy).Should().HaveCount(1, "one EMCY per error event (§7.2.7.1)");
        ErrorRegister(rig.Node).Should().Be(0x05, "the second raise changed nothing");
    }

    [Fact]
    public async Task Clearing_The_Last_Error_Sends_The_Reset()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);

        await ClearAsync(rig.Node, app, 0x5000);

        rig.Wire.Payloads(SlaveEmcy).Should().HaveCount(2);
        rig.Wire.Payloads(SlaveEmcy)[1].Should().Equal(Emcy(0x0000, 0x00));
        ErrorRegister(rig.Node).Should().Be(0);
    }

    [Fact]
    public async Task Clearing_An_Error_That_Is_Not_Active_Does_Nothing()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));

        await ClearAsync(rig.Node, app, 0x5000);

        rig.Wire.Count(SlaveEmcy).Should().Be(0);
        ErrorRegister(rig.Node).Should().Be(0);
    }

    [Fact]
    public async Task Clearing_One_Of_Two_Keeps_The_Others_Bits()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        await RaiseAsync(rig.Node, app, 0x3200, 0x20);
        ErrorRegister(rig.Node).Should().Be(0x25);

        await ClearAsync(rig.Node, app, 0x5000);

        rig.Wire.Payloads(SlaveEmcy).Last().Should().Equal(Emcy(0x0000, 0x21),
            "§7.2.7.1: the reset may be sent with the errors that remain");
        ErrorRegister(rig.Node).Should().Be(0x21);
    }

    [Fact]
    public async Task The_Applications_Own_Bits_Stay()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        await rig.Node.SendEmcyAsync(0x1000, 0x80);
        await EmcyChainSettledAsync(rig.Node);

        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        rig.Wire.Payloads(SlaveEmcy).Last().Should().Equal(Emcy(0x5000, 0x85));
        ErrorRegister(rig.Node).Should().Be(0x85);

        await ClearAsync(rig.Node, app, 0x5000);
        rig.Wire.Payloads(SlaveEmcy).Last().Should().Equal(Emcy(0x0000, 0x80));
        ErrorRegister(rig.Node).Should().Be(0x80, "the application's part of 1001h is not the profile's to clear");
    }

    [Fact]
    public async Task An_Error_In_Stopped_Is_Held()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.Stop, Slave });
        await WaitUntilAsync(() => rig.Node.State == NmtState.Stopped, "Stopped");

        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        Settle(rig.Node);

        rig.Wire.Count(SlaveEmcy).Should().Be(0, "no EMCY goes out in Stopped (§7.3.2.2.4)");

        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.EnterPreOperational, Slave });
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
        await EmcyChainSettledAsync(rig.Node);

        rig.Wire.Payloads(SlaveEmcy)[0].Should().Equal(Emcy(0x5000, 0x05));
        ErrorRegister(rig.Node).Should().Be(0x05, "1001h shows the profile error from its raise on, and the released EMCY carried the same register (#550)");
    }

    [Fact]
    public async Task An_Error_With_Emcy_Switched_Off_Still_Shows_In_1001h()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        rig.Od.WriteUnsigned(0x1014, 0, SlaveEmcy | CanOpenCobId.InvalidBit);
        Settle(rig.Node);

        await RaiseAsync(rig.Node, app, 0x5000, 0x04);

        rig.Wire.Count(SlaveEmcy).Should().Be(0, "bit 31 of 1014h: there is no EMCY to send");
        ErrorRegister(rig.Node).Should().Be(0x05, "the register shows the error whether or not a frame can say so");
    }

    [Fact]
    public async Task A_Held_Profile_Error_Replaced_By_The_Applications_Emcy_Still_Shows_In_1001h()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.Stop, Slave });
        await WaitUntilAsync(() => rig.Node.State == NmtState.Stopped, "Stopped");
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        // The application's EMCY takes the frame of the held one, and the EMCY is switched off before
        // the node leaves Stopped: the frame is gone, the profile's error is not.
        _ = rig.Node.SendEmcyAsync(0x1000, 0x00);
        Settle(rig.Node);
        rig.Od.WriteUnsigned(0x1014, 0, SlaveEmcy | CanOpenCobId.InvalidBit);
        Settle(rig.Node);

        rig.Wire.Transmit(0x000, new[] { (byte)NmtCommand.EnterPreOperational, Slave });
        await WaitUntilAsync(() => rig.Node.State == NmtState.PreOperational, "Pre-Operational");
        await EmcyChainSettledAsync(rig.Node);

        rig.Wire.Count(SlaveEmcy).Should().Be(0);
        ErrorRegister(rig.Node).Should().Be(0x05, "1001h shows a profile error from its raise to its clear, whatever became of its EMCY (#550). " +
            "For a communication error, the register write a replaced EMCY still makes is covered by " +
            "CanOpenPdoEngineTests.A_Held_Error_Reset_Replaced_By_An_Application_Emcy_Still_Clears_The_Register");
    }

    [Fact]
    public async Task A_Reset_Ends_Profile_Errors()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        rig.Wire.Count(SlaveEmcy).Should().Be(1);

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);
        await EmcyChainSettledAsync(rig.Node);

        ErrorRegister(rig.Node).Should().Be(0);
        rig.Wire.Count(SlaveEmcy).Should().Be(1, "a reset ends the errors without an EMCY");

        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        rig.Wire.Count(SlaveEmcy).Should().Be(2, "the error was over, so raising it again is a new event");
    }

    [Fact]
    public async Task A_Raise_With_Bad_Arguments_Is_Refused()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        Exception? zero = null;
        Exception? tooLong = null;
        await OnActor(rig.Node, () =>
        {
            zero = Record.Exception(() => app.Host!.RaiseError(0x0000, 0x04));
            tooLong = Record.Exception(() => app.Host!.RaiseError(0x5000, 0x04, new byte[6]));
        });

        zero.Should().BeOfType<ArgumentOutOfRangeException>("0000h is the reset, which ClearError sends");
        tooLong.Should().BeOfType<ArgumentException>("the manufacturer field is five bytes");
        // The EMCY barrier, not Settle: an EMCY a refused raise ordered would still be on its way
        // through the EMCY chain after two actor jobs (#562).
        await EmcyChainSettledAsync(rig.Node);
        rig.Wire.Count(SlaveEmcy).Should().Be(0);
        ErrorRegister(rig.Node).Should().Be(0, "a refused raise records nothing");
    }

    [Theory] // #554
    [InlineData(0x40)]
    [InlineData(0x44)]
    [InlineData(0xFF)]
    public async Task A_Raise_With_The_Reserved_Bit_6_Is_Refused(int bits)
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));
        Exception? refused = null;

        await OnActor(rig.Node, () => refused = Record.Exception(() => app.Host!.RaiseError(0x5000, (byte)bits)));
        await EmcyChainSettledAsync(rig.Node);

        refused.Should().BeOfType<ArgumentOutOfRangeException>("bit 6 of the error register is reserved, always 0b (CiA 301 v4.2.0 §7.5.2.2, Table 54)")
            .Which.ParamName.Should().Be("errorRegisterBits");
        rig.Wire.Count(SlaveEmcy).Should().Be(0);
        ErrorRegister(rig.Node).Should().Be(0, "a refused raise records nothing");

        // Nothing was recorded either: the same code raised with valid bits is a new error event.
        await RaiseAsync(rig.Node, app, 0x5000, 0x04);
        rig.Wire.Payloads(SlaveEmcy).Should().ContainSingle().Which.Should().Equal(Emcy(0x5000, 0x05));
    }

    [Fact] // #554
    public async Task Every_Bit_But_Bit_6_Reaches_1001h_And_The_Emcy()
    {
        var app = Profile();
        using var rig = new EntryRig(With(app));

        await RaiseAsync(rig.Node, app, 0x5000, 0xBE);

        rig.Wire.Payloads(SlaveEmcy).Should().ContainSingle().Which.Should().Equal(Emcy(0x5000, 0xBF),
            "the categories, the manufacturer bit and the generic bit, which comes with any error");
        ErrorRegister(rig.Node).Should().Be(0xBF);
    }

    [Fact]
    public void An_Error_Is_Raised_And_Cleared_On_The_Actor_Only()
    {
        using var bus = Open(NewSession(), 1);
        var app = Profile();
        using var node = OpenNode(bus, With(app));
        Settle(node);

        Action raise = () => app.Host!.RaiseError(0x5000, 0x04);
        Action clear = () => app.Host!.ClearError(0x5000);

        raise.Should().Throw<InvalidOperationException>();
        clear.Should().Throw<InvalidOperationException>();
        ErrorRegister(node).Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------
    // 1001h during the EMCY inhibit time (#550).
    // -----------------------------------------------------------------------------------------

    private static readonly TimeSpan LongestInhibit = TimeSpan.FromTicks(0xFFFF * 1000L); // 6.5535 s

    /// <summary>A node with the EMCY inhibit time at its longest, <c>FFFFh</c>, whose interval has just
    /// been started by an EMCY of the application.</summary>
    private static async Task<EntryRig> InsideTheInhibitIntervalAsync(RecordingApplication app, ManualTimeSource clock)
    {
        var rig = new EntryRig(new CanOpenNodeOptions { EnableEmcyInhibit = true }.WithApplication(app), clock);
        try
        {
            rig.Od.WriteUnsigned(0x1015, 0, 0xFFFF);
            Settle(rig.Node);
            await rig.Node.SendEmcyAsync(0x1000, 0x00).WithTimeoutAsync(ShortTimeout);
            await rig.Wire.WaitForCountAsync(SlaveEmcy, 1);
            return rig;
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>What a remote expedited SDO upload of <c>1001h:00</c> returns.</summary>
    private static async Task<byte> UploadErrorRegisterAsync(EntryRig rig)
    {
        var response = await rig.Wire.UploadAsync(0x1001, 0x00);
        response[0].Should().Be(0x4F, "an expedited upload of one byte");
        return response[4];
    }

    /// <summary>Waits until the head of the EMCY chain waits for its slot: decided, not transmitted.</summary>
    private static async Task UntilEmcyParkedAsync(CanOpenNode node)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!await EmcyParkedAsync(node))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The EMCY never waited for its slot.");
            await Task.Delay(1);
        }
    }

    [Fact] // #550
    public async Task A_Profile_Error_Shows_In_1001h_While_Its_Emcy_Waits_For_The_Inhibit_Time()
    {
        var app = Profile();
        var clock = new ManualTimeSource();
        using var rig = await InsideTheInhibitIntervalAsync(app, clock);

        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        await UntilEmcyParkedAsync(rig.Node);
        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(6500));

        (await UploadErrorRegisterAsync(rig)).Should().Be(0x05,
            "an active error sets its bits and the generic bit (CiA 301 v4.2.0 §7.5.2.2, Table 54), inhibit time or not");
        (await EmcyParkedAsync(rig.Node)).Should().BeTrue("the EMCY still waits for the end of the interval");
        rig.Wire.Count(SlaveEmcy).Should().Be(1, "the inhibit time still spaces the frames (§7.5.2.18)");

        Advance(clock, rig.Node, LongestInhibit - TimeSpan.FromMilliseconds(6500));
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 2);
        await EmcyChainSettledAsync(rig.Node);

        rig.Wire.Payloads(SlaveEmcy)[1].Should().Equal(Emcy(0x5000, 0x05));
        (await UploadErrorRegisterAsync(rig)).Should().Be(0x05);
    }

    [Fact] // #550
    public async Task An_Error_Cleared_During_The_Inhibit_Time_Leaves_1001h_At_Once_And_No_Older_Frame_Puts_It_Back()
    {
        var app = Profile();
        var clock = new ManualTimeSource();
        using var rig = await InsideTheInhibitIntervalAsync(app, clock);
        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        await UntilEmcyParkedAsync(rig.Node);
        (await UploadErrorRegisterAsync(rig)).Should().Be(0x05);

        await OnActor(rig.Node, () => app.Host!.ClearError(0x5000));

        (await UploadErrorRegisterAsync(rig)).Should().Be(0x00, "the cause is gone: the register shows it at once");

        // The raise's EMCY goes out at the end of the interval and carries what it was ordered with; the
        // reset behind it waits for another interval. Between the two, 1001h stays what is true now.
        Advance(clock, rig.Node, LongestInhibit);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 2);
        await UntilEmcyParkedAsync(rig.Node);
        rig.Wire.Payloads(SlaveEmcy)[1].Should().Equal(Emcy(0x5000, 0x05), "the frames keep their order and content");
        (await UploadErrorRegisterAsync(rig)).Should().Be(0x00, "the older frame does not write the ended error back");

        Advance(clock, rig.Node, LongestInhibit);
        await rig.Wire.WaitForCountAsync(SlaveEmcy, 3);
        await EmcyChainSettledAsync(rig.Node);
        rig.Wire.Payloads(SlaveEmcy)[2].Should().Equal(Emcy(0x0000, 0x00));
        ErrorRegister(rig.Node).Should().Be(0x00);
    }

    [Fact] // #550
    public async Task A_Reset_During_The_Inhibit_Time_Ends_The_Error_In_1001h()
    {
        var app = Profile();
        var clock = new ManualTimeSource();
        using var rig = await InsideTheInhibitIntervalAsync(app, clock);
        await OnActor(rig.Node, () => app.Host!.RaiseError(0x5000, 0x04));
        await UntilEmcyParkedAsync(rig.Node);
        (await UploadErrorRegisterAsync(rig)).Should().Be(0x05);

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);
        await EmcyChainSettledAsync(rig.Node);

        (await UploadErrorRegisterAsync(rig)).Should().Be(0x00, "a reset ends the error");
        rig.Wire.Count(SlaveEmcy).Should().Be(1, "the reset dropped the EMCY that was waiting, and sent none");
    }

    // -----------------------------------------------------------------------------------------
    // Communication failure.
    // -----------------------------------------------------------------------------------------

    private static RecordingApplication Watcher(string name = "watcher") => new(name)
    {
        Requirements = new CanOpenApplicationRequirements { CommunicationFailureMonitoring = true },
    };

    private static string[] Failures(RecordingApplication app)
        => app.Log.Where(e => e.StartsWith("failure", StringComparison.Ordinal)).ToArray();

    /// <summary>A watch on node 22h at 100 ms, and what moves it: a heartbeat from the wire, awaited
    /// until the node has handled it.</summary>
    private sealed class ProducerWatchRig : IDisposable
    {
        private readonly SemaphoreSlim _seen = new(0);
        internal readonly EntryRig Rig;
        internal readonly ManualTimeSource Clock = new();

        internal ProducerWatchRig(params ICanOpenApplication[] applications)
            : this(WithErrorBehavior(applications))
        {
        }

        internal ProducerWatchRig(CanOpenNodeOptions options)
        {
            Rig = new EntryRig(options, Clock);
            Rig.Node.HeartbeatReceived += (_, e) =>
            {
                if (e.ProducerNodeId == ProducerNode) _seen.Release();
            };
            Rig.Node.AddHeartbeatConsumer(ProducerNode, TimeSpan.FromMilliseconds(100));
        }

        internal async Task BeatAsync()
        {
            Rig.Wire.Transmit(ProducerHeartbeat, new[] { (byte)NmtState.Operational });
            (await _seen.WaitAsync(ShortTimeout)).Should().BeTrue("the node saw the heartbeat");
            Settle(Rig.Node);
        }

        /// <summary>The producer stays silent for longer than the 100 ms.</summary>
        internal void Silence() => Advance(Clock, Rig.Node, TimeSpan.FromMilliseconds(150));

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public async Task A_Heartbeat_Timeout_Is_A_Communication_Failure()
    {
        var app = Watcher();
        using var watch = new ProducerWatchRig(app);
        await watch.BeatAsync();
        Failures(app).Should().BeEmpty("the producer is alive");

        watch.Silence();

        Failures(app).Should().Equal("failure True");
        await OnActor(watch.Rig.Node, () => app.Host!.CommunicationFailureActive.Should().BeTrue());

        await watch.BeatAsync();

        Failures(app).Should().Equal("failure True", "failure False");
        await OnActor(watch.Rig.Node, () => app.Host!.CommunicationFailureActive.Should().BeFalse());
    }

    [Fact]
    public async Task The_Failure_Is_Reported_Once_Per_Change()
    {
        var app = Watcher();
        using var watch = new ProducerWatchRig(app);
        int timeouts = 0;
        watch.Rig.Node.HeartbeatTimeout += (_, e) =>
        {
            if (e.ProducerNodeId == ProducerNode) Interlocked.Increment(ref timeouts);
        };
        await watch.BeatAsync();

        watch.Silence();
        await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 1, "the first timeout");
        // A watch is re-armed when it times out, so a producer that stays silent times out again:
        // the condition is reported a second time, and is still the same.
        watch.Silence();
        await WaitUntilAsync(() => Volatile.Read(ref timeouts) == 2, "the second timeout of the silent producer");

        Failures(app).Should().Equal("failure True");
    }

    [Fact]
    public async Task The_Failure_Is_Reported_Before_The_Node_Leaves_Operational()
    {
        var app = Watcher();
        using var watch = new ProducerWatchRig(app);
        watch.Rig.Od.WriteUnsigned(0x1029, 1, 0);
        await watch.Rig.StartAsync();
        Settle(watch.Rig.Node);
        await watch.BeatAsync();
        int mark = app.Log.Count;

        watch.Silence();

        app.Log.Skip(mark).Should().Equal("failure True", "nmt Operational>PreOperational");
    }

    [Fact]
    public async Task An_Application_That_Did_Not_Ask_Hears_No_Failure()
    {
        var uninterested = new RecordingApplication("uninterested");
        var watcher = Watcher();
        using var watch = new ProducerWatchRig(uninterested, watcher);
        await watch.BeatAsync();

        watch.Silence();

        Failures(watcher).Should().Equal("failure True");
        Failures(uninterested).Should().BeEmpty("it asked for no communication failure monitoring");
    }

    [Fact]
    public async Task Dropping_The_Watch_Of_A_Silent_Producer_Ends_The_Failure()
    {
        var app = Watcher();
        using var watch = new ProducerWatchRig(app);
        await watch.BeatAsync();
        watch.Silence();
        Failures(app).Should().Equal("failure True");

        watch.Rig.Od.WriteUnsigned(0x1016, 1, 0);
        Settle(watch.Rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
    }

    private static Task PollLifeGuarding(CanOpenNode node)
        => node.PostToActorAsync(() => typeof(CanOpenNode)
            .GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(node, new object[] { CanOpenCobId.Heartbeat(Slave), Array.Empty<byte>(), true }));

    [Fact]
    public async Task Life_Guarding_Is_A_Communication_Failure()
    {
        var app = Watcher();
        var clock = new ManualTimeSource();
        using var rig = new EntryRig(WithErrorBehavior(app), clock);
        rig.Od.WriteUnsigned(0x100C, 0, 50);
        rig.Od.WriteUnsigned(0x100D, 0, 2);
        Settle(rig.Node);
        await PollLifeGuarding(rig.Node);
        Failures(app).Should().BeEmpty("the master is polling");

        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(101));
        Failures(app).Should().Equal("failure True");

        await PollLifeGuarding(rig.Node);
        Failures(app).Should().Equal("failure True", "failure False");
    }

    [Fact]
    public async Task Switching_Life_Guarding_Off_Ends_The_Failure()
    {
        var app = Watcher();
        var clock = new ManualTimeSource();
        using var rig = new EntryRig(WithErrorBehavior(app), clock);
        rig.Od.WriteUnsigned(0x100C, 0, 50);
        rig.Od.WriteUnsigned(0x100D, 0, 2);
        Settle(rig.Node);
        await PollLifeGuarding(rig.Node);
        Advance(clock, rig.Node, TimeSpan.FromMilliseconds(101));
        Failures(app).Should().Equal("failure True");

        rig.Od.WriteUnsigned(0x100D, 0, 0);
        Settle(rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
    }

    /// <summary>A node whose controller reports the bus state the test sets.</summary>
    private sealed class BusStateRig : IDisposable
    {
        private readonly ControllableBus _bus;
        private readonly CanBusService _service;
        internal readonly ManualTimeSource Clock = new();
        internal readonly CanOpenNode Node;

        internal BusStateRig(params ICanOpenApplication[] applications)
        {
            _bus = ControllableBus.Plain(NewSession());
            _bus.BusState = BusState.ErrActive;
            _service = new CanBusService(_bus);
            Node = new CanOpenNode(_service, Slave, WithErrorBehavior(applications), ownsService: false, Clock);
            Settle(Node);
        }

        internal void Observe(BusState state)
        {
            _bus.BusState = state;
            Advance(Clock, Node, TimeSpan.FromMilliseconds(51));
        }

        public void Dispose()
        {
            Node.Dispose();
            _service.Dispose();
            _bus.Dispose();
        }
    }

    [Fact]
    public void Bus_Off_Is_A_Communication_Failure_Until_The_Controller_Recovers()
    {
        var app = Watcher();
        using var rig = new BusStateRig(app);

        rig.Observe(BusState.BusOff);
        Failures(app).Should().Equal("failure True");

        rig.Observe(BusState.ErrActive);
        Failures(app).Should().Equal("failure True", "failure False");
    }

    [Fact]
    public async Task An_Error_Raised_When_A_Reset_Ends_The_Failure_Is_Kept()
    {
        var app = Watcher();
        app.WhenFailureChanged = active =>
        {
            if (!active) app.Host!.RaiseError(0x5000, 0x04);
        };
        using var watch = new ProducerWatchRig(app);
        await watch.BeatAsync();
        watch.Silence();
        Failures(app).Should().Equal("failure True");

        await ResetFromWireAsync(watch.Rig, NmtCommand.ResetNode);
        await EmcyChainSettledAsync(watch.Rig.Node);

        Failures(app).Should().Equal("failure True", "failure False");
        ErrorRegister(watch.Rig.Node).Should().Be(0x05, "the reset's clearing is over before the application hears of it");
        watch.Rig.Wire.Payloads(SlaveEmcy).Should().Contain(frame => frame.SequenceEqual(Emcy(0x5000, 0x05)));
    }

    [Fact]
    public async Task A_Reset_Ends_A_Bus_Off_Failure_Too()
    {
        var app = Watcher();
        using var rig = new BusStateRig(app);
        rig.Observe(BusState.BusOff);
        Failures(app).Should().Equal("failure True");
        int mark = app.Log.Count;

        await rig.Node.PostToActorAsync(() => typeof(CanOpenNode)
            .GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, new[] { (byte)NmtCommand.ResetNode, Slave }, false }));
        Settle(rig.Node);

        // The controller is still bus-off: the sample the node takes afresh after the boot-up
        // (RefreshControllerErrorBaseline, an actor job queued by the reset) reports it again.
        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "failure False", "reset ResetNode", "nmt Initializing>PreOperational",
            "failure True");
    }

    [Fact]
    public async Task A_Callback_That_Ends_The_Failure_Leaves_The_Next_Application_With_The_Truth()
    {
        // The first watcher drops the watch from inside its callback: the failure it was just told
        // about is over before the second watcher's turn.
        var first = Watcher("first");
        var second = Watcher("second");
        bool inCallback = false;
        bool reentered = false;
        first.WhenFailureChanged = active =>
        {
            if (inCallback) reentered = true;
            inCallback = true;
            if (active) first.Host!.ObjectDictionary.WriteUnsigned(0x1016, 1, 0);
            inCallback = false;
        };
        using var watch = new ProducerWatchRig(first, second);
        await watch.BeatAsync();

        watch.Silence();

        Failures(first).Should().Equal("failure True", "failure False");
        reentered.Should().BeFalse("what the first application did in its callback is not reported into it");
        Failures(second).Should().BeEmpty("it is told what is true when its turn comes, and a failure that is over is not news");
        await OnActor(watch.Rig.Node, () => second.Host!.CommunicationFailureActive.Should().BeFalse());
    }

    [Fact]
    public async Task A_Reset_Ends_The_Failure()
    {
        var app = Watcher();
        using var watch = new ProducerWatchRig(app);
        await watch.BeatAsync();
        watch.Silence();
        Failures(app).Should().Equal("failure True");
        int mark = app.Log.Count;

        await ResetFromWireAsync(watch.Rig, NmtCommand.ResetNode);

        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "failure False", "reset ResetNode", "nmt Initializing>PreOperational");
    }
}
