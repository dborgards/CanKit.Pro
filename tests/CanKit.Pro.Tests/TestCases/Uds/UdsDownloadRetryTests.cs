using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Tests.Infrastructure;
using CanKit.Pro.Uds;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.Uds;

// FR-UDS-012, #426. Deadlines run on a virtual clock; wall time only guards against hangs.
public class UdsDownloadRetryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan P2 = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan P2Star = TimeSpan.FromSeconds(2);

    private static UdsClientOptions Options(int retries) => new()
    {
        P2ClientMax = P2,
        P2StarClientMax = P2Star,
        MaxTransferDataTimeoutRetries = retries,
        MaxBusyRepeatRequests = 1,
    };

    private static Task Download(IUdsClient client, byte[]? data = null, CancellationToken token = default)
    {
        data ??= new byte[] { 0xAB };
        return client.DownloadAsync(0, 0x22, new byte[] { 0, 0 },
            new byte[] { (byte)(data.Length >> 8), (byte)data.Length }, data, token);
    }

    private static async Task Expire(VirtualClock clock, ProtocolActor actor, TimeSpan budget)
    {
        await clock.WaitUntilTimerArmedAsync(actor, budget, Guard);
        await clock.AdvanceAsync(budget + TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    public async Task Application_Timeout_Retry_Is_OptIn_And_Bounded_Per_Block(int retries, int losses, bool succeeds)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var attempts = 0;
        using var channel = new ScriptedChannel(actor, request =>
            request[0] == 0x36 && ++attempts <= losses ? null : Positive(request));
        using var client = new UdsClientImpl(channel, Options(retries), false, actor);
        var download = Download(client);
        for (var i = 0; i < Math.Min(losses, retries + 1); i++)
        {
            await channel.WaitForRequests(2 + i);
            await Expire(clock, actor, P2);
        }
        if (succeeds)
            await download.WaitAsync(Guard);
        else
            await FluentActions.Awaiting(() => download.WaitAsync(Guard)).Should().ThrowAsync<UdsTimeoutException>();
        var sent = channel.Snapshot();
        sent.Where(r => r[0] == 0x36).Should().HaveCount(succeeds ? losses + 1 : retries + 1);
        foreach (var request in sent.Where(r => r[0] == 0x36)) request.Should().Equal(0x36, 1, 0xAB);
        sent.Count(r => r[0] == 0x37).Should().Be(succeeds ? 1 : 0);
    }

    [Fact]
    public async Task P2Star_Timeout_Also_Repeats_The_Same_Block()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var attempts = 0;
        using var channel = new ScriptedChannel(actor, request =>
            request[0] == 0x36 && ++attempts == 1 ? new byte[] { 0x7F, 0x36, 0x78 } : Positive(request));
        using var client = new UdsClientImpl(channel, Options(1), false, actor);
        var download = Download(client);
        await Expire(clock, actor, P2Star);
        await download.WaitAsync(Guard);
        channel.Snapshot().Where(r => r[0] == 0x36).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0x73)]
    [InlineData(0x31)]
    [InlineData(0x72)]
    public async Task Final_Negative_Response_Is_Not_A_Timeout_Retry(int nrc)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36
            ? new byte[] { 0x7F, 0x36, (byte)nrc } : Positive(request));
        using var client = new UdsClientImpl(channel, Options(2), false, actor);
        var failure = (await FluentActions.Awaiting(() => Download(client).WaitAsync(Guard))
            .Should().ThrowAsync<UdsNegativeResponseException>()).Which;
        failure.Code.Should().Be((byte)nrc);
        channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_And_Malformed_Response_Failures_Are_Not_Retried(bool transport)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new ScriptedChannel(actor, request =>
        {
            if (request[0] != 0x36) return Positive(request);
            if (transport) throw new IsoTpTimeoutException(IsoTpTimer.NAs, "injected transport timeout");
            return new byte[] { 0x76 };
        });
        using var client = new UdsClientImpl(channel, Options(2), false, actor);
        if (transport)
            await FluentActions.Awaiting(() => Download(client).WaitAsync(Guard)).Should().ThrowAsync<IsoTpTimeoutException>();
        else
            await FluentActions.Awaiting(() => Download(client).WaitAsync(Guard)).Should().ThrowAsync<UdsProtocolException>();
        channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36);
    }

    [Fact]
    public async Task Cancellation_Ends_The_Download_Without_A_Retry_Or_TransferExit()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36 ? null : Positive(request));
        using var client = new UdsClientImpl(channel, Options(2), false, actor);
        using var cancellation = new CancellationTokenSource();
        var download = Download(client, token: cancellation.Token);
        await channel.WaitForRequests(2);
        await clock.WaitUntilTimerArmedAsync(actor, P2, Guard);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => download.WaitAsync(Guard)).Should().ThrowAsync<OperationCanceledException>();
        channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36);
    }

    [Fact]
    public async Task NRC21_Budget_Is_Independent_Of_The_Timeout_Retry_Budget()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var attempts = 0;
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36 ? ++attempts switch
        {
            1 or 3 => new byte[] { 0x7F, 0x36, 0x21 },
            2 => null,
            _ => Positive(request),
        } : Positive(request));
        using var client = new UdsClientImpl(channel, Options(1), false, actor);
        var download = Download(client);
        await channel.WaitForRequests(3); // setup, NRC 0x21, lost response
        await Expire(clock, actor, P2);
        await download.WaitAsync(Guard);
        attempts.Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_Retry_Observes_Quarantine_And_The_Whole_Operation_Cancellation(bool cancel)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var attempts = 0;
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36 && ++attempts == 1
            ? null : Positive(request));
        using var client = new UdsClientImpl(channel, new UdsClientOptions
        {
            P2ClientMax = P2,
            P2StarClientMax = P2Star,
            MaxTransferDataTimeoutRetries = 1,
            QuarantineLateResponses = true,
        }, false, actor);
        using var cancellation = new CancellationTokenSource();
        var download = Download(client, token: cancellation.Token);
        await channel.WaitForRequests(2);
        await Expire(clock, actor, P2);
        await clock.WaitUntilTimerArmedAsync(actor, P2Star, Guard);
        channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36);
        if (cancel)
        {
            cancellation.Cancel();
            await FluentActions.Awaiting(() => download.WaitAsync(Guard)).Should().ThrowAsync<OperationCanceledException>();
            channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36);
        }
        else
        {
            await clock.AdvanceAsync(P2Star);
            await download.WaitAsync(Guard);
            channel.Snapshot().Select(r => r[0]).Should().Equal(0x34, 0x36, 0x36, 0x37);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptIn_Does_Not_Retry_SingleStep_TransferData_Or_Upload(bool upload)
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36 ? null : Positive(request));
        using var client = new UdsClientImpl(channel, Options(2), false, actor);
        var operation = upload
            ? client.UploadAsync(0, 0x11, new byte[] { 0 }, new byte[] { 1 })
            : client.TransferDataAsync(1, new byte[] { 0xAB });
        await channel.WaitForRequests(upload ? 2 : 1);
        await Expire(clock, actor, P2);
        await FluentActions.Awaiting(() => operation.WaitAsync(Guard)).Should().ThrowAsync<UdsTimeoutException>();
        channel.Snapshot().Count(r => r[0] == 0x36).Should().Be(1);
        channel.Snapshot().Count(r => r[0] == 0x37).Should().Be(0);
    }

    [Fact]
    public async Task Retry_Budget_Restarts_Per_Block_And_Counter_Wrap_Advances_Only_After_Success()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        var lost = new HashSet<byte>();
        var data = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        using var channel = new ScriptedChannel(actor, request => request[0] == 0x36
            && (request[1] == 0xFF || request[1] == 0) && lost.Add(request[1]) ? null : Positive(request));
        using var client = new UdsClientImpl(channel, Options(1), false, actor);
        var download = Download(client, data);
        await channel.WaitForRequests(256); // setup and blocks 1..255; block 255 loses its answer
        await Expire(clock, actor, P2);
        await channel.WaitForRequests(258); // repeat 255 succeeds, then block 0 loses its answer
        await Expire(clock, actor, P2);
        await download.WaitAsync(Guard);
        var blocks = channel.Snapshot().Where(r => r[0] == 0x36).ToArray();
        blocks.Select(r => r[1]).Should().Equal(Enumerable.Range(1, 254).Select(i => (byte)i)
            .Concat(new byte[] { 0xFF, 0xFF, 0, 0, 1 }));
        blocks[254].Should().Equal(blocks[255]);
        blocks[256].Should().Equal(blocks[257]);
    }

    [Fact]
    public void Options_Reject_Negative_Retry_Count_And_With_Preserves_The_OptIn()
    {
        using var clock = new VirtualClock();
        var actor = clock.NewActor();
        using var channel = new ScriptedChannel(actor, Positive);
        var options = Options(2);
        options.With(p2ClientMax: P2Star).MaxTransferDataTimeoutRetries.Should().Be(2);
        new UdsClientOptions().MaxTransferDataTimeoutRetries.Should().Be(0);
        FluentActions.Invoking(() => new UdsClientImpl(channel, Options(-1), false, actor))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static byte[] Positive(byte[] request) => request[0] switch
    {
        0x34 or 0x35 => new byte[] { (byte)(request[0] + 0x40), 0x10, 3 },
        0x36 => new byte[] { 0x76, request[1] },
        0x37 => new byte[] { 0x77 },
        _ => throw new InvalidOperationException("Unexpected service"),
    };

    private sealed class ScriptedChannel : IIsoTpChannel
    {
        private readonly ProtocolActor _actor;
        private readonly Func<byte[], byte[]?> _respond;
        private readonly Channel<IsoTpReceivedPdu> _inbox = Channel.CreateUnbounded<IsoTpReceivedPdu>();
        private readonly List<byte[]> _sent = new();
        private TaskCompletionSource<bool> _changed = NewSignal();

        public ScriptedChannel(ProtocolActor actor, Func<byte[], byte[]?> respond) { _actor = actor; _respond = respond; }
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[][] Snapshot() { lock (_sent) return _sent.ToArray(); }
        public async Task WaitForRequests(int count)
        {
            while (true)
            {
                Task changed;
                lock (_sent) { if (_sent.Count >= count) return; changed = _changed.Task; }
                await changed.WaitAsync(Guard);
            }
        }
        public IsoTpEndpoint Endpoint => IsoTpEndpoint.Normal(0x7E0, 0x7E8);
        public IsoTpChannelOptions Options { get; } = new();
        public Task<IsoTpTransmitStamps> SendWithTransmitStampAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pdu.ToArray();
            lock (_sent) { _sent.Add(request); _changed.TrySetResult(true); _changed = NewSignal(); }
            var now = _actor.TimeSource.GetTimestamp();
            var response = _respond(request);
            if (response is not null) _inbox.Writer.TryWrite(new IsoTpReceivedPdu(response, now));
            return Task.FromResult(new IsoTpTransmitStamps(now, now));
        }
        public async Task SendAsync(ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken = default)
            => await SendWithTransmitStampAsync(pdu, cancellationToken);
        public Task<IsoTpReceivedPdu> ReceiveWithArrivalAsync(CancellationToken cancellationToken = default)
            => _inbox.Reader.ReadAsync(cancellationToken).AsTask();
        public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
            => (await ReceiveWithArrivalAsync(cancellationToken)).Pdu;
        public bool TryReceiveWithArrival(out IsoTpReceivedPdu pdu) => _inbox.Reader.TryRead(out pdu);
        public IReadOnlyList<IsoTpReceptionInProgress> GetReceptionsInProgress() => Array.Empty<IsoTpReceptionInProgress>();
        public Task SettleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int DiscardPendingPdus() => DiscardPendingPdus(long.MaxValue);
        public int DiscardPendingPdus(long arrivedBefore)
        {
            var kept = new List<IsoTpReceivedPdu>();
            var dropped = 0;
            while (_inbox.Reader.TryRead(out var pdu))
                if (pdu.ArrivalTimestamp < arrivedBefore) dropped++; else kept.Add(pdu);
            foreach (var pdu in kept) _inbox.Writer.TryWrite(pdu);
            return dropped;
        }
        public IAsyncEnumerable<byte[]> ReceiveAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public event EventHandler<IsoTpDatagramReceivedEventArgs>? DatagramReceived { add { } remove { } }
        public event EventHandler<Exception>? BackgroundExceptionOccurred { add { } remove { } }
        public void Dispose() => _inbox.Writer.TryComplete();
    }
}
