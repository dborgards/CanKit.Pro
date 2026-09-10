#if !NET

// The polyfill deliberately lives in System.Threading.Tasks rather than in
// CanKit.Pro.Tests.Infrastructure. `Task.WaitAsync` is the one .NET 6+ API this suite leans on
// heavily — 43 call sites across seven files — and every one of them already has
// `using System.Threading.Tasks;` at the top because it is awaiting a Task in the first place.
// Putting the extensions in that namespace means the net48 leg compiles the existing call sites
// unchanged: no `#if` at a single assertion, no extra using that only one TFM needs, and a test
// written tomorrow in a file that has never heard of this class still builds on both legs.
namespace System.Threading.Tasks;

/// <summary>
/// <c>Task.WaitAsync</c> for the <c>net48</c> leg of the suite. The method arrived in .NET 6 and
/// is absent from .NET Framework, so without it the second target framework could not compile at
/// all — see <c>tests/Directory.Build.props</c> for why that target framework exists.
/// </summary>
/// <remarks>
/// The observable contract is copied from the framework's own, because tests assert against it:
/// the timeout overload throws <see cref="TimeoutException"/> and the cancellation overload
/// throws <see cref="OperationCanceledException"/>, and in both cases the awaited task is left
/// running rather than cancelled — <c>WaitAsync</c> bounds the *wait*, not the work.
/// </remarks>
internal static class TaskWaitAsyncPolyfill
{
    public static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        await BoundAsync(task, timeout).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout)
    {
        await BoundAsync(task, timeout).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    public static async Task WaitAsync(this Task task, CancellationToken cancellationToken)
    {
        await BoundAsync(task, cancellationToken).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, CancellationToken cancellationToken)
    {
        await BoundAsync(task, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    // Returns once `task` has completed, or throws if the bound was reached first. The caller
    // then awaits `task` itself, which is what propagates its result or its exception with the
    // stack trace intact — re-throwing from here would bury it.
    private static async Task BoundAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return;
        }

        // The delay is cancelled on the happy path. A pending Task.Delay holds a timer entry
        // until it fires, and a suite with hundreds of five-second bounds would otherwise keep
        // every one of them alive to the end of the run.
        using (var delayCancellation = new CancellationTokenSource())
        {
            var delay = Task.Delay(timeout, delayCancellation.Token);
            var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task))
            {
                throw new TimeoutException("The operation has timed out.");
            }

            delayCancellation.Cancel();
        }
    }

    private static async Task BoundAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // RunContinuationsAsynchronously keeps the continuation off the thread that cancels the
        // token: without it, whatever called Cancel() would run the rest of the awaiting test
        // inline, which on a token cancelled from a timer means test code on a timer thread.
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelled))
        {
            var finished = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }
}

#endif
