using System;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Timeouts of an <see cref="ILssMaster"/>. CiA 305 leaves them to the implementation and asks
/// that they be adjustable (annex A.2, A.3.3). A slave must answer inside the timeout. Fastscan
/// and identify occupy that whole window from the request, so a late reply is consumed before
/// the next master frame.
/// </summary>
public sealed class LssMasterOptions
{
    /// <summary>
    /// How long a confirmed service (configure, store, inquire, selective switch) waits for the
    /// slave. Selective switch treats the timeout as "no slave matched", not as a failure.
    /// Defaults to 1 second, the same order as the SDO client timeout.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long one Fastscan step occupies, measured from the request (annex A.3.3, master
    /// response timeout). The step waits out the whole window even after the first response,
    /// so a later reply is not read as the next bit. A miss means "this bit is 1" or "no slave",
    /// so this should be only as long as the slowest slave needs. Defaults to 50 ms.
    /// </summary>
    public TimeSpan FastscanTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Extra gap after a Fastscan step's response window, before the next Fastscan request
    /// (annex A.3.3, master cycle time), including a new scan. The completed scan returns without
    /// waiting for this gap. The window itself is <see cref="FastscanTimeout"/>. Defaults to 1 ms.
    /// Zero disables the extra gap.
    /// </summary>
    public TimeSpan FastscanCycleTime { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// How long an identify service waits, measured from the request. The call occupies the
    /// whole window and returns <see langword="true"/> when any slave answered. Defaults to 100 ms.
    /// </summary>
    public TimeSpan IdentificationTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Called from <see cref="ILssMaster.ActivateBitTimingAsync"/> after the first switch-delay
    /// period, with the bit rate the caller named. The callback retunes the channel this master
    /// is already reading. It must not dispose that bus: the second delay, and a waiting frame
    /// sent after a zero delay, still use it. Specifying a bit rate on the call requires this
    /// callback. Leave both unset to leave the adapter alone. A caller whose
    /// adapter cannot change rate while open omits <c>returnToWaiting</c>, disposes this master
    /// after the call returns, and sends switch state global from a new master on the new bus.
    /// </summary>
    public Func<int, System.Threading.CancellationToken, System.Threading.Tasks.Task>? BitRateSwitch { get; init; }

    internal void Validate()
    {
        // Keep the service's timing bounds stable independently of the scheduler's
        // representation of absolute deadlines.
        RequireBounded(ResponseTimeout, nameof(ResponseTimeout), allowZero: false);
        RequireBounded(FastscanTimeout, nameof(FastscanTimeout), allowZero: false);
        RequireBounded(FastscanCycleTime, nameof(FastscanCycleTime), allowZero: true);
        RequireBounded(IdentificationTimeout, nameof(IdentificationTimeout), allowZero: false);
    }

    private static void RequireBounded(TimeSpan value, string name, bool allowZero)
    {
        var belowMinimum = allowZero ? value < TimeSpan.Zero : value <= TimeSpan.Zero;
        if (!belowMinimum && value <= TimeSpan.FromMilliseconds(int.MaxValue)) return;
        var message = allowZero
            ? "Must not be negative and no longer than 2147483647 milliseconds."
            : "Must be positive and no longer than 2147483647 milliseconds.";
        throw new ArgumentOutOfRangeException(name, value, message);
    }
}
