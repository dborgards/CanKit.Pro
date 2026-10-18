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
        if (_frames.Reader.TryRead(out var item))
        {
            frameEvent = item.Frame;
            item.Delivered.TrySetResult(true);
            return true;
        }
        frameEvent = default;
        return false;
    }
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        => _frames.Reader.WaitToReadAsync(cancellationToken);
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
        Started.TrySetResult(true);
        try
        {
            if (ParkReader)
            {
                // Deliberately emulate a subscription that cannot finish during Dispose's
                // bounded wait. Release is always signalled by the test's finally block.
                await Release.Task;
                try
                {
                    using var handle = cancellationToken.WaitHandle;
                    CancellationObserved.TrySetResult(handle.WaitOne(0));
                }
                catch (ObjectDisposedException ex)
                {
                    CancellationObserved.TrySetException(ex);
                }
                yield break;
            }

            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame.Frame;
                frame.Delivered.TrySetResult(true);
            }
        }
        finally
        {
            Finished.TrySetResult(true);
        }
    }
}
