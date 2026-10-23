using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CanOpenConformance.Checks;

/// <summary>
/// The device against its own EDS or DCF. This is the group that makes a device description worth
/// passing in: every readable object the file declares is uploaded from the device and compared
/// with what the file says about it, and the file is reported as the thing that can be wrong just
/// as readily as the device.
/// </summary>
internal static class EdsChecks
{
    /// <summary>How many objects a full sweep reads before it stops. A sweep is one SDO transfer
    /// per sub-index, and a large device description would otherwise turn a conformance run into
    /// a quarter of an hour on a 250 kbit/s bus.</summary>
    private const int SweepLimit = 400;

    public static async Task RunAsync(Session session, Checkbook book, CancellationToken cancellationToken)
    {
        await book.RunAsync("EDS-01", "eds", "The device description parses", Tier.Read, "FR-CO-025", () =>
        {
            if (session.DescriptionError is { } problem)
                return Task.FromResult(Outcome.NeedsSetup(problem));
            if (session.Options.EdsPath is null)
                return Task.FromResult(Outcome.NeedsSetup("No --eds given."));
            if (session.TargetDescription is not { } description)
                return Task.FromResult(Outcome.NeedsSetup("No --node given, so nothing was bound."));

            var measurements = new Dictionary<string, string>
            {
                ["file"] = Path.GetFileName(session.Options.EdsPath),
                ["kind"] = description.IsConfigurationFile ? "DCF (commissioned)" : "EDS (template)",
                ["objects declared"] = description.Objects.Objects.Count.ToString(CultureInfo.InvariantCulture),
                ["product name"] = description.DeviceInfo.ProductName ?? "(none)",
                ["vendor name"] = description.DeviceInfo.VendorName ?? "(none)",
                // What the file claims the device can do. The lss and pdo groups read these to
                // tell "the device does not support it" apart from "it does and did not answer".
                ["LSS supported"] = description.DeviceInfo.LssSupported.ToString(),
                ["TPDOs declared"] = description.DeviceInfo.NrOfTxPdo.ToString(CultureInfo.InvariantCulture),
                ["RPDOs declared"] = description.DeviceInfo.NrOfRxPdo.ToString(CultureInfo.InvariantCulture),
            };
            if (description.NodeId is { } commissioned)
                measurements["commissioned node-id"] = commissioned.ToString(CultureInfo.InvariantCulture);

            var diagnostics = description.ParseDiagnostics;
            if (diagnostics.Count > 0)
            {
                measurements["parse diagnostics"] = diagnostics.Count.ToString(CultureInfo.InvariantCulture);
                foreach (var group in diagnostics.Take(10).Select((d, i) => (d, i)))
                    measurements[$"diagnostic {group.i + 1}"] = group.d.ToString();
            }

            // A file the loader had to correct is a finding about the file, and it is the file the
            // operator can fix. It does not stop the run: the corrected dictionary is still the
            // best description of the device available.
            return Task.FromResult(diagnostics.Count == 0
                ? Outcome.Pass($"{description.Objects.Objects.Count} objects declared, no parse diagnostics.", measurements)
                : Outcome.Inconclusive(
                    $"{description.Objects.Objects.Count} objects declared; the loader raised {diagnostics.Count} parse "
                    + "diagnostic(s) about the file. The run continues against the corrected dictionary.",
                    Attribution.Setup, measurements));
        }).ConfigureAwait(false);

        await book.RunAsync("EDS-02", "eds", "Identity in the file matches the device", Tier.Read, "CiA 306 / 1018h", async () =>
        {
            if (session.TargetDescription is not { } description)
                return Outcome.NeedsSetup(session.DescriptionError ?? "No --eds given.");

            // The check that catches the commonest mistake in a conformance run: the wrong EDS.
            // Everything else in this group is measured against the file, so a file for a
            // different device makes every one of those findings meaningless.
            var declared = new Dictionary<byte, uint?>();
            for (byte sub = 1; sub <= 4; sub++)
                declared[sub] = DeclaredUnsigned(description, 0x1018, sub);

            var names = new[] { "", "vendor-id", "product code", "revision number", "serial number" };
            var measurements = new Dictionary<string, string>();
            var mismatches = new List<string>();
            var unreadable = new List<string>();
            var compared = 0;
            for (byte sub = 1; sub <= 4; sub++)
            {
                if (declared[sub] is not { } expected) continue;
                try
                {
                    var raw = await session.Client.SdoUploadAsync(session.Target, 0x1018, sub, cancellationToken)
                        .ConfigureAwait(false);
                    if (raw.Length != 4) continue;
                    var actual = BitConverter.ToUInt32(raw, 0);
                    compared++;
                    measurements[names[sub]] = $"file 0x{expected:X8}, device 0x{actual:X8}";
                    // The serial number is per unit: a template EDS carries 0 for it, and a
                    // difference there says nothing about whether the file fits the device.
                    if (actual != expected && !(sub == 4 && expected == 0))
                        mismatches.Add($"{names[sub]} (file 0x{expected:X8}, device 0x{actual:X8})");
                }
                catch (SdoAbortException abort)
                {
                    measurements[names[sub]] = $"file 0x{expected:X8}, device aborted {IdentityAndSdoChecks.Describe(abort)}";
                    unreadable.Add($"{names[sub]} ({IdentityAndSdoChecks.Describe(abort)})");
                }
            }

            // Order matters here: an earlier version returned "the file declares no 1018h values"
            // whenever nothing compared, which is what a device that aborted every declared value
            // produced — blaming the file for the device's refusal. Found in review of #388.
            if (unreadable.Count > 0 && compared == 0)
            {
                return Outcome.Inconclusive(
                    $"The file declares 1018h values, and the device would not return any of them "
                    + $"({string.Join(", ", unreadable)}). Nothing was compared, so whether the file is for this device "
                    + "is unknown — and this is the device's answer, not a gap in the file.",
                    Attribution.Device, measurements);
            }

            if (compared == 0)
                return Outcome.NeedsSetup("The file declares no 1018h values to compare (an EDS template often does not).");

            // A declared value the device would not hand over leaves the comparison unfinished, and
            // "1 identity value(s) agree" is not a statement that the file fits the device. An
            // earlier version passed on exactly that. Found in review of #388.
            if (mismatches.Count == 0 && unreadable.Count > 0)
            {
                return Outcome.Inconclusive(
                    $"{compared} declared identity value(s) agree, but {unreadable.Count} the file declares could not "
                    + $"be read from the device ({string.Join(", ", unreadable)}). The check has not established that "
                    + "the file is for this device, so nothing in the eds group should be read as confirmed.",
                    Attribution.Device, measurements);
            }

            return mismatches.Count == 0
                ? Outcome.Pass($"all {compared} declared identity value(s) agree.", measurements)
                : Outcome.Failed(Attribution.Setup,
                    $"The file is for a different device: {string.Join(", ", mismatches)}. Every other finding in the "
                    + "eds group was measured against this file and should be discarded — pass the device's own EDS "
                    + "or DCF and run again.", measurements);
        }).ConfigureAwait(false);

        await book.RunAsync("EDS-03", "eds", "Every readable declared object exists on the device", Tier.Read, "CiA 306", async () =>
        {
            if (session.TargetDescription is not { } description)
                return Outcome.NeedsSetup(session.DescriptionError ?? "No --eds given.");

            var readable = description.Objects.Objects
                .SelectMany(Pairs)
                .Where(p => p.Access.HasFlag(OdAccess.ReadOnly))
                .OrderBy(p => p.Index).ThenBy(p => p.Subindex)
                .ToList();
            if (readable.Count == 0) return Outcome.NeedsSetup("The file declares no readable objects.");

            var sweep = readable.Take(SweepLimit).ToList();
            var absent = new List<string>();
            var lengthMismatch = new List<string>();
            var refused = new List<string>();
            var ok = 0;

            foreach (var pair in sweep)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var raw = await session.Client.SdoUploadAsync(session.Target, pair.Index, pair.Subindex, cancellationToken)
                        .ConfigureAwait(false);
                    // A declared length of 0 is how a file says "variable" (strings, domains);
                    // only a fixed, known length is something the device can contradict.
                    if (pair.Bytes > 0 && raw.Length != pair.Bytes)
                        lengthMismatch.Add($"0x{pair.Index:X4}:{pair.Subindex:X2} file {pair.Bytes} B, device {raw.Length} B");
                    else
                        ok++;
                }
                catch (SdoAbortException abort) when (abort.AbortCode is (uint)SdoAbortCode.ObjectDoesNotExist or (uint)SdoAbortCode.SubIndexDoesNotExist)
                {
                    absent.Add($"0x{pair.Index:X4}:{pair.Subindex:X2}");
                }
                catch (SdoAbortException abort)
                {
                    refused.Add($"0x{pair.Index:X4}:{pair.Subindex:X2} {IdentityAndSdoChecks.Describe(abort)}");
                }
            }

            var measurements = new Dictionary<string, string>
            {
                ["declared readable"] = readable.Count.ToString(CultureInfo.InvariantCulture),
                ["read"] = sweep.Count.ToString(CultureInfo.InvariantCulture),
                ["as declared"] = ok.ToString(CultureInfo.InvariantCulture),
                ["absent on device"] = absent.Count.ToString(CultureInfo.InvariantCulture),
                ["length mismatch"] = lengthMismatch.Count.ToString(CultureInfo.InvariantCulture),
                ["other abort"] = refused.Count.ToString(CultureInfo.InvariantCulture),
            };
            if (readable.Count > SweepLimit)
                measurements["not read"] = $"{readable.Count - SweepLimit} (--groups eds reads at most {SweepLimit})";
            Enumerate(measurements, "absent", absent);
            Enumerate(measurements, "length", lengthMismatch);
            Enumerate(measurements, "abort", refused);

            // A pass here must mean the sweep established something, and it cannot while objects
            // went unanswered. An earlier version counted those aborts and then ignored them in
            // this condition, so a device that aborted every declared object got a pass reading
            // "All 0 readable declared object(s) read back" — a sweep that measured nothing,
            // reported as a clean one. Found in review of #388.
            if (absent.Count == 0 && lengthMismatch.Count == 0 && refused.Count > 0)
            {
                return Outcome.Inconclusive(
                    $"{refused.Count} of {sweep.Count} declared readable object(s) aborted for a reason other than "
                    + $"\"does not exist\", so the sweep did not establish whether the device matches the file; "
                    + $"{ok} did read back as declared. An abort such as a protocol timeout or a refused read is the "
                    + "device's answer, not the file's.", Attribution.Device, measurements);
            }

            if (absent.Count == 0 && lengthMismatch.Count == 0)
            {
                if (ok == 0)
                {
                    return Outcome.Inconclusive(
                        "The sweep read no object at all, so nothing was established.", Attribution.Unknown, measurements);
                }

                // A truncated sweep cannot say the description matched: everything past the limit
                // was never asked, and "Every readable declared object exists" under *What works*
                // would be a claim about objects nobody queried. Found in review of #388.
                if (readable.Count > SweepLimit)
                {
                    return Outcome.Inconclusive(
                        $"The {ok} object(s) that were read all matched, but the file declares {readable.Count} readable "
                        + $"pairs and this sweep reads at most {SweepLimit}. The remaining {readable.Count - SweepLimit} "
                        + "were never asked for, so the description as a whole is not confirmed.",
                        Attribution.Setup, measurements);
                }

                return Outcome.Pass($"All {ok} readable declared object(s) read back at the declared length.", measurements);
            }

            return Outcome.Failed(Attribution.Unknown,
                $"{absent.Count} declared object(s) do not exist on the device and {lengthMismatch.Count} have a "
                + "different length. One of the two is wrong — the file or the firmware — and nothing measured here "
                + "says which, so the attribution stays open: check the file's version against the device's 1018h:03 "
                + "revision before taking it to either.", measurements);
        }).ConfigureAwait(false);
    }

    private static void Enumerate(Dictionary<string, string> measurements, string label, List<string> items)
    {
        foreach (var (item, i) in items.Take(15).Select((item, i) => (item, i)))
            measurements[$"{label} {i + 1}"] = item;
        if (items.Count > 15) measurements[$"{label} ..."] = $"and {items.Count - 15} more";
    }

    /// <summary>Flattens one declared object into the (index, sub-index) pairs an SDO addresses,
    /// with the access and the declared byte length the device is compared against. The shape
    /// follows the stack's own loader: no sub-objects means the object is addressed at sub-index 0.</summary>
    private static IEnumerable<DeclaredPair> Pairs(KeyValuePair<ushort, EdsDcfNet.Models.CanOpenObject> entry)
    {
        var index = entry.Key;
        var obj = entry.Value;
        if (obj.SubObjects.Count > 0)
        {
            foreach (var sub in obj.SubObjects.Values)
                yield return new DeclaredPair(index, sub.SubIndex, Access(sub.AccessType), DataTypeBytes(sub.DataType));
            yield break;
        }

        yield return new DeclaredPair(index, 0x00, Access(obj.AccessType), DataTypeBytes(obj.DataType));
    }

    private static uint? DeclaredUnsigned(CanOpenDeviceDescription description, ushort index, byte subindex)
    {
        if (!description.Objects.Objects.TryGetValue(index, out var obj)) return null;
        string? text;
        if (obj.SubObjects.Count > 0)
        {
            if (!obj.SubObjects.TryGetValue(subindex, out var sub)) return null;
            // A DCF carries the commissioned value in ParameterValue and the template default in
            // DefaultValue; the loader prefers the former, and so does this comparison.
            text = string.IsNullOrEmpty(sub.ParameterValue) ? sub.DefaultValue : sub.ParameterValue;
        }
        else
        {
            if (subindex != 0) return null;
            text = string.IsNullOrEmpty(obj.ParameterValue) ? obj.DefaultValue : obj.ParameterValue;
        }

        return ParseUnsigned(text);
    }

    internal static uint? ParseUnsigned(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text!.Trim();
        // EDS files write hexadecimal as 0x..., and the older $ form still appears in the wild.
        var hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("$", StringComparison.Ordinal);
        var digits = trimmed.TrimStart('$');
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
        if (hex)
            return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return uint.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>ro and const are the same thing on the bus, as the stack's own MapAccess has it.</summary>
    private static OdAccess Access(EdsDcfNet.Models.AccessType access) => access switch
    {
        EdsDcfNet.Models.AccessType.WriteOnly => OdAccess.WriteOnly,
        EdsDcfNet.Models.AccessType.ReadWrite
            or EdsDcfNet.Models.AccessType.ReadWriteInput
            or EdsDcfNet.Models.AccessType.ReadWriteOutput => OdAccess.ReadWrite,
        _ => OdAccess.ReadOnly,
    };

    /// <summary>
    /// The length CiA 301 fixes for a data type, or 0 where the type does not fix one — strings,
    /// octet strings, domains and records. Only a fixed length is something the device can
    /// contradict, so 0 means "do not compare", not "zero bytes".
    /// </summary>
    private static int DataTypeBytes(ushort? dataType) => dataType switch
    {
        null or 0 => 0,
        EdsDcfNet.CanOpenDataType.Boolean => 1,
        EdsDcfNet.CanOpenDataType.Integer8 or EdsDcfNet.CanOpenDataType.Unsigned8 => 1,
        EdsDcfNet.CanOpenDataType.Integer16 or EdsDcfNet.CanOpenDataType.Unsigned16 => 2,
        EdsDcfNet.CanOpenDataType.Integer24 or EdsDcfNet.CanOpenDataType.Unsigned24 => 3,
        EdsDcfNet.CanOpenDataType.Integer32 or EdsDcfNet.CanOpenDataType.Unsigned32 => 4,
        EdsDcfNet.CanOpenDataType.Real32 => 4,
        EdsDcfNet.CanOpenDataType.Integer40 or EdsDcfNet.CanOpenDataType.Unsigned40 => 5,
        EdsDcfNet.CanOpenDataType.Integer48 or EdsDcfNet.CanOpenDataType.Unsigned48 => 6,
        EdsDcfNet.CanOpenDataType.Integer56 or EdsDcfNet.CanOpenDataType.Unsigned56 => 7,
        EdsDcfNet.CanOpenDataType.Integer64 or EdsDcfNet.CanOpenDataType.Unsigned64 => 8,
        EdsDcfNet.CanOpenDataType.Real64 => 8,
        _ => 0,
    };

    private readonly record struct DeclaredPair(ushort Index, byte Subindex, OdAccess Access, int Bytes);
}
