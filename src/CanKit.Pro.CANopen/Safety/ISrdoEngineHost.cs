using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What <see cref="SrdoEngine"/> asks of the node: sending, an EMCY, and the three
/// events. All calls arrive on the actor loop. The node implements it; tests record it.</summary>
internal interface ISrdoEngineHost
{
    void Send(uint cobId, byte[] payload);
    void EmitEmcy(ushort errorCode);
    void SrdoReceived(int srdoNumber, uint cobId, byte[] payload);
    void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason);
    void GlobalFailsafeCommandReceived();
    void ReportBackgroundException(Exception exception);
}
