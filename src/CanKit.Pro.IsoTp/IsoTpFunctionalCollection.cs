using System.Collections.Generic;

namespace CanKit.Pro.IsoTp;

/// <summary>
/// What <see cref="IsoTpFunctionalClient.SendAndCollectWithTransmitStampAsync"/> returns: the
/// responses collected within the window, and when the request went out.
/// </summary>
public readonly struct IsoTpFunctionalCollection
{
    internal IsoTpFunctionalCollection(IReadOnlyList<IsoTpFunctionalResponse> responses, IsoTpTransmitStamps transmitStamps)
    {
        Responses = responses;
        TransmitStamps = transmitStamps;
    }

    /// <summary>The Single-Frame responses collected within the window, in arrival order.</summary>
    public IReadOnlyList<IsoTpFunctionalResponse> Responses { get; }

    /// <summary>When the request was handed to the driver and transmitted; zero where unknown.</summary>
    public IsoTpTransmitStamps TransmitStamps { get; }
}
