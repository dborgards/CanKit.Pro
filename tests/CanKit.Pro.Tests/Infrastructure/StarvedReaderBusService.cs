using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Whether the subscription's <c>Frames</c> enumeration yields nothing until it is cancelled,
    /// so what <see cref="Deliver"/> buffered is left for a <c>TryRead</c> drain -- the state a
    /// collection window can end in when its reader was descheduled (#171).
    /// </summary>
    public bool HoldFrames { get; set; }

    /// <summary>When set, the reader task's wait throws it once woken, as a subscription that failed would.</summary>
    public Exception? ReaderFault { get; set; }

    /// <summary>When set, the <c>Frames</c> enumeration throws it once woken, as a subscription whose demux failed would.</summary>
    public Exception? FramesFault { get; set; }

    /// <summary>When set, the subscription ends on the wake: the reader's wait returns false, as when its service is disposed.</summary>
    public bool EndSubscriptionOnWake { get; set; }

    /// <summary>When set, a <c>TryRead</c> that took a frame holds it until the gate opens: a caller-side pump caught between taking a frame and posting it.</summary>
    public ManualResetEventSlim? TryReadGate { get; set; }

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

    /// <summary>
    /// Runs inside <see cref="SendConfirmedAsync"/> before it confirms: the time a driver takes to
    /// confirm, modelled by moving a virtual clock (#171).
    /// </summary>
    public Action? OnSendConfirmed { get; set; }

    /// <summary>Every frame the channel put on the wire, in order.</summary>
    public List<byte[]> Sent { get; } = new();

    public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(frame.Data.ToArray());
        OnSendConfirmed?.Invoke();
        return Task.FromResult(new TxConfirmation { Confirmed = true });
    }

    public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions()
        => Array.Empty<FilterOverlap>();

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        _frames.Writer.TryComplete();
    }

    private sealed class Sub : ISubscription
    {
        private readonly StarvedReaderBusService _owner;
        public Sub(StarvedReaderBusService owner) => _owner = owner;

        public IAsyncEnumerable<CanFrameEvent> Frames
            => _owner.FramesFault is not null ? Faulting(_owner)
             : _owner.HoldFrames ? Held() : _owner._frames.Reader.ReadAllAsync();

        private static async IAsyncEnumerable<CanFrameEvent> Faulting(StarvedReaderBusService owner,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await owner._wake.Task.WaitAsync(cancellationToken);
            throw owner.FramesFault!;
#pragma warning disable CS0162 // an iterator needs a yield to be one
            yield break;
#pragma warning restore CS0162
        }

        private static async IAsyncEnumerable<CanFrameEvent> Held(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public bool TryRead(out CanFrameEvent frameEvent)
        {
            bool ok = _owner._frames.Reader.TryRead(out frameEvent);
            if (!ok) _owner._drained.TrySetResult(true);
            else _owner.TryReadGate?.Wait();
            return ok;
        }

        public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _owner._wakesServed, 1) == 0)
            {
                var woken = await _owner._wake.Task.WaitAsync(cancellationToken);
                if (_owner.ReaderFault is { } fault) throw fault;
                if (_owner.EndSubscriptionOnWake) return false;
                return woken;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }

        public void Reconfigure(CanIdFilter filter) { }

        public void Reconfigure(Func<CanFrameEvent, bool>? predicate) { }

        public void Dispose() { }
    }
}
