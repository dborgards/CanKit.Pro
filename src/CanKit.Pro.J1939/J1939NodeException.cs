using System;
using CanKit.Core.Exceptions;
using CanKit.Pro.RawCan;

namespace CanKit.Pro.J1939;

/// <summary>Base class for exceptions thrown by <see cref="IJ1939Node"/> operations. Derives
/// from <see cref="CanKitException"/> so L2/L3/L4 failures can be caught uniformly across the
/// library (NFR-006 error architecture, arc42 ADR-12).</summary>
public class J1939NodeException : CanKitException
{
    /// <summary>Constructs an exception with a message.</summary>
    public J1939NodeException(string message)
        : base(CanKitErrorCode.TransportOperationFailed, message) { }

    /// <summary>Constructs an exception with a message and cause.</summary>
    public J1939NodeException(string message, Exception innerException)
        : base(CanKitErrorCode.TransportOperationFailed, message, innerException: innerException) { }

    /// <summary>Constructs an exception with a specific library error code.</summary>
    protected J1939NodeException(CanKitErrorCode errorCode, string message) : base(errorCode, message) { }
}

/// <summary>
/// Thrown by <see cref="IJ1939Node.SendAsync"/> when the caller tries to send a message
/// before <see cref="IJ1939Node.ClaimAddressAsync"/> has succeeded (SRS FR-J1939-003/004:
/// only a node with a valid, claimed address may transmit).
/// </summary>
public sealed class J1939NoAddressException : J1939NodeException
{
    /// <summary>Constructs the exception.</summary>
    public J1939NoAddressException()
        : base(ProtocolErrorCodes.AddressClaimFailed,
            "This J1939 node has no claimed address; ClaimAddressAsync must succeed before sending.")
    {
    }
}

/// <summary>
/// Thrown by <see cref="IJ1939Node.ClaimAddressAsync"/> when the preferred address cannot be
/// claimed: a peer with a lower NAME contests it and arbitrary addressing is off, every address
/// of the arbitrary field was contested as well, or a peer with this node's own NAME claims it
/// (SAE J1939-81 §4.4.3.3 gives an equal NAME to neither CA, and a tie does not scan). The node
/// transitions to <see cref="J1939ClaimState.CannotClaim"/> and broadcasts Cannot Claim
/// (SA=0xFE) before throwing (SRS FR-J1939-004).
/// </summary>
public sealed class J1939CannotClaimException : J1939NodeException
{
    /// <summary>Constructs the exception with the preferred address that was lost.</summary>
    public J1939CannotClaimException(byte preferredAddress)
        : base(ProtocolErrorCodes.AddressClaimFailed,
            $"J1939 node could not claim preferred address 0x{preferredAddress:X2}: a peer with a lower or equal NAME contested it, and no other address could be claimed.")
    {
        PreferredAddress = preferredAddress;
    }

    /// <summary>The address the caller originally requested.</summary>
    public byte PreferredAddress { get; }
}
