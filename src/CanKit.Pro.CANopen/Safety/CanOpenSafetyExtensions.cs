using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Reaches the safety layer of a node without widening <see cref="ICanOpenNode"/>
/// (adding members to a published interface breaks every implementer; see the DisposeAsync
/// extension for the precedent).</summary>
public static class CanOpenSafetyExtensions
{
    /// <summary>The <see cref="ICanOpenSafety"/> of a node this library created.</summary>
    /// <exception cref="NotSupportedException">The node is not one of this library's.</exception>
    public static ICanOpenSafety Safety(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node as ICanOpenSafety
            ?? throw new NotSupportedException("CANopen Safety is available on the nodes CanOpen.OpenNode creates.");
    }
    /// <summary>Reads a peer's safety configuration through the optional inspection surface (#403).
    /// Does not add a member requirement to existing ICanOpenSafety implementations.</summary>
    /// <exception cref="NotSupportedException">The safety implementation does not support inspection.</exception>
    public static Task<PeerSafetySnapshot> ReadPeerSafetyConfigurationAsync(this ICanOpenSafety safety,
        byte peerNodeId, PeerSafetyConfiguration? expected = null, CancellationToken cancellationToken = default)
    {
        if (safety is null) throw new ArgumentNullException(nameof(safety));
        return (safety as ICanOpenSafetyInspection
            ?? throw new NotSupportedException("This safety implementation does not support read-only inspection."))
            .ReadPeerSafetyConfigurationAsync(peerNodeId, expected, cancellationToken);
    }
}
