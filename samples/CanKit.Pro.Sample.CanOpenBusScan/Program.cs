using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Sample.CanOpenBusScan
{
    // Listen-only is the default (#131 decision 3): heartbeats and boot-up during
    // --heartbeat-ms, and no SDO read of node IDs that stayed silent. --active-scan
    // opts in to that probe. 1000h and 1018h are allowed there; --peer-description
    // still limits the reads to objects the EDS or DCF lists.
    internal static class Program
    {
        private const ushort DeviceTypeIndex = 0x1000;
        private const ushort IdentityIndex = 0x1018;

        private static async Task<int> Main(string[] args)
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return 0;
            }

            var endpoint = GetArg(args, "--endpoint") ?? "virtual://canopen-scan/0";
            var bitrate = GetIntArg(args, "--bitrate", 500_000, minimum: 1);
            var heartbeatMilliseconds = GetIntArg(args, "--heartbeat-ms", 2_000, minimum: 1);
            var sdoTimeoutMilliseconds = GetIntArg(args, "--sdo-timeout-ms", 500, minimum: 1);
            var clientNodeId = GetIntArg(args, "--client-node", CanOpenCobId.MaxNodeId,
                CanOpenCobId.MinNodeId, CanOpenCobId.MaxNodeId);
            var peerDescriptionPath = GetArg(args, "--peer-description");
            var peerDescription = peerDescriptionPath is null
                ? null
                : CanOpenDeviceDescription.Load(peerDescriptionPath);
            var activeScan = HasFlag(args, "--active-scan");

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                using var bus = CanBus.Open(endpoint, cfg =>
                    cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(bitrate));
                using var client = CanOpen.OpenNode(bus, (byte)clientNodeId, new CanOpenNodeOptions
                {
                    SdoTimeout = TimeSpan.FromMilliseconds(sdoTimeoutMilliseconds),
                });

                var heartbeatObservations = new ConcurrentDictionary<byte, HeartbeatObservation>();
                client.HeartbeatReceived += (_, e) =>
                {
                    if (e.ProducerNodeId == clientNodeId)
                    {
                        return;
                    }

                    var isHeartbeat = e.State != NmtState.Initializing;
                    heartbeatObservations.AddOrUpdate(
                        e.ProducerNodeId,
                        _ => new HeartbeatObservation(isHeartbeat, e.State),
                        (_, previous) => new HeartbeatObservation(
                            previous.HeartbeatSeen || isHeartbeat,
                            e.State));
                };

                Console.WriteLine(
                    activeScan
                        ? $"Active CANopen scan on {endpoint} at {bitrate} bit/s."
                        : $"Listen-only CANopen discovery on {endpoint} at {bitrate} bit/s.");
                Console.WriteLine(
                    $"Client node 0x{clientNodeId:X2} must be unused and is excluded from discovery.");
                Console.WriteLine(
                    $"Listening for heartbeats and boot-up for {heartbeatMilliseconds} ms ...");

                await Task.Delay(heartbeatMilliseconds, cancellation.Token).ConfigureAwait(false);

                var observations = heartbeatObservations.ToDictionary(pair => pair.Key, pair => pair.Value);
                ProbeResult[] probes;
                if (!activeScan)
                {
                    Console.WriteLine(
                        "Not probing node IDs that stayed silent. " +
                        "Pass --active-scan to SDO-read 0x1000 on those IDs; " +
                        "0x1000 and 0x1018 are allowed on that path.");
                    probes = Array.Empty<ProbeResult>();
                }
                else
                {
                    var nodesToProbe = Enumerable
                        .Range(CanOpenCobId.MinNodeId, CanOpenCobId.MaxNodeId)
                        .Select(value => (byte)value)
                        .Where(nodeId => nodeId != clientNodeId)
                        .Where(nodeId => !observations.TryGetValue(nodeId, out var observation)
                            || !observation.HeartbeatSeen)
                        .ToArray();

                    var nodesForDeviceType = nodesToProbe
                        .Where(nodeId => ListsSubindexZero(
                            DescriptionFor(peerDescription, nodeId),
                            DeviceTypeIndex))
                        .ToArray();
                    if (nodesForDeviceType.Length == 0)
                    {
                        Console.WriteLine(
                            "Not probing via SDO 0x1000: the peer description for those nodes does not list it.");
                    }
                    else if (nodesForDeviceType.Length == nodesToProbe.Length)
                    {
                        Console.WriteLine(
                            $"Probing {nodesToProbe.Length} node ID(s) without a heartbeat via SDO 0x1000 ...");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Probing {nodesForDeviceType.Length} of {nodesToProbe.Length} node ID(s) via SDO 0x1000; " +
                            "the description bound for the others does not list it.");
                    }

                    probes = nodesForDeviceType.Length == 0
                        ? Array.Empty<ProbeResult>()
                        : await Task.WhenAll(nodesForDeviceType.Select(nodeId =>
                            ProbeDeviceTypeAsync(
                                client,
                                nodeId,
                                DescriptionFor(peerDescription, nodeId),
                                cancellation.Token))).ConfigureAwait(false);
                }
                var probesByNode = probes.ToDictionary(probe => probe.NodeId);
                var finalObservations = heartbeatObservations.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);

                var detectedNodeIds = finalObservations.Keys
                    .Concat(probes.Where(probe => probe.ResponseReceived).Select(probe => probe.NodeId))
                    .Where(nodeId => nodeId != clientNodeId)
                    .Distinct()
                    .OrderBy(nodeId => nodeId)
                    .ToArray();

                Console.WriteLine();
                if (detectedNodeIds.Length == 0)
                {
                    Console.WriteLine("No CANopen devices detected.");
                    return 0;
                }

                foreach (var nodeId in detectedNodeIds)
                {
                    HeartbeatObservation? observation = finalObservations.TryGetValue(
                        nodeId,
                        out var detectedObservation)
                        ? detectedObservation
                        : null;
                    if (!activeScan)
                    {
                        PrintHeardNode(nodeId, observation);
                        continue;
                    }

                    probesByNode.TryGetValue(nodeId, out var probe);
                    await PrintDeviceAsync(
                        client,
                        nodeId,
                        observation,
                        probe?.DeviceType,
                        DescriptionFor(peerDescription, nodeId),
                        cancellation.Token).ConfigureAwait(false);
                }

                Console.WriteLine($"Detected {detectedNodeIds.Length} CANopen device(s). Done.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Scan cancelled.");
                return 130;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Scan failed: {exception.Message}");
                return 1;
            }
        }

        /// <summary>
        /// An EDS describes a device type and applies to every node. A DCF is commissioned for
        /// one node-id and applies only there; other nodes keep the no-file exemption.
        /// </summary>
        private static CanOpenDeviceDescription? DescriptionFor(
            CanOpenDeviceDescription? peerDescription,
            byte nodeId)
        {
            if (peerDescription?.NodeId is { } commissioned && commissioned != nodeId)
            {
                return null;
            }

            return peerDescription;
        }

        private static bool ListsSubindexZero(CanOpenDeviceDescription? description, ushort index)
            => description is null || description.Contains(index, 0);

        private static async Task<ProbeResult> ProbeDeviceTypeAsync(
            ICanOpenNode client,
            byte nodeId,
            CanOpenDeviceDescription? peerDescription,
            CancellationToken cancellationToken)
        {
            if (peerDescription is not null)
            {
                client.BindPeerDeviceDescription(nodeId, peerDescription);
            }

            var result = await ReadObjectAsync(
                client,
                nodeId,
                DeviceTypeIndex,
                subindex: 0,
                cancellationToken).ConfigureAwait(false);

            var responseReceived = result.Success
                || result.Error is SdoAbortException abort
                && abort.AbortCode != (uint)SdoAbortCode.SdoProtocolTimedOut;
            return new ProbeResult(nodeId, responseReceived, result);
        }

        private static void PrintHeardNode(byte nodeId, HeartbeatObservation? heartbeat)
        {
            Console.WriteLine($"Node 0x{nodeId:X2} ({nodeId})");
            Console.WriteLine($"  Heartbeat: {FormatHeartbeat(heartbeat)}");
            Console.WriteLine();
        }

        private static async Task PrintDeviceAsync(
            ICanOpenNode client,
            byte nodeId,
            HeartbeatObservation? heartbeat,
            SdoReadResult? probedDeviceType,
            CanOpenDeviceDescription? peerDescription,
            CancellationToken cancellationToken)
        {
            if (peerDescription is not null)
            {
                client.BindPeerDeviceDescription(nodeId, peerDescription);
            }

            Console.WriteLine($"Node 0x{nodeId:X2} ({nodeId})");
            Console.WriteLine($"  Heartbeat: {FormatHeartbeat(heartbeat)}");

            if (peerDescription is null || peerDescription.Contains(DeviceTypeIndex, 0))
            {
                var deviceType = probedDeviceType
                    ?? await ReadObjectAsync(
                        client,
                        nodeId,
                        DeviceTypeIndex,
                        subindex: 0,
                        cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"  0x1000:00 Device type = {FormatResult(deviceType)}");
            }

            await PrintIdentityAsync(client, nodeId, peerDescription, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine();
        }

        private static async Task PrintIdentityAsync(
            ICanOpenNode client,
            byte nodeId,
            CanOpenDeviceDescription? peerDescription,
            CancellationToken cancellationToken)
        {
            if (peerDescription is not null && !peerDescription.Contains(IdentityIndex, 0))
            {
                return;
            }

            var identityCount = await ReadObjectAsync(
                client,
                nodeId,
                IdentityIndex,
                subindex: 0,
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"  0x1018:00 {GetIdentitySubindexName(0)} = {FormatResult(identityCount)}");

            if (peerDescription is null)
            {
                if (!identityCount.Success || identityCount.Data!.Length == 0)
                {
                    return;
                }

                // 1018h:00–04 are readable with no peer file. Anything the node reports above 04h
                // still needs a description.
                var reportedHighest = identityCount.Data[0];
                var readable = reportedHighest > 4 ? (byte)4 : reportedHighest;
                for (byte subindex = 1; subindex <= readable; subindex++)
                {
                    var value = await ReadObjectAsync(
                        client,
                        nodeId,
                        IdentityIndex,
                        subindex,
                        cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(
                        $"  0x1018:{subindex:X2} {GetIdentitySubindexName(subindex)} = " +
                        FormatResult(value));
                }

                if (reportedHighest > 4)
                {
                    Console.WriteLine(
                        "  Further 0x1018 sub-indices need a peer EDS/DCF (--peer-description).");
                }

                return;
            }

            if (!identityCount.Success || identityCount.Data!.Length == 0)
            {
                return;
            }

            var highestSubindex = identityCount.Data[0];
            for (byte subindex = 1; subindex <= highestSubindex; subindex++)
            {
                if (!peerDescription.Contains(IdentityIndex, subindex))
                {
                    Console.WriteLine(
                        $"  0x1018:{subindex:X2} {GetIdentitySubindexName(subindex)} = " +
                        "<not in the peer description>");
                    continue;
                }

                var value = await ReadObjectAsync(
                    client,
                    nodeId,
                    IdentityIndex,
                    subindex,
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine(
                    $"  0x1018:{subindex:X2} {GetIdentitySubindexName(subindex)} = " +
                    FormatResult(value, useDecimal: subindex == 5));

                if (subindex == byte.MaxValue)
                {
                    break;
                }
            }
        }

        private static async Task<SdoReadResult> ReadObjectAsync(
            ICanOpenNode client,
            byte nodeId,
            ushort index,
            byte subindex,
            CancellationToken cancellationToken)
        {
            try
            {
                var data = await client
                    .SdoUploadAsync(nodeId, index, subindex, cancellationToken)
                    .ConfigureAwait(false);
                return SdoReadResult.FromData(data);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return SdoReadResult.FromError(exception);
            }
        }

        private static string FormatHeartbeat(HeartbeatObservation? observation)
        {
            if (observation is null)
            {
                return "off";
            }

            if (observation.Value.HeartbeatSeen)
            {
                return $"on ({observation.Value.LastState})";
            }

            return "off (boot-up frame seen)";
        }

        private static string FormatResult(SdoReadResult result, bool useDecimal = false)
        {
            if (!result.Success)
            {
                if (result.Error is SdoAbortException abort)
                {
                    return $"<SDO abort 0x{abort.AbortCode:X8}>";
                }

                return $"<{result.Error!.GetType().Name}: {result.Error.Message}>";
            }

            var data = result.Data!;
            if (useDecimal)
            {
                return data.Length switch
                {
                    1 => data[0].ToString(CultureInfo.InvariantCulture),
                    2 => ReadUInt16LittleEndian(data).ToString(CultureInfo.InvariantCulture),
                    4 => ReadUInt32LittleEndian(data).ToString(CultureInfo.InvariantCulture),
                    _ => data.Length == 0 ? "<empty>" : BitConverter.ToString(data),
                };
            }

            return data.Length switch
            {
                1 => $"0x{data[0]:X2}",
                2 => $"0x{ReadUInt16LittleEndian(data):X4}",
                4 => $"0x{ReadUInt32LittleEndian(data):X8}",
                _ => data.Length == 0 ? "<empty>" : BitConverter.ToString(data),
            };
        }

        private static ushort ReadUInt16LittleEndian(IReadOnlyList<byte> data)
        {
            return (ushort)(data[0] | data[1] << 8);
        }

        private static uint ReadUInt32LittleEndian(IReadOnlyList<byte> data)
        {
            return (uint)(data[0]
                | data[1] << 8
                | data[2] << 16
                | data[3] << 24);
        }

        private static string GetIdentitySubindexName(byte subindex)
        {
            return subindex switch
            {
                0 => "Highest sub-index supported",
                1 => "Vendor ID",
                2 => "Product code",
                3 => "Revision number",
                4 => "Serial number",
                5 => "Housing number",
                _ => "Additional identity entry",
            };
        }

        private static string? GetArg(string[] args, string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static bool HasFlag(string[] args, string name)
        {
            return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        }

        private static int GetIntArg(
            string[] args,
            string name,
            int defaultValue,
            int minimum,
            int maximum = int.MaxValue)
        {
            var text = GetArg(args, name);
            if (text is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < minimum
                || value > maximum)
            {
                throw new ArgumentException(
                    $"{name} must be an integer in the range {minimum}..{maximum}.");
            }

            return value;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "Usage: CanOpenBusScan [--endpoint <endpoint>] [--bitrate 500000] " +
                "[--heartbeat-ms 2000] [--sdo-timeout-ms 500] [--client-node 127] " +
                "[--peer-description <eds-or-dcf>] [--active-scan]");
            Console.WriteLine();
            Console.WriteLine(
                "The default is listen-only (#131 decision 3): heartbeats and boot-up for " +
                "--heartbeat-ms. Node IDs that stayed silent are not probed.");
            Console.WriteLine(
                "--active-scan opts in to an SDO probe of node IDs that did not heartbeat. " +
                "1000h:00 and 1018h:00–04 are allowed on that path without a peer file. " +
                "With --peer-description, only objects the file lists are read. An EDS is " +
                "used for every node; a DCF is used only for the node-id it was commissioned for.");
            Console.WriteLine(
                "--client-node is the CANopen node ID used by the scanner. It must be unused " +
                "on the bus and is excluded from discovery.");
        }

        private readonly struct HeartbeatObservation
        {
            public HeartbeatObservation(bool heartbeatSeen, NmtState lastState)
            {
                HeartbeatSeen = heartbeatSeen;
                LastState = lastState;
            }

            public bool HeartbeatSeen { get; }

            public NmtState LastState { get; }
        }

        private sealed class ProbeResult
        {
            public ProbeResult(byte nodeId, bool responseReceived, SdoReadResult deviceType)
            {
                NodeId = nodeId;
                ResponseReceived = responseReceived;
                DeviceType = deviceType;
            }

            public byte NodeId { get; }

            public bool ResponseReceived { get; }

            public SdoReadResult DeviceType { get; }
        }

        private sealed class SdoReadResult
        {
            private SdoReadResult(byte[]? data, Exception? error)
            {
                Data = data;
                Error = error;
            }

            public bool Success => Data is not null;

            public byte[]? Data { get; }

            public Exception? Error { get; }

            public static SdoReadResult FromData(byte[] data)
            {
                return new SdoReadResult(data, error: null);
            }

            public static SdoReadResult FromError(Exception error)
            {
                return new SdoReadResult(data: null, error);
            }
        }
    }
}
