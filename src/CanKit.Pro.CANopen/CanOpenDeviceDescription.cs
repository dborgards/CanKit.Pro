using System;
using System.Collections.Generic;
using System.IO;
using EdsDcfNet;
using EdsDcfNet.Diagnostics;
using EdsDcfNet.Models;

namespace CanKit.Pro.CANopen;

/// <summary>
/// A CiA 306 device description — an EDS (electronic data sheet) or a DCF (device configuration
/// file) — read with the maintainer's <c>EdsDcfNet</c> library and ready to be applied to a
/// node: <see cref="CanOpen.OpenNode(CanKit.Abstractions.API.Can.ICanBus, byte, CanOpenDeviceDescription, CanOpenNodeOptions?)"/>
/// creates the node's object dictionary and its PDO, SYNC, EMCY, heartbeat and guarding
/// configuration from it, and reports in <see cref="DeviceDescriptionReport"/> what it could not
/// take as written.
/// </summary>
/// <remarks>
/// The description is the source of the node's power-on values: an NMT Reset Communication
/// returns the communication profile area to what the file says, an NMT Reset Node the
/// application objects as well, unless the application stored other values since
/// (<see cref="ICanOpenNode.StoreParameters"/>). A DCF's <c>ParameterValue</c> takes precedence
/// over the <c>DefaultValue</c> of the EDS it was made from, and its <c>NodeID</c> is the node-id
/// <see cref="CanOpen.OpenNode(CanKit.Abstractions.API.Can.ICanBus, CanOpenDeviceDescription, CanOpenNodeOptions?)"/>
/// uses. <c>$NODEID+…</c> expressions are evaluated against the node-id the node is opened with.
/// The same type is what <see cref="ICanOpenNode.BindPeerDeviceDescription"/> binds for a remote
/// node: the SDO client then transfers only pairs <see cref="Contains"/> reports. A DCF binds
/// only to the node-id it was commissioned for; an EDS binds to any node.
/// </remarks>
public sealed class CanOpenDeviceDescription
{
    private CanOpenDeviceDescription(ElectronicDataSheet? eds, DeviceConfigurationFile? dcf,
        IReadOnlyList<ParseDiagnostic> parseDiagnostics)
    {
        Eds = eds;
        Dcf = dcf;
        ParseDiagnostics = parseDiagnostics;
    }

    /// <summary>The EDS model, when the description is an EDS.</summary>
    public ElectronicDataSheet? Eds { get; }

    /// <summary>The DCF model, when the description is a DCF.</summary>
    public DeviceConfigurationFile? Dcf { get; }

    /// <summary>The object dictionary the description declares (the EdsDcfNet model, not the
    /// node's <see cref="CanKit.Pro.CANopen.ObjectDictionary"/>).</summary>
    public EdsDcfNet.Models.ObjectDictionary Objects => Eds?.ObjectDictionary ?? Dcf!.ObjectDictionary;

    /// <summary>The device information section of the description.</summary>
    public DeviceInfo DeviceInfo => Eds?.DeviceInfo ?? Dcf!.DeviceInfo;

    /// <summary>The node-id a DCF was commissioned for (<c>[DeviceComissioning] NodeID</c>);
    /// <see langword="null"/> for an EDS, which describes a device type rather than a node.</summary>
    public byte? NodeId => Dcf?.DeviceCommissioning.NodeId;

    /// <summary>Whether the description is a DCF (a commissioned device) rather than an EDS.</summary>
    public bool IsConfigurationFile => Dcf is not null;

    /// <summary>What the parser repaired or tolerated while reading the file (lenient mode);
    /// empty when the file was clean. These describe the file, not this node.</summary>
    public IReadOnlyList<ParseDiagnostic> ParseDiagnostics { get; }

    /// <summary>Wraps an already-parsed EDS.</summary>
    public static CanOpenDeviceDescription FromEds(ElectronicDataSheet eds)
        => new(eds ?? throw new ArgumentNullException(nameof(eds)), null, Array.Empty<ParseDiagnostic>());

    /// <summary>Wraps an already-parsed DCF.</summary>
    public static CanOpenDeviceDescription FromDcf(DeviceConfigurationFile dcf)
        => new(null, dcf ?? throw new ArgumentNullException(nameof(dcf)), Array.Empty<ParseDiagnostic>());

    /// <summary>Reads an EDS or DCF file; the extension decides which (<c>.dcf</c> is a DCF,
    /// anything else an EDS).</summary>
    /// <exception cref="EdsDcfNet.Exceptions.EdsParseException">The file is not a readable description.</exception>
    public static CanOpenDeviceDescription Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (string.Equals(Path.GetExtension(path), ".dcf", StringComparison.OrdinalIgnoreCase))
        {
            var dcf = CanOpenFile.Dcf.ReadFileWithDiagnostics(path);
            return new CanOpenDeviceDescription(null, dcf.Model, dcf.Diagnostics);
        }
        var eds = CanOpenFile.Eds.ReadFileWithDiagnostics(path);
        return new CanOpenDeviceDescription(eds.Model, null, eds.Diagnostics);
    }

    /// <summary>Parses EDS content.</summary>
    public static CanOpenDeviceDescription ParseEds(string content)
    {
        var eds = CanOpenFile.Eds.ReadStringWithDiagnostics(content ?? throw new ArgumentNullException(nameof(content)));
        return new CanOpenDeviceDescription(eds.Model, null, eds.Diagnostics);
    }

    /// <summary>Parses DCF content.</summary>
    public static CanOpenDeviceDescription ParseDcf(string content)
    {
        var dcf = CanOpenFile.Dcf.ReadStringWithDiagnostics(content ?? throw new ArgumentNullException(nameof(content)));
        return new CanOpenDeviceDescription(null, dcf.Model, dcf.Diagnostics);
    }

    /// <summary>
    /// Whether the description declares <paramref name="index"/>:<paramref name="subindex"/>.
    /// </summary>
    /// <remarks>
    /// A variable or domain (no sub-objects) is sub-index 0. A record or array is the sub-indices
    /// the file actually carries, including those compact storage synthesized. A sub-index the
    /// file does not list is absent even when the index itself is present.
    /// </remarks>
    public bool Contains(ushort index, byte subindex)
    {
        if (!Objects.Objects.TryGetValue(index, out var obj)) return false;
        if (obj.SubObjects.Count == 0) return subindex == 0;
        return obj.SubObjects.ContainsKey(subindex);
    }
}

/// <summary>What the node did with an entry of a device description it could not take as written.</summary>
public enum DeviceDescriptionOutcome
{
    /// <summary>The entry was not created: its data type, its value or its position is one the
    /// node cannot represent (a 24/40/48/56-bit integer, a fifth PDO, an unparsable value, a
    /// sub-index of a fixed record it does not implement).</summary>
    Omitted,

    /// <summary>The entry exists with a value the node can implement instead of the one the
    /// description gave (the CiA 301 default it was created with), because the described value
    /// was rejected by the same rule an SDO download would have hit.</summary>
    Corrected,

    /// <summary>The PDO stays destroyed (bit 31 of its COB-ID set) because its communication
    /// record or its mapping could not be applied as described.</summary>
    PdoDisabled,

    /// <summary>The entry exists with the described value, but the node implements no
    /// behaviour behind it: a master can read and write it and nothing follows.</summary>
    NotImplemented,

    /// <summary>A mandatory object (CiA 301 §7.5.2: <c>1000h</c>, <c>1001h</c>, <c>1018h</c>) the
    /// description does not declare; the node keeps its placeholder.</summary>
    SuppliedDefault,
}

/// <summary>One entry of a device description the node could not take as written, and what it
/// did instead.</summary>
public sealed class DeviceDescriptionFinding
{
    /// <summary>Constructs a finding.</summary>
    public DeviceDescriptionFinding(ushort index, byte subindex, DeviceDescriptionOutcome outcome,
        string reason, string? describedValue = null, Sdo.SdoAbortCode? abortCode = null)
    {
        Index = index;
        Subindex = subindex;
        Outcome = outcome;
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        DescribedValue = describedValue;
        AbortCode = abortCode;
    }

    /// <summary>Object index.</summary>
    public ushort Index { get; }

    /// <summary>Sub-index (0 for a VAR object).</summary>
    public byte Subindex { get; }

    /// <summary>What the node did.</summary>
    public DeviceDescriptionOutcome Outcome { get; }

    /// <summary>Why, in words.</summary>
    public string Reason { get; }

    /// <summary>The value the description gave, as written in the file, when the finding is
    /// about a value.</summary>
    public string? DescribedValue { get; }

    /// <summary>The SDO abort code the value was rejected with, when a validation rule rejected it —
    /// the same code a master writing that value would receive.</summary>
    public Sdo.SdoAbortCode? AbortCode { get; }

    /// <inheritdoc />
    public override string ToString()
        => $"0x{Index:X4}:{Subindex:X2} {Outcome}: {Reason}"
           + (DescribedValue is null ? "" : $" (described: {DescribedValue})")
           + (AbortCode is { } code ? $" [SDO abort 0x{(uint)code:X8}]" : "");
}

/// <summary>
/// The result of applying a <see cref="CanOpenDeviceDescription"/> to a node: what was loaded and
/// every entry that was degraded, omitted or supplied. A description whose every entry the node
/// implements as written has no <see cref="Findings"/>.
/// </summary>
public sealed class DeviceDescriptionReport
{
    internal DeviceDescriptionReport(CanOpenDeviceDescription description, byte nodeId, int entriesLoaded,
        IReadOnlyList<DeviceDescriptionFinding> findings)
    {
        Description = description;
        NodeId = nodeId;
        EntriesLoaded = entriesLoaded;
        Findings = findings;
    }

    /// <summary>The description that was applied.</summary>
    public CanOpenDeviceDescription Description { get; }

    /// <summary>The node-id the description was applied for (the one <c>$NODEID</c> resolved to).</summary>
    public byte NodeId { get; }

    /// <summary>The number of <c>(index, sub-index)</c> entries the dictionary took from the description.</summary>
    public int EntriesLoaded { get; }

    /// <summary>Everything the node could not take as written, in index order.</summary>
    public IReadOnlyList<DeviceDescriptionFinding> Findings { get; }

    /// <summary>Whether every entry was applied as described.</summary>
    public bool IsExact => Findings.Count == 0;
}
