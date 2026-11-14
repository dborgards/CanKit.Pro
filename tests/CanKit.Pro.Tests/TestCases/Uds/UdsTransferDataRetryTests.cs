using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Tests.Infrastructure;
using CanKit.Pro.Uds;
using Xunit;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

namespace CanKit.Pro.Tests.TestCases.Uds;

/// <summary>
/// FR-UDS-012, #426: <see cref="UdsClientOptions.TransferDataRetryCount"/>. DownloadAsync and
/// UploadAsync repeat a TransferData block, with the same blockSequenceCounter, after a P2/P2*
/// timeout or an ISO-TP transport error (ISO 14229-1:2026 §14.4, Table 451), never after a
/// negative response. The simulated server recognises a repeat by its counter, as the standard
/// has it: a repeated download block is answered without being written again, a repeated upload
/// block is sent again.
/// </summary>
public class UdsTransferDataRetryTests : IClassFixture<VirtualAdapterFixture>
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan P2 = TimeSpan.FromMilliseconds(150);

    private static string NewSession() => $"uds-retry-{Guid.NewGuid():N}";

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

    private static UdsClientOptions Options(int retries, int maxBusyRepeats = 3) => new()
    {
        P2ClientMax = P2,
        TransferDataRetryCount = retries,
        MaxBusyRepeatRequests = maxBusyRepeats,
    };

    private sealed class Pair : IDisposable
    {
        private readonly IDisposable[] _owned;

        public Pair(IUdsClient client, SimulatedUdsEcu ecu, params IDisposable[] owned)
        {
            Client = client;
            Ecu = ecu;
            _owned = owned;
        }

        public IUdsClient Client { get; }

        public SimulatedUdsEcu Ecu { get; }

        public void Dispose()
        {
            foreach (var d in _owned)
            {
                try { d.Dispose(); } catch { /* ignored */ }
            }
        }
    }

    private static Pair BuildPair(Action<SimulatedUdsEcu> configure, UdsClientOptions options,
        Func<IIsoTpChannel, IIsoTpChannel>? wrap = null)
    {
        var session = NewSession();
        var busClient = OpenClassic(session, 0);
        var busEcu = OpenClassic(session, 1);
        var clientChannel = IsoTpFactory.Open(busClient, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastIsoTp());
        var ecuChannel = IsoTpFactory.Open(busEcu, IsoTpEndpoint.Normal(0x7E8, 0x7E0), FastIsoTp());
        var ecu = new SimulatedUdsEcu(ecuChannel);
        configure(ecu);
        ecu.Start();
        var client = UdsClient.Create(wrap is null ? clientChannel : wrap(clientChannel), options);
        return new Pair(client, ecu, client, ecu, ecuChannel, clientChannel, busEcu, busClient);
    }

    /// <summary>
    /// A download server that tells a repeat by its counter. <see cref="Fault"/> decides, per
    /// TransferData request, whether it is answered: given the counter and how many times in a
    /// row that counter came, it returns null to answer, or the exception the simulated ECU acts
    /// on (<see cref="EcuSilent"/>, <see cref="EcuNegativeResponse"/>).
    /// </summary>
    private sealed class DownloadServer
    {
        private readonly object _gate = new();
        private byte? _lastProcessed;
        private byte _expected = 0x01;
        private byte? _lastSeen;
        private int _attempt;

        public List<byte> Written { get; } = new();

        public List<byte[]> TransferRequests { get; } = new();

        public int Exits;

        public Func<byte, int, Exception?> Fault { get; set; } = (_, _) => null;

        /// <summary>When true, a request that is not answered is still processed: the answer is what got lost.</summary>
        public bool ProcessSilentRequests { get; set; }

        public DownloadServer Wire(SimulatedUdsEcu ecu, byte maxBlockLength)
        {
            ecu.On(0x34, _ => new byte[] { 0x10, maxBlockLength });
            ecu.On(0x36, req =>
            {
                lock (_gate)
                {
                    TransferRequests.Add(req.ToArray());
                    byte bsc = req[1];
                    _attempt = _lastSeen == bsc ? _attempt + 1 : 1;
                    _lastSeen = bsc;
                    var fault = Fault(bsc, _attempt);
                    bool repeat = _lastProcessed == bsc;
                    if (fault is EcuNegativeResponse) throw fault;
                    if (!repeat)
                    {
                        if (bsc != _expected)
                            throw new EcuNegativeResponse(0x73);
                        if (fault is null || ProcessSilentRequests)
                        {
                            for (int i = 2; i < req.Length; i++) Written.Add(req[i]);
                            _lastProcessed = bsc;
                            unchecked { _expected++; }
                        }
                    }
                    if (fault is not null) throw fault;
                    return new byte[] { bsc };
                }
            });
            ecu.On(0x37, _ =>
            {
                Interlocked.Increment(ref Exits);
                return Array.Empty<byte>();
            });
            return this;
        }

        public List<byte> Counters()
        {
            lock (_gate) return TransferRequests.Select(r => r[1]).ToList();
        }
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13 + 1)).ToArray();

    private static Task DownloadAsync(IUdsClient client, byte[] payload, CancellationToken cancellationToken)
        => client.DownloadAsync(
            dataFormatIdentifier: 0x00,
            addressAndLengthFormatIdentifier: 0x21,
            memoryAddress: new byte[] { 0x10 },
            memorySize: new byte[] { (byte)(payload.Length >> 8), (byte)payload.Length },
            data: payload,
            cancellationToken: cancellationToken);

    // Tests 1 and 3: block 2 times out once, then goes through with the same counter and the same
    // bytes; the transfer goes on with counter 3 and ends with one RequestTransferExit. Whether
    // the server had processed the lost attempt or not, it writes the block once.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Timed_Out_Block_Is_Repeated_With_The_Same_Counter_And_Bytes(bool processedBeforeTheAnswerWasLost)
    {
        var server = new DownloadServer
        {
            Fault = (bsc, attempt) => bsc == 0x02 && attempt == 1 ? new EcuSilent() : null,
            ProcessSilentRequests = processedBeforeTheAnswerWasLost,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries: 1));
        var payload = Payload(12); // three blocks of 4

        using var cts = new CancellationTokenSource(ShortTimeout);
        await DownloadAsync(pair.Client, payload, cts.Token);

        server.Counters().Should().Equal(0x01, 0x02, 0x02, 0x03);
        server.TransferRequests[2].Should().Equal(server.TransferRequests[1], "the repeat is byte for byte the request it repeats");
        server.Written.Should().Equal(payload);
        Volatile.Read(ref server.Exits).Should().Be(1);
    }

    // Test 2: the block with counter FFh is repeated as FFh, and the next block is 00h.
    [Fact]
    public async Task A_Repeat_At_The_Counter_Wrap_Keeps_FF_And_Goes_On_With_00()
    {
        var server = new DownloadServer
        {
            Fault = (bsc, attempt) => bsc == 0xFF && attempt == 1 ? new EcuSilent() : null,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 3), Options(retries: 1));
        var payload = Payload(257); // one byte per block: counters 01..FF, 00, 01

        using var cts = new CancellationTokenSource(ShortTimeout);
        await DownloadAsync(pair.Client, payload, cts.Token);

        var counters = server.Counters();
        counters.Should().HaveCount(258);
        counters.Skip(253).Take(4).Should().Equal(0xFE, 0xFF, 0xFF, 0x00);
        counters[257].Should().Be(0x01);
        server.Written.Should().Equal(payload);
        Volatile.Read(ref server.Exits).Should().Be(1);
    }

    // Tests 4 and 9: the budget is per block. Spent, the timeout surfaces as it is and no
    // RequestTransferExit is sent; with the default of 0 the first timeout surfaces at once.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task A_Spent_Budget_Surfaces_The_Timeout_Without_A_Transfer_Exit(int retries)
    {
        var server = new DownloadServer
        {
            Fault = (bsc, _) => bsc == 0x02 ? new EcuSilent() : null,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries));

        using var cts = new CancellationTokenSource(ShortTimeout);
        Func<Task> act = () => DownloadAsync(pair.Client, Payload(12), cts.Token);

        (await act.Should().ThrowAsync<UdsTimeoutException>())
            .Which.RequestedService.Should().Be(UdsServiceId.TransferData);
        server.Counters().Should().Equal(new byte[] { 0x01 }.Concat(Enumerable.Repeat((byte)0x02, retries + 1)));
        Volatile.Read(ref server.Exits).Should().Be(0);
    }

    // The budget is per block, not per transfer: every block may use all of it.
    [Fact]
    public async Task Every_Block_Has_The_Whole_Budget()
    {
        var server = new DownloadServer
        {
            Fault = (_, attempt) => attempt <= 2 ? new EcuSilent() : null,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries: 2));
        var payload = Payload(8);

        using var cts = new CancellationTokenSource(ShortTimeout);
        await DownloadAsync(pair.Client, payload, cts.Token);

        server.Counters().Should().Equal(0x01, 0x01, 0x01, 0x02, 0x02, 0x02);
        server.Written.Should().Equal(payload);
    }

    // Test 5: a negative response is never repeated by the budget -- 73h surfaces at once.
    [Theory]
    [InlineData(0x73)]
    [InlineData(0x24)]
    [InlineData(0x72)]
    public async Task A_Negative_Response_Is_Not_Repeated(byte nrc)
    {
        var server = new DownloadServer
        {
            Fault = (bsc, _) => bsc == 0x02 ? new EcuNegativeResponse(nrc) : null,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries: 3));

        using var cts = new CancellationTokenSource(ShortTimeout);
        Func<Task> act = () => DownloadAsync(pair.Client, Payload(12), cts.Token);

        (await act.Should().ThrowAsync<UdsNegativeResponseException>()).Which.Code.Should().Be(nrc);
        server.Counters().Should().Equal(0x01, 0x02);
        Volatile.Read(ref server.Exits).Should().Be(0);
    }

    // Test 6: NRC 21h keeps its own repeat and does not use up the block budget; once that
    // repeat is spent, the 21h surfaces and the block budget does not repeat it either.
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, false)]
    public async Task Busy_Repeat_Is_Separate_From_The_Block_Budget(int busyAnswers, int maxBusyRepeats, bool succeeds)
    {
        var server = new DownloadServer
        {
            Fault = (bsc, attempt) => bsc == 0x02 && attempt <= busyAnswers ? new EcuNegativeResponse(0x21) : null,
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries: 1, maxBusyRepeats));
        var payload = Payload(8);

        using var cts = new CancellationTokenSource(ShortTimeout);
        Func<Task> act = () => DownloadAsync(pair.Client, payload, cts.Token);

        if (succeeds)
        {
            await act.Should().NotThrowAsync();
            server.Counters().Should().Equal(0x01, 0x02, 0x02, 0x02);
            server.Written.Should().Equal(payload);
        }
        else
        {
            (await act.Should().ThrowAsync<UdsNegativeResponseException>()).Which.Code.Should().Be(0x21);
            server.Counters().Should().Equal(0x01, 0x02, 0x02, 0x02);
            Volatile.Read(ref server.Exits).Should().Be(0);
        }
    }

    // Test 7: cancelled while a repeated block waits for its answer, the call ends with the
    // cancellation, repeats nothing more and sends no RequestTransferExit.
    [Fact]
    public async Task Cancellation_During_A_Repeat_Ends_Without_A_Transfer_Exit()
    {
        var repeatSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DownloadServer
        {
            Fault = (bsc, attempt) =>
            {
                if (bsc != 0x01) return null;
                if (attempt == 2) repeatSeen.TrySetResult(true);
                return new EcuSilent();
            },
        };
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), new UdsClientOptions
        {
            P2ClientMax = TimeSpan.FromMilliseconds(300),
            TransferDataRetryCount = 5,
        });

        using var cts = new CancellationTokenSource(ShortTimeout);
        var download = DownloadAsync(pair.Client, Payload(8), cts.Token);
        await repeatSeen.Task.WaitAsync(ShortTimeout);
        cts.Cancel();

        Func<Task> act = () => download;
        await act.Should().ThrowAsync<OperationCanceledException>();
        server.Counters().Should().Equal(0x01, 0x01);
        Volatile.Read(ref server.Exits).Should().Be(0);
    }

    // Test 8: an upload block whose answer was lost is asked for again with the same counter;
    // the server sends the same data again, and the client appends it once.
    [Fact]
    public async Task An_Upload_Appends_A_Repeated_Block_Once()
    {
        var source = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        var counters = new List<byte>();
        int exits = 0;
        byte? lastSent = null;
        int offset = 0, lastOffset = 0, attemptsOfTwo = 0;
        using var pair = BuildPair(e =>
        {
            e.On(0x35, _ => new byte[] { 0x10, 0x40 });
            e.On(0x36, req =>
            {
                lock (counters)
                {
                    byte bsc = req[1];
                    counters.Add(bsc);
                    if (lastSent == bsc) offset = lastOffset; // a repeat: the same block again
                    lastOffset = offset;
                    int take = Math.Min(7, source.Length - offset);
                    var body = new byte[1 + take];
                    body[0] = bsc;
                    Buffer.BlockCopy(source, offset, body, 1, take);
                    offset += take;
                    lastSent = bsc;
                    if (bsc == 0x02 && ++attemptsOfTwo == 1) throw new EcuSilent(); // sent, but the answer is lost
                    return body;
                }
            });
            e.On(0x37, _ => { Interlocked.Increment(ref exits); return Array.Empty<byte>(); });
        }, Options(retries: 1));

        using var cts = new CancellationTokenSource(ShortTimeout);
        var data = await pair.Client.UploadAsync(0x00, 0x11, new byte[] { 0x10 }, new byte[] { (byte)source.Length }, cts.Token);

        data.Should().Equal(source);
        lock (counters) counters.Should().Equal(0x01, 0x02, 0x02, 0x03);
        Volatile.Read(ref exits).Should().Be(1);
    }

    [Fact]
    public void A_Negative_TransferDataRetryCount_Is_Refused()
    {
        var session = NewSession();
        using var bus = OpenClassic(session, 0);
        using var channel = IsoTpFactory.Open(bus, IsoTpEndpoint.Normal(0x7E0, 0x7E8), FastIsoTp());

        Action act = () => UdsClient.Create(channel, new UdsClientOptions { TransferDataRetryCount = -1 }).Dispose();

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*TransferDataRetryCount*");
    }

    [Fact]
    public void With_Carries_TransferDataRetryCount_Over()
    {
        new UdsClientOptions().TransferDataRetryCount.Should().Be(0);
        new UdsClientOptions { TransferDataRetryCount = 4 }.With(maxBusyRepeatRequests: 1)
            .TransferDataRetryCount.Should().Be(4);
    }

#if NET5_0_OR_GREATER // DispatchProxy
    // Transport errors on the request: an ISO-TP fault is repeated, an overflow on our own
    // request is not (the server cannot take a request that large), and neither is an exception
    // that is not an ISO-TP one.
    [Theory]
    [InlineData("timeout", true)]
    [InlineData("otherIsoTp", true)]
    [InlineData("overflow", false)]
    [InlineData("driver", false)]
    public async Task A_Transport_Fault_On_A_Block_Is_Repeated_Unless_It_Is_An_Overflow(string kind, bool repeated)
    {
        var server = new DownloadServer();
        Exception fault = kind switch
        {
            "timeout" => new IsoTpTimeoutException(IsoTpTimer.NBs, "N_Bs expired"),
            "otherIsoTp" => new OtherIsoTpException(),
            "overflow" => new IsoTpOverflowException("peer overflow"),
            _ => new InvalidOperationException("driver failed"),
        };
        FaultingChannel? stub = null;
        using var pair = BuildPair(e => server.Wire(e, maxBlockLength: 6), Options(retries: 1), inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<IIsoTpChannel, FaultingChannel>();
            stub = (FaultingChannel)proxy;
            stub.Inner = inner;
            stub.FaultFor = request => request.Length > 1 && request[0] == 0x36 && request[1] == 0x02 ? fault : null;
            return proxy;
        });
        var payload = Payload(8);

        using var cts = new CancellationTokenSource(ShortTimeout);
        Func<Task> act = () => DownloadAsync(pair.Client, payload, cts.Token);

        if (repeated)
        {
            await act.Should().NotThrowAsync();
            stub!.Faulted.Should().Be(1);
            server.Written.Should().Equal(payload);
            Volatile.Read(ref server.Exits).Should().Be(1);
        }
        else
        {
            (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);
            stub!.Faulted.Should().Be(1);
            Volatile.Read(ref server.Exits).Should().Be(0);
        }
    }

    // An IsoTpException that is not one of the named ones: any ISO-TP fault counts.
    private sealed class OtherIsoTpException : IsoTpException
    {
        public OtherIsoTpException() : base("ISO-TP reception aborted") { }
    }

    // Forwards to a real channel; the first send FaultFor names fails with that exception
    // instead of going out.
    private class FaultingChannel : System.Reflection.DispatchProxy
    {
        private int _faulted;

        public IIsoTpChannel Inner { get; set; } = null!;

        public Func<byte[], Exception?> FaultFor { get; set; } = _ => null;

        public int Faulted => Volatile.Read(ref _faulted);

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IIsoTpChannel.SendWithTransmitStampAsync))
            {
                var request = ((ReadOnlyMemory<byte>)args![0]!).ToArray();
                var fault = FaultFor(request);
                if (fault is not null && Interlocked.CompareExchange(ref _faulted, 1, 0) == 0)
                    return Task.FromException<IsoTpTransmitStamps>(fault);
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
#endif
}
