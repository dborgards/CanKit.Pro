using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// What the adapter is, and who is on the bus. These run first and change nothing: a run that
/// stops here has still recorded the two things every later finding has to be read against — the
/// interface's own capabilities and the set of nodes that were present.
/// </summary>
internal static class BusAndDiscoveryChecks
{
    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        await book.RunAsync("BUS-01", "bus", "Adapter opened, capabilities recorded", Tier.Read, null, () =>
        {
            var options = session.Bus.Options;
            var measurements = new Dictionary<string, string>
            {
                ["endpoint"] = session.Options.Endpoint,
                ["work mode"] = options.WorkMode.ToString(),
                ["features"] = options.Features.ToString(),
                ["declares Echo"] = options.Features.HasFlag(CanFeature.Echo).ToString(),
                ["protocol mode"] = options.ProtocolMode.ToString(),
                ["bit rate (requested)"] = session.Options.UseFd
                    ? $"{session.Options.Bitrate} / {session.Options.DataBitrate} bit/s (FD)"
                    : $"{session.Options.Bitrate} bit/s",
            };
            return Task.FromResult(Outcome.Pass("The bus is open.", measurements));
        }).ConfigureAwait(false);

        await book.RunAsync("BUS-02", "bus", "Bus is not in an error state", Tier.Read, null, () =>
        {
            // Read before anything else transmits: a bus that is already off, or already heavily
            // error-passive, explains every timeout the rest of the run would otherwise blame on
            // the device. Wrong bit rate and missing termination both land here.
            var state = session.Bus.BusState;
            var measurements = new Dictionary<string, string> { ["bus state"] = state.ToString() };
            try
            {
                var errors = session.Bus.ErrorCounters();
                measurements["error counters"] = $"TEC {errors.TransmitErrorCounter}, REC {errors.ReceiveErrorCounter}";
            }
            catch (Exception ex)
            {
                // Not every adapter exposes TEC/REC, and "the driver will not tell us" is not a
                // finding about the device.
                measurements["error counters"] = $"not available ({ex.GetType().Name})";
            }

            return Task.FromResult(state switch
            {
                // Transport, not device: this branch names the bit rate and the termination as the
                // cause in its own message, and Outcome.Fail would have filed it against the
                // firmware. Found in review of #388.
                BusState.BusOff => Outcome.TransportFault(
                    "The interface is bus-off before the run sent anything. Check the bit rate and the termination; "
                    + "nothing measured after this is about the device.", measurements),
                BusState.ErrPassive or BusState.ErrWarning => Outcome.Inconclusive(
                    $"The interface is {state} before the run sent anything — usually a bit-rate or wiring mismatch. "
                    + "Findings below may be about the bus rather than the device.", Attribution.Transport, measurements),
                _ => Outcome.Pass($"Bus state {state}.", measurements),
            });
        }).ConfigureAwait(false);

        // What the passive scan heard about the target, for DSC-02 to read: whether a node with no
        // SDO server answering ought to have one is a question about its NMT state, and the state
        // is on the bus rather than behind the SDO server that is not answering. Shared the way
        // PDO-01's descriptors are shared with PDO-03, and in the order the groups already run.
        NmtState? targetStateHeard = null;

        await book.RunAsync("DSC-01", "discovery", "Passive presence scan (heartbeat and boot-up)", Tier.Read, "FR-CO-033", async () =>
        {
            // Listen-only in the sense that matters: ListenAsync opens no node and sends nothing,
            // so what it finds is what the bus volunteers.
            var window = TimeSpan.FromMilliseconds(session.Options.ListenMs);
            var heard = await CanOpenDiscovery.ListenAsync(session.Service, window, cancellationToken).ConfigureAwait(false);
            if (session.Options.TargetNode is { } listenTarget)
            {
                targetStateHeard = heard.FirstOrDefault(n => n.NodeId == listenTarget)?.HeartbeatState;
            }
            var measurements = heard.ToDictionary(
                n => $"node {n.NodeId}",
                n => Describe(n),
                StringComparer.Ordinal);

            if (heard.Count == 0)
            {
                return Outcome.Inconclusive(
                    $"Nothing was heard in {session.Options.ListenMs} ms. A device that produces no heartbeat and "
                    + "has already booted is silent until asked, so this is not yet a fault — DSC-02 asks.",
                    Attribution.Unknown, measurements);
            }

            if (session.Options.TargetNode is { } target && heard.All(n => n.NodeId != target))
            {
                return Outcome.Inconclusive(
                    $"{heard.Count} node(s) were heard, but node {target} was not among them.",
                    Attribution.Unknown, measurements);
            }

            return Outcome.Pass($"{heard.Count} node(s) announced themselves.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("DSC-02", "discovery", "Active scan (SDO upload of 1000h:00 per node-id)", Tier.Read, "FR-CO-034", async () =>
        {
            // ScanAsync sends one SDO upload of 1000h:00 to every node-id. 1000h:00 is one of the
            // three pairs the peer-SDO gate allows with no description bound, so this works on an
            // unknown bus — which is the whole point of a scan.
            var answered = await CanOpenDiscovery.ScanAsync(session.Client, skipNodeIds: null, cancellationToken)
                .ConfigureAwait(false);
            var measurements = answered.ToDictionary(
                n => $"node {n.NodeId}",
                n => Describe(n),
                StringComparer.Ordinal);

            if (answered.Count == 0)
            {
                // Nothing on the bus at all: the adapter, the bit rate and the wiring are as
                // likely as the device, and BUS-02 above is where that is decided.
                return Outcome.Inconclusive("No node-id answered an SDO upload of 1000h:00.",
                    Attribution.Unknown, measurements);
            }

            if (session.Options.TargetNode is { } target && answered.All(n => n.NodeId != target))
            {
                // A node in Stopped has no SDO server, by CiA 301 Table 37 — the same rule the NMT
                // and PDO checks are built around, and the reason the confirmation budget cannot
                // read 1017h there. Failing the device for not answering a scan in that state
                // reported a conforming node as broken and took the run's exit code with it, so
                // the state the bus volunteered decides. Found in review of #388.
                if (targetStateHeard is NmtState.Stopped)
                {
                    measurements[$"node {target}"] = "heard in NMT state Stopped (04h), where CiA 301 Table 37 has no "
                        + "SDO server";
                    return Outcome.NotApplicable(
                        $"Node {target} did not answer an SDO upload of 1000h:00, and DSC-01 heard it in NMT state "
                        + "Stopped — where CiA 301 Table 37 gives it no SDO server to answer with. Nothing is "
                        + "concluded from the silence of a service the device is not meant to be running. Start the "
                        + "node (or let the nmt group do it) to scan it actively.", measurements);
                }

                if (targetStateHeard is null)
                {
                    return Outcome.Inconclusive(
                        $"Node {target} did not answer an SDO upload of 1000h:00, although {answered.Count} other "
                        + "node(s) did — and DSC-01 heard no NMT state from it, so whether it ought to have an SDO "
                        + "server answering is unknown: a node in Stopped conformingly has none (CiA 301 Table 37). "
                        + "The bus works, which is what the other answers show.", Attribution.Unknown, measurements);
                }

                return Outcome.Fail(
                    $"Node {target} did not answer an SDO upload of 1000h:00, although {answered.Count} other node(s) did, "
                    + $"and DSC-01 heard it in NMT state {targetStateHeard} — a state in which CiA 301 requires an SDO "
                    + "server. The bus works; that node-id does not have one answering on it.", measurements);
            }

            return Outcome.Pass($"{answered.Count} node(s) answered.", measurements);
        }).ConfigureAwait(false);
    }

    private static string Describe(CanOpenDiscoveredNode node)
    {
        var parts = new List<string> { $"evidence {node.Evidence}" };
        if (node.HeartbeatState is { } state) parts.Add($"state {state} ({(byte)state})");
        if (node.DeviceType is { } deviceType)
            parts.Add($"1000h = 0x{deviceType.ToString("X8", CultureInfo.InvariantCulture)}");
        return string.Join(", ", parts);
    }
}
