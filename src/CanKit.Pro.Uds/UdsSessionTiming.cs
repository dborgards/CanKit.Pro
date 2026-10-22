using System;

namespace CanKit.Pro.Uds;

/// <summary>
/// The server timing a DiagnosticSessionControl positive response declares for the session it
/// has started: the sessionParameterRecord after the echoed session type, P2Server_max in
/// 1 ms units then P2*Server_max in 10 ms units, both big-endian (ISO 14229-1 §9.2.2.4) (#340,
/// carried-over finding U2).
/// </summary>
internal readonly struct UdsSessionTiming
{
    /// <summary>The record's length: two bytes for each of the two timing parameters.</summary>
    public const int RecordLength = 4;

    private UdsSessionTiming(TimeSpan p2ServerMax, TimeSpan p2StarServerMax)
    {
        P2ServerMax = p2ServerMax;
        P2StarServerMax = p2StarServerMax;
    }

    /// <summary>P2Server_max: 0 to 65 535 ms.</summary>
    public TimeSpan P2ServerMax { get; }

    /// <summary>P2*Server_max: 0 to 655 350 ms.</summary>
    public TimeSpan P2StarServerMax { get; }

    /// <summary>
    /// Decodes the first <see cref="RecordLength"/> bytes of <paramref name="record"/>; false for
    /// a shorter record, which some ECUs send on legacy sessions and which declares nothing.
    /// Bytes beyond the two parameters are ignored.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> record, out UdsSessionTiming timing)
    {
        if (record.Length < RecordLength)
        {
            timing = default;
            return false;
        }
        int p2 = (record[0] << 8) | record[1];
        int p2Star = (record[2] << 8) | record[3];
        timing = new UdsSessionTiming(TimeSpan.FromMilliseconds(p2), TimeSpan.FromMilliseconds(p2Star * 10.0));
        return true;
    }

    /// <summary>
    /// The client's P2 for this session: the declared P2Server_max plus <see cref="UdsClientOptions.SessionTimingMargin"/>, or
    /// <paramref name="configured"/> if that is longer -- a budget a caller set deliberately is
    /// never shortened.
    /// </summary>
    public TimeSpan ClientP2(TimeSpan configured) => Max(configured, P2ServerMax + UdsClientOptions.SessionTimingMargin);

    /// <summary>As <see cref="ClientP2"/>, for P2*.</summary>
    public TimeSpan ClientP2Star(TimeSpan configured) => Max(configured, P2StarServerMax + UdsClientOptions.SessionTimingMargin);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
