using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// Process data: what the device publishes and what it accepts, the SYNC it reacts to, and the
/// emergency objects it sends. Reading the communication and mapping parameters first is not
/// ceremony — a TPDO is observed at the COB-ID 1800h says it uses, and a run that assumed the
/// default would report a working device as silent the moment anyone had remapped it.
/// </summary>
internal static class PdoSyncEmcyChecks
{
    /// <summary>How many TPDOs/RPDOs to look at. CiA 301 gives four of each as the default set;
    /// a device may have up to 512, and reading all of them is a different tool's job.</summary>
    private const int PdoCount = 4;

    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        var tpdos = new List<PdoDescriptor>();
        var rpdos = new List<PdoDescriptor>();

        await book.RunAsync("PDO-01", "pdo", "TPDO communication and mapping parameters (1800h/1A00h)", Tier.Read, "FR-CO-005", async () =>
        {
            var found = await ReadPdosAsync(session, 0x1800, 0x1A00, cancellationToken).ConfigureAwait(false);
            tpdos.AddRange(found.Descriptors);
            return Summarise("TPDO", found, session);
        }).ConfigureAwait(false);

        await book.RunAsync("PDO-02", "pdo", "RPDO communication and mapping parameters (1400h/1600h)", Tier.Read, "FR-CO-005", async () =>
        {
            var found = await ReadPdosAsync(session, 0x1400, 0x1600, cancellationToken).ConfigureAwait(false);
            rpdos.AddRange(found.Descriptors);
            return Summarise("RPDO", found, session);
        }).ConfigureAwait(false);

        await book.RunAsync("PDO-03", "pdo", "TPDOs are transmitted in Operational and not in Stopped", Tier.Write, "FR-CO-022", async () =>
        {
            var valid = tpdos.Where(p => p.Valid).ToList();
            if (valid.Count == 0)
            {
                return tpdos.Count == 0
                    ? Outcome.NeedsSetup("PDO-01 read no TPDO parameters, so there is no COB-ID to watch.")
                    : Outcome.NotApplicable($"All {tpdos.Count} TPDO(s) the device declares are marked invalid (1800h:01 bit 31).");
            }

            var watched = valid.Select(p => p.CobId).ToHashSet();
            using var tap = new BusTap(session.Service, id => watched.Contains(id));

            TappedFrame[] inOperational;
            int inStopped;
            string leftInState;
            try
            {
                // Operational is the state PDOs flow in; Stopped is the state they must not.
                // Checking only the first would pass a device that transmits regardless of its NMT
                // state, which is the failure that matters on a machine being switched off.
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);
                await tap.ListenAsync(session.Options.PdoWindowMs, cancellationToken).ConfigureAwait(false);
                inOperational = tap.Frames.ToArray();

                await session.Client.SendNmtCommandAsync(NmtCommand.Stop, session.Target, cancellationToken).ConfigureAwait(false);
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                var beforeStopWindow = tap.Frames.Count;
                await tap.ListenAsync(Math.Min(2000, session.Options.PdoWindowMs), cancellationToken).ConfigureAwait(false);
                inStopped = tap.Frames.Count - beforeStopWindow;
            }
            finally
            {
                // Pre-operational is the state a CANopen device powers up in and the only one it is
                // safe to leave a bus in. In a finally and on CancellationToken.None because a
                // Ctrl+C between the Start and the Stop above would otherwise walk away from a
                // machine this check had just stopped.
                leftInState = await RestorePreOperationalAsync(session, book, "PDO-03").ConfigureAwait(false);
            }

            var measurements = new Dictionary<string, string>
            {
                ["TPDOs watched"] = string.Join(", ", valid.Select(p => $"TPDO{p.Number} 0x{p.CobId:X3}")),
                ["frames in Operational"] = $"{inOperational.Length} in {session.Options.PdoWindowMs} ms",
                ["frames in Stopped"] = inStopped.ToString(CultureInfo.InvariantCulture),
                ["left in state"] = leftInState,
            };

            foreach (var pdo in valid)
            {
                var mine = inOperational.Where(f => f.CanId == pdo.CobId).ToArray();
                if (mine.Length == 0)
                {
                    measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3})"] =
                        $"silent; transmission type {DescribeTransmission(pdo.TransmissionType)}";
                    continue;
                }

                var summary = BusTap.Summarise(mine);
                var expected = pdo.EventTimerMs > 0 ? $", event timer 1800h:05 = {pdo.EventTimerMs} ms" : string.Empty;
                measurements[$"TPDO{pdo.Number} (0x{pdo.CobId:X3})"] = summary is { } s
                    ? $"{s}; type {DescribeTransmission(pdo.TransmissionType)}{expected}"
                    : $"{mine.Length} frame(s), {pdo.Bytes} B mapped; type {DescribeTransmission(pdo.TransmissionType)}{expected}";

                var wrongLength = mine.Where(f => pdo.Bytes > 0 && f.Data.Length != pdo.Bytes).ToArray();
                if (wrongLength.Length > 0)
                {
                    measurements[$"TPDO{pdo.Number} length"] =
                        $"mapping says {pdo.Bytes} B, {wrongLength.Length} frame(s) carried {string.Join("/", wrongLength.Select(f => f.Data.Length).Distinct())} B";
                }
            }

            if (inStopped > 0)
            {
                return Outcome.Fail(
                    $"{inStopped} TPDO frame(s) were transmitted while the device was Stopped. CiA 301 allows process "
                    + "data only in Operational.", measurements);
            }

            var mismatched = valid.Any(p => inOperational.Any(f => f.CanId == p.CobId && p.Bytes > 0 && f.Data.Length != p.Bytes));
            if (mismatched)
            {
                return Outcome.Fail(
                    "A TPDO carried a payload of a different length than its own mapping (1A00h) declares.", measurements);
            }

            if (inOperational.Length == 0)
            {
                // Event-driven TPDOs only transmit when their data changes, and a synchronous one
                // waits for a SYNC this check does not send. Silence is therefore not a fault.
                var synchronous = valid.Where(p => CanOpenTransmissionType.IsSynchronous(p.TransmissionType)).ToArray();
                return Outcome.Inconclusive(
                    $"No TPDO was transmitted in {session.Options.PdoWindowMs} ms of Operational. "
                    + (synchronous.Length > 0
                        ? $"{synchronous.Length} of them are synchronous and wait for a SYNC — see the sync group."
                        : "Event-driven TPDOs transmit when their mapped data changes, so a static device is legitimately silent."),
                    Attribution.Unknown, measurements);
            }

            return Outcome.Pass(
                $"{inOperational.Length} TPDO frame(s) in Operational, none in Stopped.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("SYN-01", "sync", "1005h/1006h SYNC parameters", Tier.Read, "FR-CO-010", async () =>
        {
            if (!session.MayTransfer(0x1005, 0x00))
                return Outcome.NeedsSetup(session.GateReason(0x1005, 0x00));

            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, 0x1005, 0x00, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4)
                    return Outcome.Fail($"1005h:00 is UNSIGNED32, the device returned {IdentityAndSdoChecks.Hex(raw)}.");

                var cobId = BitConverter.ToUInt32(raw, 0);
                var measurements = new Dictionary<string, string>
                {
                    ["1005h COB-ID SYNC"] = $"0x{cobId:X8} (id 0x{cobId & CanOpenCobId.CanIdMask:X3})",
                    ["generates SYNC"] = ((cobId & CanOpenCobId.SyncGenerateBit) != 0).ToString(),
                };

                if (session.MayTransfer(0x1006, 0x00))
                {
                    var period = await session.Client.SdoUploadAsync(session.Target, 0x1006, 0x00, cancellationToken).ConfigureAwait(false);
                    measurements["1006h communication cycle period"] = period.Length == 4
                        ? $"{BitConverter.ToUInt32(period, 0)} us"
                        : IdentityAndSdoChecks.Hex(period);
                }

                return Outcome.Pass("SYNC parameters read.", measurements);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist)
            {
                return Outcome.NotApplicable("The device has no 1005h; SYNC is optional in CiA 301.");
            }
        }).ConfigureAwait(false);

        await book.RunAsync("SYN-02", "sync", "Synchronous TPDOs answer a SYNC produced by this tool", Tier.Write, "FR-CO-010", async () =>
        {
            var synchronous = tpdos.Where(p => p.Valid && CanOpenTransmissionType.IsSynchronous(p.TransmissionType)).ToList();
            if (synchronous.Count == 0)
            {
                return tpdos.Count == 0
                    ? Outcome.NeedsSetup("PDO-01 read no TPDO parameters.")
                    : Outcome.NotApplicable("No TPDO has a synchronous transmission type (1800h:02 in 0..240), so a SYNC changes nothing observable.");
            }

            var watched = synchronous.Select(p => p.CobId).ToHashSet();
            using var tap = new BusTap(session.Service, id => watched.Contains(id));
            const int Syncs = 5;
            int quiet;
            int afterSync;
            string leftInState;
            try
            {
                await session.Client.SendNmtCommandAsync(NmtCommand.Start, session.Target, cancellationToken).ConfigureAwait(false);
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);

                quiet = tap.Frames.Count;
                for (var i = 0; i < Syncs; i++)
                {
                    await session.Client.SendSyncAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }

                afterSync = tap.Frames.Count - quiet;
            }
            finally
            {
                leftInState = await RestorePreOperationalAsync(session, book, "SYN-02").ConfigureAwait(false);
            }

            var measurements = new Dictionary<string, string>
            {
                ["synchronous TPDOs"] = string.Join(", ", synchronous.Select(p => $"TPDO{p.Number} 0x{p.CobId:X3} type {p.TransmissionType}")),
                ["SYNCs sent"] = Syncs.ToString(CultureInfo.InvariantCulture),
                ["frames before the SYNCs"] = quiet.ToString(CultureInfo.InvariantCulture),
                ["frames after the SYNCs"] = afterSync.ToString(CultureInfo.InvariantCulture),
                ["left in state"] = leftInState,
            };

            if (afterSync == 0)
            {
                return Outcome.Fail(
                    $"{Syncs} SYNC objects were sent and no synchronous TPDO followed. A device with a synchronous "
                    + "transmission type must transmit on SYNC.", measurements);
            }

            return Outcome.Pass($"{afterSync} synchronous TPDO frame(s) followed {Syncs} SYNC object(s).", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("EMC-01", "emcy", "Emergency objects on 80h+node-id", Tier.Read, "FR-CO-011", async () =>
        {
            // Passive only. There is no way to make a conforming device raise an emergency on
            // request, so this records what it sent in the window — and says so, rather than
            // reporting silence as a pass.
            var cobId = session.Options.TargetNode is { } target ? CanOpenCobId.Emcy(target) : 0u;
            using var tap = new BusTap(session.Service,
                id => session.Options.TargetNode is null
                    ? id > CanOpenCobId.EmcyBase && id < CanOpenCobId.EmcyBase + 128
                    : id == cobId);

            var decoded = new List<string>();
            void OnEmcy(object? sender, EmcyReceivedEventArgs e)
            {
                var m = e.Message;
                lock (decoded)
                {
                    decoded.Add($"node {m.ProducerNodeId}: error 0x{m.ErrorCode:X4}, register 0x{m.ErrorRegister:X2}, "
                        + $"manufacturer {IdentityAndSdoChecks.Hex(m.ManufacturerSpecific)}");
                }
            }

            session.Client.EmcyReceived += OnEmcy;
            try
            {
                await tap.ListenAsync(session.Options.ListenMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.Client.EmcyReceived -= OnEmcy;
            }

            var frames = tap.Frames;
            var measurements = new Dictionary<string, string>
            {
                ["window"] = $"{session.Options.ListenMs} ms",
                ["frames"] = frames.Count.ToString(CultureInfo.InvariantCulture),
                ["decoded by the stack"] = decoded.Count.ToString(CultureInfo.InvariantCulture),
            };
            foreach (var (text, i) in decoded.Take(10).Select((t, i) => (t, i)))
                measurements[$"emergency {i + 1}"] = text;

            if (frames.Count == 0)
            {
                return Outcome.Inconclusive(
                    $"No emergency object in {session.Options.ListenMs} ms. That is the normal state of a healthy "
                    + "device and not evidence that it can produce one — this check cannot provoke an emergency.",
                    Attribution.None, measurements);
            }

            var malformed = frames.Count(f => f.Data.Length != 8);
            if (malformed > 0) measurements["payload not 8 bytes"] = malformed.ToString(CultureInfo.InvariantCulture);

            if (malformed == frames.Count)
                return Outcome.Fail($"All {frames.Count} frame(s) on the emergency COB-ID have a payload other than 8 bytes.", measurements);

            return decoded.Count == 0
                ? Outcome.ToolFault(
                    $"{frames.Count} well-formed emergency frame(s) were on the bus and the stack decoded none of them.",
                    measurements)
                : Outcome.Pass($"{decoded.Count} emergency object(s) received and decoded.", measurements);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the device back in pre-operational, whatever happened above. Never cancellable: this
    /// is the step that stops a conformance run from leaving a machine stopped, so it must run on
    /// the way out of a cancelled check as much as a successful one. A failure here is recorded
    /// rather than thrown — it would otherwise replace the finding the check had just reached.
    /// </summary>
    private static async Task<string> RestorePreOperationalAsync(Session session, Checkbook book, string checkId)
    {
        try
        {
            await session.Client.SendNmtCommandAsync(NmtCommand.EnterPreOperational, session.Target, CancellationToken.None)
                .ConfigureAwait(false);
            return "pre-operational";
        }
        catch (Exception ex)
        {
            book.Note($"{checkId} restore to pre-operational", ex);
            return $"UNKNOWN — the restore to pre-operational failed ({ex.GetType().Name}); the device may still be stopped";
        }
    }

    /// <summary>One PDO as its own parameters describe it.</summary>
    private readonly record struct PdoDescriptor(
        int Number, uint CobId, bool Valid, byte TransmissionType, int InhibitMs, int EventTimerMs, int Bytes, int MappedEntries);

    private readonly record struct PdoSweep(List<PdoDescriptor> Descriptors, List<string> Blocked, List<string> Absent, List<string> Problems);

    /// <summary>
    /// Reads the communication and mapping parameters of up to <see cref="PdoCount"/> PDOs. The
    /// COB-ID comes from the device rather than from <see cref="CanOpenCobId.TpdoDefault"/>,
    /// because a remapped device is exactly the case a default would get wrong.
    /// </summary>
    private static async Task<PdoSweep> ReadPdosAsync(Session session, ushort commBase, ushort mapBase, CancellationToken cancellationToken)
    {
        var descriptors = new List<PdoDescriptor>();
        var blocked = new List<string>();
        var absent = new List<string>();
        var problems = new List<string>();

        for (var i = 0; i < PdoCount; i++)
        {
            var comm = (ushort)(commBase + i);
            var map = (ushort)(mapBase + i);
            if (!session.MayTransfer(comm, 0x01))
            {
                blocked.Add($"0x{comm:X4}");
                continue;
            }

            uint cobIdWord;
            try
            {
                var raw = await session.Client.SdoUploadAsync(session.Target, comm, 0x01, cancellationToken).ConfigureAwait(false);
                if (raw.Length != 4)
                {
                    problems.Add($"0x{comm:X4}:01 is UNSIGNED32, got {IdentityAndSdoChecks.Hex(raw)}");
                    continue;
                }

                cobIdWord = BitConverter.ToUInt32(raw, 0);
            }
            catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist or (uint)SdoAbortCode.SubIndexDoesNotExist)
            {
                absent.Add($"0x{comm:X4}");
                continue;
            }

            var transmission = await ByteOrDefault(session, comm, 0x02, cancellationToken).ConfigureAwait(false);
            var inhibit = await UShortOrDefault(session, comm, 0x03, cancellationToken).ConfigureAwait(false);
            var eventTimer = await UShortOrDefault(session, comm, 0x05, cancellationToken).ConfigureAwait(false);

            var (bits, entries) = await ReadMappingAsync(session, map, problems, cancellationToken).ConfigureAwait(false);

            descriptors.Add(new PdoDescriptor(
                Number: i + 1,
                CobId: cobIdWord & CanOpenCobId.CanIdMask,
                Valid: (cobIdWord & CanOpenCobId.InvalidBit) == 0,
                TransmissionType: transmission ?? 0xFF,
                InhibitMs: inhibit is { } inh ? inh / 10 : 0, // 1800h:03 counts 100 us units
                EventTimerMs: eventTimer ?? 0,
                Bytes: (bits + 7) / 8,
                MappedEntries: entries));
        }

        return new PdoSweep(descriptors, blocked, absent, problems);
    }

    private static async Task<(int Bits, int Entries)> ReadMappingAsync(
        Session session, ushort map, List<string> problems, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(map, 0x00)) return (0, 0);
        try
        {
            var count = await session.Client.SdoUploadAsync(session.Target, map, 0x00, cancellationToken).ConfigureAwait(false);
            if (count.Length != 1) return (0, 0);

            var bits = 0;
            for (byte sub = 1; sub <= count[0] && sub <= 64; sub++)
            {
                if (!session.MayTransfer(map, sub)) continue;
                var entry = await session.Client.SdoUploadAsync(session.Target, map, sub, cancellationToken).ConfigureAwait(false);
                if (entry.Length != 4)
                {
                    problems.Add($"0x{map:X4}:{sub:X2} is UNSIGNED32, got {IdentityAndSdoChecks.Hex(entry)}");
                    continue;
                }

                bits += entry[0]; // the low byte of a mapping entry is its bit length
            }

            return (bits, count[0]);
        }
        catch (SdoAbortException)
        {
            return (0, 0);
        }
    }

    private static async Task<byte?> ByteOrDefault(Session session, ushort index, byte sub, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(index, sub)) return null;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, index, sub, cancellationToken).ConfigureAwait(false);
            return raw.Length == 1 ? raw[0] : null;
        }
        catch (SdoAbortException)
        {
            return null;
        }
    }

    private static async Task<int?> UShortOrDefault(Session session, ushort index, byte sub, CancellationToken cancellationToken)
    {
        if (!session.MayTransfer(index, sub)) return null;
        try
        {
            var raw = await session.Client.SdoUploadAsync(session.Target, index, sub, cancellationToken).ConfigureAwait(false);
            return raw.Length == 2 ? BitConverter.ToUInt16(raw, 0) : null;
        }
        catch (SdoAbortException)
        {
            return null;
        }
    }

    private static Outcome Summarise(string kind, PdoSweep sweep, Session session)
    {
        var measurements = new Dictionary<string, string>();
        foreach (var pdo in sweep.Descriptors)
        {
            measurements[$"{kind}{pdo.Number}"] =
                $"COB-ID 0x{pdo.CobId:X3}{(pdo.Valid ? string.Empty : " (invalid)")}, "
                + $"type {DescribeTransmission(pdo.TransmissionType)}, "
                + $"{pdo.MappedEntries} entry/entries = {pdo.Bytes} B"
                + (pdo.EventTimerMs > 0 ? $", event timer {pdo.EventTimerMs} ms" : string.Empty)
                + (pdo.InhibitMs > 0 ? $", inhibit {pdo.InhibitMs} ms" : string.Empty);
        }

        if (sweep.Blocked.Count > 0) measurements["blocked by the peer-SDO gate"] = string.Join(", ", sweep.Blocked);
        if (sweep.Absent.Count > 0) measurements["not implemented"] = string.Join(", ", sweep.Absent);
        foreach (var (problem, i) in sweep.Problems.Take(10).Select((p, i) => (p, i)))
            measurements[$"problem {i + 1}"] = problem;

        if (sweep.Problems.Count > 0)
            return Outcome.Fail($"{sweep.Descriptors.Count} {kind}(s) read; {sweep.Problems.Count} parameter(s) have the wrong length.", measurements);

        if (sweep.Descriptors.Count == 0)
        {
            return sweep.Blocked.Count > 0
                ? Outcome.NeedsSetup($"Every {kind} parameter is blocked: {session.GateReason((ushort)(kind == "TPDO" ? 0x1800 : 0x1400), 0x01)}")
                : Outcome.NotApplicable($"The device implements none of the first {PdoCount} {kind}s.");
        }

        var valid = sweep.Descriptors.Count(p => p.Valid);
        return Outcome.Pass($"{sweep.Descriptors.Count} {kind}(s) read, {valid} valid.", measurements);
    }

    private static string DescribeTransmission(byte type) => type switch
    {
        CanOpenTransmissionType.SynchronousAcyclic => "0 (synchronous, acyclic)",
        <= CanOpenTransmissionType.SynchronousCyclicMax => $"{type} (synchronous, every {type} SYNC)",
        CanOpenTransmissionType.RtrOnlySynchronous => "252 (RTR only, synchronous)",
        CanOpenTransmissionType.RtrOnlyEventDriven => "253 (RTR only, event-driven)",
        CanOpenTransmissionType.EventDrivenManufacturer => "254 (event-driven, manufacturer)",
        CanOpenTransmissionType.EventDrivenProfile => "255 (event-driven, profile)",
        _ => $"{type} (reserved)",
    };
}
