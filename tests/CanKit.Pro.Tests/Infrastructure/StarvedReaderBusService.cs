using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// A bus service whose subscription's <c>WaitToReadAsync</c> stays pending until
/// <see cref="WakeReader"/>, so the channel's reader task is starved by construction and
/// only a caller-side pump sees what <see cref="Deliver"/> buffered.
/// </summary>
internal sealed class StarvedReaderBusService : ICanBusService
{
    private readonly Channel<CanFrameEvent> _frames = Channel.CreateUnbounded<CanFrameEvent>();
    private readonly TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _wakesServed;

    /// <summary>
    /// Buffers a frame as the demux would. Unstamped by default -- the channel stamps it when
    /// it is pumped; <paramref name="hostArrivalTimestamp"/> makes it arrive at a given
    /// instant, as a frame stamped in time but still on its way through the channel (#150).
    /// </summary>
    public void Deliver(CanFrameView frame, long hostArrivalTimestamp = 0) => _frames.Writer.TryWrite(
        new CanFrameEvent(frame, isEcho: false, TimeSpan.Zero, hostArrivalTimestamp));

    /// <summary>Lets the reader task's wait complete once; every later wait stays pending.</summary>
    public void WakeReader() => _wake.TrySetResult(true);

    /// <summary>Completes when a pump has emptied the buffer (a TryRead returned false).</summary>
    public Task PumpDrained => _drained.Task;

    public void ResetDrained() => _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ICanBus Bus => throw new NotSupportedException();

    public int SubscriptionCount => 1;

    public event EventHandler<Exception>? BackgroundExceptionOccurred
    {
        add { }
        remove { }
    }

    public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null,
        int? bufferCapacity = null, bool includeEcho = false)
        => new Sub(this);

    public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null,
        bool includeEcho = false)
        => new Sub(this);

    /// <summary>Every frame the channel put on the wire, in order.</summary>
    public List<byte[]> Sent { get; } = new();

    public Task<TxConfirmation> SendConfirmed(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(frame.Data.ToArray());
        return Task.FromResult(new TxConfirmation { Confirmed = true });
    }

    public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
        => Array.Empty<FilterOverlap>();

    public void Dispose() => _frames.Writer.TryComplete();

    private sealed class Sub : ISubscription
    {
        private readonly StarvedReaderBusService _owner;
        public Sub(StarvedReaderBusService owner) => _owner = owner;

        public IAsyncEnumerable<CanFrameEvent> Frames => _owner._frames.Reader.ReadAllAsync();

        public bool TryRead(out CanFrameEvent frameEvent)
        {
            bool ok = _owner._frames.Reader.TryRead(out frameEvent);
            if (!ok) _owner._drained.TrySetResult(true);
            return ok;
        }

        public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _owner._wakesServed, 1) == 0)
                return await _owner._wake.Task.WaitAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }

        public void Reconfigure(CanIdFilter filter) { }

        public void Reconfigure(Func<CanFrameEvent, bool>? predicate) { }

        public void Dispose() { }
    }
}
