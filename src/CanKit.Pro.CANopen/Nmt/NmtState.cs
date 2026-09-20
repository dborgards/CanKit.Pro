namespace CanKit.Pro.CANopen.Nmt;

/// <summary>
/// CANopen NMT state (CiA 301 §7.3.2.2) as this node reports it and as a heartbeat or guarding
/// reply carries it in bits 0..6. The values are the wire encoding of §7.2.8.3.2.2 Figure 45.
/// </summary>
public enum NmtState : byte
{
    /// <summary>NMT state Initialisation (§7.3.2.2.1): the node is being (re)initialised and has
    /// not yet entered Pre-Operational. On the wire this is the boot-up message, a single
    /// <c>0x00</c> byte on <c>0x700 + node-id</c> (§7.2.8.3.3); a node never reports it in a
    /// heartbeat or guarding reply.</summary>
    Initializing = 0x00,

    /// <summary>Node reports itself as <c>Stopped</c> in a heartbeat (0x04).</summary>
    Stopped = 0x04,

    /// <summary>Node reports itself as <c>Operational</c> in a heartbeat (0x05).</summary>
    Operational = 0x05,

    /// <summary>Node reports itself as <c>Pre-Operational</c> in a heartbeat (0x7F).</summary>
    PreOperational = 0x7F,
}

/// <summary>
/// CiA 301 §7.2.8 NMT master command specifiers. Encoded in byte 0 of the NMT master frame at
/// COB-ID <c>0x000</c>, followed by the target node-id in byte 1 (0 = broadcast to all nodes).
/// </summary>
public enum NmtCommand : byte
{
    /// <summary>Start Remote Node — transitions the target into <see cref="NmtState.Operational"/>.</summary>
    Start = 0x01,

    /// <summary>Stop Remote Node — transitions the target into <see cref="NmtState.Stopped"/>.</summary>
    Stop = 0x02,

    /// <summary>Enter Pre-Operational — transitions the target into
    /// <see cref="NmtState.PreOperational"/>.</summary>
    EnterPreOperational = 0x80,

    /// <summary>Reset Node (§7.2.8.2.1.5): the application and the communication profile return
    /// to their power-on values, then the node sends boot-up and enters Pre-Operational. Without
    /// a device description this node has no source for the application objects' power-on
    /// values, so it restores the communication profile and leaves the application objects to
    /// the application (see <c>ICanOpenNode.NmtCommandReceived</c>).</summary>
    ResetNode = 0x81,

    /// <summary>Reset Communication (§7.2.8.2.1.6): the communication-profile objects
    /// (<c>1000h</c>–<c>1FFFh</c>) return to their power-on values, the guarding toggle is reset,
    /// then the node sends boot-up and enters Pre-Operational.</summary>
    ResetCommunication = 0x82,
}
