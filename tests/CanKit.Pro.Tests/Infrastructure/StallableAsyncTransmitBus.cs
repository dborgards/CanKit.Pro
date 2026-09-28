using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;

namespace CanKit.Pro.Tests.Infrastructure;

/// <summary>
/// An <see cref="ICanBus"/> forwarding to a real bus, except that
/// <see cref="ICanBus.TransmitAsync(CanFrame, CancellationToken)"/> can be made genuinely
/// asynchronous: while <see cref="Stalled"/> is set it returns a task that completes only when
/// the test says so, the trigger of #202 (every shipped adapter completes it synchronously).
/// </summary>
/// <remarks>
/// A <see cref="DispatchProxy"/> rather than a hand-written double so it cannot drift from
/// <see cref="ICanBus"/>, and unlike <see cref="ControllableBus"/> it reports the wrapped bus's
/// own options, i.e. no <c>Echo</c> work mode: <c>SendConfirmed</c> takes the approximated path.
/// </remarks>
public class StallableAsyncTransmitBus : DispatchProxy
{
    private ICanBus _inner = null!;
    private TaskCompletionSource<int>? _stall;

    /// <summary>Wraps <paramref name="inner"/>; the proxy does not take ownership of it.</summary>
    public static ICanBus Wrap(ICanBus inner, out StallableAsyncTransmitBus control)
    {
        var proxy = Create<ICanBus, StallableAsyncTransmitBus>();
        control = (StallableAsyncTransmitBus)proxy;
        control._inner = inner;
        return proxy;
    }

    /// <summary>Whether the next <c>TransmitAsync</c> calls hang until <see cref="Release"/>.</summary>
    public bool Stalled
    {
        get => Volatile.Read(ref _stall) is not null;
        set => Interlocked.Exchange(ref _stall, value ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null);
    }

    /// <summary>Completes every stalled transmit as accepted and stops stalling.</summary>
    public void Release() => Interlocked.Exchange(ref _stall, null)?.TrySetResult(1);

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(ICanBus.TransmitAsync)
            && args![0] is CanFrame && Volatile.Read(ref _stall) is { } stall)
            return stall.Task;
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
