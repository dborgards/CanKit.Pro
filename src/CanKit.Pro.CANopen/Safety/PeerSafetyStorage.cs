namespace CanKit.Pro.CANopen.Safety;

/// <summary>Step 5 of the safe configuration procedure (CiA 319 v2.0.0 §5.3): whether the
/// configuration entity, once the SRD has acknowledged the configuration, asks it to store the
/// safety-related configuration — 13FEh and 13FFh included — in non-volatile memory through
/// object 1010h (CiA 301 §7.5.2.13), with the "save" signature. The value is the sub-index
/// written.</summary>
public enum PeerSafetyStorage : byte
{
    /// <summary>Nothing is stored: the configuration lasts until the SRD's next reset.</summary>
    None = 0,

    /// <summary>1010h:01, "save all parameters".</summary>
    AllParameters = 1,

    /// <summary>1010h:02, "save communication parameters".</summary>
    CommunicationParameters = 2,
}
