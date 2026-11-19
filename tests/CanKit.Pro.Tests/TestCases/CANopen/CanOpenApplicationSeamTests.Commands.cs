using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// Command entries (#557): every committed write of a command entry reaches its claimant, in commit
// order and with the value it stored, also when several land before the actor loop comes round.
public partial class CanOpenApplicationSeamTests
{
    /// <summary>The command entry of <see cref="Commands"/>: a control word, UNSIGNED16.</summary>
    private static readonly CanOpenObjectAddress ControlWord = new(0x6040, 0x00);

    /// <summary>The queue bound the node documents (<see cref="ICanOpenApplication.OnCommandWritten"/>),
    /// stated once, by the node.</summary>
    private const int CommandQueueBound = CanOpenNode.CommandQueueCapacity;

    /// <summary>The claimant of <c>6000h</c>-<c>67FFh</c> with one command entry, <c>6040h:00</c> at
    /// <c>0001h</c>, and an ordinary entry <c>6000h:01</c>; <c>2101h</c> outside the claim is the
    /// witness TPDO's object.</summary>
    private static RecordingApplication Commands(string name = "commands") => new(name, Claim6000)
    {
        OnAttach = host =>
        {
            var od = host.ObjectDictionary;
            od.AddU16(0x6040, 0x00, 0x0001);
            od.AddU8(0x6000, 0x01, 0);
            od.AddU8(0x2101, 0x00, 0);
        },
        CommandEntries = new[] { ControlWord },
    };

    private static IEnumerable<string> CommandCalls(RecordingApplication app)
        => app.Log.Where(e => e.StartsWith("command", StringComparison.Ordinal));

    private static string Command(ushort value) => $"command 6040:00={value & 0xFF:X2}{value >> 8:X2}";

    private static byte[] DownloadU16(ushort index, byte subindex, ushort value)
        => new byte[] { 0x2B, (byte)index, (byte)(index >> 8), subindex, (byte)value, (byte)(value >> 8), 0, 0 };

    /// <summary>Holds the node's actor in a job of its own while <paramref name="whileHeld"/> runs, so
    /// that what the test does there lands before the actor comes round; then releases it.</summary>
    private static async Task WhileActorHeldAsync(CanOpenNode node, Func<Task> whileHeld)
    {
        using var release = new ManualResetEventSlim();
        var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = node.PostToActorAsync(() =>
        {
            holding.SetResult(true);
            release.Wait();
        });
        try
        {
            await holding.Task.WithTimeoutAsync(ShortTimeout);
            await whileHeld();
        }
        finally
        {
            release.Set();
        }
        await hold;
    }

    private static Task WhileActorHeldAsync(CanOpenNode node, Action whileHeld)
        => WhileActorHeldAsync(node, () =>
        {
            whileHeld();
            return Task.CompletedTask;
        });

    /// <summary>Sends <paramref name="frames"/> from the raw wire while the actor is held, and waits
    /// until the reader has handed each to the actor: they then run before anything the test queues
    /// afterwards.</summary>
    private static Task QueueFramesAsync(EntryRig rig, params (uint CobId, byte[] Data)[] frames)
    {
        int baseline = MailboxDepth(rig.Node);
        foreach (var (cobId, data) in frames) rig.Wire.Transmit(cobId, data);
        return WaitUntilAsync(() => MailboxDepth(rig.Node) - baseline >= frames.Length, $"{frames.Length} frame(s) in the actor's mailbox");
    }

    // -----------------------------------------------------------------------------------------
    // Delivery.
    // -----------------------------------------------------------------------------------------

    [Fact] // Review Focus 1, #557
    public async Task Two_Command_Writes_Before_The_Actor_Drains_Are_Both_Delivered_In_Order()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        ushort readBack = 0;

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
            readBack = (ushort)rig.Od.ReadUnsigned(0x6040, 0x00);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        Settle(rig.Node);

        readBack.Should().Be(0x0009, "the first value can be read back right after its write");
        CommandCalls(app).Should().Equal(Command(0x0009), Command(0x0001));
        Written(app).Should().BeEmpty("a command entry is told through OnCommandWritten only");
    }

    [Fact]
    public async Task The_Same_Value_Written_Twice_Is_Delivered_Twice()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        Settle(rig.Node);

        CommandCalls(app).Should().Equal(Command(0x0001), Command(0x0001));
    }

    [Fact]
    public async Task Each_Call_Carries_A_Value_Of_Its_Own()
    {
        var app = Commands();
        var values = new List<byte[]>();
        app.WhenCommand = (_, value) =>
        {
            values.Add(value);
            if (values.Count == 1) value[0] = 0xEE; // must change nothing the node or a later call holds
        };
        using var rig = new EntryRig(With(app));

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0102);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0102);
        });
        Settle(rig.Node);

        values.Should().HaveCount(2);
        values[1].Should().Equal(new byte[] { 0x02, 0x01 }, "each call gets its own copy of the committed value");
        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0x0102u);
    }

    [Fact] // Review Focus 2 of the seam, for commands
    public async Task Many_Command_Writes_Queue_One_Evaluation()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        int depth = -1;

        await WhileActorHeldAsync(rig.Node, () =>
        {
            int baseline = MailboxDepth(rig.Node);
            for (int i = 0; i < 200; i++) rig.Od.WriteUnsigned(0x6040, 0x00, (uint)i);
            rig.Od.WriteUnsigned(0x6000, 0x01, 7);
            depth = MailboxDepth(rig.Node) - baseline;
        });
        Settle(rig.Node);

        depth.Should().Be(1, "commands and entries share one queued job, not one per write");
        CommandCalls(app).Should().Equal(Enumerable.Range(0, 200).Select(i => Command((ushort)i)));
    }

    [Fact]
    public async Task Commands_Are_Delivered_Before_The_Coalesced_Entries()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6000, 0x01, 1);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
            rig.Od.WriteUnsigned(0x6000, 0x01, 2);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        Settle(rig.Node);

        app.Log.Where(e => e.StartsWith("command", StringComparison.Ordinal) || e.StartsWith("written", StringComparison.Ordinal))
            .Should().Equal(Command(0x0009), Command(0x0001), "written 6000:01");
    }

    [Fact]
    public async Task Sdo_Rpdo_And_Local_Command_Writes_Are_Delivered_In_Commit_Order()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureRpdo(1, new PdoMapping().Add(0x6040, 0x00, 16));
        await rig.StartAsync();

        // The SDO download and the RPDO reach the actor first and are committed after the local
        // write, which commits on the test's thread while the actor is held: commit order is local,
        // SDO, RPDO, whatever order they were sent in.
        await WhileActorHeldAsync(rig.Node, async () =>
        {
            await QueueFramesAsync(rig, (SdoRequest, DownloadU16(0x6040, 0x00, 0x0002)), (Rpdo1, new byte[] { 0x03, 0x00 }));
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        await rig.Wire.WaitForCountAsync(SdoResponse, 1);
        Settle(rig.Node);
        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0004);
        Settle(rig.Node);

        rig.Wire.Payloads(SdoResponse)[0][0].Should().Be(0x60, "the download was accepted");
        CommandCalls(app).Should().Equal(Command(0x0001), Command(0x0002), Command(0x0003), Command(0x0004));
    }

    [Fact]
    public async Task A_Throwing_Command_Callback_Does_Not_Keep_The_Next_Write_From_Its_Call()
    {
        var app = Commands();
        bool thrown = false;
        app.WhenCommand = (_, _) =>
        {
            if (thrown) return;
            thrown = true;
            throw new InvalidOperationException("command failed");
        };
        using var rig = new EntryRig(With(app));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        Settle(rig.Node);

        (await reported.Task.WithTimeoutAsync(ShortTimeout)).Message.Should().Be("command failed");
        CommandCalls(app).Should().Equal(Command(0x0009), Command(0x0001));
    }

    // -----------------------------------------------------------------------------------------
    // What is not delivered.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Refused_Command_Write_Is_Not_Delivered()
    {
        var app = Commands();
        int asked = 0;
        app.Validate = (index, _, value) =>
        {
            if (index != 0x6040) return OdWriteDecision.Accept;
            Interlocked.Increment(ref asked);
            return value[0] switch
            {
                0x09 => OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded),
                0x0A => OdWriteDecision.Handled,
                _ => OdWriteDecision.Accept,
            };
        };
        using var rig = new EntryRig(With(app));

        var refused = await rig.Wire.SdoAsync(DownloadU16(0x6040, 0x00, 0x0009));
        var handled = await rig.Wire.SdoAsync(DownloadU16(0x6040, 0x00, 0x000A));
        var tooShort = await rig.Wire.DownloadU8Async(0x6040, 0x00, 0x0B);
        int askedBeforeAccepted = Volatile.Read(ref asked);
        var accepted = await rig.Wire.SdoAsync(DownloadU16(0x6040, 0x00, 0x0002));
        Settle(rig.Node);

        AbortCode(refused).Should().Be((uint)SdoAbortCode.ValueRangeExceeded);
        handled[0].Should().Be(0x60, "a handled write is accepted as a command, and stores nothing");
        AbortCode(tooShort).Should().Be((uint)SdoAbortCode.LengthTooLow);
        askedBeforeAccepted.Should().Be(2, "the dictionary refuses a write of the wrong size before the application is asked");
        accepted[0].Should().Be(0x60);
        CommandCalls(app).Should().Equal(Command(0x0002));
    }

    [Fact] // Review Focus 3 of the seam, for commands
    public async Task The_Applications_Own_Command_Writes_Are_Not_Delivered()
    {
        var app = Commands();
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.ObjectDictionary.WriteUnsigned(0x6040, 0x00, 0x0005); // during Attach
        };
        app.WhenWritten = _ =>
        {
            app.Host!.ObjectDictionary.WriteUnsigned(0x6040, 0x00, 0x0006);
            app.Host.Post(() => app.Host.ObjectDictionary.WriteUnsigned(0x6040, 0x00, 0x0007));
        };
        using var rig = new EntryRig(With(app));

        rig.Od.WriteUnsigned(0x6000, 0x01, 1);
        Settle(rig.Node);
        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0008);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0x0008u);
        CommandCalls(app).Should().Equal(new[] { Command(0x0008) }, "the application's own writes are not news to it");
    }

    [Fact]
    public void Another_Applications_Command_Write_From_Its_Callback_Is_Delivered()
    {
        var first = Entries("first");
        var second = new RecordingApplication("second", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            OnAttach = host => host.ObjectDictionary.AddU16(0x6840, 0x00, 0),
            CommandEntries = new[] { new CanOpenObjectAddress(0x6840, 0x00) },
        };
        first.WhenWritten = _ => first.Host!.ObjectDictionary.WriteUnsigned(0x6840, 0x00, 0x0003);
        using var rig = new EntryRig(With(first, second));

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        Settle(rig.Node);

        CommandCalls(second).Should().Equal("command 6840:00=0300");
    }

    // -----------------------------------------------------------------------------------------
    // The bound.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Full_Command_Queue_Refuses_The_Write()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        Exception? local = null;

        // The download reaches the actor ahead of the job that drains the queue: it is handled with
        // the queue full.
        await WhileActorHeldAsync(rig.Node, async () =>
        {
            await QueueFramesAsync(rig, (SdoRequest, DownloadU16(0x6040, 0x00, 0xBEEF)));
            for (int i = 0; i < CommandQueueBound; i++) rig.Od.WriteUnsigned(0x6040, 0x00, (uint)i);
            local = Record.Exception(() => rig.Od.WriteUnsigned(0x6040, 0x00, 0xCAFE));
        });
        await rig.Wire.WaitForCountAsync(SdoResponse, 1);
        Settle(rig.Node);

        local.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("0x08000022");
        AbortCode(rig.Wire.Payloads(SdoResponse)[0]).Should().Be((uint)SdoAbortCode.DataCannotBeTransferredDeviceState);
        CommandCalls(app).Should().Equal(Enumerable.Range(0, CommandQueueBound).Select(i => Command((ushort)i)),
            "the queued writes are intact and nothing refused was delivered");
        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(CommandQueueBound - 1u, "a refused write is not stored");

        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        Settle(rig.Node);
        CommandCalls(app).Last().Should().Be(Command(0x0001), "a drained queue takes writes again");
    }

    [Fact]
    public async Task A_Full_Command_Queue_Refuses_An_Rpdo_Entry_And_The_Rest_Of_The_Rpdo_Is_Written()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureRpdo(1, new PdoMapping().Add(0x6040, 0x00, 16).Add(0x6000, 0x01, 8));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.RpdoReceived += (_, _) => received.TrySetResult(true);
        await rig.StartAsync();

        await WhileActorHeldAsync(rig.Node, async () =>
        {
            await QueueFramesAsync(rig, (Rpdo1, new byte[] { 0xEF, 0xBE, 0x42 }));
            for (int i = 0; i < CommandQueueBound; i++) rig.Od.WriteUnsigned(0x6040, 0x00, (uint)i);
        });
        await received.Task.WithTimeoutAsync(ShortTimeout);
        Settle(rig.Node);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        failure.Message.Should().Contain("RPDO1").And.Contain("0x6040:00").And.Contain("0x08000022");
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(0x42u, "the RPDO's other entry is written");
        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(CommandQueueBound - 1u);
        CommandCalls(app).Should().HaveCount(CommandQueueBound).And.NotContain(Command(0xBEEF));
    }

    [Fact]
    public async Task A_Refused_Write_Takes_No_Room_In_The_Queue()
    {
        var app = Commands();
        app.Validate = (index, subindex, value) =>
        {
            if (index != 0x6040) return OdWriteDecision.Accept;
            switch (value[1])
            {
                case 0xAA: return OdWriteDecision.Handled;
                case 0xBB: return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                case 0xCC:
                    // Accepted, and then refused by the dictionary: the entry is gone when the write
                    // is to be stored. The one refusal that can follow the application's acceptance.
                    app.Host!.ObjectDictionary.Remove(index, subindex);
                    return OdWriteDecision.Accept;
                default: return OdWriteDecision.Accept;
            }
        };
        // The filler writes 0 to bound − 2 go through the validator's default branch: their high byte,
        // value[1], must not be one of the markers above.
        ((CommandQueueBound - 2) >> 8).Should().BeLessThan(0xAA, "the filler values must not carry a marker in their high byte");
        using var rig = new EntryRig(With(app));
        var failures = new List<Exception?>();

        await WhileActorHeldAsync(rig.Node, () =>
        {
            for (int i = 0; i < CommandQueueBound - 1; i++) rig.Od.WriteUnsigned(0x6040, 0x00, (uint)i);
            failures.Add(Record.Exception(() => rig.Od.WriteRaw(0x6040, 0x00, new byte[] { 0x01 })));
            failures.Add(Record.Exception(() => rig.Od.WriteUnsigned(0x6040, 0x00, 0xAA00)));
            failures.Add(Record.Exception(() => rig.Od.WriteUnsigned(0x6040, 0x00, 0xBB00)));
            failures.Add(Record.Exception(() => rig.Od.WriteUnsigned(0x6040, 0x00, 0xCC00)));
            rig.Od.AddU16(0x6040, 0x00, 0x0000);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0100); // the last free place
            failures.Add(Record.Exception(() => rig.Od.WriteUnsigned(0x6040, 0x00, 0x0200)));
        });
        Settle(rig.Node);

        failures[0].Should().BeOfType<InvalidOperationException>("refused for its size");
        failures[1].Should().BeNull("a handled write is accepted, and stores nothing");
        failures[2].Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("0x06090030");
        failures[3].Should().BeOfType<KeyNotFoundException>("accepted, then not stored");
        failures[4].Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("0x08000022");
        CommandCalls(app).Should().Equal(
            Enumerable.Range(0, CommandQueueBound - 1).Select(i => Command((ushort)i)).Append(Command(0x0100)),
            "no refused write is delivered, and none took a place in the queue");
    }

    [Fact]
    public void A_Command_Entry_Gone_When_Its_Write_Is_Reported_Is_Told_As_Written_Not_As_A_Command()
    {
        // The entry disappears between the store and the node's report of it: a subscriber to the
        // dictionary's EntryWritten that was added in Attach runs before the node's own, and
        // ObjectDictionary.Remove does not take the write gate. The write has no value left to tell.
        var app = Commands();
        bool armed = false;
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.ObjectDictionary.EntryWritten += (index, subindex) =>
            {
                if (!armed || index != 0x6040) return;
                armed = false;
                host.ObjectDictionary.Remove(index, subindex);
            };
        };
        using var rig = new EntryRig(With(app));

        armed = true;
        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
        Settle(rig.Node);

        rig.Od.ContainsIndex(0x6040).Should().BeFalse("the subscriber removed the entry");
        CommandCalls(app).Should().BeEmpty("there is no committed value left to deliver");
        Written(app).Should().Equal("written 6040:00");
    }

    // -----------------------------------------------------------------------------------------
    // Reset and disposal.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Reset_Drops_The_Queued_Command_Writes_Before_The_Application_Hears_Of_It()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;
        Task? reset = null;

        // The reset job is queued ahead of the job the write queues: the write is committed, and
        // waits in the queue, when the reset comes.
        await WhileActorHeldAsync(rig.Node, () =>
        {
            reset = rig.Node.ActorForTests.PostAsync(() => typeof(CanOpenNode)
                .GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, ResetCommand(NmtCommand.ResetNode), false }));
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
        });
        await reset!;
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 2);
        Settle(rig.Node);

        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "reset ResetNode", "nmt Initializing>PreOperational");
        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0x0001u, "Reset Node restored the entry");
    }

    [Fact]
    public async Task A_Command_Write_Committed_During_A_Reset_Before_Its_Restore_Is_Not_Delivered()
    {
        // The window between the reset dropping the queue and restoring the dictionary, made
        // deterministic: another application writes the command entry from its notification of the
        // reset, which comes after the drop and before the restore. Reset Node then overwrites the
        // value, so delivering it after the reset would tell a value the entry no longer holds.
        var app = Commands();
        var other = new RecordingApplication("other", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            WhenNmtChanged = (_, current) =>
            {
                if (current == NmtState.Initializing) app.Host!.ObjectDictionary.WriteUnsigned(0x6040, 0x00, 0x0009);
            },
        };
        using var rig = new EntryRig(With(app, other));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);

        await ResetFromWireAsync(rig, NmtCommand.ResetNode);
        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0002);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0x0002u);
        CommandCalls(app).Should().Equal(new[] { Command(0x0002) },
            "the write committed before the restore belongs to the device the reset ended; the one after it is told");
    }

    [Fact]
    public async Task A_Command_Write_Committed_During_A_Reset_Communication_Is_Delivered()
    {
        // The window of the Reset Node test above, under Reset Communication: that reset restores
        // 1000h to 1FFFh only, so the command entry keeps the value written in the window, and the
        // application is told of it, after the reset.
        var app = Commands();
        var other = new RecordingApplication("other", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            WhenNmtChanged = (_, current) =>
            {
                if (current == NmtState.Initializing) app.Host!.ObjectDictionary.WriteUnsigned(0x6040, 0x00, 0x0009);
            },
        };
        using var rig = new EntryRig(With(app, other));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;

        await ResetFromWireAsync(rig, NmtCommand.ResetCommunication);

        rig.Od.ReadUnsigned(0x6040, 0x00).Should().Be(0x0009u, "Reset Communication leaves the application's objects alone");
        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "reset ResetCommunication", "nmt Initializing>PreOperational", Command(0x0009));
    }

    [Fact]
    public async Task A_Command_Write_Queued_Before_A_Reset_Communication_Is_Delivered_After_The_Reset()
    {
        // Committed while the actor is held, with the reset already queued ahead of the job that
        // delivers the write: the reset runs first, keeps the queue (the value survives it), and the
        // write is told in the next round, after the reset's notifications, in commit order.
        var app = Commands();
        using var rig = new EntryRig(With(app));
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 1);
        Settle(rig.Node);
        int mark = app.Log.Count;
        Task? reset = null;

        await WhileActorHeldAsync(rig.Node, () =>
        {
            reset = rig.Node.ActorForTests.PostAsync(() => typeof(CanOpenNode)
                .GetMethod("HandleIncoming", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig.Node, new object[] { CanOpenCobId.NmtCommand, ResetCommand(NmtCommand.ResetCommunication), false }));
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
        });
        await reset!;
        await rig.Wire.WaitForCountAsync(SlaveHeartbeat, 2);
        Settle(rig.Node);

        app.Log.Skip(mark).Should().Equal(
            "nmt PreOperational>Initializing", "reset ResetCommunication", "nmt Initializing>PreOperational",
            Command(0x0009), Command(0x0001));
    }

    [Fact]
    public async Task A_Node_Disposed_By_A_Command_Callback_Delivers_No_More()
    {
        var app = Commands();
        using var rig = new EntryRig(With(app));
        app.WhenCommand = (_, _) => rig.Node.Dispose();
        Task? after = null;

        await WhileActorHeldAsync(rig.Node, () =>
        {
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0001);
            rig.Od.WriteUnsigned(0x6040, 0x00, 0x0002);
            // Behind the job that delivers the writes; the actor runs what it holds as it ends.
            after = rig.Node.ActorForTests.PostAsync(() => { });
        });
        await after!.WithTimeoutAsync(ShortTimeout);

        CommandCalls(app).Should().Equal(new[] { Command(0x0009) }, "the node is gone after the first call");
    }

    // -----------------------------------------------------------------------------------------
    // What the open checks.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void A_Command_Entry_Outside_The_Claims_Fails_The_Open()
    {
        var app = Commands("outside");
        app.CommandEntries = new[] { ControlWord, new CanOpenObjectAddress(0x2101, 0x00) };

        OpenFails(With(app)).Message.Should().Contain("outside").And.Contain("2101:00");
    }

    [Fact]
    public void A_Command_Entry_That_Does_Not_Exist_Fails_The_Open()
    {
        var app = Commands("missing");
        app.CommandEntries = new[] { new CanOpenObjectAddress(0x6040, 0x01) };

        OpenFails(With(app)).Message.Should().Contain("missing").And.Contain("6040:01");
    }

    [Fact]
    public void A_Command_Entry_Of_Variable_Width_Fails_The_Open()
    {
        var app = Commands("domain");
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.ObjectDictionary.AddDomain(0x6050, 0x00, new byte[] { 1, 2 });
        };
        app.CommandEntries = new[] { new CanOpenObjectAddress(0x6050, 0x00) };

        OpenFails(With(app)).Message.Should().Contain("domain").And.Contain("6050:00");
    }

    [Fact]
    public void Null_Command_Entries_Fail_The_Open()
    {
        var app = Commands("nothing");
        app.CommandEntries = null!;

        OpenFails(With(app)).Message.Should().Contain("nothing").And.Contain("CommandEntries");
    }

    [Fact]
    public void Command_Entries_Are_Read_After_Attach()
    {
        var app = Commands("late");
        app.CommandEntries = Array.Empty<CanOpenObjectAddress>();
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            app.CommandEntries = new[] { ControlWord }; // what Attach declared decides
        };
        using var rig = new EntryRig(With(app));

        rig.Od.WriteUnsigned(0x6040, 0x00, 0x0009);
        Settle(rig.Node);

        CommandCalls(app).Should().Equal(Command(0x0009));
    }
}
