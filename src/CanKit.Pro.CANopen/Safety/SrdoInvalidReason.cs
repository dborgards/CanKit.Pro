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
}
