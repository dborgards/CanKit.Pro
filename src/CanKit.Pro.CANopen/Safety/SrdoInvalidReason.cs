namespace CanKit.Pro.CANopen.Safety;

/// <summary>Why an SRDO is not valid (CiA DSP 304 V1.0 §8.1.1, §8.1.3.1, §9.5).</summary>
public enum SrdoInvalidReason
{
    /// <summary>Consumer: no complete pair since entering Operational.</summary>
    NotReceived,
    /// <summary>The node is not in NMT state Operational; SRDOs exist only there (§8.3.2).</summary>
    NotOperational,
    /// <summary>13FEh is not A5h, or 13FFh does not match the record (§8.3.1 D, §9.5).</summary>
    ConfigurationInvalid,
    /// <summary>Consumer: no valid pair within the SCT (§8.1.1 Figure 2).</summary>
    SafeguardCycleExpired,
    /// <summary>Consumer: the second frame did not follow the first within the SRVT (§8.1.1 Figure 3).</summary>
    ValidationTimeExpired,
    /// <summary>Consumer: the inverted frame arrived without a preceding plain frame (§9.5).</summary>
    OutOfOrder,
    /// <summary>Consumer: the second frame is not the bitwise inverse of the first (§8.1, §9.5).</summary>
    Mismatch,
    /// <summary>Consumer: a complete pair arrived, but a mapped object refused the bytes meant
    /// for it (its write validator, or a declaration that no longer takes them). Data the
    /// consumer cannot use counts as not received, as §8.1.3.1 leaves a pair too short for the
    /// mapping unprocessed: <see cref="ICanOpenSafety.SrdoReceived"/> is not raised and the SCT
    /// is not restarted, so it runs on from the last pair that was taken and, when it expires,
    /// replaces this reason with <see cref="SafeguardCycleExpired"/>.
    /// The writing stops at the refusal: the objects mapped before it keep the bytes of this
    /// pair, the refused one and those after it keep their previous values. The refusal is
    /// reported on <c>BackgroundExceptionOccurred</c>. The next pair the objects take makes the
    /// SRDO valid again.</summary>
    ActuationRefused,
    /// <summary>Producer: a mapped object is missing or no longer has the width its mapping
    /// entry gives it (§8.1.3.1: the SRDO carries its mapped objects at that length). The
    /// producer transmits nothing — no pair is padded or cut to fit — and stays silent until the
    /// SRDO is rebuilt (a record written, or Operational entered again); the failure is
    /// reported on <c>BackgroundExceptionOccurred</c>. A node refuses to re-declare an object an
    /// existing SRDO maps, so this is the guard behind that rule, not a way to reach it.</summary>
    MappedObjectUnavailable,
}
