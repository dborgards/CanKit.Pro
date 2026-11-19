using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// How a <see cref="Cia408Peer"/> waits for the status a command sequence expects
/// (<see cref="Cia408Peer.EnableAsync"/> and its kin): how often it reads the status word, and how long
/// it pauses between two reads. Handed to <see cref="Cia408Profile"/>, which gives every peer it makes
/// the same options.
/// </summary>
/// <remarks>
/// <para>A device need not change state within an SDO round trip: D3 enables the spool controller,
/// and D4 may wait for the hardware enable (CiA 408 v2.2.0 §7.7, Table 7, NOTE 1). So the wait is a
/// number of reads with a pause before each but the first, and the time it allows is about
/// <see cref="StatusReads"/> × <see cref="StatusPause"/> plus the round trips — by default
/// <see cref="DefaultStatusReads"/> reads <see cref="DefaultStatusPause"/> apart. The norm gives no
/// figure; the defaults are a choice that lets a valve take a second without the bus being polled
/// back to back.</para>
/// <para><b>The clock.</b> The pause is taken by <see cref="Delay"/>, <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// unless set. The peer has no clock of its own and the node offers none to its users
/// (<see cref="ICanOpenNode"/> has no time source), so a caller that runs the node on a virtual clock
/// passes a delay on that clock, as the tests do.</para>
/// </remarks>
internal sealed class Cia408PeerOptions
{
    /// <summary>The default of <see cref="StatusReads"/>: 50.</summary>
    public const int DefaultStatusReads = 50;

    /// <summary>The default of <see cref="StatusPause"/>: 20 ms.</summary>
    public static TimeSpan DefaultStatusPause { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The options a peer has when none are given.</summary>
    public static Cia408PeerOptions Default { get; } = new();

    /// <summary>How often a command sequence reads the status word after a write, and while the device
    /// shows no resting state before it, before it gives up; at least 1.</summary>
    public int StatusReads { get; init; } = DefaultStatusReads;

    /// <summary>The pause before each read but the first; zero or positive. Zero reads back to back.</summary>
    public TimeSpan StatusPause { get; init; } = DefaultStatusPause;

    /// <summary>What takes a pause: a task that completes once the time has passed, or is cancelled
    /// by the token (see the remarks on the class).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (pause, token) => Task.Delay(pause, token);

    /// <summary>Checks the options.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="StatusReads"/> is below 1, or
    /// <see cref="StatusPause"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><see cref="Delay"/> is null.</exception>
    internal void Validate()
    {
        if (StatusReads < 1)
            throw new ArgumentOutOfRangeException(nameof(StatusReads), StatusReads, "A command sequence reads the status word at least once.");
        if (StatusPause < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StatusPause), StatusPause, "A pause is zero or positive.");
        if (Delay is null) throw new ArgumentNullException(nameof(Delay));
    }
}
