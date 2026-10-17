using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Abstractions.API.Common.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Lss;
using CanKit.Pro.CANopen.Sdo;
using CanOpenLssApi = CanKit.Pro.CANopen.Lss.CanOpenLss;

namespace CanKit.Sample.CanOpenLss
{
    // Commissioning tool on LssCommissioning (FR-CO-049). --demo starts slaves on the virtual
    // bus so the commands can be tried without hardware.
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            var verb = args[0];
            if (!IsVerb(verb))
            {
                Console.Error.WriteLine($"Unknown command '{verb}'.");
                PrintUsage();
                return 2;
            }

            var demo = HasFlag(args, "--demo");
            var endpoint = demo
                ? DemoNetwork.Endpoint
                : GetArg(args, "--endpoint") ?? "virtual://canopen-lss/0";
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                var bitrate = GetInt(args, "--bitrate", 500_000, 1);
                using var demoNetwork = demo ? DemoNetwork.Start() : null;
                using var bus = CanBus.Open(endpoint, cfg =>
                    cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(bitrate));
                await using var master = CanOpenLssApi.OpenMaster(bus, new LssMasterOptions
                {
                    ResponseTimeout = TimeSpan.FromMilliseconds(GetInt(args, "--timeout-ms", 1000, 1)),
                    FastscanTimeout = TimeSpan.FromMilliseconds(GetInt(args, "--fastscan-ms", 50, 1)),
                });
                var tool = new LssCommissioning(master);
                Console.WriteLine($"LSS on {endpoint} at {bitrate} bit/s.");
                return await RunAsync(tool, verb, args, bus, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Cancelled.");
                return 130;
            }
            catch (LssTimeoutException exception)
            {
                return Fail(exception);
            }
            catch (CanOpenTransportException exception)
            {
                return Fail(exception);
            }
            catch (SdoAbortException exception)
            {
                return Fail(exception);
            }
            catch (ArgumentException exception)
            {
                return Fail(exception);
            }
            catch (FormatException exception)
            {
                return Fail(exception);
            }
            catch (OverflowException exception)
            {
                return Fail(exception);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(exception);
            }
        }

        private static int Fail(Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        private static async Task<int> RunAsync(
            LssCommissioning tool,
            string verb,
            string[] args,
            CanKit.Abstractions.API.Can.ICanBus bus,
            CancellationToken cancellationToken)
        {
            switch (verb.ToLowerInvariant())
            {
                case "scan":
                    return await ScanAsync(tool, args, bus, cancellationToken).ConfigureAwait(false);
                case "assign":
                    return await AssignOneAsync(tool, args, cancellationToken).ConfigureAwait(false);
                case "assign-unconfigured":
                    return await AssignUnconfiguredAsync(tool, args, cancellationToken).ConfigureAwait(false);
                case "inquire":
                    return Print(await tool.InquireSlaveAsync(ReadAddress(args), cancellationToken)
                        .ConfigureAwait(false));
                default:
                    return 2;
            }
        }

        private static async Task<int> ScanAsync(
            LssCommissioning tool,
            string[] args,
            CanKit.Abstractions.API.Can.ICanBus bus,
            CancellationToken cancellationToken)
        {
            var scan = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan(), cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"Unconfigured ({scan.Stop}):");
            var failed = PrintAll(scan.Slaves);
            if (!HasFlag(args, "--all"))
                return failed || scan.Stop == LssScanStop.NoMatch ? 1 : 0;

            int clientId = GetInt(args, "--client-node", CanOpenCobId.MaxNodeId,
                CanOpenCobId.MinNodeId, CanOpenCobId.MaxNodeId);
            using var client = CanOpen.OpenNode(bus, (byte)clientId, new CanOpenNodeOptions
            {
                SdoTimeout = TimeSpan.FromMilliseconds(GetInt(args, "--sdo-timeout-ms", 200, 1)),
            });
            IEnumerable<byte> ids;
            if (HasFlag(args, "--probe-all"))
            {
                Console.WriteLine("Probing node-ids 1..127 together, then reading 1018h of the nodes that answer.");
                var present = await CanOpenDiscovery.ScanAsync(client, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                ids = present.Select(node => node.NodeId);
            }
            else
            {
                int listen = GetInt(args, "--heartbeat-ms", 400, 1);
                Console.WriteLine($"Listening {listen} ms for heartbeats and boot-up, then reading 1018h of those nodes.");
                var heard = await CanOpenDiscovery.ListenAsync(bus, TimeSpan.FromMilliseconds(listen), cancellationToken)
                    .ConfigureAwait(false);
                ids = heard.Select(node => node.NodeId);
            }

            Console.WriteLine("Configured (1018h via SDO):");
            var configured = await tool.ReadIdentitiesAsync(client, ids, cancellationToken).ConfigureAwait(false);
            return PrintAll(configured) || failed ? 1 : 0;
        }

        private static async Task<int> AssignOneAsync(
            LssCommissioning tool, string[] args, CancellationToken cancellationToken)
        {
            var configuration = new LssSlaveConfiguration
            {
                NodeId = ReadNodeId(args, "--node-id", required: false),
                Store = HasFlag(args, "--store"),
                BitTimingSelector = ReadOptionalByte(args, "--selector"),
                BitTimingIndex = ReadOptionalByte(args, "--index"),
                ActivateDelayMilliseconds = ReadOptionalUShort(args, "--activate-ms"),
                ActivateBitRate = ReadOptionalInt(args, "--bit-rate"),
            };
            var result = await tool.ConfigureSlaveAsync(ReadAddress(args), configuration, cancellationToken)
                .ConfigureAwait(false);
            if (configuration.NodeId is not null && result.Succeeded)
            {
                Console.WriteLine(
                    "The node-id is pending until NMT reset communication to the node-id that is active now.");
            }
            return Print(result);
        }

        private static async Task<int> AssignUnconfiguredAsync(
            LssCommissioning tool, string[] args, CancellationToken cancellationToken)
        {
            ushort? activate = ReadOptionalUShort(args, "--activate-ms");
            int? count = HasFlag(args, "--count") ? GetInt(args, "--count", 1, 1) : null;
            if (activate is not null && count != 1)
            {
                Console.Error.WriteLine(
                    "Activating bit timing while assigning unconfigured slaves needs --count 1.");
                return 2;
            }

            byte? first = ReadNodeId(args, "--first-node-id", required: true);
            var scan = await tool.ScanUnconfiguredAsync(new LssUnconfiguredScan
            {
                AssignFromNodeId = first,
                Limit = count,
                Store = HasFlag(args, "--store"),
                BitTimingSelector = ReadOptionalByte(args, "--selector"),
                BitTimingIndex = ReadOptionalByte(args, "--index"),
                ActivateDelayMilliseconds = activate,
                ActivateBitRate = ReadOptionalInt(args, "--bit-rate"),
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Stop: {scan.Stop}");
            bool failed = PrintAll(scan.Slaves) || scan.Stop == LssScanStop.SlaveRejected
                || scan.Stop == LssScanStop.NoMatch || scan.Stop == LssScanStop.TimedOut;
            return failed ? 1 : 0;
        }

        private static int Print(LssCommissioningResult result)
        {
            Console.WriteLine(Format(result));
            return result.Succeeded ? 0 : 1;
        }

        private static bool PrintAll(IReadOnlyList<LssCommissioningResult> results)
        {
            if (results.Count == 0)
                Console.WriteLine("  (none)");
            bool failed = false;
            foreach (var result in results)
            {
                Console.WriteLine("  " + Format(result));
                failed |= !result.Succeeded;
            }

            return failed;
        }

        private static string Format(LssCommissioningResult result)
        {
            if (!result.Selected)
                return $"{result.Address}  not selected";
            var node = result.NodeId is { } id ? $"node 0x{id:X2}" : "node (unchanged)";
            var nodeIdError = result.NodeIdResult is { Succeeded: false } rejected
                ? $"node-id {rejected}"
                : null;
            var store = result.StoreResult is { } stored
                ? (stored.Succeeded ? "stored" : $"store {stored}")
                : null;
            var timing = result.BitTimingResult is { } rate
                ? (rate.Succeeded ? "bit timing set" : $"bit timing {rate}")
                : null;
            return string.Join("  ", new[] { result.Address.ToString(), node, nodeIdError, timing, store }
                .Where(part => part is not null));
        }

        private static LssAddress ReadAddress(string[] args) => new(
            GetUInt(args, "--vendor"),
            GetUInt(args, "--product"),
            GetUInt(args, "--revision"),
            GetUInt(args, "--serial"));

        private static bool IsVerb(string verb)
        {
            switch (verb.ToLowerInvariant())
            {
                case "scan":
                case "assign":
                case "assign-unconfigured":
                case "inquire":
                    return true;
                default:
                    return false;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "Usage: CanOpenLss <scan|assign|assign-unconfigured|inquire> [--endpoint <endpoint>] " +
                "[--bitrate 500000] [--demo] [--timeout-ms 1000] [--fastscan-ms 50]");
            Console.WriteLine(
                "  scan [--all] [--client-node 127] [--heartbeat-ms 400] [--probe-all] [--sdo-timeout-ms 200]");
            Console.WriteLine("  assign --vendor <n> --product <n> --revision <n> --serial <n> [--node-id <n>]");
            Console.WriteLine("         [--selector <n> --index <n>] [--activate-ms <n>] [--bit-rate <n>] [--store]");
            Console.WriteLine("  assign-unconfigured --first-node-id <n> [--count <n>] [--selector <n> --index <n>] [--store]");
            Console.WriteLine("         [--activate-ms <n> --bit-rate <n> only together with --count 1]");
            Console.WriteLine("  inquire --vendor <n> --product <n> --revision <n> --serial <n>");
            Console.WriteLine();
            Console.WriteLine(
                "--demo starts three unconfigured LSS slaves (serials 1, 2, 3) and configured node 4 " +
                "on the virtual bus. Numbers accept decimal or 0x hex.");
            Console.WriteLine(
                "Activating bit timing waits both switch delays. The open channel is not reconfigured.");
        }

        private static string? GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static bool HasFlag(string[] args, string name)
            => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

        private static int GetInt(string[] args, string name, int defaultValue, int minimum, int maximum = int.MaxValue)
        {
            var text = GetArg(args, name);
            if (text is null) return defaultValue;
            if (!TryParseInt(text, out var value) || value < minimum || value > maximum)
                throw new ArgumentException($"{name} must be an integer in {minimum}..{maximum}.");

            return value;
        }

        private static uint GetUInt(string[] args, string name)
        {
            var text = GetArg(args, name) ?? throw new ArgumentException($"{name} is required.");
            if (!TryParseUInt(text, out var value))
                throw new ArgumentException($"{name} must be an unsigned integer.");
            return value;
        }

        private static byte? ReadNodeId(string[] args, string name, bool required)
        {
            if (TryParseBounded(args, name, required, byte.MaxValue, out _) is not { } parsed)
                return null;
            return (byte)parsed;
        }

        private static byte? ReadOptionalByte(string[] args, string name)
        {
            if (TryParseBounded(args, name, required: false, byte.MaxValue, out _) is not { } parsed)
                return null;
            return (byte)parsed;
        }

        private static ushort? ReadOptionalUShort(string[] args, string name)
        {
            if (TryParseBounded(args, name, required: false, ushort.MaxValue, out _) is not { } parsed)
                return null;
            return (ushort)parsed;
        }

        private static int? ReadOptionalInt(string[] args, string name)
        {
            var text = GetArg(args, name);
            if (text is null) return null;
            if (!TryParseInt(text, out var value))
                throw new ArgumentException($"{name} must be an integer.");
            return value;
        }

        /// <summary>
        /// Decimal or <c>0x</c> hex. <see langword="null"/> when the flag is absent and
        /// <paramref name="required"/> is false. Throws <see cref="ArgumentException"/> otherwise.
        /// </summary>
        private static uint? TryParseBounded(string[] args, string name, bool required, uint maximum, out uint value)
        {
            var text = GetArg(args, name);
            if (text is null)
            {
                if (required) throw new ArgumentException($"{name} is required.");
                value = 0;
                return null;
            }

            if (!TryParseUInt(text, out value) || value > maximum)
                throw new ArgumentException($"{name} must be an integer in 0..{maximum}.");
            return value;
        }

        private static bool TryParseUInt(string text, out uint value)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseInt(string text, out int value)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unsigned)
                    && unsigned <= int.MaxValue)
                {
                    value = (int)unsigned;
                    return true;
                }

                value = 0;
                return false;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Three unconfigured LSS slaves and one configured node, so <c>scan</c> and
        /// <c>scan --all</c> both have something to print.
        /// </summary>
        private sealed class DemoNetwork : IDisposable
        {
            public const string Endpoint = "virtual://canopen-lss-demo/0";

            private readonly List<IDisposable> _owned = new();

            private DemoNetwork()
            {
                OpenSlave(1, CanOpenCobId.UnconfiguredNodeId, new LssAddress(1, 1, 1, 1), heartbeat: false);
                OpenSlave(2, CanOpenCobId.UnconfiguredNodeId, new LssAddress(1, 1, 1, 2), heartbeat: false);
                OpenSlave(3, CanOpenCobId.UnconfiguredNodeId, new LssAddress(1, 1, 1, 3), heartbeat: false);
                OpenSlave(4, 4, new LssAddress(1, 1, 1, 4), heartbeat: true);
            }

            public static DemoNetwork Start()
            {
                Console.WriteLine("Demo: unconfigured serials 1..3 and configured node 4.");
                return new DemoNetwork();
            }

            public void Dispose()
            {
                for (int i = _owned.Count - 1; i >= 0; i--)
                    _owned[i].Dispose();
            }

            private void OpenSlave(int channel, byte nodeId, LssAddress address, bool heartbeat)
            {
                var bus = CanBus.Open($"virtual://canopen-lss-demo/{channel}", cfg =>
                    cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));
                _owned.Add(bus);
                var node = CanOpen.OpenNode(bus, nodeId, new CanOpenNodeOptions
                {
                    Lss = new LssSlaveOptions { Store = new MemoryStore() },
                });
                _owned.Add(node);
                if (heartbeat)
                    node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100));
                var od = node.ObjectDictionary;
                od.WriteUnsigned(0x1018, 0x00, 4);
                od.WriteUnsigned(0x1018, 0x01, address.VendorId);
                Write(od, 0x02, address.ProductCode);
                Write(od, 0x03, address.RevisionNumber);
                Write(od, 0x04, address.SerialNumber);
            }

            private static void Write(ObjectDictionary od, byte subindex, uint value)
            {
                if (!od.TryGet(0x1018, subindex, out _)) od.AddU32(0x1018, subindex, value);
                else od.WriteUnsigned(0x1018, subindex, value);
            }

            private sealed class MemoryStore : ILssConfigurationStore
            {
                private bool _has;
                private byte _nodeId;
                private byte _selector;
                private byte _index;

                public bool TryLoad(out byte nodeId, out byte tableSelector, out byte tableIndex)
                {
                    nodeId = _nodeId;
                    tableSelector = _selector;
                    tableIndex = _index;
                    return _has;
                }

                public bool TryStore(byte nodeId, byte tableSelector, byte tableIndex, out byte specError)
                {
                    _has = true;
                    _nodeId = nodeId;
                    _selector = tableSelector;
                    _index = tableIndex;
                    specError = 0;
                    return true;
                }
            }
        }
    }
}
