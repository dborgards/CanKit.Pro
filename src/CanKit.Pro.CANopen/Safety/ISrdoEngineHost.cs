using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What <see cref="SrdoEngine"/> asks of the node: sending, an EMCY, and the three
/// events. All calls arrive on the actor loop. The node implements it; tests record it.</summary>
internal interface ISrdoEngineHost
{
    /// <summary>A single frame, sent at once: the global failsafe command.</summary>
    void Send(uint cobId, byte[] payload);

    /// <summary>One SRDO transmission: the plain frame, then the inverted one, as one unit, on
    /// one ordered send chain for all SRDOs. While the previous pair of the same SRDO is still
    /// being sent, the host keeps this one as that SRDO's pending pair — replacing an older
    /// pending one, so the latest data wins — and sends it once the previous pair completes,
    /// unless the node has left Operational by then.</summary>
    void SendPair(int srdoNumber, uint cobId1, byte[] plain, uint cobId2, byte[] inverted);

    void EmitEmcy(ushort errorCode);
    void SrdoReceived(int srdoNumber, uint cobId, byte[] payload);
    void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason);
    void GlobalFailsafeCommandReceived();
    void ReportBackgroundException(Exception exception);
}
