namespace CanKit.Pro.CANopen.Nmt;

/// <summary>
/// Where this node sits in the NMT flying-master election. <see cref="Inactive"/> until
/// <c>1F80h</c> bits 0 and 5 are set; the other values are the election itself.
/// </summary>
public enum FlyingMasterRole
{
    /// <summary>Not participating. NMT master commands this node sends are ordinary CiA 301
    /// commands; nothing answers the flying-master services.</summary>
    Inactive = 0,

    /// <summary>Waiting the negotiation time delay (<c>1F90h:02</c>) before asking whether a
    /// master is already active.</summary>
    Delaying,

    /// <summary>Has asked whether an active NMT master exists (<c>0x073</c>) and is waiting
    /// for the reply (<c>1F90h:01</c>).</summary>
    Detecting,

    /// <summary>In the timeslot race (<c>0x072</c>). The node with the shortest wait transmits
    /// the master claim (<c>0x071</c>).</summary>
    Negotiating,

    /// <summary>This node is the active NMT master.</summary>
    Active,

    /// <summary>Another master won. This node monitors that master with the heartbeat consumer
    /// and starts a new election when the consumer times out.</summary>
    Standby,
}

/// <summary>
/// Why <see cref="CanKit.Pro.CANopen.ICanOpenNode.FlyingMasterChanged"/> was raised.
/// </summary>
public enum FlyingMasterSignal
{
    /// <summary>This node transmitted the winning claim and is the active NMT master.</summary>
    BecameActive,

    /// <summary>A master of better or equal priority is active. An equal priority does not
    /// depose a master that is already active; during the timeslot race the first claim of
    /// equal priority wins.</summary>
    BecameStandby,

    /// <summary>This node outranks the active master and has requested a new election
    /// (<c>0x076</c>).</summary>
    ForcedRenegotiation,

    /// <summary>The heartbeat consumer for the active master timed out. A new election has
    /// started, without the cold-boot Reset Communication.</summary>
    ActiveMasterLost,

    /// <summary>A node of worse priority claimed the mastership, which the published procedure
    /// treats as a network configuration error. A new election was requested.</summary>
    ConfigurationError,
}
