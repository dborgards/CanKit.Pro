using System;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>Why a command sequence of <see cref="Cia408Peer"/> failed: what a caller needs to decide
/// whether a retry makes sense.</summary>
internal enum Cia408CommandFailure
{
    /// <summary>The device is in local control (status bit 4, <c>604Fh</c> = 1, CiA 408 v2.2.0 §9.3.9)
    /// and ignores a control word from the bus. Switch it to the bus first; a retry alone changes
    /// nothing.</summary>
    LocalControl,

    /// <summary>The device is in a fault state, or fell into one on the way. Only a fault reset
    /// leaves it (§7.7, Table 7).</summary>
    FaultState,

    /// <summary>The device did not show the expected state within the reads the options allow
    /// (<see cref="Cia408PeerOptions"/>). It may still be moving: read the status, and retry with a
    /// wider bound if it is. A fault reset the device refuses — CiA 408 does not say whether it may
    /// while the cause lasts — ends here too, with <see cref="Cia408CommandException.State"/> the fault
    /// state it stays in.</summary>
    NotReached,

    /// <summary>A fault reset was asked of a device in no fault state; nothing was written.</summary>
    NoFault,
}

/// <summary>A command sequence of <see cref="Cia408Peer"/> (<see cref="Cia408Peer.EnableAsync"/>,
/// <see cref="Cia408Peer.HoldAsync"/>, <see cref="Cia408Peer.DisableAsync"/>,
/// <see cref="Cia408Peer.ResetFaultAsync"/>) did not end in the state it asked for.</summary>
/// <remarks>A sequence that failed after it wrote leaves the last control word it wrote in the
/// device, and the device follows that word: it may keep moving after the call has failed. The
/// sequence does not write a word back.</remarks>
internal sealed class Cia408CommandException : InvalidOperationException
{
    internal Cia408CommandException(Cia408CommandFailure reason, Cia408State state, string message)
        : base(message)
    {
        Reason = reason;
        State = state;
    }

    /// <summary>Why the sequence failed.</summary>
    public Cia408CommandFailure Reason { get; }

    /// <summary>The state the device showed in the last status read.</summary>
    public Cia408State State { get; }
}
