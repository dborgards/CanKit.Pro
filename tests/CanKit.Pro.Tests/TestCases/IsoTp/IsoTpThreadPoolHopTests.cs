using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.IsoTp;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.IsoTp;

/// <summary>
/// The awaitable that <see cref="IsoTpChannel"/>'s <c>SendAsync</c> uses to leave the thread that
/// released it from the send gate (#339, #373). The channel-level test of that only discriminates
/// on .NET Framework, where <c>SemaphoreSlim</c> completes a cancelled waiter inline; on .NET 10 the
/// runtime already hops. These pin the hop's own contract on every target: it never resumes inline,
/// never on the awaiting thread's <see cref="SynchronizationContext"/>, and <c>OnCompleted</c> (which
/// an <c>await</c> never calls) flows the <see cref="ExecutionContext"/> as that member must.
/// </summary>
public sealed class IsoTpThreadPoolHopTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Awaiting_The_Hop_Resumes_On_The_Thread_Pool_And_Not_On_The_Awaiting_Context()
    {
        var context = new CountingSynchronizationContext();
        Task<bool>? hopped = null;

        // A dedicated thread is not a pool thread, so resuming inline on it is told apart from
        // the hop by IsThreadPoolThread alone; the installed context records any attempt to
        // resume through it, which Task.Yield would make.
        await RunOnDedicatedThreadAsync(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            hopped = ResumedOnPoolThreadAsync();
        });

        (await hopped!.WaitAsync(ShortTimeout)).Should().BeTrue("the hop resumes on a thread-pool thread");
        context.Calls.Should().Be(0, "the hop ignores the awaiting thread's SynchronizationContext");

        static async Task<bool> ResumedOnPoolThreadAsync()
        {
            await IsoTpChannel.ThreadPoolHop.Instance;
            return Thread.CurrentThread.IsThreadPoolThread;
        }
    }

    [Fact]
    public async Task OnCompleted_Queues_To_The_Thread_Pool_And_Flows_The_ExecutionContext()
    {
        var flowed = new AsyncLocal<string?>();
        var ran = new TaskCompletionSource<(bool PoolThread, string? Flowed)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await RunOnDedicatedThreadAsync(() =>
        {
            flowed.Value = "caller";
            IsoTpChannel.ThreadPoolHop.Instance.OnCompleted(
                () => ran.TrySetResult((Thread.CurrentThread.IsThreadPoolThread, flowed.Value)));
        });

        var (poolThread, value) = await ran.Task.WaitAsync(ShortTimeout);
        poolThread.Should().BeTrue("OnCompleted queues the continuation instead of running it inline");
        value.Should().Be("caller", "OnCompleted, unlike UnsafeOnCompleted, flows the ExecutionContext");
    }

    // LongRunning gives the action a thread of its own rather than a pool thread, and the task
    // carries anything it throws. The precondition is checked rather than assumed: on a pool
    // thread, a hop that resumed inline would pass the IsThreadPoolThread assertions unseen.
    private static async Task RunOnDedicatedThreadAsync(Action action)
    {
        var onPoolThread = await Task.Factory.StartNew(() =>
        {
            var poolThread = Thread.CurrentThread.IsThreadPoolThread;
            action();
            return poolThread;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(ShortTimeout);

        onPoolThread.Should().BeFalse("the action must start on a thread that is not a pool thread");
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _calls);
            base.Post(d, state);
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _calls);
            base.Send(d, state);
        }
    }
}
