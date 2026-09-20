namespace CanKit.Pro.CANopen.Sdo;

/// <summary>
/// A subset of the CiA 301 §7.2.4.3 Table 45 SDO abort codes needed by the MVP. Values are the
/// exact 32-bit codes carried in an SDO Abort frame's data bytes 4..7 (little-endian).
/// </summary>
/// <remarks>
/// Only the codes actually emitted or matched by the MVP are enumerated. The wider CANopen
/// standard defines dozens more; unrecognized abort codes coming in from a remote peer are still
/// preserved as raw <c>uint</c> in <see cref="SdoAbortException"/> so callers see the exact
/// vendor-specified code.
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

    /// <summary>Invalid sequence number (block mode only) — CiA 301 v4.2.0 Table 22,
    /// "0504 0003h Invalid sequence number (block mode only)". A block segment's seqno must
    /// satisfy <c>0 &lt; seqno &lt; 128</c> (§7.2.4.3.10 / §7.2.4.3.14) and cannot exceed the
    /// blksize in force for the sub-block; a receiver aborts with this code when it does.</summary>
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

    /// <summary>Object cannot be mapped to a PDO (CiA 301 §7.2.4.6, PDO mapping parameter).</summary>
    ObjectCannotBeMapped = 0x06040041u,

    /// <summary>The number and length of the objects to be mapped would exceed the PDO capacity
    /// (CiA 301 §7.2.4.6 — more than 8 assembled payload bytes in this MVP).</summary>
    PdoMappingLengthExceeded = 0x06040042u,

    /// <summary>Data type does not match / length of service parameter does not match
    /// (CiA 301 §7.2.4.6 — used for PDO mapping entries whose bit length is not a positive
    /// multiple of eight, and for wrong widths when writing mapping parameter records).</summary>
    DataTypeLengthMismatch = 0x06070010u,

    /// <summary>Data type does not match — length of service parameter too high.</summary>
    LengthTooHigh = 0x06070012u,

    /// <summary>Data type does not match — length of service parameter too low.</summary>
    LengthTooLow = 0x06070013u,

    /// <summary>Sub-index does not exist.</summary>
    SubIndexDoesNotExist = 0x06090011u,

    /// <summary>Value range of parameter exceeded, write access only — CiA 301 v4.2.0 Table 22
    /// words it "0609 0030h Invalid value for parameter (download only)"; the member name keeps
    /// the older, more widely quoted phrasing. The server-side answer to a download whose value
    /// is outside what the object accepts, e.g. a reserved bit set in a communication parameter
    /// (the profile text uses this code for such writes throughout §7.5.2).</summary>
    ValueRangeExceeded = 0x06090030u,

    /// <summary>General error / unspecified.</summary>
    General = 0x08000000u,
}
