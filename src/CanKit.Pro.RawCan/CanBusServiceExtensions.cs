using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can.Definitions;

namespace CanKit.Pro.RawCan
{
    /// <summary>
    /// Callback-style convenience layer over <see cref="ICanBusService.Subscribe(Func{CanFrameView,bool}?,int?)"/>
    /// for callers who want "filter + handler" instead of driving the async-enumerable
    /// <see cref="ISubscription.Frames"/> stream themselves.
    /// (对 <see cref="ICanBusService.Subscribe(Func{CanFrameView,bool}?,int?)"/> 的回调式便捷封装，
    /// 面向只想要"过滤 + 处理函数"、不想自己驱动 <see cref="ISubscription.Frames"/> 异步流的调用方。)
    /// </summary>
    public static class CanBusServiceExtensions
    {
        /// <summary>
        /// Registers a subscription and invokes <paramref name="onNext"/> for every frame it
        /// accepts, on a dedicated background task. Disposing the returned handle stops delivery
        /// and lets that task end.
        /// (注册一路订阅，对其接收到的每一帧在专用后台任务上调用 <paramref name="onNext"/>；
        /// 释放返回的句柄会停止投递并使该任务结束。)
        /// </summary>
        /// <remarks>
        /// Built entirely on the existing <see cref="ISubscription"/> pull API, so the same
        /// per-subscription bounded, drop-oldest buffer applies (FR-RAW-011): a slow
        /// <paramref name="onNext"/> only ever falls behind and drops its own oldest frames -- it
        /// can never delay delivery to other subscriptions or to the bus's own
        /// <c>FrameObserved</c> event, because the dispatch hot path never waits on a
        /// subscriber's consumer. An exception thrown by <paramref name="onNext"/> is isolated
        /// per frame -- delivery continues with the next frame -- and, when <paramref name="service"/>
        /// is a <see cref="CanBusService"/>, routed through
        /// <see cref="ICanBusService.BackgroundExceptionOccurred"/>, the same fault channel every
        /// other background failure in this service uses.
        /// (完全基于现有的 <see cref="ISubscription"/> 拉取式 API 构建，因此同样适用逐订阅有界、
        /// 丢弃最旧的缓冲区（FR-RAW-011）：迟缓的 <paramref name="onNext"/> 只会自己落后并丢弃自己最旧的帧——
        /// 因为分发热路径从不等待订阅方的消费者，它永远不会延迟向其他订阅或总线自身 <c>FrameObserved</c>
        /// 事件的投递。<paramref name="onNext"/> 抛出的异常按帧隔离——投递会以下一帧继续——当
        /// <paramref name="service"/> 是 <see cref="CanBusService"/> 时，异常会经由
        /// <see cref="ICanBusService.BackgroundExceptionOccurred"/> 上抛，与本服务其余后台故障共用同一通道。)
        /// </remarks>
        /// <param name="service">The service to subscribe on.</param>
        /// <param name="onNext">Invoked for each accepted frame, in arrival order.</param>
        /// <param name="predicate">Per-frame filter, or null to accept all frames.</param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity for the underlying subscription; null uses
        /// <see cref="CanBusService.DefaultBufferCapacity"/>.
        /// </param>
        /// <returns>Disposing this stops the subscription and the background delivery task.</returns>
        public static IDisposable Subscribe(
            this ICanBusService service,
            Action<CanFrameView> onNext,
            Func<CanFrameView, bool>? predicate = null,
            int? bufferCapacity = null)
        {
            if (service is null) throw new ArgumentNullException(nameof(service));
            if (onNext is null) throw new ArgumentNullException(nameof(onNext));

            return new CallbackSubscription(service.Subscribe(predicate, bufferCapacity), service, onNext);
        }

        private sealed class CallbackSubscription : IDisposable
        {
            private readonly ISubscription _subscription;
            private readonly Task _pumpTask;
            private readonly AsyncLocal<bool> _isOnPump = new();
            private int _disposed;

            public CallbackSubscription(ISubscription subscription, ICanBusService service, Action<CanFrameView> onNext)
            {
                _subscription = subscription;
                _pumpTask = Task.Run(async () =>
                {
                    // Marks the entire pump (including onNext) so Dispose can skip the join
                    // below when invoked from the callback itself -- waiting on this task from
                    // inside it would deadlock until the timeout. Same reentrancy guard as
                    // ProtocolActor.Dispose / _isOnLoop.
                    _isOnPump.Value = true;
                    await foreach (var frame in _subscription.Frames.ConfigureAwait(false))
                    {
                        try
                        {
                            onNext(frame);
                        }
                        catch (Exception ex)
                        {
                            if (service is CanBusService concrete)
                                concrete.RaiseBackgroundException(ex);
                        }
                    }
                });
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
                // Completes the channel, which ends the pump task's `await foreach` gracefully
                // (no exception -- see Subscription.Dispose/ReadAsync).
                _subscription.Dispose();
                if (_isOnPump.Value) return;
                // Best-effort bounded join so a caller who disposes and then immediately tears
                // down surrounding state doesn't race the last in-flight onNext call; matches the
                // same dispose-teardown idiom used for background readers throughout this codebase.
                try { _pumpTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            }
        }
    }
}
