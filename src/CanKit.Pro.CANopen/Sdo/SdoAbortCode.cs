namespace CanKit.Pro.CANopen.Sdo;

/// <summary>
/// The CiA 301 §7.2.4.3.17 Table 22 SDO abort codes this node emits or matches. Values are the
/// exact 32-bit codes carried in an SDO Abort frame's data bytes 4..7 (little-endian).
/// </summary>
/// <remarks>
/// Table 22 is a protocol reference, not an implementation order: only the codes for conditions
/// this node actually detects are named here. Unrecognized abort codes coming in from a remote
/// peer are still preserved as raw <c>uint</c> in <see cref="SdoAbortException"/> so callers see
/// the exact vendor-specified code.
/// </remarks>
public enum SdoAbortCode : uint
{
    /// <summary>Toggle bit not alternated (segmented transfer protocol violation).</summary>
    ToggleBitNotAlternated = 0x05030000u,

    /// <summary>SDO protocol timed out.</summary>
    SdoProtocolTimedOut = 0x05040000u,

    /// <summary>Client/server command specifier not valid or unknown.</summary>
    CommandSpecifierInvalid = 0x05040001u,

    /// <summary>Invalid block size (only used by block transfer).</summary>
    InvalidBlockSize = 0x05040002u,

    /// <summary>Invalid sequence number (block mode only, CiA 301 Table 22): a block segment
    /// carried a sequence number of 0 or above the negotiated block size.</summary>
    InvalidSequenceNumber = 0x05040003u,

    /// <summary>CRC error (block transfer only). Set when the CRC-16 carried in the end-of-block
    /// frame does not match the CRC computed by the receiver over the reassembled payload.</summary>
    CrcError = 0x05040004u,

    /// <summary>Out of memory — a segmented SDO transfer declared more bytes than the node is
    /// willing to buffer (see <c>CanOpenNodeOptions.MaxSdoTransferBytes</c>).</summary>
    OutOfMemory = 0x05040005u,

    /// <summary>Unsupported access to an object.</summary>
    UnsupportedAccess = 0x06010000u,

    /// <summary>Attempt to read a write-only object.</summary>
    AttemptReadWriteOnly = 0x06010001u,

    /// <summary>Attempt to write a read-only object.</summary>
    AttemptWriteReadOnly = 0x06010002u,

    /// <summary>Object does not exist in the object dictionary.</summary>
    ObjectDoesNotExist = 0x06020000u,

    /// <summary>Object cannot be mapped to a PDO (CiA 301 §7.5.2.36 / §7.5.2.38): the target is
    /// not mappable, or its declared size does not match the mapped bit length.</summary>
    ObjectCannotBeMapped = 0x06040041u,

    /// <summary>The number and length of the objects to be mapped would exceed the PDO length
    /// (CiA 301 §7.5.2.36 / §7.5.2.38 — more than 8 assembled payload bytes).</summary>
    PdoMappingLengthExceeded = 0x06040042u,

    /// <summary>General parameter incompatibility reason (CiA 301 Table 22). Emitted for a
    /// consumer heartbeat time (<c>1016h</c>) that names a node-id another sub-index already
    /// monitors (§7.5.2.19).</summary>
    GeneralParameterIncompatibility = 0x06040043u,

    /// <summary>Data type does not match / length of service parameter does not match
    /// (used for PDO mapping entries whose bit length is not a positive multiple of eight, and
    /// for wrong widths when writing parameter records).</summary>
    DataTypeLengthMismatch = 0x06070010u,

    /// <summary>Data type does not match — length of service parameter too high.</summary>
    LengthTooHigh = 0x06070012u,

    /// <summary>Data type does not match — length of service parameter too low.</summary>
    LengthTooLow = 0x06070013u,

    /// <summary>Sub-index does not exist.</summary>
    SubIndexDoesNotExist = 0x06090011u,

    /// <summary>Value range of parameter exceeded, only for write access (CiA 301 Table 22).
    /// The code CiA 301 prescribes for an unsupported transmission type (§7.5.2.35 / §7.5.2.37),
    /// for setting the 29-bit frame bit on a node that supports base frames only (§7.5.2.5,
    /// §7.5.2.17, §7.5.2.33), and for changing the CAN-ID part of a COB-ID while the object
    /// exists and is valid.</summary>
    ValueRangeExceeded = 0x06090030u,

    /// <summary>General error / unspecified.</summary>
    General = 0x08000000u,

    /// <summary>Data cannot be transferred or stored to the application (CiA 301 Table 22).
    /// Emitted for a wrong signature written to <c>1010h</c> / <c>1011h</c> (§7.5.2.13 /
    /// §7.5.2.14, "abort code: 0800 002xh").</summary>
    DataCannotBeTransferred = 0x08000020u,

    /// <summary>Data cannot be transferred or stored to the application because of the present
    /// device state (CiA 301 Table 22). Emitted for an SDO server session that was open when the
    /// node entered NMT state Stopped, where SDO is not available (§7.3.2.2.5 Table 37).</summary>
    DataCannotBeTransferredDeviceState = 0x08000022u,
}
