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
    /// Extra gap after a Fastscan step's response window, before the next request (annex A.3.3,
    /// master cycle time). The window itself is <see cref="FastscanTimeout"/>. Defaults to 1 ms.
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
    /// period, with the bit rate the caller named, so this master's own adapter can change rate
    /// together with the slaves. Null does not touch the adapter: many adapters cannot change
    /// rate while the channel is open, and the caller reopens the bus after the second period.
    /// </summary>
    public Func<int, System.Threading.CancellationToken, System.Threading.Tasks.Task>? BitRateSwitch { get; init; }

    internal void Validate()
    {
        // CancelAfter rejects a delay above Int32.MaxValue milliseconds, and a longer span
        // overflows the tick multiply that builds a receive deadline.
        RequireBounded(ResponseTimeout, nameof(ResponseTimeout), allowZero: false);
        RequireBounded(FastscanTimeout, nameof(FastscanTimeout), allowZero: false);
        RequireBounded(FastscanCycleTime, nameof(FastscanCycleTime), allowZero: true);
        RequireBounded(IdentificationTimeout, nameof(IdentificationTimeout), allowZero: false);
    }

    private static void RequireBounded(TimeSpan value, string name, bool allowZero)
    {
        var above = allowZero ? value < TimeSpan.Zero : value <= TimeSpan.Zero;
        if (!above && value <= TimeSpan.FromMilliseconds(int.MaxValue)) return;
        var message = allowZero
            ? "Must not be negative and no longer than 2147483647 milliseconds."
            : "Must be positive and no longer than 2147483647 milliseconds.";
        throw new ArgumentOutOfRangeException(name, value, message);
    }
}
