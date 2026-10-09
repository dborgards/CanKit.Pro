using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>A consumer SRDO received a complete, correct pair in time; the plain data was written
/// to the mapped objects (CiA DSP 304 V1.0 §8.1.3.1, "Indication").</summary>
public sealed class SrdoReceivedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public SrdoReceivedEventArgs(int srdoNumber, uint cobId, byte[] payload, DateTime timestamp)
    {
        SrdoNumber = srdoNumber;
        CobId = cobId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        Timestamp = timestamp;
    }
    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>COB-ID 1, the plain-data frame's id.</summary>
    public uint CobId { get; }
    /// <summary>The plain data (0..8 bytes).</summary>
    public byte[] Payload { get; }
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}

/// <summary>An SRDO became valid or invalid. Delivered on transitions only, never dropped
/// (a critical event like a heartbeat timeout).</summary>
public sealed class SrdoStateChangedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public SrdoStateChangedEventArgs(int srdoNumber, bool isValid, SrdoInvalidReason? reason, DateTime timestamp)
    {
        SrdoNumber = srdoNumber;
        IsValid = isValid;
        Reason = reason;
        Timestamp = timestamp;
    }
    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>The new validity.</summary>
    public bool IsValid { get; }
    /// <summary>Why, when invalid.</summary>
    public SrdoInvalidReason? Reason { get; }
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}

/// <summary>A global failsafe command (COB-ID 001h, DLC 0) was received while 1300h is 1
/// (CiA DSP 304 V1.0 §8.2). On a bus that echoes, the node's own GFC arrives here too.</summary>
public sealed class GlobalFailsafeCommandReceivedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public GlobalFailsafeCommandReceivedEventArgs(DateTime timestamp) => Timestamp = timestamp;
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}
