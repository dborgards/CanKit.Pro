namespace CanKit.Pro.CANopen.Safety;

/// <summary>Information direction of an SRDO, the value of sub-index 1 of its communication
/// parameter record (CiA DSP 304 V1.0 §8.4.2.2, object 1301h–1340h).</summary>
public enum SrdoDirection : byte
{
    /// <summary>0: the SRDO does not exist / is not valid.</summary>
    None = 0,
    /// <summary>1: exists, this node is its producer (tx).</summary>
    Transmit = 1,
    /// <summary>2: exists, this node is a consumer (rx).</summary>
    Receive = 2,
}
