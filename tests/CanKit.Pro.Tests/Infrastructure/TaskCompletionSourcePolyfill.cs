#if !NET

using System.Collections.Generic;

// Same placement argument as TaskWaitAsyncPolyfill: the type belongs in the namespace the real
// one lives in, so a file that uses it needs no using it would not otherwise have and no `#if`
// around the declaration. Arity keeps it unambiguous — `TaskCompletionSource<T>` still resolves
// to the framework's generic type on every target framework; only the arity-0 spelling, which
// .NET Framework simply does not have, resolves here.
namespace System.Threading.Tasks;

/// <summary>
/// The non-generic <c>TaskCompletionSource</c> for the <c>net48</c> leg of the suite. It arrived
/// in .NET 5; netstandard2.0 and .NET Framework have only <see cref="TaskCompletionSource{TResult}"/>,
/// so a test-infrastructure file that produces a plain <see cref="Task"/> — DeferredEchoQueue is
/// the first — would not compile on that leg without it.
/// </summary>
/// <remarks>
/// A thin forwarder over <c>TaskCompletionSource&lt;bool&gt;</c>, which is exactly how .NET
/// described its own non-generic version before it existed. The surface is reproduced in full
/// rather than trimmed to today's callers: the point of shimming instead of rewriting the caller
/// is that the *next* test author does not have to know the net48 leg exists, and a shim missing
/// <c>SetResult</c> would only move the surprise.
/// <para>
/// One difference is not reproducible and does not matter here: <see cref="Task"/> is statically
/// a <see cref="System.Threading.Tasks.Task"/>, as on .NET, but at run time it is a
/// <c>Task&lt;bool&gt;</c>. Awaiting it, cancelling it, faulting it and combining it all behave
/// identically; only reflection over its type could tell, and nothing does that.
/// </para>
/// <para>
/// .NET 8's <c>SetFromTask</c> / <c>TrySetFromTask</c> are deliberately left out — no caller here
/// wants them, and unlike the members below they have real semantics to get wrong rather than one
/// call to forward.
/// </para>
/// </remarks>
internal sealed class TaskCompletionSource
{
    private readonly TaskCompletionSource<bool> _inner;

    public TaskCompletionSource()
        => _inner = new TaskCompletionSource<bool>();

    public TaskCompletionSource(TaskCreationOptions creationOptions)
        => _inner = new TaskCompletionSource<bool>(creationOptions);

    public TaskCompletionSource(object? state)
        => _inner = new TaskCompletionSource<bool>(state);

    public TaskCompletionSource(object? state, TaskCreationOptions creationOptions)
        => _inner = new TaskCompletionSource<bool>(state, creationOptions);

    public Task Task => _inner.Task;

    public void SetResult() => _inner.SetResult(true);

    public void SetCanceled() => _inner.SetCanceled();

    // TaskCompletionSource<T> gained TrySetCanceled(CancellationToken) long before .NET Framework
    // stopped shipping, but never the throwing SetCanceled(CancellationToken). The real one throws
    // InvalidOperationException on an already-completed source, so raise it rather than letting a
    // double-complete pass silently on this leg only.
    public void SetCanceled(CancellationToken cancellationToken)
    {
        if (!_inner.TrySetCanceled(cancellationToken))
        {
            throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed.");
        }
    }

    public void SetException(Exception exception) => _inner.SetException(exception);

    public void SetException(IEnumerable<Exception> exceptions) => _inner.SetException(exceptions);

    public bool TrySetResult() => _inner.TrySetResult(true);

    public bool TrySetCanceled() => _inner.TrySetCanceled();

    public bool TrySetCanceled(CancellationToken cancellationToken) => _inner.TrySetCanceled(cancellationToken);

    public bool TrySetException(Exception exception) => _inner.TrySetException(exception);

    public bool TrySetException(IEnumerable<Exception> exceptions) => _inner.TrySetException(exceptions);
}

#endif
