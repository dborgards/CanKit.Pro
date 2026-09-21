namespace CanKit.Pro.J1939Tp;

/// <summary>
/// Connection Abort reason codes carried in byte 1 of the TP.CM Abort control frame, as
/// SAE J1939-21 table 7 assigns them (#33). The values are the standard's own, so they
/// round-trip on the wire without translation; a peer's abort carrying a value outside the
/// table is surfaced as that value, and 0 -- which the table does not assign -- as
/// <see cref="Unknown"/>.
/// </summary>
public enum J1939TpAbortReason : byte
{
    /// <summary>Not a code of the table: the default, and what a malformed abort decodes to.</summary>
    Unknown = 0,

    /// <summary>
    /// Already in one or more connection managed sessions and cannot support another
    /// (table 7, code 1). Sent when an RTS arrives while a session with that peer is open.
    /// </summary>
    SessionAlreadyOpen = 1,

    /// <summary>
    /// System resources were needed for another task so this connection managed session was
    /// terminated (table 7, code 2).
    /// </summary>
    NoResourcesAvailable = 2,

    /// <summary>
    /// A timeout occurred and this is the connection abort to close the session (table 7,
    /// code 3). Used by every T1/T2/T3/T4 enforcement path in this stack.
    /// </summary>
    Timeout = 3,

    /// <summary>CTS messages received when data transfer is in progress (table 7, code 4).</summary>
    CtsReceivedDuringDataTransfer = 4,

    /// <summary>
    /// Maximum retransmit request limit reached (table 7, code 5). This stack does not
    /// retransmit, so a CTS asking for a packet already sent reaches the limit at once.
    /// </summary>
    MaximumRetransmitRequestsReached = 5,

    /// <summary>Unexpected data transfer packet (table 7, code 6).</summary>
    UnexpectedDataTransferPacket = 6,

    /// <summary>Bad sequence number, the software cannot recover (table 7, code 7).</summary>
    BadSequenceNumber = 7,

    /// <summary>Duplicate sequence number, the software cannot recover (table 7, code 8).</summary>
    DuplicateSequenceNumber = 8,

    /// <summary>"Total Message Size" is greater than 1785 bytes (table 7, code 9).</summary>
    MessageSizeExceeded = 9,

    /// <summary>
    /// The top of the range table 7 leaves to J1939-71 definitions (251..255), used for a
    /// situation the table has no code for -- an EndOfMsgAck out of turn, an RTS whose totals
    /// do not agree, a CTS granting more packets than remain.
    /// </summary>
    Unassigned = 255,
}
