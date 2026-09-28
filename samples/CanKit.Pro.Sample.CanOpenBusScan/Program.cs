using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Sample.CanOpenBusScan
{
    // Listen-only is the default (#131 decision 3): CanOpenDiscovery.ListenAsync opens no node
    // and transmits nothing. --active-scan then opens --client-node and calls
    // CanOpenDiscovery.ScanAsync, which sends an SDO upload of 1000h:00 to the node IDs the
    // listen did not hear. --peer-description limits every read to the EDS or DCF.
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
            var listenMilliseconds = GetIntArg(args, "--heartbeat-ms",
                (int)CanOpenDiscovery.DefaultListenWindow.TotalMilliseconds, minimum: 1);
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

                Console.WriteLine($"CANopen discovery on {endpoint} at {bitrate} bit/s.");
                Console.WriteLine("No CANopen node is open while listening, so nothing is transmitted.");
                Console.WriteLine($"Listening for heartbeats and boot-up for {listenMilliseconds} ms ...");

                var heard = await CanOpenDiscovery.ListenAsync(
                    bus,
                    TimeSpan.FromMilliseconds(listenMilliseconds),
                    cancellation.Token).ConfigureAwait(false);

                if (!activeScan)
                {
                    Console.WriteLine(
                        "Not probing node IDs that stayed silent. " +
                        "Pass --active-scan to open a node and SDO-read 0x1000:00 on those IDs.");
                    Console.WriteLine();
                    foreach (var node in heard)
                    {
                        Console.WriteLine($"Node 0x{node.NodeId:X2} ({node.NodeId})");
                        Console.WriteLine($"  Heard: {FormatEvidence(node)}");
                        Console.WriteLine();
                    }

                    PrintSummary(heard.Count);
                    return 0;
                }

                using var client = CanOpen.OpenNode(bus, (byte)clientNodeId, new CanOpenNodeOptions
                {
                    SdoTimeout = TimeSpan.FromMilliseconds(sdoTimeoutMilliseconds),
                });

                var skip = heard.Select(node => node.NodeId).ToArray();
                if (peerDescription is not null)
                {
                    // An EDS applies to every node; a DCF only to the node-id it was commissioned
                    // for. Nodes without a binding keep the no-file exemption for 1000h:00.
                    foreach (var nodeId in NodeIds().Where(id => id != clientNodeId))
                    {
                        if (DescriptionFor(peerDescription, nodeId) is { } description)
                        {
                            client.BindPeerDeviceDescription(nodeId, description);
                        }
                    }
                }

                Console.WriteLine(
                    $"Client node 0x{clientNodeId:X2} must be unused and is excluded from discovery.");
                Console.WriteLine(
                    "Probing the node IDs not heard via SDO 0x1000:00 ...");

                var answered = await CanOpenDiscovery.ScanAsync(client, skip, cancellation.Token)
                    .ConfigureAwait(false);
                var detected = heard.Concat(answered)
                    .Where(node => node.NodeId != clientNodeId)
                    .OrderBy(node => node.NodeId)
                    .ToArray();

                Console.WriteLine();
                foreach (var node in detected)
                {
                    await PrintDeviceAsync(client, node, cancellation.Token).ConfigureAwait(false);
                }

                PrintSummary(detected.Length);
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

        private static IEnumerable<byte> NodeIds() => Enumerable
            .Range(CanOpenCobId.MinNodeId, CanOpenCobId.MaxNodeId)
            .Select(value => (byte)value);

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

        private static void PrintSummary(int count)
        {
            Console.WriteLine(count == 0
                ? "No CANopen devices detected."
                : $"Detected {count} CANopen device(s). Done.");
        }

        private static string FormatEvidence(CanOpenDiscoveredNode node)
        {
            var parts = new List<string>();
            if (node.Evidence.HasFlag(CanOpenPresenceEvidence.Heartbeat))
            {
                parts.Add($"heartbeat ({node.HeartbeatState})");
            }

            if (node.Evidence.HasFlag(CanOpenPresenceEvidence.BootUp))
            {
                parts.Add("boot-up");
            }

            if (node.Evidence.HasFlag(CanOpenPresenceEvidence.SdoResponse))
            {
                parts.Add("SDO response");
            }

            return string.Join(", ", parts);
        }

        private static async Task PrintDeviceAsync(
            ICanOpenNode client,
            CanOpenDiscoveredNode node,
            CancellationToken cancellationToken)
        {
            var nodeId = node.NodeId;
            var peerDescription = client.GetPeerDeviceDescription(nodeId);
            Console.WriteLine($"Node 0x{nodeId:X2} ({nodeId})");
            Console.WriteLine($"  Found by: {FormatEvidence(node)}");

            if (node.Evidence.HasFlag(CanOpenPresenceEvidence.SdoResponse))
            {
                // ScanAsync already asked. A null DeviceType means the node answered with an SDO
                // abort, a malformed response or a value that is not four bytes; the result does
                // not say which, and asking again would only repeat it.
                Console.WriteLine(node.DeviceType is { } scanned
                    ? $"  0x1000:00 Device type = 0x{scanned:X8}"
                    : "  0x1000:00 Device type = <answered, but not with a 4-byte value>");
            }
            else if (peerDescription is null || peerDescription.Contains(DeviceTypeIndex, 0))
            {
                // Heard, not scanned: this is the first read of 1000h:00 for the node.
                var deviceType = await ReadObjectAsync(
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
                "The default is listen-only (#131 decision 3): CanOpenDiscovery.ListenAsync " +
                "collects heartbeats and boot-up during --heartbeat-ms (default 2000). One of " +
                "the two is enough. No CANopen node is opened and nothing is transmitted. " +
                "Node IDs that stayed silent are not probed.");
            Console.WriteLine(
                "--active-scan then opens --client-node and calls CanOpenDiscovery.ScanAsync, " +
                "which SDO-reads 1000h:00 on the node IDs not heard. That node ID must be unused " +
                "on the bus and is excluded from discovery. The identity of every node found is " +
                "printed from 1018h:00–04, which is allowed without a peer " +
                "file. With --peer-description, only objects the file lists are read. An EDS " +
                "is used for every node; a DCF is used only for the node-id it was commissioned for.");
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
