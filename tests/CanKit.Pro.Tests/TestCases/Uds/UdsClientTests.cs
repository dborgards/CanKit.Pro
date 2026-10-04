using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using CanKit.Pro.Uds;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// End-to-end integration tests for <see cref="IUdsClient"/> against a
/// <see cref="SimulatedUdsEcu"/> running over two ISO-TP channels on the Virtual bus.
/// Covers SRS FR-UDS-001..010 plus the FR-UDS-011 SHOULD (multi-DID read).
/// </summary>
public class UdsClientTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);

    private static string NewSession() => $"uds-{Guid.NewGuid():N}";

    private static ICanBus OpenClassic(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static IsoTpChannelOptions FastIsoTp() => new()
    {
        UseCanFd = false,
        UsePadding = true,
        NAs = TimeSpan.FromMilliseconds(500),
        NBs = TimeSpan.FromMilliseconds(500),
        NCr = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>
    /// Builds a client ↔ ECU pair over a fresh Virtual session, wires the ECU handlers via
    /// <paramref name="configure"/>, and returns everything the test needs to drive the client.
    /// The returned <see cref="IDisposable"/> tears down the whole stack in the right order.
    /// </summary>
    private static (IUdsClient client, SimulatedUdsEcu ecu, IDisposable dispose) BuildPair(
        Action<SimulatedUdsEcu> configure, UdsClientOptions? options = null, bool useCanFd = false,
        IsoTpChannelOptions? clientIsoTp = null)
    {
        var session = NewSession();
        var busClient = useCanFd ? OpenCanFd(session, 0) : OpenClassic(session, 0);
        var busEcu = useCanFd ? OpenCanFd(session, 1) : OpenClassic(session, 1);

        var clientEndpoint = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
        var ecuEndpoint = IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0);

        var clientChannel = IsoTpFactory.Open(busClient, clientEndpoint, clientIsoTp ?? FastIsoTp(useCanFd));
        var ecuChannel = IsoTpFactory.Open(busEcu, ecuEndpoint, FastIsoTp(useCanFd));

        var ecu = new SimulatedUdsEcu(ecuChannel);
        configure(ecu);
        // Start() blocks until the ECU receive loop is subscribed (no fixed Sleep race).
        ecu.Start();

        var client = UdsClient.Create(clientChannel, options);

        var dispose = new CompositeDisposable(client, ecu, ecuChannel, clientChannel, busEcu, busClient);
        return (client, ecu, dispose);
    }

    /// <summary>
    /// A client whose P2, P2* and suppressed-response windows are measured <em>and waited out</em>
    /// on a <see cref="VirtualClock"/>, opposite a <see cref="SimulatedUdsEcu"/> on the wall
    /// clock (#171). Nothing a test asserts about those windows then depends on how the host
    /// schedules: the client's waits end only when the test moves the clock, every frame is
    /// stamped with the virtual instant it arrived at, and <see cref="Service"/> says when a
    /// frame from the ECU has been handed to the client's actor.
    /// </summary>
    private sealed class ClockPair : IDisposable
    {
        private readonly IDisposable _stack;

        public ClockPair(Action<SimulatedUdsEcu> configure, UdsClientOptions? options = null,
            IsoTpChannelOptions? clientIsoTp = null)
        {
            var session = NewSession();
            var busClient = OpenClassic(session, 0);
            var busEcu = OpenClassic(session, 1);

            // A zero stamp means "unstamped" to the channel, so the clock starts off zero.
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            Actor = Clock.NewActor();
            Service = new FrameConsumptionCountingBusService(
                new CanBusService(busClient, Actor.TimeSource.GetTimestamp));
            Channel = new IsoTpChannel(Service, IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8),
                clientIsoTp ?? FastIsoTp(useCanFd: false), ownsService: true, Actor);
            var ecuChannel = IsoTpFactory.Open(busEcu, IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0),
                FastIsoTp(useCanFd: false));

            EcuBus = busEcu;
            Ecu = new SimulatedUdsEcu(ecuChannel);
            configure(Ecu);
            Ecu.Start();
            Client = UdsClient.Create(Channel, Actor, options);
            _stack = new CompositeDisposable(Client, Ecu, ecuChannel, Channel, busEcu, busClient);
        }

        public VirtualClock Clock { get; } = new();

        public ProtocolActor Actor { get; }

        public FrameConsumptionCountingBusService Service { get; }

        public IsoTpChannel Channel { get; }

        public SimulatedUdsEcu Ecu { get; }

        /// <summary>The ECU's raw bus, for a frame its ISO-TP channel would not send.</summary>
        public ICanBus EcuBus { get; private set; } = null!;

        public IUdsClient Client { get; }

        /// <summary>
        /// Returns once the client has armed a timer exactly <paramref name="remaining"/> away:
        /// the wait the test is about to end by moving the clock, and the one a client waiting on
        /// something else would not have armed.
        /// </summary>
        public Task WaitUntilWaitingAsync(TimeSpan remaining)
            => Clock.WaitUntilTimerArmedAsync(Actor, remaining, ShortTimeout);

        /// <summary>
        /// Runs <paramref name="release"/> -- whatever makes the ECU answer -- and returns once
        /// the client's actor has taken in the frame it sends, a Single Frame matching
        /// <paramref name="match"/>. Its stamp is then the instant the clock stood at, and the
        /// clock may be moved again.
        /// </summary>
        public async Task DeliverAsync(Action release, Func<byte[], bool> match)
        {
            var taken = Service.WaitUntilConsumedAsync(e =>
                e.Frame.ID == 0x7E8 && match(e.Frame.Data.ToArray()));
            release();
            if (await Task.WhenAny(taken, Task.Delay(ShortTimeout)) != taken)
                throw new TimeoutException("The ECU's frame did not reach the client.");
            await Clock.SettleAsync();
        }

        public void Dispose()
        {
            _stack.Dispose();
            Clock.Dispose();
        }
    }

    // The task's result, or a timeout rather than a hang when it never comes.
    private static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(ShortTimeout)) != task)
            throw new TimeoutException($"No result within {ShortTimeout}.");
        return await task;
    }

    // As Within<T>, for a task with no result -- a wait that hangs (a suppressed-window bug
    // left waiting forever on a clock nobody advances further) surfaces as a timeout here
    // rather than the test itself hanging.
    private static async Task Within(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(ShortTimeout)) != task)
            throw new TimeoutException($"No result within {ShortTimeout}.");
        await task;
    }

    // A Single Frame carrying a negative response [0x7F, sid, nrc].
    private static Func<byte[], bool> Negative(byte sid, byte nrc)
        => data => data.Length >= 4 && data[1] == 0x7F && data[2] == sid && data[3] == nrc;

    /// <summary>
    /// Stands in for <see cref="SimulatedUdsEcu.Delay"/>: each delay the ECU asks for waits until
    /// the test releases that delay by its length, after moving the clock to where it ends.
    /// </summary>
    private sealed class EcuSteps
    {
        private readonly Dictionary<TimeSpan, TaskCompletionSource<bool>> _gates = new();

        // Honours the ECU's token: a test that fails before releasing a gate still disposes the
        // ECU, and an ECU loop parked on a gate nobody opens would hang that disposal -- and the
        // test run with it -- instead of letting the failure be reported.
        public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetCanceled()))
                await await Task.WhenAny(Gate(delay).Task, cancelled.Task).ConfigureAwait(false);
        }

        public void Release(TimeSpan delay) => Gate(delay).TrySetResult(true);

        private TaskCompletionSource<bool> Gate(TimeSpan delay)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(delay, out var gate))
                    _gates[delay] = gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return gate;
            }
        }
    }

    /// <summary>
    /// Stands in for <see cref="SimulatedUdsEcu.Delay"/> like <see cref="EcuSteps"/>, but gates
    /// by call order rather than by the delay's length: needed whenever a scenario asks for the
    /// same duration more than once (two 0x78s spaced by the same gap), where <see cref="EcuSteps"/>
    /// would hand both callers the same, already-resolved gate.
    /// </summary>
    private sealed class OrderedGates
    {
        private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
        private readonly object _gate = new();
        private int _pendingReleases;

        // Symmetric with EcuSteps' own race-freedom (see its remarks): a ReleaseNext() that
        // arrives before the matching WaitAsync -- the ECU has not yet reached its gate on its
        // own thread -- must not be lost. It is banked instead, so the next WaitAsync call
        // returns at once.
        public Task WaitAsync(TimeSpan _, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_pendingReleases > 0)
                {
                    _pendingReleases--;
                    return Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(tcs);
                return tcs.Task.WaitAsync(ct);
            }
        }

        public void ReleaseNext()
        {
            TaskCompletionSource<bool>? tcs = null;
            lock (_gate)
            {
                if (_waiters.Count > 0) tcs = _waiters.Dequeue();
                else _pendingReleases++;
            }
            tcs?.TrySetResult(true);
        }
    }

    private static ICanBus OpenCanFd(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.CanFd).Fd(VirtualAdapterFixture.Bitrate, VirtualAdapterFixture.DataBitrate));

    private static IsoTpChannelOptions FastIsoTp(bool useCanFd) => new()
    {
        UseCanFd = useCanFd,
        UsePadding = true,
        NAs = TimeSpan.FromMilliseconds(500),
        NBs = TimeSpan.FromMilliseconds(500),
        NCr = TimeSpan.FromMilliseconds(500),
    };

    // -----------------------------------------------------------------------------------
    // CAN-FD matrix (FR-UDS over ISO-TP on CAN FD): the core diagnostic flows must behave
    // identically when the transport runs on CAN-FD frames instead of classic CAN.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task CanFd_DiagnosticSessionControl_And_ReadDid_Work_Like_On_Classic()
    {
        var (client, _, dispose) = BuildPair(e =>
        {
            e.On(0x10, req => new byte[] { req[1], 0x00, 0x32, 0x01, 0xF4 });
            e.On(0x22, req => new byte[] { req[1], req[2], 0x57, 0x42, 0x41 });
        }, useCanFd: true);
        using (dispose)
        {
            await client.DiagnosticSessionControlAsync(UdsSessionType.Extended,
                new CancellationTokenSource(ShortTimeout).Token);
            client.CurrentSession.Should().Be((byte)UdsSessionType.Extended);

            var data = await client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);
            data.Should().Equal(0x57, 0x42, 0x41);
        }
    }

    [Fact]
    public async Task CanFd_WriteDid_And_MultiFrame_Transfer_Work_Like_On_Classic()
    {
        var written = new List<byte>();
        var (client, _, dispose) = BuildPair(e => e.On(0x2E, req =>
        {
            written.Clear();
            written.AddRange(req);
            return new byte[] { req[1], req[2] };
        }), useCanFd: true);
        using (dispose)
        {
            // 300-byte DID forces a multi-frame ISO-TP transfer on both directions.
            var payload = Enumerable.Range(0, 300).Select(i => (byte)(i & 0xFF)).ToArray();
            await client.WriteDataByIdentifierAsync(0xF190, payload,
                new CancellationTokenSource(ShortTimeout).Token);

            written.Should().HaveCount(303, "0x2E + DID (2) + 300 payload bytes must arrive intact over CAN FD");
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-001 — DiagnosticSessionControl (0x10): tester requests Extended session,
    // ECU replies with the session parameter record; client tracks CurrentSession.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task DiagnosticSessionControl_Switches_Session_And_Returns_Parameter_Record()
    {
        var (client, ecu, dispose) = BuildPair(e => e.On(0x10, req =>
        {
            req.Should().Equal(0x10, (byte)UdsSessionType.Extended);
            return new byte[] { (byte)UdsSessionType.Extended, 0x00, 0x32, 0x01, 0xF4 };
        }));
        using (dispose)
        {
            client.CurrentSession.Should().Be((byte)UdsSessionType.Default);
            var record = await client.DiagnosticSessionControlAsync(UdsSessionType.Extended,
                new CancellationTokenSource(ShortTimeout).Token);

            record.Should().Equal(0x00, 0x32, 0x01, 0xF4);
            client.CurrentSession.Should().Be((byte)UdsSessionType.Extended);
            ecu.RequestsHandled.Should().Be(1);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-002 — ReadDataByIdentifier (0x22) single DID.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task ReadDataByIdentifier_Returns_Data_Record()
    {
        byte[] vin = System.Text.Encoding.ASCII.GetBytes("WBADT43452G296403"); // 17 bytes
        var (client, _, dispose) = BuildPair(e => e.On(0x22, req =>
        {
            req.Length.Should().Be(3);
            req[0].Should().Be(0x22);
            var did = (ushort)((req[1] << 8) | req[2]);
            did.Should().Be(0xF190);

            var body = new byte[2 + vin.Length];
            body[0] = 0xF1; body[1] = 0x90;
            Buffer.BlockCopy(vin, 0, body, 2, vin.Length);
            return body;
        }));
        using (dispose)
        {
            var data = await client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);
            data.Should().Equal(vin);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-003 — WriteDataByIdentifier (0x2E).
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task WriteDataByIdentifier_Completes_On_Positive_Response()
    {
        byte[] written = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        byte[]? received = null;
        var (client, _, dispose) = BuildPair(e => e.On(0x2E, req =>
        {
            req[0].Should().Be(0x2E);
            var did = (ushort)((req[1] << 8) | req[2]);
            did.Should().Be(0xF200);
            received = req.AsSpan(3).ToArray();
            return new byte[] { 0xF2, 0x00 };
        }));
        using (dispose)
        {
            await client.WriteDataByIdentifierAsync(0xF200, written,
                new CancellationTokenSource(ShortTimeout).Token);
            received.Should().Equal(written);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-004 — RoutineControl (0x31): exercise all three sub-functions.
    // -----------------------------------------------------------------------------------
    [Theory]
    [InlineData(UdsRoutineControlType.StartRoutine)]
    [InlineData(UdsRoutineControlType.StopRoutine)]
    [InlineData(UdsRoutineControlType.RequestRoutineResults)]
    public async Task RoutineControl_Round_Trips_All_Three_SubFunctions(
        UdsRoutineControlType sub)
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x31, req =>
        {
            req[0].Should().Be(0x31);
            req[1].Should().Be((byte)sub);
            var rid = (ushort)((req[2] << 8) | req[3]);
            rid.Should().Be(0x0203);
            return new byte[] { (byte)sub, 0x02, 0x03, 0xAB, 0xCD };
        }));
        using (dispose)
        {
            var info = await client.RoutineControlAsync(sub, 0x0203,
                cancellationToken: new CancellationTokenSource(ShortTimeout).Token);
            info.Should().Equal(0xAB, 0xCD);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-005 — ECUReset (0x11). After a successful reset the ECU returns to the
    // default session, so CurrentSession must not keep a stale DiagnosticSessionControl
    // value (Bugbot 3597974544).
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task EcuReset_Completes_Before_Ecu_Would_Reboot()
    {
        var (client, _, dispose) = BuildPair(e =>
        {
            e.On(0x10, req =>
            {
                req.Should().Equal(0x10, (byte)UdsSessionType.Extended);
                return new byte[] { (byte)UdsSessionType.Extended, 0x00, 0x32, 0x01, 0xF4 };
            });
            e.On(0x11, req =>
            {
                req[0].Should().Be(0x11);
                req[1].Should().Be((byte)UdsEcuResetType.HardReset);
                return new byte[] { (byte)UdsEcuResetType.HardReset };
            });
        });
        using (dispose)
        {
            await client.DiagnosticSessionControlAsync(UdsSessionType.Extended,
                new CancellationTokenSource(ShortTimeout).Token);
            client.CurrentSession.Should().Be((byte)UdsSessionType.Extended);

            var tail = await client.EcuResetAsync(UdsEcuResetType.HardReset,
                new CancellationTokenSource(ShortTimeout).Token);
            tail.Should().BeEmpty();
            client.CurrentSession.Should().Be((byte)UdsSessionType.Default);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-006 — SecurityAccess (0x27): seed → caller-computed key → sendKey.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task SecurityAccess_Sends_ComputedKey_And_Accepts_Unlock()
    {
        byte[] seed = { 0xDE, 0xAD, 0xBE, 0xEF };
        byte[]? sentKey = null;
        bool unlocked = false;

        var (client, _, dispose) = BuildPair(e => e
            .On(0x27, req =>
            {
                req[0].Should().Be(0x27);
                var sub = req[1];
                if (sub == 0x01) // requestSeed
                {
                    var body = new byte[1 + seed.Length];
                    body[0] = 0x01;
                    Buffer.BlockCopy(seed, 0, body, 1, seed.Length);
                    return body;
                }
                if (sub == 0x02) // sendKey
                {
                    sentKey = req.AsSpan(2).ToArray();
                    unlocked = true;
                    return new byte[] { 0x02 };
                }
                throw new EcuNegativeResponse(0x12); // subFunctionNotSupported
            }));

        using (dispose)
        {
            await client.SecurityAccessAsync(
                requestSeedLevel: 0x01,
                computeKey: s => s.Select(b => (byte)(b ^ 0x55)).ToArray(),
                cancellationToken: new CancellationTokenSource(ShortTimeout).Token);

            unlocked.Should().BeTrue();
            sentKey.Should().Equal(seed.Select(b => (byte)(b ^ 0x55)).ToArray());
        }
    }

    // -----------------------------------------------------------------------------------
    // #29 — ISO 14229-1 §9.4.5.3: a seed of all zeroes says the level is already unlocked.
    // The client must complete without sending a key; a key for that seed earns NRC 0x24.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task SecurityAccess_Treats_An_AllZero_Seed_As_Already_Unlocked()
    {
        int keyRequests = 0;
        int keysComputed = 0;

        var (client, _, dispose) = BuildPair(e => e
            .On(0x27, req =>
            {
                if (req[1] == 0x01)
                    return new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 }; // the usual length, zeroes
                if (req[1] == 0x02)
                {
                    Interlocked.Increment(ref keyRequests);
                    throw new EcuNegativeResponse(0x24); // requestSequenceError
                }
                throw new EcuNegativeResponse(0x12);
            }));

        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            await client.SecurityAccessAsync(
                requestSeedLevel: 0x01,
                computeKey: s =>
                {
                    Interlocked.Increment(ref keysComputed);
                    return s.Select(b => (byte)(b ^ 0x55)).ToArray();
                },
                cancellationToken: cts.Token);

            keyRequests.Should().Be(0, "an all-zero seed means already unlocked; no sendKey");
            keysComputed.Should().Be(0);
        }
    }

    // The same repeat without an injected clock, where the delay is a real Task.Delay: the
    // request goes out again and succeeds. No timing is asserted -- how long the delay lasts is
    // what the clock test below proves; this only runs the wall-clock path end to end.
    [Fact]
    public async Task A_Busy_Repeat_With_A_Delay_Repeats_On_The_Wall_Clock()
    {
        var calls = 0;
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, req => Interlocked.Increment(ref calls) == 1
                ? throw new EcuNegativeResponse(0x21)
                : new byte[] { 0xF1, 0x90, 0xAA }),
            options: new UdsClientOptions { BusyRepeatRequestDelay = TimeSpan.FromMilliseconds(10) });
        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            (await client.ReadDataByIdentifierAsync(0xF190, cts.Token)).Should().Equal(0xAA);
            Volatile.Read(ref calls).Should().Be(2);
        }
    }

    // The clock-injecting overload (#171) guards its channel as the public one does.
    [Fact]
    public void Create_On_A_Clock_Rejects_A_Null_Channel()
    {
        using var clock = new VirtualClock();
        Action act = () => UdsClient.Create(null!, clock.NewActor());
        act.Should().Throw<ArgumentNullException>().WithParameterName("channel");
    }

    // Omitted options default as the public overload's do.
    [Fact]
    public void Create_On_A_Clock_Defaults_Omitted_Options()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var service = new StarvedReaderBusService();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
            FastIsoTp(useCanFd: false), ownsService: false, actor);
        using var client = UdsClient.Create(channel, actor);
        client.Options.P2ClientMax.Should().Be(new UdsClientOptions().P2ClientMax);
    }

    // #57 on a virtual clock (#171): NRC 0x21 repeats the request after BusyRepeatRequestDelay,
    // measured on the client's clock like its P2 -- the repeat goes out only once the test has
    // moved the clock past the delay.
    [Fact]
    public async Task A_Busy_Repeat_Waits_Its_Delay_On_The_Clients_Clock()
    {
        var calls = 0;
        using var pair = new ClockPair(
            e => e.On(0x22, req => Interlocked.Increment(ref calls) == 1
                ? throw new EcuNegativeResponse(0x21)
                : new byte[] { 0xF1, 0x90, 0xAA }),
            options: new UdsClientOptions { BusyRepeatRequestDelay = TimeSpan.FromMilliseconds(100) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(100)); // the busy delay, not P2
        Volatile.Read(ref calls).Should().Be(1, "the repeat waits for the delay");

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        (await Within(read)).Should().Equal(0xAA);
        Volatile.Read(ref calls).Should().Be(2);
    }

    // A caller cancelling during the busy delay ends the wait at once, as Task.Delay's token did.
    [Fact]
    public async Task A_Busy_Repeat_Delay_On_The_Clients_Clock_Ends_On_Cancellation()
    {
        using var pair = new ClockPair(
            e => e.On(0x22, req => throw new EcuNegativeResponse(0x21)),
            options: new UdsClientOptions { BusyRepeatRequestDelay = TimeSpan.FromMilliseconds(100) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(100));
        cts.Cancel();

        Func<Task> act = () => Within(read); // a delay deaf to the token would never end
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // -----------------------------------------------------------------------------------
    // #28 — ISO 14229-2: P2 ends with the *first* frame of the response. A multi-frame
    // response whose transfer outlasts P2 (here: paced by the client's own STmin) is not a
    // timeout; the remainder is the transport's, bounded by N_Cr.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task P2_Ends_With_The_First_Frame_Of_A_MultiFrame_Response()
    {
        // 90-byte response = FF (6 data bytes) + 12 CFs, paced by the client's advertised STmin
        // of 127 ms: the ECU needs 1.5 s of real time to deliver it. On a virtual clock (#171)
        // the client's P2 is made to expire in the middle of that transfer -- the clock is moved
        // past P2 once the First Frame is in -- and its N_Cr, on the same clock, never can. On
        // the wall clock the transfer was a race between 12 real CF gaps and a 500 ms N_Cr,
        // which macOS CI lost (#185, #188).
        var record = Enumerable.Range(0, 87).Select(i => (byte)i).ToArray();
        var p2 = TimeSpan.FromMilliseconds(500);

        using var pair = new ClockPair(
            e => e.On(0x22, req =>
            {
                var body = new byte[2 + record.Length];
                body[0] = 0xF1;
                body[1] = 0x90;
                Buffer.BlockCopy(record, 0, body, 2, record.Length);
                return body;
            }),
            options: new UdsClientOptions { P2ClientMax = p2, P2StarClientMax = p2 },
            clientIsoTp: new IsoTpChannelOptions
            {
                UseCanFd = false,
                UsePadding = true,
                NAs = TimeSpan.FromMilliseconds(500),
                NBs = TimeSpan.FromMilliseconds(500),
                NCr = TimeSpan.FromSeconds(10),
                LocalStMin = TimeSpan.FromMilliseconds(127),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (pair.Channel.GetReceptionsInProgress().Count == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The First Frame never arrived.");
            await Task.Delay(1);
        }
        await pair.Clock.AdvanceAsync(p2 + TimeSpan.FromMilliseconds(100)); // P2 runs out mid-transfer
        // The transfer really outlasted P2: otherwise the result below would hold for a client
        // that still measures P2 against the last frame. 1.4 s of CF gaps are still to come.
        pair.Channel.GetReceptionsInProgress().Should().NotBeEmpty();

        var data = await Within(read);
        data.Should().Equal(record);
    }

    // -----------------------------------------------------------------------------------
    // #28, Codex on #143 — the wait beyond P2 is for *this* request's response. A multi-frame
    // transfer for another service that began inside the budget does not extend it: the peer
    // is busy with that transfer, so the real answer cannot start in time anyway.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task P2_Is_Not_Extended_By_A_MultiFrame_Transfer_For_Another_Service()
    {
        // The ECU answers the RDBI request with silence, but first starts a 146-byte
        // ReadDTCInformation response (SID 0x59) and never finishes it: only its First Frame goes
        // on the bus. On a virtual clock (#171) the client's N_Cr cannot end that reception, so
        // it stays in progress for as long as the test runs. P2 is then moved past on the clock:
        // the client must time out, because that transfer is somebody else's answer -- the
        // sibling of P2_Ends_With_The_First_Frame_Of_A_MultiFrame_Response, where the transfer
        // in progress is this request's own and does extend the wait. A client fooled into
        // waiting for it never returns, and the test's own token ends it with a cancellation,
        // not a timeout: no bound on the wall clock is needed to tell the two apart.
        var p2 = TimeSpan.FromMilliseconds(500);
        ClockPair? stack = null;

        using var pair = stack = new ClockPair(
            e => e.On(0x22, req =>
            {
                // FF of a 146-byte (0x92) response for SID 0x59; the Consecutive Frames never follow.
                stack!.EcuBus.Transmit(CanFrame.Classic(0x7E8,
                    new byte[] { 0x10, 0x92, 0x59, 0x00, 0x00, 0x00, 0x00, 0x00 }, isExtendedFrame: false));
                throw new EcuSilent();
            }),
            options: new UdsClientOptions { P2ClientMax = p2, P2StarClientMax = p2 },
            clientIsoTp: new IsoTpChannelOptions
            {
                UseCanFd = false,
                UsePadding = true,
                NAs = TimeSpan.FromMilliseconds(500),
                NBs = TimeSpan.FromMilliseconds(500),
                NCr = TimeSpan.FromSeconds(10), // longer than the test: the reception outlives P2
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);

        var deadline = DateTime.UtcNow + ShortTimeout;
        while (pair.Channel.GetReceptionsInProgress().Count == 0)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The unrelated First Frame never arrived.");
            await Task.Delay(1);
        }
        await pair.Clock.AdvanceAsync(p2 + TimeSpan.FromMilliseconds(100)); // P2 runs out mid-transfer
        pair.Channel.GetReceptionsInProgress().Should().NotBeEmpty(
            "the unrelated transfer is still in progress when P2 runs out");

        Func<Task> act = () => read;
        await act.Should().ThrowAsync<UdsTimeoutException>(
            "an unrelated transfer must not hold the request past its budget");
    }

    // -----------------------------------------------------------------------------------
    // #57 — the collected UDS findings.
    // -----------------------------------------------------------------------------------

    // Bit 7 of the session type is suppressPosRspMsgIndication; masking it silently sent a
    // session the caller did not ask for. Rejected before anything is on the wire.
    [Theory]
    [InlineData(0x83)]
    [InlineData(0x00)]
    public async Task DiagnosticSessionControl_Rejects_A_Session_Type_Outside_01_To_7F(byte sessionType)
    {
        var (client, ecu, dispose) = BuildPair(e => e.On(0x10, req => new byte[] { req[1], 0x00, 0x32, 0x01, 0xF4 }));

        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            Func<Task> act = () => client.DiagnosticSessionControlAsync(sessionType, cts.Token);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            ecu.RequestsHandled.Should().Be(0, "nothing was sent");
        }
    }

    // A raw request with the suppress bit set gets no positive response; waiting P2 for one
    // ended in a timeout every time. It is sent and returns empty.
    [Fact]
    public async Task SendRaw_With_The_Suppress_Bit_Does_Not_Wait_For_A_Response()
    {
        var (client, ecu, dispose) = BuildPair(
            e => e.On(0x3E, req => Array.Empty<byte>()),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromMilliseconds(300) });

        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            var response = await client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token);
            response.Should().BeEmpty();

            // The frame reached the ECU: it counts a suppressed TesterPresent as handled.
            var deadline = Stopwatch.StartNew();
            while (ecu.RequestsHandled == 0 && deadline.Elapsed < ShortTimeout) await Task.Delay(5);
            ecu.RequestsHandled.Should().Be(1);
        }
    }

    // A contended lock with nobody subscribed to RequestLockContended is the production shape
    // of every request lock acquisition: the event is a test hook and normally has no listener
    // (#171).
    [Fact]
    public async Task A_Contended_Lock_Needs_No_Subscriber()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, req => new byte[] { req[1], req[2] }));
        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            var first = client.ReadDataByIdentifierAsync(0xF190, cts.Token);
            var second = client.ReadDataByIdentifierAsync(0xF191, cts.Token);
            await Task.WhenAll(first, second);
        }
    }

    // Codex on #150: a suppressed send may still draw a negative response, up to P2 after it.
    // The next request for the same service waits that window out rather than taking the
    // negative response as its own.
    [Fact]
    public async Task A_Late_Negative_Response_To_A_Suppressed_Send_Is_Not_The_Next_Requests()
    {
        // On a virtual clock (#171): the ECU holds its negative answer until the test lets it go,
        // so "late" is an instant on the clock rather than a sleep that must land on the right
        // side of the next request's send.
        using var answer = new ManualResetEventSlim();
        using var pair = new ClockPair(
            e => e.On(0x3E, req =>
            {
                if ((req[1] & 0x80) != 0)
                {
                    answer.Wait(ShortTimeout);          // late ...
                    throw new EcuNegativeResponse(0x12); // ... and negative, to the suppressed one
                }
                return new byte[] { 0x00 };
            }),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromMilliseconds(300) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: 300 ms
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        // The next request waits out what is left of the window -- 200 ms -- rather than going
        // out and arming a fresh P2 of 300.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(200));
        await pair.DeliverAsync(answer.Set, Negative(0x3E, 0x12)); // at 100 ms

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200)); // the window closes
        Func<Task> act = () => next;
        await act.Should().NotThrowAsync("the negative response belongs to the suppressed send");
        pair.Ecu.RequestsHandled.Should().Be(2);
    }

    // Codex and Bugbot on #150: the windows are per service. A suppressed send for another
    // service in between must not shorten the first's.
    [Fact]
    public async Task Suppressed_Send_Windows_Are_Kept_Per_Service()
    {
        using var answer = new ManualResetEventSlim();
        using var pair = new ClockPair(
            e => e
                .On(0x3E, req =>
                {
                    if ((req[1] & 0x80) != 0)
                    {
                        answer.Wait(ShortTimeout);
                        throw new EcuNegativeResponse(0x12);
                    }
                    return new byte[] { 0x00 };
                })
                .On(0x11, req => Array.Empty<byte>()),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromMilliseconds(300) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: to 300 ms
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));
        await pair.Client.SendRawAsync(new byte[] { 0x11, 0x81 }, cts.Token); // another service's: to 350 ms
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));

        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        // TesterPresent's own window, 200 ms from here -- not the later one's 250, and not none.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(200));
        await pair.DeliverAsync(answer.Set, Negative(0x3E, 0x12));

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        Func<Task> act = () => next;
        await act.Should().NotThrowAsync("the TesterPresent window is still open, whatever came after");
        pair.Ecu.RequestsHandled.Should().Be(3);
    }

    // Codex on #150: NRC 0x78 to a suppressed send says the final answer is still coming, up
    // to P2* later; the window moves out with it.
    [Fact]
    public async Task A_Pending_Answer_To_A_Suppressed_Send_Extends_Its_Window_By_P2Star()
    {
        // On a virtual clock (#171): the ECU's 0x78 and its following negative are each held on
        // a gate the test releases at the virtual instant they belong to.
        var steps = new EcuSteps();
        var pendingAt = TimeSpan.FromMilliseconds(50);
        var negativeAt = TimeSpan.FromMilliseconds(400); // after the 0x78, not from the send
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x3E, req =>
                {
                    if ((req[1] & 0x80) != 0)
                        throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                            delayBefore: pendingAt, delayAfter: negativeAt);
                    return new byte[] { 0x00 };
                });
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(300),
                P2StarClientMax = TimeSpan.FromMilliseconds(1500),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: 300 ms

        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(300));

        await pair.Clock.AdvanceAsync(pendingAt); // 50 ms: past nothing yet, inside the window
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x3E, 0x78));
        // The window moves out to P2* from the 0x78's arrival: 50 + 1500 = 1550 ms, 1500 from here.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(1500));

        await pair.Clock.AdvanceAsync(negativeAt); // 450 ms total: past the original P2 (300 ms)
        await pair.DeliverAsync(() => steps.Release(negativeAt), Negative(0x3E, 0x12));

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1100)); // the window closes at 1550
        Func<Task> act = () => next;
        await act.Should().NotThrowAsync("the negative answer belongs to the suppressed send");
        pair.Ecu.RequestsHandled.Should().Be(2);
    }

    // Bugbot on #150: a 0x78 already queued when the window is over still moves it out.
    [Fact]
    public async Task A_Queued_Pending_Answer_Still_Extends_A_Window_That_Has_Run_Out()
    {
        // On a virtual clock (#171): the 0x78 is delivered -- so it is queued, not merely on its
        // way -- before the test moves the clock past the window's end, which is the scenario
        // this test names (Bugbot on #150): a 0x78 already queued when the window is over still
        // moves it out.
        var steps = new EcuSteps();
        var pendingAt = TimeSpan.FromMilliseconds(20);
        var p2 = TimeSpan.FromMilliseconds(1500);
        var p2Star = TimeSpan.FromMilliseconds(2500);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x3E, req =>
                {
                    if ((req[1] & 0x80) != 0)
                        throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                            delayBefore: pendingAt, delayAfter: TimeSpan.FromSeconds(20));
                    return new byte[] { 0x00 };
                });
            },
            options: new UdsClientOptions { P2ClientMax = p2, P2StarClientMax = p2Star });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: 1500 ms

        await pair.Clock.AdvanceAsync(pendingAt); // 20 ms: well inside the window
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x3E, 0x78));

        await pair.Clock.AdvanceAsync(p2 - pendingAt); // the window is now over, as measured

        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        // The 0x78, already queued, moves the window out to P2* from its arrival: 20 + 2500 =
        // 2520 ms; the clock stands at p2 (1500 ms), so 1020 ms remain.
        var remaining = p2Star - p2 + pendingAt;
        await pair.WaitUntilWaitingAsync(remaining);
        await pair.Clock.AdvanceAsync(remaining);

        Func<Task> act = () => next;
        await act.Should().NotThrowAsync("the queued 0x78 moved the window out to P2* from its arrival");
        pair.Ecu.LastRequest.Should().BeEquivalentTo(new byte[] { 0x3E, 0x00 });
    }

    // Codex on #150: the converse -- a 0x78 queued after the window ran out answers nothing
    // the window covers, and does not revive it for a full P2*.
    [Fact]
    public async Task A_Queued_Pending_Answer_From_After_The_Windows_End_Does_Not_Revive_It()
    {
        // On a virtual clock (#171): the 0x78 is delivered, and so queued, only once the clock
        // already stands past the window's end -- late by construction, not by a race against a
        // sleep.
        var steps = new EcuSteps();
        var pendingAt = TimeSpan.FromMilliseconds(500);
        var p2 = TimeSpan.FromMilliseconds(200);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x3E, req =>
                {
                    if ((req[1] & 0x80) != 0)
                        throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                            delayBefore: pendingAt, delayAfter: TimeSpan.FromSeconds(3));
                    return new byte[] { 0x00 };
                });
            },
            options: new UdsClientOptions { P2ClientMax = p2, P2StarClientMax = TimeSpan.FromMilliseconds(2000) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: 200 ms

        await pair.Clock.AdvanceAsync(pendingAt); // 500 ms: past the window's own end (200 ms)
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x3E, 0x78));

        // Revived, the window would reach 2500 ms; not revived, nothing is left to wait out, and
        // this resolves without the clock moving any further -- the only way it *can* resolve,
        // since nothing here ever advances it that far.
        await Within(pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token));
    }

    // Codex on #150: a 0x78 stamped inside a suppressed send's window may still be on its way
    // through the channel's actor when the window is measured as over; the next request for
    // the service waits for the channel to settle before it reads the inbox empty.
    [Fact]
    public async Task A_Pending_Answer_Still_On_Its_Way_Through_The_Channel_Extends_The_Window()
    {
        // On a virtual clock (#171): the client's channel is opened on a VirtualClock actor, as
        // ClockPair does, so the property -- a 0x78 stamped inside the window may still be on
        // its way through the channel's actor when the window is measured as over -- is a fact
        // about the interval the client arms, not a race against a sleep.
        using var clock = new VirtualClock();
        clock.Advance(TimeSpan.FromMilliseconds(1)); // a zero stamp means "unstamped" to the channel
        using var actor = clock.NewActor();
        using var service = new StarvedReaderBusService();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
            FastIsoTp(useCanFd: false), ownsService: false, actor);
        var pendingBudget = TimeSpan.FromMilliseconds(600);
        using var client = UdsClient.Create(channel, actor, new UdsClientOptions
        {
            P2ClientMax = TimeSpan.FromMilliseconds(100),
            P2StarClientMax = pendingBudget,
        });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // suppressed: window P2 = 100 ms

        // The ECU's 0x78, stamped at the send -- inside the window by construction, not by a
        // timer -- buffered by the demux; the reader task that would take it to the actor is
        // starved by construction.
        long arrival = clock.Elapsed.Ticks;
        byte[] sf = { 0x03, 0x7F, 0x3E, 0x78, 0x00, 0x00, 0x00, 0x00 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), arrival);

        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // the window has run out, as measured

        var next = client.SendRawAsync(new byte[] { 0x3E, 0x00 }, cts.Token);
        // The window moves out to P2* from the 0x78's arrival, settled in before the deadline is
        // read as empty: 600 ms from the arrival stamp, 450 ms from here.
        var remaining = TimeSpan.FromTicks(arrival + pendingBudget.Ticks - clock.Elapsed.Ticks);
        await clock.WaitUntilTimerArmedAsync(actor, remaining, ShortTimeout);
        await clock.AdvanceAsync(remaining); // the window closes; the real request goes out

        // Nothing answers it either: its own P2 must now run out too.
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(100), ShortTimeout);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        Func<Task> nextFn = () => next;
        await nextFn.Should().ThrowAsync<UdsTimeoutException>();
    }

    // Codex on #150: a suppressed request the channel refuses before transmitting -- here,
    // longer than a classic-CAN ISO-TP PDU can be -- reaches no ECU, and leaves no window for
    // the next request to wait out.
    [Fact]
    public async Task A_Suppressed_Send_The_Channel_Refuses_Leaves_No_Window()
    {
        // On a virtual clock (#171): a window left behind by mistake would have this call wait
        // out P2 = 2 s on a clock nothing here ever advances, so it can only resolve -- inside
        // Within's real timeout -- if no window was left at all.
        using var pair = new ClockPair(
            e => e.On(0x3E, req => new byte[] { 0x00 }),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromSeconds(2), P2StarClientMax = TimeSpan.FromSeconds(2) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var oversized = new byte[4200];
        oversized[0] = 0x3E;
        oversized[1] = 0x80;
        Func<Task> refused = () => pair.Client.SendRawAsync(oversized, cts.Token);
        await refused.Should().ThrowAsync<ArgumentOutOfRangeException>();

        await Within(pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token));
    }

    // Bugbot on #150: a refused request puts the window back to what it was -- an earlier
    // suppressed send's window still open stays open, and the next request still waits it out.
    [Fact]
    public async Task A_Suppressed_Send_The_Channel_Refuses_Leaves_An_Earlier_Window_As_It_Was()
    {
        // On a virtual clock (#171): the earlier window's survival is proven by the exact
        // interval the next call arms, not by a real elapsed-time floor.
        using var pair = new ClockPair(
            e => e.On(0x3E, req => new byte[] { 0x00 }),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromSeconds(2), P2StarClientMax = TimeSpan.FromSeconds(2) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // suppressed: a 2 s window, not waited out by the next suppressed send
        var oversized = new byte[4200];
        oversized[0] = 0x3E;
        oversized[1] = 0x80;
        Func<Task> refused = () => pair.Client.SendRawAsync(oversized, cts.Token);
        await refused.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        // The unsuppressed TesterPresent waits the first send's window out: 2 s, unshortened by
        // the refused send in between.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromSeconds(2));
        await pair.Clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await Within(next);
    }

    // Codex on #150: the discard on an aborted request -- here, one the caller cancelled --
    // settles the channel and routes what it holds before dropping it, as the pre-send
    // discard does; a 0x78 for a suppressed send still on its way is not dropped unrouted.
    [Fact]
    public async Task A_Pending_Answer_Still_On_Its_Way_Is_Routed_By_An_Aborted_Requests_Discard()
    {
        // On a virtual clock (#171), as A_Pending_Answer_Still_On_Its_Way_Through_The_Channel_
        // Extends_The_Window above: only the frame's stamp and the client's own timers need to
        // be virtual. The race the comment below describes is about *ordering* (the frame must
        // be delivered before the abort, once the send is visible on the wire) rather than about
        // *when* on a clock, so the poll for service.Sent.Count stays a bounded real poll of an
        // observable condition.
        using var clock = new VirtualClock();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var actor = clock.NewActor();
        using var service = new StarvedReaderBusService();
        using var channel = new IsoTpChannel(service, IsoTpEndpoint.Normal(0x7E0, 0x7E8),
            FastIsoTp(useCanFd: false), ownsService: false, actor);
        var pendingBudget = TimeSpan.FromMilliseconds(600);
        using var client = UdsClient.Create(channel, actor, new UdsClientOptions
        {
            P2ClientMax = TimeSpan.FromMilliseconds(100),
            P2StarClientMax = pendingBudget,
        });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // suppressed: window P2 = 100 ms

        // Another service's request, cancelled by its caller once the request is on the wire.
        // The 0x78 is stamped right after the request's synchronous start -- after its pre-send
        // discard's stamp, which would otherwise drop the frame as older, and inside the window
        // by construction rather than by a timer (macOS CI on #150) -- and delivered while the
        // request waits, before its abort, whose discard is the one under test. Cancelling from
        // here, once the send is visible, does not depend on a timer firing first. (A host that
        // delays the delivery past the abort lets the next call's wait-out route it instead: a
        // pass for the wrong reason, never a failure.)
        using var early = new CancellationTokenSource();
        var other = client.ReadDataByIdentifierAsync(0xF190, early.Token);
        long arrival = clock.Elapsed.Ticks;
        var sent = DateTime.UtcNow + ShortTimeout;
        while (service.Sent.Count < 2)
        {
            if (DateTime.UtcNow > sent) throw new TimeoutException("the request was not transmitted");
            await Task.Delay(5);
        }
        byte[] sf = { 0x03, 0x7F, 0x3E, 0x78, 0x00, 0x00, 0x00, 0x00 };
        service.Deliver(new CanFrameView(CanFrameType.Can20, 0x7E8, sf, FrameFlags.None), arrival);
        early.Cancel();
        Func<Task> cancelled = () => other;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var next = client.SendRawAsync(new byte[] { 0x3E, 0x00 }, cts.Token);
        // The window moves out to P2* from the 0x78's arrival: 600 ms from the arrival stamp,
        // and the clock has not moved since, so 600 ms from here too.
        var remaining = TimeSpan.FromTicks(arrival + pendingBudget.Ticks - clock.Elapsed.Ticks);
        await clock.WaitUntilTimerArmedAsync(actor, remaining, ShortTimeout);
        await clock.AdvanceAsync(remaining); // the window closes; the real request goes out

        // Nothing answers it either: its own P2 must now run out too.
        await clock.WaitUntilTimerArmedAsync(actor, TimeSpan.FromMilliseconds(100), ShortTimeout);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        Func<Task> nextFn = () => next;
        await nextFn.Should().ThrowAsync<UdsTimeoutException>(
            "the aborted request's discard routed the 0x78 to the suppressed send's window");
    }

    // Codex on #150: a late 0x78 to an earlier request for the service, queued since that
    // request was cancelled, is not the suppressed send's; the suppressed send discards it
    // before it goes out, and the next call does not wait P2* on it.
    [Fact]
    public async Task A_Stale_Pending_Answer_Queued_Before_A_Suppressed_Send_Does_Not_Extend_Its_Window()
    {
        // On a virtual clock (#171): the earlier request's own P2 wait, its cancellation, and
        // the stale 0x78's delivery are all driven by the test rather than raced against sleeps.
        var steps = new EcuSteps();
        int calls = 0;
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x3E, req =>
                {
                    switch (Interlocked.Increment(ref calls))
                    {
                        case 1: // the request the caller cancels: its 0x78 is stale by the time the suppressed send discards it
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: TimeSpan.FromMilliseconds(100), delayAfter: TimeSpan.FromSeconds(3));
                        case 2: // the suppressed send: no answer
                            throw new EcuSilent();
                        default:
                            return new byte[] { 0x00 };
                    }
                });
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(100),
                P2StarClientMax = TimeSpan.FromMilliseconds(2000),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        using var early = new CancellationTokenSource();
        var cancelled = pair.Client.SendRawAsync(new byte[] { 0x3E, 0x00 }, early.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(100)); // call #1 waiting on its own P2
        early.Cancel();
        Func<Task> cancelledFn = () => cancelled;
        await cancelledFn.Should().ThrowAsync<OperationCanceledException>();

        // The stale 0x78 (its 100 ms delay) is queued before the suppressed send opens its window.
        await pair.DeliverAsync(() => steps.Release(TimeSpan.FromMilliseconds(100)), Negative(0x3E, 0x78));

        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // suppressed: window P2 = 100 ms

        var next = pair.Client.SendRawAsync(new byte[] { 0x3E, 0x00 }, cts.Token);
        // Read as this send's, the stale 0x78 would move the window out to 2100 ms; the exact
        // interval armed here is the suppressed send's own 100 ms, unextended.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(100));
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        var answer = await Within(next);
        answer.Should().Equal(0x7E, 0x00);
    }

    // Bugbot on #150: a wait cancelled part-way keeps what remains of the window.
    [Fact]
    public async Task A_Cancelled_Wait_Keeps_The_Rest_Of_The_Window()
    {
        using var answer = new ManualResetEventSlim();
        using var pair = new ClockPair(
            e => e.On(0x3E, req =>
            {
                if ((req[1] & 0x80) != 0)
                {
                    answer.Wait(ShortTimeout);
                    throw new EcuNegativeResponse(0x12);
                }
                return new byte[] { 0x00 };
            }),
            options: new UdsClientOptions { P2ClientMax = TimeSpan.FromMilliseconds(400) });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // its window: 400 ms
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));

        using var early = new CancellationTokenSource();
        var cancelled = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, early.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(350)); // waiting out the window
        early.Cancel();
        Func<Task> cancel = () => cancelled;
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(50));

        // The negative is still coming; the rest of the window -- 300 ms -- must still be honoured.
        var next = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(300));
        await pair.DeliverAsync(answer.Set, Negative(0x3E, 0x12));

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(300));
        Func<Task> act = () => next;
        await act.Should().NotThrowAsync("the remaining window survived the cancelled wait");
        pair.Ecu.RequestsHandled.Should().Be(2);
    }

    // Codex on #150: a 0x78 for service B, heard while waiting out service A's window, moves
    // B's window out; it is not dropped as A's noise.
    [Fact]
    public async Task A_Pending_Answer_For_Another_Service_Heard_During_A_Wait_Extends_That_Services_Window()
    {
        // On a virtual clock (#171): B's 0x78 is delivered while A's TesterPresent is actively
        // waiting out A's window, at the exact instant the test moves the clock to, and B's
        // window's extension is then proven by the exact interval B's own next request arms --
        // stronger than the original's implicit race against a slow positive reply, and not
        // dependent on one.
        var steps = new EcuSteps();
        var pendingAt = TimeSpan.FromMilliseconds(50);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x3E, req =>
                    {
                        if ((req[1] & 0x80) != 0) throw new EcuSilent();
                        return new byte[] { 0x00 };
                    })
                    .On(0x11, req =>
                    {
                        if ((req[1] & 0x80) != 0)
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: pendingAt, delayAfter: TimeSpan.FromMilliseconds(500));
                        return new byte[] { 0x01 };
                    });
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(400),
                P2StarClientMax = TimeSpan.FromMilliseconds(1500),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // A, silent: window(0x3E) to 400 ms
        await pair.Client.SendRawAsync(new byte[] { 0x11, 0x81 }, cts.Token); // B, suppressed: window(0x11) to 400 ms too

        // A's request waits A's window out and hears B's 0x78 meanwhile.
        var a = pair.Client.TesterPresentAsync(suppressPositiveResponse: false, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(400));

        await pair.Clock.AdvanceAsync(pendingAt); // 50 ms
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x11, 0x78)); // heard as B's, not A's

        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(350)); // A's own window, unaffected
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(350)); // A's window closes at 400 ms
        await Within(a);

        // B's request follows: B's window now reaches P2* past the 0x78 -- 1550 ms, 1150 from here.
        var reset = pair.Client.SendRawAsync(new byte[] { 0x11, 0x01 }, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(1150));
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1150));

        var result = await Within(reset);
        result.Should().Equal(0x51, 0x01);
    }

    // Codex on #150: a 0x78 for service A, consumed as a stray while service B's request runs
    // (or dropped by B's pre-send discard), still moves A's window out.
    [Fact]
    public async Task A_Pending_Answer_Consumed_As_Another_Requests_Stray_Still_Extends_Its_Window()
    {
        // On a virtual clock (#171), each ECU answer released at its instant: the 400 ms and
        // 200 ms sleeps that placed them before were a 200 ms margin a loaded runner overran.
        var steps = new EcuSteps();
        using var answerB = new ManualResetEventSlim();
        var pendingAt = TimeSpan.FromMilliseconds(100);  // A's 0x78, while B's request is out
        var negativeAt = TimeSpan.FromMilliseconds(650); // A's final negative, 650 ms after it
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x11, req =>
                    {
                        if ((req[1] & 0x80) != 0)
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: pendingAt, delayAfter: negativeAt);
                        return new byte[] { 0x01 };
                    })
                    .On(0x22, req =>
                    {
                        answerB.Wait(ShortTimeout); // B's request is on the wire while A's 0x78 arrives
                        return new byte[] { 0xF1, 0x90, 0xAA };
                    });
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(600),
                P2StarClientMax = TimeSpan.FromMilliseconds(2000),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x11, 0x81 }, cts.Token); // A, suppressed: its window to 600 ms
        var b = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);      // B, another service
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(600));     // B's P2

        await pair.Clock.AdvanceAsync(pendingAt);
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x11, 0x78)); // B consumes it as a stray
        answerB.Set();
        await Within(b);

        // A's request follows: its window now reaches P2* past the 0x78 -- 2100 ms, 2000 from here.
        // Without the routing it would end at 600 ms, before A's negative at 750 is on the wire.
        var a = pair.Client.SendRawAsync(new byte[] { 0x11, 0x01 }, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(2000));

        await pair.Clock.AdvanceAsync(negativeAt);
        await pair.DeliverAsync(() => steps.Release(negativeAt), Negative(0x11, 0x12)); // at 750 ms

        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1350)); // the window closes at 2100
        var reset = await Within(a);
        reset.Should().Equal(0x51, 0x01);
    }

    // Codex on #150: a 0x78 for service A that arrives after A's window has run out answers
    // nothing the window still covers; routed from B's wait, it must not revive A's window
    // for a full P2*, or A's next request waits seconds for nothing.
    [Fact]
    public async Task A_Pending_Answer_From_After_A_Windows_End_Does_Not_Revive_It()
    {
        // On a virtual clock (#171): A's late 0x78 is delivered once B's own receive loop is
        // waiting for it, at an instant already past A's window's end -- late by construction --
        // and "not revived" is then proven by A's next request resolving without the clock
        // moving any further, the only way it can resolve if nothing was revived.
        var steps = new EcuSteps();
        var pendingAt = TimeSpan.FromMilliseconds(500);
        var bGap = TimeSpan.FromMilliseconds(700);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x11, req =>
                    {
                        if ((req[1] & 0x80) != 0)
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: pendingAt, delayAfter: TimeSpan.FromSeconds(3));
                        return new byte[] { 0x01 };
                    })
                    .On(0x22, req => throw new EcuResponsePending(pendingCount: 1,
                        finalResponse: new byte[] { 0xF1, 0x90, 0xAA }, delayBetween: bGap));
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(200),
                P2StarClientMax = TimeSpan.FromMilliseconds(2000),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x11, 0x81 }, cts.Token); // A, suppressed: window(0x11) to 200 ms

        // B's own first 0x78 goes out the instant its request is handled, with nothing pacing
        // it -- on a fast host that can land before a check for B's initial P2 would even see
        // it armed, so what is checked directly is the interval B restarts to, which only a
        // client that has already received and processed that 0x78 could have armed.
        var b = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(2000)); // B's P2*, restarted
        await pair.Clock.AdvanceAsync(pendingAt); // 500 ms: past A's window's end (200 ms)
        await pair.DeliverAsync(() => steps.Release(pendingAt), Negative(0x11, 0x78)); // A's late 0x78, heard as B's stray

        // B's own wait is unaffected: 2000 ms from its 0x78 at 1 ms, minus the 500 ms elapsed.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(1500));
        await pair.Clock.AdvanceAsync(bGap - pendingAt); // 200 ms further: B's own gap (700 ms) ends
        await pair.DeliverAsync(() => steps.Release(bGap), data => data.Length >= 2 && data[1] == 0x62); // B's final response
        var data = await Within(b);
        data.Should().Equal(0xAA);

        // A's next request: the window ran out at 200 ms, and the late 0x78 did not reopen it --
        // revived, it would reach 2500 ms, and this resolves without the clock moving any
        // further, which is only possible if nothing was revived.
        var reset = await Within(pair.Client.SendRawAsync(new byte[] { 0x11, 0x01 }, cts.Token));
        reset.Should().Equal(0x51, 0x01);
    }

    // Codex on #150: the same, heard while another service's wait-out runs rather than by a
    // request's stray branch.
    [Fact]
    public async Task A_Pending_Answer_From_After_A_Windows_End_Heard_In_A_Wait_Out_Does_Not_Revive_It()
    {
        // On a virtual clock (#171): B's late 0x78 is delivered while A is actively waiting out
        // A's own (self-extended) window -- the "heard in a wait-out" path, as opposed to the
        // sibling test's "heard by a request's stray branch" -- again proven by exact intervals
        // rather than a race against a sleep.
        var steps = new EcuSteps();
        var aPendingAt = TimeSpan.FromMilliseconds(100);
        var bPendingAt = TimeSpan.FromMilliseconds(1500);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = steps.WaitAsync;
                e.On(0x11, req =>
                    {
                        if ((req[1] & 0x80) != 0)
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: bPendingAt, delayAfter: TimeSpan.FromSeconds(3));
                        return new byte[] { 0x01 };
                    })
                    .On(0x3E, req =>
                    {
                        if ((req[1] & 0x80) != 0)
                            throw new EcuResponsePendingThenNegative(pendingCount: 1, nrc: 0x12,
                                delayBefore: aPendingAt, delayAfter: TimeSpan.FromSeconds(3));
                        return new byte[] { 0x00 };
                    });
            },
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(200),
                P2StarClientMax = TimeSpan.FromMilliseconds(2000),
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        await pair.Client.SendRawAsync(new byte[] { 0x11, 0x81 }, cts.Token); // B, suppressed: window(0x11) to 200 ms
        await pair.Client.SendRawAsync(new byte[] { 0x3E, 0x80 }, cts.Token); // A, suppressed: window(0x3E) to 200 ms too

        var again = pair.Client.SendRawAsync(new byte[] { 0x3E, 0x00 }, cts.Token); // A again: waits its own window out
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(200));

        await pair.Clock.AdvanceAsync(aPendingAt); // 100 ms: A's own 0x78 arrives
        await pair.DeliverAsync(() => steps.Release(aPendingAt), Negative(0x3E, 0x78));
        // A's own window moves out to P2* from its 0x78: 100 + 2000 = 2100 ms, 2000 from here.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(2000));

        await pair.Clock.AdvanceAsync(bPendingAt - aPendingAt); // 1500 ms total: B's late 0x78 arrives
        await pair.DeliverAsync(() => steps.Release(bPendingAt), Negative(0x11, 0x78)); // heard in A's own wait-out
        // A's wait is unaffected: 2100 ms total, 600 ms from here.
        await pair.WaitUntilWaitingAsync(TimeSpan.FromMilliseconds(600));
        await pair.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(600)); // A's window closes at 2100 ms
        await Within(again);

        // B's next request: revived, B's window would reach 3500 ms; unrevived, it resolves
        // without the clock moving any further.
        var reset = await Within(pair.Client.SendRawAsync(new byte[] { 0x11, 0x01 }, cts.Token));
        reset.Should().Equal(0x51, 0x01);
    }

    // NRC 0x21 asks for a repeat; the client repeats, up to MaxBusyRepeatRequests.
    [Fact]
    public async Task BusyRepeatRequest_Is_Repeated_Until_The_Server_Answers()
    {
        int calls = 0;
        var (client, ecu, dispose) = BuildPair(e => e.On(0x22, req =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new EcuNegativeResponse(0x21);
            return new byte[] { 0xF1, 0x90, 0xAB };
        }));

        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            var data = await client.ReadDataByIdentifierAsync(0xF190, cts.Token);
            data.Should().Equal(0xAB);
            ecu.RequestsHandled.Should().Be(3, "two busy answers, then the data");
        }
    }

    // Codex on #150: a negative repeat count is a configuration mistake, not "disabled".
    [Fact]
    public void A_Negative_Busy_Repeat_Count_Is_Rejected_At_Construction()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastIsoTp());
        Action act = () => UdsClient.Create(channel, new UdsClientOptions { MaxBusyRepeatRequests = -1 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // Codex on #150: every duration in the options runs a timer, which measures about 49 days
    // at most; one beyond that would throw when armed, after the request went out.
    [Theory]
    [InlineData("P2ClientMax")]
    [InlineData("P2StarClientMax")]
    [InlineData("BusyRepeatRequestDelay")]
    [InlineData("TesterPresentPeriod")]
    public void A_Duration_Beyond_A_Timers_Reach_Is_Rejected_At_Construction(string option)
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastIsoTp());
        var fiftyDays = TimeSpan.FromDays(50);
        var options = option switch
        {
            "P2ClientMax" => new UdsClientOptions { P2ClientMax = fiftyDays },
            "P2StarClientMax" => new UdsClientOptions { P2StarClientMax = fiftyDays },
            "BusyRepeatRequestDelay" => new UdsClientOptions { BusyRepeatRequestDelay = fiftyDays },
            _ => new UdsClientOptions { TesterPresentPeriod = fiftyDays },
        };
        Action act = () => UdsClient.Create(channel, options);
        act.Should().Throw<ArgumentException>();
    }

    // Codex on #150: the With(...) clone carries the busy-repeat settings.
    [Fact]
    public void Options_With_Carries_The_Busy_Repeat_Settings()
    {
        var options = new UdsClientOptions
        {
            MaxBusyRepeatRequests = 7,
            BusyRepeatRequestDelay = TimeSpan.FromMilliseconds(15),
        };

        var clone = options.With(p2ClientMax: TimeSpan.FromMilliseconds(50));

        clone.MaxBusyRepeatRequests.Should().Be(7);
        clone.BusyRepeatRequestDelay.Should().Be(TimeSpan.FromMilliseconds(15));
        options.With(maxBusyRepeatRequests: 0).MaxBusyRepeatRequests.Should().Be(0);
    }

    [Fact]
    public async Task BusyRepeatRequest_Is_Surfaced_Once_The_Repeats_Are_Used_Up()
    {
        var (client, ecu, dispose) = BuildPair(
            e => e.On(0x22, req => throw new EcuNegativeResponse(0x21)),
            options: new UdsClientOptions { MaxBusyRepeatRequests = 1 });

        using (dispose)
        {
            using var cts = new CancellationTokenSource(ShortTimeout);
            Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, cts.Token);
            var ex = (await act.Should().ThrowAsync<UdsNegativeResponseException>()).Which;
            ex.Code.Should().Be(0x21);
            ecu.RequestsHandled.Should().Be(2, "the request and one repeat");
        }
    }

    // -----------------------------------------------------------------------------------
    // SecurityAccess must hold the request lock across seed + sendKey so TesterPresent
    // keep-alive cannot interleave and provoke NRC requestSequenceError on real ECUs.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task SecurityAccess_Holds_Lock_Across_Seed_And_Key()
    {
        var wireOrder = new List<byte>();
        var orderGate = new object();
        byte[] seed = { 0x01, 0x02, 0x03, 0x04 };
        var keyStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseKey = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var (client, _, dispose) = BuildPair(e => e
            .On(0x27, req =>
            {
                lock (orderGate) wireOrder.Add(req[1]);
                if (req[1] == 0x01)
                {
                    var body = new byte[1 + seed.Length];
                    body[0] = 0x01;
                    Buffer.BlockCopy(seed, 0, body, 1, seed.Length);
                    return body;
                }
                if (req[1] == 0x02)
                    return new byte[] { 0x02 };
                throw new EcuNegativeResponse(0x12);
            })
            .On(0x3E, req =>
            {
                lock (orderGate) wireOrder.Add(0x3E);
                return Array.Empty<byte>();
            }),
            options: new UdsClientOptions
            {
                TesterPresentPeriod = TimeSpan.FromMilliseconds(30),
                KeepAliveSuppressPositiveResponse = true,
            });

        using (dispose)
        {
            var impl = (UdsClientImpl)client;
            // The property is that a keep-alive tick queued behind SecurityAccess does not
            // transmit until the lock is released -- proven by observing at least one tick
            // actually contend for the lock while computeKey blocks, not by guessing how many
            // 30 ms periods a fixed wait covers (#171). Stronger than the counted-periods guess
            // it replaces: that could pass with zero ticks ever firing.
            var keepAliveContended = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            impl.RequestLockContended += () => keepAliveContended.TrySetResult(true);

            using var keepAlive = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(30));

            var unlock = client.SecurityAccessAsync(
                requestSeedLevel: 0x01,
                computeKey: s =>
                {
                    keyStarted.TrySetResult(true);
                    // Block inside computeKey (still under the request lock) long enough that
                    // a keep-alive tick contends for it; it must not transmit until unlock ends.
                    releaseKey.Task.Wait(ShortTimeout);
                    return s.Select(b => (byte)(b ^ 0x55)).ToArray();
                },
                cancellationToken: new CancellationTokenSource(ShortTimeout).Token);

            await keyStarted.Task.WaitAsync(ShortTimeout);
            await keepAliveContended.Task.WaitAsync(ShortTimeout); // a keep-alive tick queued behind SecurityAccess
            releaseKey.TrySetResult(true);
            await unlock;

            lock (orderGate)
            {
                // Seed (0x01) then key (0x02) must be adjacent; 0x3E may appear only outside.
                int seedIdx = wireOrder.IndexOf(0x01);
                int keyIdx = wireOrder.IndexOf(0x02);
                seedIdx.Should().BeGreaterThanOrEqualTo(0);
                keyIdx.Should().Be(seedIdx + 1);
                wireOrder.Skip(seedIdx).Take(2).Should().Equal((byte)0x01, (byte)0x02);
            }
        }
    }

    // -----------------------------------------------------------------------------------
    // After P2 timeout, a late ECU reply must not be consumed as the next request's answer
    // when the next request uses the same service (SID correlation alone is insufficient).
    //
    // The reply used to be a Thread.Sleep(250) against an 80 ms P2. That is a bet on the
    // runner: starve the client past the sleep and both the expired deadline and the response
    // are waiting, and a client that lets the response win the race returns 0xAA instead of
    // throwing (#92). The client was the side that was wrong — P2 is the response's arrival
    // stamp measured from the request's transmit stamp, not whichever of the deadline callback
    // and the inbox write the scheduler runs first (UdsExpiredDeadlineTests). This test forces
    // that ordering on the real channel. The actor is held before the request's transmit
    // confirmation is posted, so the client cannot arm P2 or settle an empty inbox while the
    // wait runs. The late response is then pumped onto that same actor — posted, not merely
    // seen on the wire — and only then is the hold released. The timeout probe therefore
    // finds the PDU and rejects it by its arrival stamp. A probe that ran first would time
    // out on an empty inbox and the following discard would drop the frame before
    // DatagramReceived.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task TimedOut_Request_Does_Not_Poison_Next_Same_Service_Transaction()
    {
        var budget = TimeSpan.FromMilliseconds(80);
        int calls = 0;
        int holdArmed = 0;
        var requestSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var actorHeld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActor = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStamp = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responsePosted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleEnqueued = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var session = NewSession();
        using var busClient = OpenClassic(session, 0);
        using var busEcu = OpenClassic(session, 1);
        using var sniffer = OpenClassic(session, 2);
        using var clientActor = new ProtocolActor();
        using var clientService = new CanBusService(busClient);
        var stamping = new FirstConfirmStampService(clientService, requestStamp);
        using var clientChannel = new IsoTpChannel(stamping,
            IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8),
            FastIsoTp(), ownsService: false, clientActor);

        // The request's FrameObserved runs inside Transmit, before SendConfirmedAsync returns and
        // therefore before OnSendConfirmed is posted. Queuing the hold here puts it ahead of
        // that confirmation, so the client stays inside SendAsync — P2 is not armed and no
        // settle probe is queued — until the hold is released.
        sniffer.FrameObserved += (_, view) =>
        {
            if (view.CanFrame.ID != 0x7E0) return;
            if (Interlocked.Exchange(ref holdArmed, 1) != 0) return;
            clientActor.Post(() =>
            {
                actorHeld.TrySetResult(true);
                releaseActor.Task.GetAwaiter().GetResult();
            });
        };
        // CanBusService subscribed first, so when this runs the demux has already buffered the
        // frame. SettleAsync pumps that buffer onto the actor before returning; awaiting its
        // task would wait for the no-op posted behind the pump, which cannot run until the
        // hold is released.
        busClient.FrameObserved += (_, view) =>
        {
            if (view.CanFrame.ID != 0x7E8) return;
            _ = clientChannel.SettleAsync();
            responsePosted.TrySetResult(true);
        };

        using var ecuChannel = IsoTpFactory.Open(busEcu,
            IsoTpEndpoint.Normal(txCanId: 0x7E8, rxCanId: 0x7E0), FastIsoTp());
        using var ecu = new SimulatedUdsEcu(ecuChannel);
        ecu.On(0x22, _ =>
        {
            int n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                // The request is already reassembled. Holding here keeps the answer off the bus
                // until the test has moved past P2 and the actor is still held.
                requestSeen.TrySetResult(true);
                if (!releaseResponse.Task.Wait(ShortTimeout))
                    throw new TimeoutException("the test did not release the late response");
                return new byte[] { 0xF1, 0x90, 0xAA };
            }

            return new byte[] { 0xF1, 0x90, 0xBB };
        });
        ecu.Start();

        using var client = UdsClient.Create(clientChannel, new UdsClientOptions
        {
            P2ClientMax = budget,
            P2StarClientMax = budget,
        });
        client.Channel.DatagramReceived += (_, args) =>
        {
            if (args.Data.Length >= 4 && args.Data[0] == 0x62 && args.Data[3] == 0xAA)
                staleEnqueued.TrySetResult(true);
        };

        try
        {
            var first = client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);

            await requestSeen.Task.WaitAsync(ShortTimeout);
            await actorHeld.Task.WaitAsync(ShortTimeout);
            // Taken inside the driver's completion, before OnSendConfirmed is posted. Waiting
            // out P2 from it — while the hold still owns the actor — makes the response's
            // arrival stamp late against the same instant the client will use.
            long transmitStamp = await requestStamp.Task.WaitAsync(ShortTimeout);
            transmitStamp.Should().BeGreaterThan(0);
            while (ElapsedSince(transmitStamp) <= budget)
                await Task.Delay(1);

            releaseResponse.TrySetResult(true);
            await responsePosted.Task.WaitAsync(ShortTimeout);
            releaseActor.TrySetResult(true);

            Func<Task> act = () => first;
            await act.Should().ThrowAsync<UdsTimeoutException>(
                "a response posted to the actor after P2 must not answer the request");

            await staleEnqueued.Task.WaitAsync(ShortTimeout);

            var data = await client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);
            data.Should().Equal(0xBB);
        }
        finally
        {
            releaseResponse.TrySetResult(true);
            releaseActor.TrySetResult(true);
        }
    }

    private static TimeSpan ElapsedSince(long startTimestamp)
    {
        var ticks = Stopwatch.GetTimestamp() - startTimestamp;
        if (ticks <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-007 — TesterPresent (0x3E): keep-alive fires periodically without blocking.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task TesterPresent_KeepAlive_Fires_Periodically()
    {
        int count = 0;
        var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, _, dispose) = BuildPair(e => e.On(0x3E, req =>
        {
            req[0].Should().Be(0x3E);
            (req[1] & 0x80).Should().Be(0x80, "keep-alive must suppress positive response");
            int c = Interlocked.Increment(ref count);
            if (c >= 3) handled.TrySetResult(true);
            return Array.Empty<byte>();
        }),
        options: new UdsClientOptions { TesterPresentPeriod = TimeSpan.FromMilliseconds(80) });

        using (dispose)
        {
            using (client.StartTesterPresentKeepAlive())
            {
                await handled.Task.WaitAsync(ShortTimeout);
            }
            count.Should().BeGreaterThanOrEqualTo(3);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-008 — P2 timeout: ECU silent -> client faults with UdsTimeoutException(P2).
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Client_Times_Out_When_Ecu_Silent_Within_P2()
    {
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, _ => throw new EcuSilent()),
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(150),
                P2StarClientMax = TimeSpan.FromMilliseconds(150),
            });
        using (dispose)
        {
            Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);
            var ex = (await act.Should().ThrowAsync<UdsTimeoutException>()).Which;
            ex.Timer.Should().Be(UdsTimeoutTimer.P2);
            ex.RequestedService.Should().Be(UdsServiceId.ReadDataByIdentifier);
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-008 — P2* timeout: ECU sends only NRC 0x78 (response pending), never a final
    // response -> the restarted P2* timer must expire and fault with
    // UdsTimeoutException(Timer = P2Star). This path had no test at all before (the only
    // Timer assertion in the suite was P2); the elapsed-time assertion proves the timeout
    // fired on the restarted P2* budget, not on the initial P2 budget.
    //
    // The first 0x78 is an ISO-TP round trip this test does not pace (#118). Measured with
    // this 100 ms and the 120 ms below lowered together, 8 CPU burners on 4 cores: no P2
    // timeout at 25 ms or above, the first one at 20 ms, and a certain one at 5 ms. The
    // unmodified class stayed green under that load. These budgets are not the tight edge.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Client_Times_Out_With_P2Star_When_Ecu_Sends_Only_ResponsePending()
    {
        // On a virtual clock (#171): each 0x78 is delivered at the exact virtual instant it
        // belongs to, via OrderedGates because the two delays share the same 40 ms length --
        // EcuSteps would hand both callers the same, already-resolved gate. The first 0x78 goes
        // out the instant the request is handled, with nothing pacing it -- on a fast host that
        // can land, and be processed, before a check for the initial P2 would even see it armed
        // -- so what is checked directly is the interval the client restarts to, which only a
        // client that has already received and processed that 0x78 could have armed.
        var gates = new OrderedGates();
        var gap = TimeSpan.FromMilliseconds(40);
        var p2 = TimeSpan.FromMilliseconds(100);
        var p2Star = TimeSpan.FromMilliseconds(250);
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = gates.WaitAsync;
                e.On(0x22, _ => throw new EcuResponsePendingThenSilent(pendingCount: 2, delayBetween: gap));
            },
            options: new UdsClientOptions
            {
                P2ClientMax = p2,
                P2StarClientMax = p2Star,
                MaxResponsePendingCount = 10,
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);

        await pair.WaitUntilWaitingAsync(p2Star); // restarted on the first 0x78's arrival
        await pair.Clock.AdvanceAsync(gap);
        await pair.DeliverAsync(gates.ReleaseNext, Negative(0x22, 0x78)); // the second 0x78, at 40 ms

        await pair.WaitUntilWaitingAsync(p2Star); // restarted again, from the second 0x78
        await pair.Clock.AdvanceAsync(p2Star); // then silence: P2* must expire

        Func<Task> act = () => read;
        var ex = (await act.Should().ThrowAsync<UdsTimeoutException>()).Which;
        ex.Timer.Should().Be(UdsTimeoutTimer.P2Star,
            "the ECU answered with NRC 0x78 (response pending), so the running timer is P2* — not P2");
        ex.RequestedService.Should().Be(UdsServiceId.ReadDataByIdentifier);
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-009 — NRC 0x78 responsePending: ECU sends 3× 0x78 then final response;
    // client MUST wait inside P2* and deliver the final response.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task ResponsePending_Restarts_P2Star_And_Returns_Final_Response()
    {
        // On a virtual clock (#171): the three 0x78s (60 ms apart) and the final response are
        // each delivered at the exact instant they belong to, via OrderedGates for the same
        // reason as the P2* timeout test above. The first 0x78 goes out unpaced and can land
        // before a check for the initial P2 would see it armed, so what is checked directly is
        // the interval the client restarts to. The exact interval armed after every 0x78 proves
        // the restart directly, rather than by the client merely finishing with the right
        // payload despite a short initial P2.
        var gates = new OrderedGates();
        var gap = TimeSpan.FromMilliseconds(60);
        var p2 = TimeSpan.FromMilliseconds(120);
        var p2Star = TimeSpan.FromSeconds(1);
        byte[] finalBody = { 0xF1, 0x90, 0xAA, 0xBB, 0xCC, 0xDD };
        using var pair = new ClockPair(
            e =>
            {
                e.Delay = gates.WaitAsync;
                e.On(0x22, _ => throw new EcuResponsePending(
                    pendingCount: 3, finalResponse: finalBody, delayBetween: gap));
            },
            options: new UdsClientOptions
            {
                P2ClientMax = p2,
                P2StarClientMax = p2Star,
                MaxResponsePendingCount = 10,
            });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var read = pair.Client.ReadDataByIdentifierAsync(0xF190, cts.Token);

        await pair.WaitUntilWaitingAsync(p2Star); // restarted on the 1st (unpaced) 0x78's arrival
        await pair.Clock.AdvanceAsync(gap);
        await pair.DeliverAsync(gates.ReleaseNext, Negative(0x22, 0x78)); // 2nd 0x78 @ 60 ms

        await pair.WaitUntilWaitingAsync(p2Star);
        await pair.Clock.AdvanceAsync(gap);
        await pair.DeliverAsync(gates.ReleaseNext, Negative(0x22, 0x78)); // 3rd 0x78 @ 120 ms

        await pair.WaitUntilWaitingAsync(p2Star);
        await pair.Clock.AdvanceAsync(gap);
        // Releasing the 3rd gate lets the final positive response go out right away.
        await pair.DeliverAsync(gates.ReleaseNext, data => data.Length >= 2 && data[1] == 0x62);

        var data = await Within(read);
        data.Should().Equal(0xAA, 0xBB, 0xCC, 0xDD);
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-009 (edge) — MaxResponsePendingCount bounds the loop.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task ResponsePending_Loop_Aborts_When_Exceeding_MaxResponsePendingCount()
    {
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, _ => throw new EcuResponsePending(
                pendingCount: 5, finalResponse: new byte[] { 0xF1, 0x90 },
                // No spacing: what bounds this loop is the count, not the gap, and every 0x78
                // restarts P2* -- so a gap is a wait racing a 1 s budget for no gain. It cost
                // two red macOS legs (#115, #116) before anyone read which test was failing:
                // the host stretches the Delay, P2* wins, and the client raises the timeout it
                // is entitled to raise instead of the abort this test asserts.
                //
                // Checked before removing it rather than after: with the P2* restart mutated
                // away entirely, this test passes at the old 20 ms too, so the gap carried
                // nothing. Its two neighbours are not the same -- the 60 ms in
                // ResponsePending_Restarts_P2Star_And_Returns_Final_Response is what pushes that
                // transfer past P2, and the same mutation passes there once it is zero.
                delayBetween: TimeSpan.Zero)),
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromMilliseconds(500),
                P2StarClientMax = TimeSpan.FromSeconds(1),
                MaxResponsePendingCount = 2,
            });
        using (dispose)
        {
            Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190,
                new CancellationTokenSource(ShortTimeout).Token);
            await act.Should().ThrowAsync<UdsProtocolException>()
                .WithMessage("*NRC 0x78*MaxResponsePendingCount=2*");
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-010 — structured NRC: ECU returns 0x31 (requestOutOfRange) for RDBI.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Negative_Response_Is_Surfaced_As_Structured_Exception()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, _ => throw new EcuNegativeResponse(0x31)));
        using (dispose)
        {
            Func<Task> act = () => client.ReadDataByIdentifierAsync(0x1234,
                new CancellationTokenSource(ShortTimeout).Token);
            var ex = (await act.Should().ThrowAsync<UdsNegativeResponseException>()).Which;
            ex.RequestedService.Should().Be(UdsServiceId.ReadDataByIdentifier);
            ex.Code.Should().Be(0x31);
            ex.CodeAsEnum.Should().Be(UdsNegativeResponseCode.RequestOutOfRange);
            ex.CodeName.Should().Be("RequestOutOfRange");
        }
    }

    // -----------------------------------------------------------------------------------
    // FR-UDS-011 (SHOULD) — multi-DID ReadDataByIdentifier.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task ReadDataByIdentifier_Multi_Did_Splits_Records()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, req =>
        {
            // Request layout: [0]=0x22 [1..]=DIDs (3× 2 bytes)
            req.Length.Should().Be(1 + 3 * 2);
            // Response layout: DID + record for each requested DID, concatenated after SID+0x40.
            //   0xF1 0x90 (VIN, 3 bytes for brevity)
            //   0xF1 0x87 (2 bytes)
            //   0xF1 0x89 (1 byte)
            return new byte[]
            {
                0xF1, 0x90, 0x41, 0x42, 0x43,
                0xF1, 0x87, 0x11, 0x22,
                0xF1, 0x89, 0x99,
            };
        }));
        using (dispose)
        {
            var lengths = new Dictionary<ushort, int>
            {
                [(ushort)0xF190] = 3,
                [(ushort)0xF187] = 2,
                [(ushort)0xF189] = 1,
            };
            var results = await client.ReadDataByIdentifierAsync(
                new ushort[] { 0xF190, 0xF187, 0xF189 },
                lengths,
                new CancellationTokenSource(ShortTimeout).Token);

            results.Should().ContainKey((ushort)0xF190).WhoseValue.Should().Equal(0x41, 0x42, 0x43);
            results.Should().ContainKey((ushort)0xF187).WhoseValue.Should().Equal(0x11, 0x22);
            results.Should().ContainKey((ushort)0xF189).WhoseValue.Should().Equal(0x99);
        }
    }

    // Bugbot 3596522007 — ISO 14229-1 allows empty dataRecord; adjacent DIDs must not throw.
    [Fact]
    public async Task ReadDataByIdentifier_Multi_Did_Accepts_Empty_Records()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, _ => new byte[]
        {
            // DID 0xF190 with empty record, then DID 0xF187 with 2 bytes, then DID 0xF189 empty.
            0xF1, 0x90,
            0xF1, 0x87, 0x11, 0x22,
            0xF1, 0x89,
        }));
        using (dispose)
        {
            var lengths = new Dictionary<ushort, int>
            {
                [(ushort)0xF190] = 0,
                [(ushort)0xF187] = 2,
                [(ushort)0xF189] = 0,
            };
            var results = await client.ReadDataByIdentifierAsync(
                new ushort[] { 0xF190, 0xF187, 0xF189 },
                lengths,
                new CancellationTokenSource(ShortTimeout).Token);

            results.Should().ContainKey((ushort)0xF190).WhoseValue.Should().BeEmpty();
            results.Should().ContainKey((ushort)0xF187).WhoseValue.Should().Equal(0x11, 0x22);
            results.Should().ContainKey((ushort)0xF189).WhoseValue.Should().BeEmpty();
        }
    }

    // Bugbot 3596550854 — payload bytes matching a later DID must not become record boundaries.
    [Fact]
    public async Task ReadDataByIdentifier_Multi_Did_Does_Not_Split_On_Data_Bytes()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, _ => new byte[]
        {
            // DID 0x1234 data contains the byte pair of the next DID (0xF187); length-based
            // parsing must keep those bytes inside 0x1234's record.
            0x12, 0x34, 0xF1, 0x87, 0xAA,
            0xF1, 0x87, 0x11, 0x22,
        }));
        using (dispose)
        {
            var lengths = new Dictionary<ushort, int>
            {
                [(ushort)0x1234] = 3,
                [(ushort)0xF187] = 2,
            };
            var results = await client.ReadDataByIdentifierAsync(
                new ushort[] { 0x1234, 0xF187 },
                lengths,
                new CancellationTokenSource(ShortTimeout).Token);

            results.Should().ContainKey((ushort)0x1234).WhoseValue.Should().Equal(0xF1, 0x87, 0xAA);
            results.Should().ContainKey((ushort)0xF187).WhoseValue.Should().Equal(0x11, 0x22);
        }
    }

    // -----------------------------------------------------------------------------------
    // Sanity: unknown service still surfaces the ECU's serviceNotSupported NRC (defense in
    // depth for FR-UDS-010 mapping of arbitrary NRC bytes).
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Unknown_Service_Response_Is_Surfaced_As_ServiceNotSupported()
    {
        // No handlers configured — the ECU returns 0x11 for anything it sees.
        var (client, _, dispose) = BuildPair(_ => { });
        using (dispose)
        {
            Func<Task> act = () => client.DiagnosticSessionControlAsync(UdsSessionType.Extended,
                new CancellationTokenSource(ShortTimeout).Token);
            var ex = (await act.Should().ThrowAsync<UdsNegativeResponseException>()).Which;
            ex.Code.Should().Be(0x11);
            ex.CodeAsEnum.Should().Be(UdsNegativeResponseCode.ServiceNotSupported);
        }
    }

    // -----------------------------------------------------------------------------------
    // Bugbot 3596444327 — Dispose must cancel the lifetime token and wait for an in-flight
    // ExecuteAsync to Release _requestLock before disposing the semaphore.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Dispose_During_InFlight_Request_Does_Not_Race_RequestLock()
    {
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, _ => throw new EcuSilent()),
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromSeconds(5),
                P2StarClientMax = TimeSpan.FromSeconds(5),
            });
        using var teardown = dispose;
        var impl = (UdsClientImpl)client;
        // The ECU never answers (EcuSilent), so once the lock is held the request stays parked
        // in the receive: the observable is the lock, not a guess at how long entering the
        // receive on top of it takes (#171).
        var lockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        impl.RequestLockAcquired += () => lockHeld.TrySetResult(true);
        var inFlight = client.ReadDataByIdentifierAsync(0xF190,
            new CancellationTokenSource(ShortTimeout).Token);
        await lockHeld.Task; // holds _requestLock, parked in ReceiveWithTimeout

        Action act = () => client.Dispose();
        act.Should().NotThrow(
            "Dispose must wait for the in-flight request to Release before disposing _requestLock");

        Func<Task> wait = () => inFlight;
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    // DisposeAsync does what Dispose does without holding a thread: the in-flight request is
    // cancelled, the request lock is released before it is disposed, and the owned channel goes.
    [Fact]
    public async Task DisposeAsync_During_InFlight_Request_Cancels_It_And_Waits_For_The_Lock()
    {
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, _ => throw new EcuSilent()),
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromSeconds(5),
                P2StarClientMax = TimeSpan.FromSeconds(5),
            });
        using var teardown = dispose;
        var impl = (UdsClientImpl)client;
        var lockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        impl.RequestLockAcquired += () => lockHeld.TrySetResult(true);
        using var cts = new CancellationTokenSource(ShortTimeout);
        var inFlight = client.ReadDataByIdentifierAsync(0xF190, cts.Token);
        await lockHeld.Task;

        await Within(client.DisposeAsync().AsTask());

        Func<Task> wait = () => inFlight;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> again = () => client.ReadDataByIdentifierAsync(0xF190);
        await again.Should().ThrowAsync<ObjectDisposedException>();
    }

    // Two calls at once: the second returns when the disposal has finished, not at once.
    [Fact]
    public async Task A_Concurrent_DisposeAsync_Returns_When_The_Disposal_Has_Finished()
    {
        var (client, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;
        using var keepAlive = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(30));

        var first = client.DisposeAsync();
        var second = client.DisposeAsync();

        await Within(second.AsTask());
        ((UdsClientImpl)client).DisposalFinished.Should().BeTrue("the second call returned only once the first had finished");
        await Within(first.AsTask());
        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // Dispose with a keep-alive running joins its loop; the handle the caller got back may be
    // disposed before or after, synchronously or not, and none of those calls is an error.
    [Fact]
    public async Task Dispose_Joins_A_Running_KeepAlive_And_Its_Handle_Can_Be_Disposed_Again()
    {
        var (client, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;
        var handle = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(30));

        client.Dispose();

        handle.Dispose();
        await ((IAsyncDisposable)handle).DisposeAsync();
        await ((IAsyncDisposable)handle).DisposeAsync();
    }

    // The client that owns its channel releases it with DisposeAsync as well.
    [Fact]
    public async Task DisposeAsync_Releases_An_Owned_Channel()
    {
        var (spare, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;
        var channel = spare.Channel;
        var owner = UdsClient.Create(channel, leaveOpen: false);

        await Within(owner.DisposeAsync().AsTask());

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await channel.ReceiveAsync());
        ex.Message.Should().Contain("dispos", "the channel was released by the client that owned it");
    }

    // A second keep-alive is refused, and the candidate that was never started is disposed: there
    // is no loop to join for it.
    [Fact]
    public void A_Second_KeepAlive_Is_Refused_And_The_Unstarted_Candidate_Is_Disposed()
    {
        var (client, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;
        using var first = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(50));

        Action second = () => client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(50));

        second.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_KeepAlive_Period_Longer_Than_Task_Delay_Allows_Is_Refused_Up_Front()
    {
        var (client, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;

        Action act = () => client.StartTesterPresentKeepAlive(TimeSpan.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>();
        client.Dispose(); // a client with no keep-alive of the refused one disposes as usual
    }

#if NET5_0_OR_GREATER
    // An operation's cancellation callback throws when the client is disposed: Cancel surfaces an
    // AggregateException, and the disposal still ends -- the owned channel is released and the
    // client does not throw.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Cancellation_Callback_That_Throws_Does_Not_Stop_The_Disposal(bool asynchronous)
    {
        var channel = System.Reflection.DispatchProxy.Create<IIsoTpChannel, ThrowingOnCancelChannel>();
        var stub = (ThrowingOnCancelChannel)channel;
        using var client = UdsClient.Create(channel, leaveOpen: false);
        // The request below never ends (the stub ignores the cancellation), so the disposal waits
        // out the request lock; a short wait keeps the test short.
        ((UdsClientImpl)client).DisposeLockTimeout = TimeSpan.FromMilliseconds(200);
        var request = client.ReadDataByIdentifierAsync(0xF190);
        if (await Task.WhenAny(stub.SendStarted.Task, request, Task.Delay(ShortTimeout)) != stub.SendStarted.Task)
            throw new InvalidOperationException("The request did not reach SendAsync: " + (request.IsFaulted ? request.Exception!.GetBaseException().ToString() : request.Status.ToString()));

        if (asynchronous) await Within(client.DisposeAsync().AsTask());
        else client.Dispose();

        stub.Disposed.Should().BeTrue("the owned channel was released although a callback threw");
        ((UdsClientImpl)client).DisposalFinished.Should().BeTrue();
        request.IsCompleted.Should().BeFalse("the stub's send never ends; the disposal went on without it");
    }

    // The throwing callback sits on the keep-alive's own request: disposing the keep-alive handle
    // must neither throw nor skip the bounded join, and the client's disposal still cancels what
    // is queued.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Cancellation_Callback_That_Throws_In_A_KeepAlive_Request_Does_Not_Stop_Its_Disposal(bool asynchronous)
    {
        var channel = System.Reflection.DispatchProxy.Create<IIsoTpChannel, ThrowingOnCancelChannel>();
        var stub = (ThrowingOnCancelChannel)channel;
        using var client = UdsClient.Create(channel, leaveOpen: false);
        ((UdsClientImpl)client).DisposeLockTimeout = TimeSpan.FromMilliseconds(200);
        var handle = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(10));
        await Within(stub.SendStarted.Task); // the keep-alive's request is in flight, callback registered

        if (asynchronous) await ((IAsyncDisposable)handle).DisposeAsync();
        else handle.Dispose();

        // The request never ends, so the join gave up after the disposal timeout and left the
        // loop alone; the client can still be disposed afterwards.
        if (asynchronous) await Within(client.DisposeAsync().AsTask());
        else client.Dispose();
        ((UdsClientImpl)client).DisposalFinished.Should().BeTrue();
    }

    private class ThrowingOnCancelChannel : System.Reflection.DispatchProxy
    {
        public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IDisposable.Dispose):
                    Disposed = true;
                    return null;
                case nameof(IIsoTpChannel.SendWithTransmitStampAsync):
                    // Never completes; its token's cancellation throws out of Cancel.
                    var token = (CancellationToken)args![1]!;
                    token.Register(() => throw new InvalidOperationException("a cancellation callback that throws"));
                    SendStarted.TrySetResult(true);
                    return new TaskCompletionSource<IsoTpTransmitStamps>().Task;
                default:
                    // Whatever else the client asks of the channel before it sends: nothing to do.
                    var parameters = targetMethod!.GetParameters();
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (parameters[i].ParameterType.IsByRef)
                            args![i] = Activator.CreateInstance(parameters[i].ParameterType.GetElementType()!);
                    }
                    var returns = targetMethod.ReturnType;
                    if (returns == typeof(Task)) return Task.CompletedTask;
                    if (returns == typeof(ValueTask)) return default(ValueTask);
                    return returns.IsValueType ? Activator.CreateInstance(returns) : null;
            }
        }
    }
#endif

    // The keep-alive loop is joined without a thread too, and without waiting for ever.
    [Fact]
    public async Task DisposeAsync_Stops_A_Running_KeepAlive()
    {
        var (client, _, dispose) = BuildPair(e => { });
        using var teardown = dispose;
        using var keepAlive = client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(30));

        await Within(client.DisposeAsync().AsTask());

        // The loop ended and cleared its slot: a new keep-alive on a disposed client is refused
        // for the disposal, not for a live one.
        Action start = () => client.StartTesterPresentKeepAlive(TimeSpan.FromMilliseconds(30));
        start.Should().Throw<ObjectDisposedException>();
    }

#if NET5_0_OR_GREATER
    // A client that is not this library's has no DisposeAsync of its own.
    [Fact]
    public async Task DisposeAsync_On_A_Client_That_Is_Not_This_Librarys_Disposes_It_On_The_Thread_Pool()
    {
        var foreign = System.Reflection.DispatchProxy.Create<IUdsClient, ForeignClient>();

        await foreign.DisposeAsync();

        ((ForeignClient)foreign).Disposed.Should().BeTrue();
    }

    private class ForeignClient : System.Reflection.DispatchProxy
    {
        public bool Disposed { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDisposable.Dispose)) Disposed = true;
            return null;
        }
    }
#endif

    [Fact]
    public async Task DisposeAsync_Rejects_A_Null_Client()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await UdsClientExtensions.DisposeAsync(null!));
    }

    // -----------------------------------------------------------------------------------
    // Bugbot 3596586770 — suppress-positive TesterPresentAsync must honor _lifetimeCts so
    // Dispose cancels a call blocked on _requestLock (or about to Send) instead of letting
    // it transmit after teardown.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task Dispose_Cancels_Suppress_TesterPresent_Blocked_On_RequestLock()
    {
        var (client, _, dispose) = BuildPair(
            e => e.On(0x22, _ => throw new EcuSilent()),
            options: new UdsClientOptions
            {
                P2ClientMax = TimeSpan.FromSeconds(5),
                P2StarClientMax = TimeSpan.FromSeconds(5),
            });
        using var teardown = dispose;
        var impl = (UdsClientImpl)client;
        // Hold the request lock with a silent ECU read so suppress TesterPresent blocks
        // in WaitAsync rather than racing through Send.
        var readHoldsLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        impl.RequestLockAcquired += () => readHoldsLock.TrySetResult(true);
        var inFlight = client.ReadDataByIdentifierAsync(0xF190,
            new CancellationTokenSource(ShortTimeout).Token);
        await readHoldsLock.Task;

        var testerPresentQueued = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        impl.RequestLockContended += () => testerPresentQueued.TrySetResult(true);
        var testerPresent = client.TesterPresentAsync(suppressPositiveResponse: true,
            new CancellationTokenSource(ShortTimeout).Token);
        await testerPresentQueued.Task; // parked on _requestLock.WaitAsync

        Action act = () => client.Dispose();
        act.Should().NotThrow();

        Func<Task> waitTp = () => testerPresent;
        await waitTp.Should().ThrowAsync<OperationCanceledException>(
            "suppress TesterPresent must link _lifetimeCts so Dispose aborts WaitAsync/Send");

        Func<Task> waitRead = () => inFlight;
        await waitRead.Should().ThrowAsync<OperationCanceledException>();
    }

    // -----------------------------------------------------------------------------------
    // #171 — the uncontended fast path of the request lock must honour a token that is
    // already cancelled, as the plain WaitAsync it replaced did: a call cancelled before it
    // starts never takes the lock.
    // -----------------------------------------------------------------------------------
    [Fact]
    public async Task An_Already_Cancelled_Call_Does_Not_Take_A_Free_Request_Lock()
    {
        var (client, _, dispose) = BuildPair(e => e.On(0x22, _ => new byte[] { 0xF1, 0x90, 0x01 }));
        using var teardown = dispose;
        var impl = (UdsClientImpl)client;
        var acquired = false;
        impl.RequestLockAcquired += () => acquired = true;

        Func<Task> act = () => client.ReadDataByIdentifierAsync(0xF190, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        acquired.Should().BeFalse("a call cancelled before it starts must not take the request lock");
    }

    /// <summary>
    /// Records <see cref="TxConfirmation.HostTransmitTimestamp"/> of the first
    /// <see cref="ICanBusService.SendConfirmedAsync"/> and completes that task only afterwards,
    /// so the channel posts its transmit confirmation after the stamp is visible to the test.
    /// </summary>
    private sealed class FirstConfirmStampService : ICanBusService
    {
        private readonly ICanBusService _inner;
        private readonly TaskCompletionSource<long> _stamp;
        private int _recorded;

        public FirstConfirmStampService(ICanBusService inner, TaskCompletionSource<long> stamp)
        {
            _inner = inner;
            _stamp = stamp;
        }

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

        public async Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var confirmation = await _inner.SendConfirmedAsync(frame, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (Interlocked.Exchange(ref _recorded, 1) == 0)
                _stamp.TrySetResult(confirmation.HostTransmitTimestamp);
            return confirmation;
        }

        public void Dispose() { /* the test owns the inner service */ }
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _items;
        public CompositeDisposable(params IDisposable[] items) => _items = items;
        public void Dispose()
        {
            foreach (var d in _items)
            {
                try { d.Dispose(); } catch { /* ignored */ }
            }
        }
    }
}
