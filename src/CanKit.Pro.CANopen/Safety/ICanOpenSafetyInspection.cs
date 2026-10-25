using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Optional tool-side read-only inspection without widening published ICanOpenSafety.</summary>
public interface ICanOpenSafetyInspection
{
    /// <summary>Uploads a bounded safety configuration image and repeats its entries to detect observed
    /// changes. Requires a bound peer EDS/DCF for access validation; an expected target DCF/configuration
    /// is optional. Holds this client's peer-SDO channel for the whole acquisition, snapshots the bound
    /// description and expectation once that channel is held, and sends no downloads or state commands.</summary>
    /// <remarks>Read failures become entry findings and explicit file/default provenance where available.
    /// Cancellation/disposal propagate rather than returning a completed partial acquisition. Repeated
    /// identical reads cannot establish atomic consistency: another tool/device task may still edit the
    /// peer. Unknown/unsupported entries are reported without changing its state. No safety approval.</remarks>
    /// <exception cref="PeerSdoAccessException">No peer description is bound; refused before any frame.</exception>
    Task<PeerSafetySnapshot> ReadPeerSafetyConfigurationAsync(byte peerNodeId,
        PeerSafetyConfiguration? expected = null, CancellationToken cancellationToken = default);
}
