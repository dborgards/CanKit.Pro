using System;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Timeouts of an <see cref="ILssMaster"/>. CiA 305 leaves them to the implementation and asks
/// that they be adjustable (annex A.2, A.3.3). A slave must answer inside the timeout: a response
/// that arrives later is not a confirmation, and during Fastscan it can be read as the answer to
/// the next step.
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
    /// How long one Fastscan step waits for an identify-slave response (annex A.3.3, master
    /// response timeout). A miss means "this bit is 1" or "no slave", so this should be only as
    /// long as the slowest slave needs. Defaults to 50 ms.
    /// </summary>
    public TimeSpan FastscanTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Minimum time from the end of one Fastscan wait to the next request (annex A.3.3, master
    /// cycle time). Responses that arrive during this gap are not attributed to the next step.
    /// Defaults to 1 ms. Zero disables the gap.
    /// </summary>
    public TimeSpan FastscanCycleTime { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// How long an identify service waits when no slave has answered yet. The call returns as
    /// soon as one response arrives. Defaults to 100 ms.
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
        if (ResponseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ResponseTimeout), ResponseTimeout, "Must be positive.");
        if (FastscanTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(FastscanTimeout), FastscanTimeout, "Must be positive.");
        if (FastscanCycleTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(FastscanCycleTime), FastscanCycleTime, "Must not be negative.");
        if (IdentificationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdentificationTimeout), IdentificationTimeout,
                "Must be positive.");
    }
}
