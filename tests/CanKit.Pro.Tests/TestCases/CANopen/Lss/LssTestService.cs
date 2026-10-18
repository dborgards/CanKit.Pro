using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

internal sealed class LssTestService : ICanBusService
{
    public LssTestSubscription Subscription { get; } = new();
    public Func<CanFrame, Task<TxConfirmation>> Send { get; set; } = _ => throw new NotSupportedException();
    public System.Collections.Concurrent.ConcurrentQueue<byte> Commands { get; } = new();
    public int Disposals { get; private set; }
    public ICanBus Bus => throw new NotSupportedException();
    public int SubscriptionCount => Subscription.Disposed ? 0 : 1;

    public event EventHandler<Exception>? BackgroundExceptionOccurred
    {
        add { }
        remove { }
    }

    public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
        bool includeEcho = false)
    {
        Subscription.Filter = predicate;
        return Subscription;
    }

    public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
        => Subscription;

    public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => Array.Empty<FilterOverlap>();

    public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(frame.Data.Span[0]);
        return Send(frame);
    }

    public void Dispose()
    {
        Disposals++;
        Subscription.Dispose();
    }
}

internal sealed class LssTestSubscription : ISubscription
{
    private readonly Channel<(CanFrameEvent Frame, TaskCompletionSource<bool> Delivered)> _frames = Channel.CreateUnbounded<(CanFrameEvent, TaskCompletionSource<bool>)>();
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<CanFrameEvent, bool>? Filter { get; set; }
    public bool ParkReader { get; set; }
    public Exception? ReadFailure { get; set; }
    public Action? BeforeRead { get; set; }
    public Exception? WaitFailure { get; set; }
    public bool FailAfterFirstRead { get; set; }
    private bool _readOne;
    public bool Disposed { get; private set; }
    public IAsyncEnumerable<CanFrameEvent> Frames => ReadFramesAsync();

    public Task Enqueue(CanFrameEvent frame)
    {
        if (Filter is { } filter && !filter(frame)) return Task.CompletedTask;
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _frames.Writer.TryWrite((frame, delivered)).Should().BeTrue();
        return delivered.Task;
    }

    public bool TryRead(out CanFrameEvent frameEvent)
    {
        bool read = TryRead(out frameEvent, out var delivered);
        delivered?.TrySetResult(true);
        return read;
    }

    private bool TryRead(out CanFrameEvent frameEvent, out TaskCompletionSource<bool>? delivered)
    {
        BeforeRead?.Invoke();
        if (ReadFailure is { } error && (!FailAfterFirstRead || _readOne)) throw error;
        if (_frames.Reader.TryRead(out var item))
        {
            _readOne = true;
            frameEvent = item.Frame;
            delivered = item.Delivered;
            return true;
        }
        frameEvent = default;
        delivered = null;
        return false;
    }
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        Started.TrySetResult(true);
        if (WaitFailure is { } error) throw error;
        return ParkReader ? new ValueTask<bool>(WaitParkedAsync(cancellationToken))
            : _frames.Reader.WaitToReadAsync(cancellationToken);
    }
    public void Reconfigure(CanIdFilter filter) => throw new NotSupportedException();
    public void Reconfigure(Func<CanFrameEvent, bool>? predicate) => throw new NotSupportedException();

    public void Dispose()
    {
        Disposed = true;
        _frames.Writer.TryComplete();
    }

    private async IAsyncEnumerable<CanFrameEvent> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await WaitToReadAsync(cancellationToken))
            while (TryRead(out var frame, out var delivered))
            {
                yield return frame;
                // The consumer has handled the frame before its enqueue is acknowledged.
                delivered!.TrySetResult(true);
            }
    }

    private async Task<bool> WaitParkedAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Ignore cancellation until the test releases this intentionally stalled reader.
            await Release.Task;
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var handle = cancellationToken.WaitHandle;
                    CancellationObserved.TrySetResult(handle.WaitOne(0));
                }
                catch (ObjectDisposedException ex)
                {
                    CancellationObserved.TrySetException(ex);
                }
            }
            return false;
        }
        finally
        {
            Finished.TrySetResult(true);
        }
    }
}
