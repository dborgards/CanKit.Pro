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
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen;

// Written entries: who validates a write to a claimed entry, who is told about it, and who decides
// that a TPDO goes out for it.
public partial class CanOpenApplicationSeamTests
{
    private const uint SdoRequest = 0x600 + Slave;
    private const uint SdoResponse = 0x580 + Slave;
    private const uint Tpdo1 = 0x180 + Slave;
    private const uint Tpdo2 = 0x280 + Slave;
    private const uint Rpdo1 = 0x200 + Slave;

    private static readonly CanOpenIndexRange Claim6000 = new(0x6000, 0x67FF);

    /// <summary>The claimant of <c>6000h</c>-<c>67FFh</c>. It declares four of its entries and, outside the claim,
    /// <c>2100h</c> and <c>2101h</c>, which belong to the node's own dictionary.</summary>
    private static RecordingApplication Entries(string name = "entries") => new(name, Claim6000)
    {
        OnAttach = host =>
        {
            var od = host.ObjectDictionary;
            od.AddU8(0x6000, 0x01, 5);
            od.AddU8(0x6000, 0x02, 0);
            od.AddU8(0x6100, 0x00, 0);
            od.AddU8(0x6200, 0x01, 0);
            od.AddU8(0x2100, 0x00, 0);
            od.AddU8(0x2101, 0x00, 0);
        },
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    private static IEnumerable<string> Written(RecordingApplication app) => app.Log.Where(e => e.StartsWith("written", StringComparison.Ordinal));

    /// <summary>The number of items the node's actor has queued and not yet started. The mailbox is
    /// private; its depth is the quantity a coalescing writer is about, and nothing else shows it.
    /// The profile rigs read it too (<see cref="CanOpenProfileRig.MailboxDepth"/>).</summary>
    private static int MailboxDepth(CanOpenNode node) => CanOpenProfileRig.MailboxDepth(node);

    /// <summary>A raw channel on the virtual bus: records every data frame per COB-ID, and sends what
    /// a master or a PDO producer would.</summary>
    private sealed class RawWire : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly object _gate = new();
        private readonly Dictionary<uint, List<byte[]>> _frames = new();
        private readonly List<(uint CobId, byte[] Data)> _sequence = new();

        internal RawWire(string session, int channel)
        {
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var frame = e.CanFrame;
                if (frame.IsRemoteFrame) return;
                var data = frame.Data.ToArray();
                lock (_gate)
                {
                    if (!_frames.TryGetValue((uint)frame.ID, out var list)) _frames[(uint)frame.ID] = list = new List<byte[]>();
                    list.Add(data);
                    _sequence.Add(((uint)frame.ID, data));
                }
            };
        }

        /// <summary>Every data frame seen, across all COB-IDs, in the order the wire carried them.</summary>
        internal (uint CobId, byte[] Data)[] Sequence
        {
            get
            {
                lock (_gate) return _sequence.ToArray();
            }
        }

        internal int Count(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.Count : 0;
        }

        internal byte[][] Payloads(uint cobId)
        {
            lock (_gate) return _frames.TryGetValue(cobId, out var list) ? list.ToArray() : Array.Empty<byte[]>();
        }

        internal Task WaitForCountAsync(uint cobId, int count)
            => WaitUntilAsync(() => Count(cobId) >= count, $"{count} frame(s) on 0x{cobId:X3}, saw {Count(cobId)}");

        internal void Transmit(uint cobId, byte[] data) => _bus.Transmit(CanFrame.Classic((int)cobId, data));

        /// <summary>One SDO request to the slave, and its response.</summary>
        internal async Task<byte[]> SdoAsync(byte[] request)
        {
            // The response that answers the request, not the next frame by position: see
            // CanOpenProfileRig.SdoAsync.
            int before = Count(SdoResponse);
            Transmit(SdoRequest, request);
            byte[]? response = null;
            await WaitUntilAsync(() => (response = Payloads(SdoResponse).Skip(before)
                .FirstOrDefault(r => CanOpenProfileRig.Answers(request, r))) is not null, "the response to the SDO request");
            return response!;
        }

        internal Task<byte[]> DownloadU8Async(ushort index, byte subindex, byte value)
            => SdoAsync(new byte[] { 0x2F, (byte)index, (byte)(index >> 8), subindex, value, 0, 0, 0 });

        internal Task<byte[]> UploadAsync(ushort index, byte subindex)
            => SdoAsync(new byte[] { 0x40, (byte)index, (byte)(index >> 8), subindex, 0, 0, 0, 0 });

        public void Dispose() => _bus.Dispose();
    }

    private static void AssertSingleFrame(byte[][] frames, byte value)
    {
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(new[] { value });
    }

    private static uint AbortCode(byte[] response)
    {
        response[0].Should().Be(0x80, "the server aborted the transfer");
        return BitConverter.ToUInt32(response, 4);
    }

    /// <summary>The slave on a virtual bus with a raw channel beside it.</summary>
    private sealed class EntryRig : IDisposable
    {
        private readonly ICanBus _bus;
        internal readonly RawWire Wire;
        internal readonly CanOpenNode Node;

        internal EntryRig(CanOpenNodeOptions options, ITimeSource? clock = null, byte nodeId = Slave)
        {
            var session = NewSession();
            _bus = Open(session, 1);
            Wire = new RawWire(session, 2);
            Node = OpenNode(_bus, options, clock, nodeId: nodeId);
        }

        internal ObjectDictionary Od => Node.ObjectDictionary;

        /// <summary>TPDO 2 on the unclaimed <c>2101h</c>, the witness: a frame the node is still
        /// transmitting when <see cref="WitnessAsync"/> runs.</summary>
        internal void ConfigureWitness() => Node.ConfigureTpdo(2, new PdoMapping().Add(0x2101, 0x00, 8));

        /// <summary>Triggers the witness and waits for its frame, then for the barrier
        /// (<see cref="CanOpenTransmitBarrier"/>): the witness frame alone does not order the frames
        /// before it on the wire, as each takes its own trip through the thread pool; the barrier
        /// does, for what the actor decided before this call.</summary>
        internal async Task WitnessAsync()
        {
            // The COB-ID TPDO 2 has now: an LSS slave moves it to the node-id it is given.
            var witnessCobId = Od.ReadUnsigned(0x1801, 0x01) & 0x1FFFFFFFu;
            int before = Wire.Count(witnessCobId);
            await Node.TriggerTpdoAsync(2);
            await Wire.WaitForCountAsync(witnessCobId, before + 1);
            await CanOpenTransmitBarrier.DrainAsync(Node);
        }

        internal async Task StartAsync()
        {
            Wire.Transmit(0x000, new[] { (byte)NmtCommand.Start, Slave });
            await WaitUntilAsync(() => Node.State == NmtState.Operational, "Operational");
        }

        public void Dispose()
        {
            Node.Dispose();
            Wire.Dispose();
            _bus.Dispose();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Validation.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Write_To_A_Claimed_Entry_Asks_Its_Claimant()
    {
        var app = Entries();
        var asked = new List<string>();
        app.Validate = (index, subindex, value, _) =>
        {
            lock (asked) asked.Add($"{index:X4}:{subindex:X2}={value[0]}");
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        };
        using var rig = new EntryRig(With(app));

        var response = await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);

        AbortCode(response).Should().Be(0x06090030u);
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(5u, "a rejected write is not stored");
        lock (asked) asked.Should().Equal("6000:01=7");
    }

    [Fact]
    public async Task Only_The_Claimants_Host_Identifies_A_Write_As_Its_Own()
    {
        var claimant = Entries();
        var other = new RecordingApplication("other");
        claimant.Validate = (_, _, _, isOwnWrite) => isOwnWrite
            ? OdWriteDecision.Accept : OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        using var rig = new EntryRig(With(claimant, other));

        claimant.Host!.WriteOwn(0x6000, 1, new byte[] { 7 });
        FluentActions.Invoking(() => other.Host!.WriteOwn(0x6000, 1, new byte[] { 8 }))
            .Should().Throw<ArgumentException>().WithMessage("*0x06010002*");
        AbortCode(await rig.Wire.DownloadU8Async(0x6000, 1, 9)).Should().Be(0x06010002u);
        rig.Od.ReadUnsigned(0x6000, 1).Should().Be(7u);
    }

    [Fact]
    public async Task An_Unclaimed_Entry_Is_Not_The_Applications_Business()
    {
        var app = Entries();
        int asked = 0;
        app.Validate = (_, _, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        };
        using var rig = new EntryRig(With(app));

        var response = await rig.Wire.DownloadU8Async(0x2100, 0x00, 7);
        Settle(rig.Node);

        response[0].Should().Be(0x60, "the download succeeded");
        rig.Od.ReadUnsigned(0x2100, 0x00).Should().Be(7u);
        Volatile.Read(ref asked).Should().Be(0, "2100h lies outside the claim");
        Written(app).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Handled_Write_Is_Not_Stored()
    {
        var app = Entries();
        app.Validate = (_, _, _, _) => OdWriteDecision.Handled;
        using var rig = new EntryRig(With(app));

        var response = await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);
        Settle(rig.Node);

        response[0].Should().Be(0x60, "a handled write is accepted as a command");
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(5u, "and the stored value is the application's to change");
        Written(app).Should().BeEmpty("nothing was stored, so nothing was written");
    }

    // -----------------------------------------------------------------------------------------
    // Notification.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_Sdo_Download_Is_Reported_On_The_Actor()
    {
        var app = Entries();
        int actorThread = 0;
        int callbackThread = -1;
        NmtState? stateInCallback = null;
        app.WhenWritten = _ =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            stateInCallback = app.Host!.State;
        };
        using var rig = new EntryRig(With(app));
        await rig.Node.PostToActorAsync(() => actorThread = Environment.CurrentManagedThreadId);

        var response = await rig.Wire.DownloadU8Async(0x6200, 0x01, 9);
        Settle(rig.Node);

        response[0].Should().Be(0x60);
        Written(app).Should().Equal("written 6200:01");
        callbackThread.Should().Be(actorThread).And.NotBe(Environment.CurrentManagedThreadId);
        stateInCallback.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public async Task An_Rpdo_Is_Reported_On_The_Actor()
    {
        var app = Entries();
        int actorThread = 0;
        int callbackThread = -1;
        app.WhenWritten = _ => callbackThread = Environment.CurrentManagedThreadId;
        using var rig = new EntryRig(With(app));
        await rig.Node.PostToActorAsync(() => actorThread = Environment.CurrentManagedThreadId);
        rig.Node.ConfigureRpdo(1, new PdoMapping().Add(0x6200, 0x01, 8));
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.RpdoReceived += (_, _) => received.TrySetResult(true);
        await rig.StartAsync();

        rig.Wire.Transmit(Rpdo1, new byte[] { 0x42 });
        await received.Task.WithTimeoutAsync(ShortTimeout);
        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x6200, 0x01).Should().Be(0x42u);
        Written(app).Should().Equal("written 6200:01");
        callbackThread.Should().Be(actorThread);
    }

    [Fact] // Review Focus 2
    public async Task Many_Writes_Are_Reported_Once()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        using var release = new ManualResetEventSlim();
        var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = rig.Node.PostToActorAsync(() =>
        {
            holding.SetResult(true);
            release.Wait();
        });
        try
        {
            await holding.Task.WithTimeoutAsync(ShortTimeout);
            int baseline = MailboxDepth(rig.Node);

            for (uint i = 0; i < 1000; i++) rig.Od.WriteUnsigned(0x6000, 0x01, 10 + i % 200);

            (MailboxDepth(rig.Node) - baseline).Should().Be(1, "a thousand writes queue one evaluation, not a thousand");
        }
        finally
        {
            release.Set();
        }
        await hold;
        Settle(rig.Node);

        Written(app).Should().Equal("written 6000:01");
    }

    [Fact]
    public async Task Entries_Are_Reported_Sorted_And_Once()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        using var release = new ManualResetEventSlim();
        var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = rig.Node.PostToActorAsync(() =>
        {
            holding.SetResult(true);
            release.Wait();
        });
        try
        {
            await holding.Task.WithTimeoutAsync(ShortTimeout);
            rig.Od.WriteUnsigned(0x6200, 0x01, 1);
            rig.Od.WriteUnsigned(0x6000, 0x02, 1);
            rig.Od.WriteUnsigned(0x6100, 0x00, 1);
            rig.Od.WriteUnsigned(0x6000, 0x01, 1);
            rig.Od.WriteUnsigned(0x6000, 0x02, 2);
            rig.Od.WriteUnsigned(0x2100, 0x00, 1); // not claimed: not reported
        }
        finally
        {
            release.Set();
        }
        await hold;
        Settle(rig.Node);

        Written(app).Should().Equal("written 6000:01,6000:02,6100:00,6200:01");
    }

    [Fact] // Review Focus 3
    public void The_Applications_Own_Write_In_A_Callback_Is_Not_Reported()
    {
        var app = Entries();
        bool wrote = false;
        app.WhenWritten = _ =>
        {
            if (wrote) return;
            wrote = true;
            app.Host!.ObjectDictionary.WriteUnsigned(0x6000, 0x02, 9);
        };
        using var rig = new EntryRig(With(app));

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        Settle(rig.Node);

        wrote.Should().BeTrue();
        rig.Od.ReadUnsigned(0x6000, 0x02).Should().Be(9u, "the application's own write was stored");
        Written(app).Should().Equal("written 6000:01");
    }

    [Fact]
    public void One_Applications_Write_In_A_Callback_Is_Reported_To_The_Other()
    {
        var first = Entries("first");
        var second = new RecordingApplication("second", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            OnAttach = host => host.ObjectDictionary.AddU8(0x6800, 0x01, 0),
        };
        first.WhenWritten = _ => first.Host!.ObjectDictionary.WriteUnsigned(0x6800, 0x01, 3);
        using var rig = new EntryRig(With(first, second));

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        Settle(rig.Node);

        Written(second).Should().Equal(new[] { "written 6800:01" }, "only a claimant's own writes are kept from it");
    }

    [Fact]
    public void A_Write_During_Attach_Is_Not_Reported()
    {
        var app = Entries();
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 8);
        };
        using var rig = new EntryRig(With(app));

        Settle(rig.Node);

        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(8u);
        Written(app).Should().BeEmpty("the application declared that value itself, while the node was opening");
    }

    [Fact] // Review Focus 1
    public async Task A_Throwing_Callback_Is_A_Background_Exception()
    {
        var first = Entries("first");
        var second = new RecordingApplication("second", new CanOpenIndexRange(0x6800, 0x6FFF))
        {
            OnAttach = host => host.ObjectDictionary.AddU8(0x6800, 0x01, 0),
        };
        first.WhenWritten = _ => throw new InvalidOperationException("first application failed");
        using var rig = new EntryRig(With(first, second));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);
        using var release = new ManualResetEventSlim();
        var holding = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = rig.Node.PostToActorAsync(() =>
        {
            holding.SetResult(true);
            release.Wait();
        });
        try
        {
            await holding.Task.WithTimeoutAsync(ShortTimeout);
            // Both land in one evaluation: the first claimant's failure must not keep the second from being told.
            rig.Od.WriteUnsigned(0x6000, 0x01, 7);
            rig.Od.WriteUnsigned(0x6800, 0x01, 7);
        }
        finally
        {
            release.Set();
        }
        await hold;

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        Settle(rig.Node);
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("first application failed");
        Written(second).Should().Equal("written 6800:01");

        // The node carries on: a later write is reported, and the SDO server still answers.
        rig.Od.WriteUnsigned(0x6800, 0x01, 8);
        Settle(rig.Node);
        Written(second).Should().Equal("written 6800:01", "written 6800:01");
        var upload = await rig.Wire.UploadAsync(0x6000, 0x01);
        upload[0].Should().Be(0x4F);
        upload[4].Should().Be(7);
    }

    [Fact]
    public async Task A_Reset_Does_Not_Report_The_Restored_Entries()
    {
        var app = DeclaringApplication();
        using var rig = new ResetRig(app);
        await rig.Bootups.First;
        rig.Od.WriteUnsigned(0x6000, 0x01, 9);
        Settle(rig.SlaveNode);
        Written(app).Should().Equal("written 6000:01");
        int before = app.Log.Count;

        await rig.ResetAsync(NmtCommand.ResetNode);
        Settle(rig.SlaveNode);

        rig.Value.Should().Be(1u, "the reset did write the entry back");
        app.Log.Skip(before).Where(e => e.StartsWith("written", StringComparison.Ordinal)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Who decides a transmission.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Claimed_Entry_Does_Not_Transmit_By_Itself()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 0x01, 8));
        rig.ConfigureWitness();
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await rig.WitnessAsync();
        Settle(rig.Node);

        rig.Wire.Count(Tpdo1).Should().Be(0, "an application that claims the entry decides whether it goes out");
        Written(app).Should().Equal(new[] { "written 6000:01" }, "the claimant is still told");
    }

    [Fact]
    public void A_Startup_Transmission_List_Must_Claim_Its_Entries()
    {
        var app = Entries();
        app.OperationalTransmissionEntries = new[] { new CanOpenObjectAddress(0x1001, 0) };

        OpenFails(With(app)).Message.Should().Contain(app.Name).And.Contain("1001:00");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Profile_Event_Tpdos_On_Start_Are_Opt_In(bool request, int expected)
    {
        var app = Entries();
        if (request) app.OperationalTransmissionEntries = Array.Empty<CanOpenObjectAddress>();
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x2100, 0, 8));
        rig.Od.WriteUnsigned(0x1800, 2, 0xFF);
        rig.ConfigureWitness();

        await rig.StartAsync();
        await rig.WitnessAsync();

        rig.Wire.Count(Tpdo1).Should().Be(expected);
    }

    [Fact]
    public async Task The_Claimant_Requests_The_Transmission()
    {
        var app = Entries();
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 0x01, 8));
        rig.ConfigureWitness();
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await rig.Wire.WaitForCountAsync(Tpdo1, 1);
        await rig.WitnessAsync();

        AssertSingleFrame(rig.Wire.Payloads(Tpdo1), 7);
    }

    [Fact]
    public async Task A_Requested_Transmission_Ignores_The_Automatism_Switch()
    {
        var app = Entries();
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(new CanOpenNodeOptions { EnableChangeOfStateTpdo = false }.WithApplication(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 0x01, 8));
        rig.ConfigureWitness();
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        await rig.Wire.WaitForCountAsync(Tpdo1, 1);
        await rig.WitnessAsync();

        AssertSingleFrame(rig.Wire.Payloads(Tpdo1), 7);
    }

    [Fact]
    public async Task A_Requested_Acyclic_Tpdo_Is_Sent_Once_On_The_Next_Sync()
    {
        var app = Entries();
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(new CanOpenNodeOptions { EnableChangeOfStateTpdo = false }.WithApplication(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 1, 8), TpdoTransmission.SynchronousAcyclic);
        rig.Node.ConfigureTpdo(2, new PdoMapping().Add(0x2101, 0, 8), TpdoTransmission.Synchronous);
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 1, 0x5A);
        await CanOpenTransmitBarrier.DrainAsync(rig.Node);
        rig.Wire.Count(Tpdo1).Should().Be(0, "the request waits for SYNC");

        // The cyclic TPDO witnesses each SYNC. Then drain the sends from that same actor job.
        for (int sync = 1; sync <= 2; sync++)
        {
            rig.Wire.Transmit(0x080, Array.Empty<byte>());
            await rig.Wire.WaitForCountAsync(Tpdo2, sync);
            await CanOpenTransmitBarrier.DrainAsync(rig.Node);
            AssertSingleFrame(rig.Wire.Payloads(Tpdo1), 0x5A);
        }
    }

    [Fact]
    public async Task A_Requested_Transmit_Srdo_Sends_The_Mapped_Value_And_Its_Inverse()
    {
        var app = Entries();
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        var options = new CanOpenNodeOptions { SrdoCount = 1, EnableChangeOfStateSrdo = false };
        // Hold time before the first cyclic SRDO. Only the application request can send this pair.
        using var rig = new EntryRig(options.WithApplication(app), new ManualTimeSource());
        var safety = rig.Node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x6000, 1, 8), TimeSpan.FromMilliseconds(100));
        safety.CommitSafetyConfiguration();
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 1, 0x3C);
        const uint first = 0x0FFu + 2 * Slave;
        await rig.Wire.WaitForCountAsync(first + 1, 1);

        AssertSingleFrame(rig.Wire.Payloads(first), 0x3C);
        AssertSingleFrame(rig.Wire.Payloads(first + 1), 0xC3);
    }

    [Fact]
    public async Task A_Requested_Transmission_Outside_Operational_Is_Dropped()
    {
        var app = Entries();
        app.WhenWritten = entries => app.Host!.RequestTransmission(entries);
        using var rig = new EntryRig(With(app));
        // Pins the rule: nothing is sent for a request made outside Operational, neither at once
        // nor on the first SYNC after the node starts (TPDO 1 is the kind that latches for a SYNC).
        // It does not detect the guard in TriggerChangeOfState being removed: the guards behind it
        // (RequestEventDrivenTransmission, OnEnterOperational clearing the latch, SrdoEngine.Trigger)
        // hold the same line. TPDO 2 answers the SYNC in the same actor job as TPDO 1, so its frame
        // says the SYNC has been handled; it does not order TPDO 1's frame on the wire, as each frame
        // takes its own trip through the thread pool. The barrier does: once it opens, whatever
        // TPDO 1 sent on that SYNC has been seen (CanOpenTransmitBarrier).
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 0x01, 8), TpdoTransmission.SynchronousAcyclic);
        rig.Node.ConfigureTpdo(2, new PdoMapping().Add(0x2101, 0x00, 8), TpdoTransmission.Synchronous);

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);
        Settle(rig.Node);
        await rig.StartAsync();
        rig.Wire.Transmit(0x080, Array.Empty<byte>());
        await rig.Wire.WaitForCountAsync(Tpdo2, 1);
        await CanOpenTransmitBarrier.DrainAsync(rig.Node);

        rig.Wire.Count(Tpdo1).Should().Be(0, "PDOs exist only in Operational, and the request is not kept for later");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Request_With_A_Foreign_Entry_Is_Refused_In_Full(bool includeOwnedEntryFirst)
    {
        var app = Entries();
        var owned = new CanOpenObjectAddress(0x6000, 1);
        var foreign = new CanOpenObjectAddress(0x2100, 0);
        app.WhenWritten = _ => app.Host!.RequestTransmission(includeOwnedEntryFirst ? [owned, foreign] : [foreign]);
        using var rig = new EntryRig(new CanOpenNodeOptions { EnableChangeOfStateTpdo = false }.WithApplication(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x6000, 1, 8).Add(0x2100, 0, 8));
        rig.ConfigureWitness();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x6000, 0x01, 7);

        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        await rig.WitnessAsync();
        failure.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("2100:00");
        rig.Wire.Count(Tpdo1).Should().Be(0, "a rejected list transmits nothing, including its valid prefix");
    }

    [Fact]
    public void A_Request_Off_The_Actor_Is_Refused()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));

        Action request = () => app.Host!.RequestTransmission(new[] { new CanOpenObjectAddress(0x6000, 0x01) });

        request.Should().Throw<InvalidOperationException>().WithMessage("*actor loop*");
    }

    [Fact]
    public async Task An_Unclaimed_Entry_Still_Transmits_By_Itself()
    {
        var app = Entries();
        using var rig = new EntryRig(With(app));
        rig.Node.ConfigureTpdo(1, new PdoMapping().Add(0x2100, 0x00, 8));
        rig.ConfigureWitness();
        await rig.StartAsync();

        rig.Od.WriteUnsigned(0x2100, 0x00, 7);
        await rig.Wire.WaitForCountAsync(Tpdo1, 1);
        await rig.WitnessAsync();

        AssertSingleFrame(rig.Wire.Payloads(Tpdo1), 7);
    }

    // -----------------------------------------------------------------------------------------
    // A failing application does not take the node's own paths with it.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Throwing_Validation_Aborts_The_Write_And_Is_A_Background_Exception()
    {
        var app = Entries();
        app.Validate = (_, _, _, _) => throw new InvalidOperationException("validation failed");
        using var rig = new EntryRig(With(app));
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Node.BackgroundExceptionOccurred += (_, ex) => reported.TrySetResult(ex);

        // The writing thread is the actor loop here: the throw must not travel into the SDO server.
        var response = await rig.Wire.DownloadU8Async(0x6000, 0x01, 7);

        AbortCode(response).Should().Be((uint)SdoAbortCode.General);
        rig.Od.ReadUnsigned(0x6000, 0x01).Should().Be(5u, "a write whose validation failed is not stored");
        var failure = await reported.Task.WithTimeoutAsync(ShortTimeout);
        failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("validation failed");
        var upload = await rig.Wire.UploadAsync(0x6000, 0x01);
        upload[0].Should().Be(0x4F, "the server still answers");
        upload[4].Should().Be(5);
        Written(app).Should().BeEmpty();
    }

    [Fact]
    public async Task An_Applications_Deadline_Write_Is_Not_Reported_Back_To_It()
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource();
        // Completed after the write has returned, and so after the node has queued whatever it
        // queues for it: what the actor is handed afterwards runs behind that.
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = Entries();
        var declare = app.OnAttach!;
        // Armed in Attach: the constructing thread, before the node has run anything.
        app.OnAttach = host =>
        {
            declare(host);
            host.Arm(TimeSpan.FromMilliseconds(50), () =>
            {
                host.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 9);
                ran.SetResult(true);
            });
        };
        using var node = OpenNode(bus, With(app), clock);

        Advance(clock, node, TimeSpan.FromMilliseconds(50));
        await ran.Task.WithTimeoutAsync(ShortTimeout);
        Settle(node);

        node.ObjectDictionary.ReadUnsigned(0x6000, 0x01).Should().Be(9u, "the deadline ran");
        Written(app).Should().BeEmpty("a write the application makes in its own callback is not news to it");
    }

    [Fact]
    public async Task A_Throwing_Deadline_Is_A_Background_Exception_And_The_Node_Keeps_Working()
    {
        using var bus = Open(NewSession(), 1);
        var clock = new ManualTimeSource();
        var app = Entries();
        var declare = app.OnAttach!;
        app.OnAttach = host =>
        {
            declare(host);
            host.Arm(TimeSpan.FromMilliseconds(50), () => throw new InvalidOperationException("deadline failed"));
        };
        using var node = OpenNode(bus, With(app), clock);
        var reported = new List<Exception>();
        node.BackgroundExceptionOccurred += (_, ex) => { lock (reported) reported.Add(ex); };

        Advance(clock, node, TimeSpan.FromMilliseconds(50));
        node.ObjectDictionary.WriteUnsigned(0x6000, 0x01, 7);
        Settle(node);

        await WaitUntilAsync(() => { lock (reported) return reported.Count > 0; }, "the background exception");
        lock (reported) reported.Should().ContainSingle().Which.Message.Should().Be("deadline failed");
        Written(app).Should().Equal(new[] { "written 6000:01" }, "the node still tells the application about writes");
    }
}
