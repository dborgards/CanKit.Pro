using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.RawCan;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Lss;

public class LssMasterBoundaryTests
{
    [Theory]
    [InlineData(499, false, false)] // Before handoff.
    [InlineData(500, false, true)] // At handoff.
    [InlineData(1000, false, true)] // At transmit completion.
    [InlineData(0, false, true)] // Missing timestamps do not mean stale frames.
    [InlineData(-1, false, true)]
    [InlineData(0, true, true)] // At the deadline.
    [InlineData(1, true, false)] // Immediately after the deadline.
    public async Task Inquire_Filters_Queued_Replies_By_Their_Arrival_Timestamp(
        long timestamp, bool relativeToDeadline, bool accepted)
    {
        using var service = new ControlledService();
        var timeout = TimeSpan.FromSeconds(1);
        long deadline = 1000 + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        long arrival = relativeToDeadline ? deadline + timestamp : timestamp;
        service.Send = async frame =>
        {
            var reply = new byte[LssFrames.Length];
            reply[0] = frame.Data.Span[0];
            reply[1] = 0x42;
            service.Subscription.Enqueue(new CanFrameEvent(
                new CanFrameView(CanFrameType.Can20, (int)CanOpenCobId.LssSlaveMessage, reply, FrameFlags.None),
                isEcho: false, receiveTimestamp: TimeSpan.Zero, hostArrivalTimestamp: arrival));
            // Resume the sender only after the master has buffered the reply. The absolute
            // deadline is already past, so neither acceptance nor rejection needs a timer.
            await service.Subscription.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new TxConfirmation
            {
                Confirmed = true,
                HostHandoffTimestamp = 500,
                HostTransmitTimestamp = 1000,
            };
        };
        using var master = CanOpenLss.OpenMaster(service, new LssMasterOptions { ResponseTimeout = timeout });

        if (accepted)
            (await master.InquireNodeIdAsync()).Should().Be(0x42);
        else
        {
            var inquire = async () => await master.InquireNodeIdAsync();
            await inquire.Should().ThrowAsync<LssTimeoutException>();
        }
    }

    [Fact]
    public async Task Dispose_Leaves_A_Parked_Readers_Cancellation_Source_Alive()
    {
        using var service = new ControlledService();
        service.Subscription.ParkReader = true;
        using var master = CanOpenLss.OpenMaster(service, leaveOpen: false);
        try
        {
            await service.Subscription.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            master.Dispose();

            service.Disposals.Should().Be(1);
            service.Subscription.Disposed.Should().BeTrue();
            service.Subscription.Finished.Task.IsCompleted.Should().BeFalse();
            service.Subscription.Release.TrySetResult(true);
            // Accessing WaitHandle throws if Dispose tore down the source while this reader
            // was still using its token. The reader is held by a signal, not a scheduling delay.
            (await service.Subscription.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().BeTrue();
        }
        finally
        {
            service.Subscription.Release.TrySetResult(true);
            await service.Subscription.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ControlledService : ICanBusService
    {
        public ControlledSubscription Subscription { get; } = new();
        public Func<CanFrame, Task<TxConfirmation>> Send { get; set; } = _ => throw new NotSupportedException();
        public int Disposals { get; private set; }
        public ICanBus Bus => throw new NotSupportedException();
        public int SubscriptionCount => Subscription.Disposed ? 0 : 1;

        public event EventHandler<Exception>? BackgroundExceptionOccurred
        {
            add { }
            remove { }
        }

        public ISubscription Subscribe(Func<CanFrameEvent, bool>? predicate = null, int? bufferCapacity = null,
            bool includeEcho = false) => Subscription;

        public ISubscription Subscribe(CanIdFilter filter, int? bufferCapacity = null, bool includeEcho = false)
            => Subscription;

        public IReadOnlyList<FilterOverlap> FindOverlappingFilterSubscriptions() => Array.Empty<FilterOverlap>();

        public Task<TxConfirmation> SendConfirmedAsync(CanFrame frame, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) => Send(frame);

        public void Dispose()
        {
            Disposals++;
            Subscription.Dispose();
        }
    }

    private sealed class ControlledSubscription : ISubscription
    {
        private readonly Channel<CanFrameEvent> _frames = Channel.CreateUnbounded<CanFrameEvent>();
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ParkReader { get; set; }
        public bool Disposed { get; private set; }
        public IAsyncEnumerable<CanFrameEvent> Frames => ReadFramesAsync();

        public void Enqueue(CanFrameEvent frame) => _frames.Writer.TryWrite(frame).Should().BeTrue();
        public bool TryRead(out CanFrameEvent frameEvent) => _frames.Reader.TryRead(out frameEvent);
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
                    yield return frame;
                    Delivered.TrySetResult(true);
                }
            }
            finally
            {
                Finished.TrySetResult(true);
            }
        }
    }
}
