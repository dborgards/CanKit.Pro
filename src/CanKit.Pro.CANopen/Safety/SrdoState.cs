using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>A snapshot of one SRDO's validity. For a consumer, valid means the last pair was
/// complete, correct and in time; for a producer, valid means the node is Operational with a
/// valid configuration and is transmitting the cycle.</summary>
public sealed class SrdoState
{
    internal SrdoState(int srdoNumber, SrdoDirection direction, bool isValid, SrdoInvalidReason? reason, DateTime? lastValidAt)
    {
        SrdoNumber = srdoNumber;
        Direction = direction;
        IsValid = isValid;
        Reason = reason;
        LastValidAt = lastValidAt;
    }

    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>Sub-index 1 of the record.</summary>
    public SrdoDirection Direction { get; }
    /// <summary>See the class summary.</summary>
    public bool IsValid { get; }
    /// <summary>Why not, when <see cref="IsValid"/> is false and the SRDO exists; null for an SRDO with direction <see cref="SrdoDirection.None"/>.</summary>
    public SrdoInvalidReason? Reason { get; }
    /// <summary>Consumer: when the last valid pair was accepted (UTC).</summary>
    public DateTime? LastValidAt { get; }
}
