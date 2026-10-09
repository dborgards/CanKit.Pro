using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using EdsDcfNet;
using EdsDcfNet.Models;
using EdsDcfNet.Utilities;

namespace CanKit.Pro.CANopen;

/// <summary>
/// The device-description loader of <see cref="CanOpenNode"/> (scope items 1, 2, 13 and 26 of
/// docs/reviews/2026-09-15-canopen-scope.md): an EDS or DCF shapes the object dictionary —
/// application objects are created from it, the managed communication objects take its access
/// rights and values through the same validated write path an SDO download uses — and the
/// runtime follows the dictionary as always. What the node cannot implement as written is
/// degraded, and the degradation is corrected in the dictionary and reported, never silently
/// ignored.
/// </summary>
/// <remarks>
/// <para>
/// Order of work: the mandatory objects the description lacks are noted; PDO records it does not
/// declare are removed; every object other than a PDO record is applied in index order (the
/// application objects a mapping names among them), then the PDO communication records with
/// the PDO destroyed, then the mapping records, and last the "create PDO" step (bit 31 of the
/// COB-ID cleared) — steps 1 to 5 of CiA 301 §7.5.2.38; the loaded state then becomes the
/// power-on state and the defaults a "load" (1011h) returns to.
/// </para>
/// <para>
/// Runs on the constructing thread before the node's boot-up is posted, so a master sees the
/// described device from the first frame. The individual applies it posts to the actor are
/// followed by the constructor's full rebuild, which is what makes the runtime match.
/// </para>
/// </remarks>
internal sealed partial class CanOpenNode
{
    private DeviceDescriptionReport? _deviceDescription;

    /// <inheritdoc />
    public DeviceDescriptionReport? DeviceDescription => _deviceDescription;

    private static readonly ushort[] MandatoryObjects = { Co.DeviceType, Co.ErrorRegister, Co.Identity };

    private void ApplyDeviceDescription(CanOpenDeviceDescription description)
    {
        var findings = new List<DeviceDescriptionFinding>();
        var objects = description.Objects.Objects;
        int loaded = 0;

        foreach (var mandatory in MandatoryObjects.Where(m => !objects.ContainsKey(m)))
        {
            findings.Add(new DeviceDescriptionFinding(mandatory, 0, DeviceDescriptionOutcome.SuppliedDefault,
                "CiA 301 §7.5.2 makes this object mandatory and the description does not declare it; the node keeps its placeholder"));
        }

        // §7.3.3: "A CANopen device shall provide the corresponding CAN-IDs only for the
        // supported communication objects" — a PDO the description does not declare does not exist.
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            foreach (var baseIndex in new[] { Co.RpdoComm, Co.RpdoMap, Co.TpdoComm, Co.TpdoMap })
            {
                var index = (ushort)(baseIndex + n - 1);
                if (!objects.ContainsKey(index)) RemoveObject(index);
            }
        }
        // The same for an SRDO (CiA DSP 304): a record the description does not declare does not
        // exist — except the ones the SrdoCount option asked for, which is a floor.
        for (int n = Math.Max(1, _options.SrdoCount + 1); n <= _srdoCount; n++)
        {
            if (!objects.ContainsKey(SrdoRecords.CommIndex(n))) RemoveObject(SrdoRecords.CommIndex(n));
            if (!objects.ContainsKey(SrdoRecords.MapIndex(n))) RemoveObject(SrdoRecords.MapIndex(n));
        }

        // A mapping names application objects, so every other object comes first, then the
        // PDO communication records (the PDO is destroyed while its mapping is written), then
        // the mapping records, then — below — the creates.
        var pendingPdoCreates = new List<(ushort CommIndex, uint Word, string Raw)>();
        var pendingSrdoCreates = new List<(int Srdo, byte Direction, string Raw)>();
        var failedMappings = new HashSet<ushort>();
        var ordered = objects.Values.OrderBy(o => PdoRecordRank(o.Index)).ThenBy(o => o.Index).ToList();
        foreach (var obj in ordered.Where(o => PdoRecordRank(o.Index) < ConfigurationValidRank))
        {
            loaded += ApplyDescribedObject(obj, description, findings, pendingPdoCreates, pendingSrdoCreates, failedMappings);
        }

        // Step 5 for every PDO the description creates, now that its mapping is in.
        foreach (var (commIndex, word, raw) in pendingPdoCreates)
        {
            var mapIndex = (ushort)(commIndex + 0x200);
            if (!objects.ContainsKey(mapIndex))
            {
                findings.Add(new DeviceDescriptionFinding(commIndex, 0x01, DeviceDescriptionOutcome.PdoDisabled,
                    $"the description declares no mapping record 0x{mapIndex:X4} for this PDO; a PDO exists only with both records (CiA 301 objects overview, footnote *)", raw));
                continue;
            }
            if (failedMappings.Contains(mapIndex))
            {
                findings.Add(new DeviceDescriptionFinding(commIndex, 0x01, DeviceDescriptionOutcome.PdoDisabled,
                    "the PDO stays destroyed because its mapping record could not be applied as described", raw));
                continue;
            }
            if (!_od.TryWriteRaw(commIndex, 0x01, ObjectDictionary.EncodeU32(word), out var abort))
            {
                findings.Add(new DeviceDescriptionFinding(commIndex, 0x01, DeviceDescriptionOutcome.PdoDisabled,
                    "the COB-ID word was rejected; the PDO stays destroyed", raw, abort));
            }
        }

        // The same for every SRDO the description creates (direction not 0). Creating one is a
        // write to a checksummed record, so it clears 13FEh: that object is applied after it.
        CreateDescribedSrdos(_od, description, pendingSrdoCreates, failedMappings, findings);
        foreach (var obj in ordered.Where(o => PdoRecordRank(o.Index) >= ConfigurationValidRank))
        {
            loaded += ApplyDescribedObject(obj, description, findings, pendingPdoCreates, pendingSrdoCreates, failedMappings);
        }

        // What the description declared has a power-on value from now on: the objects it
        // created and the managed ones it gave values to, as far as they still exist.
        foreach (var index in objects.Keys.Where(index => _od.ContainsIndex(index))) _describedObjects.Add(index);

        _factoryDefaults = SnapshotRestorableValues();
        _powerOnValues = _factoryDefaults;
        _deviceDescription = new DeviceDescriptionReport(description, _nodeId, loaded,
            findings.OrderBy(f => f.Index).ThenBy(f => f.Subindex).ToList());
    }

    // -----------------------------------------------------------------------------------------
    // CiA DSP 304: the loader's SRDO steps are static over the dictionary they write, so that
    // LoadDescribedSafetyObjects runs the very same code for PeerSafetyConfiguration.
    // -----------------------------------------------------------------------------------------

    /// <summary>The deferred "create SRDO" step: the direction is written last, once the mapping
    /// is in, and only over a mapping record that exists and was applied as described.</summary>
    private static void CreateDescribedSrdos(ObjectDictionary od, CanOpenDeviceDescription description,
        List<(int Srdo, byte Direction, string Raw)> pendingSrdoCreates, HashSet<ushort> failedMappings, List<DeviceDescriptionFinding> findings)
    {
        var objects = description.Objects.Objects;
        foreach (var (srdo, direction, raw) in pendingSrdoCreates)
        {
            var srdoComm = SrdoRecords.CommIndex(srdo);
            var srdoMap = SrdoRecords.MapIndex(srdo);
            if (!objects.ContainsKey(srdoMap))
            {
                findings.Add(new DeviceDescriptionFinding(srdoComm, 0x01, DeviceDescriptionOutcome.Corrected,
                    $"the description declares no mapping record 0x{srdoMap:X4} for this SRDO; the SRDO stays deleted", raw));
                continue;
            }
            if (failedMappings.Contains(srdoMap))
            {
                findings.Add(new DeviceDescriptionFinding(srdoComm, 0x01, DeviceDescriptionOutcome.Corrected,
                    "the SRDO stays deleted because its mapping record could not be applied as described", raw));
                continue;
            }
            if (!od.TryWriteRaw(SrdoRecords.CommIndex(srdo), 0x01, new[] { direction }, out var abort))
                findings.Add(new DeviceDescriptionFinding(SrdoRecords.CommIndex(srdo), 0x01, DeviceDescriptionOutcome.Corrected,
                    "the direction was rejected; the SRDO stays deleted", raw, abort));
        }
    }

    /// <summary>
    /// The safety objects a node with node-id <paramref name="nodeId"/> holds after loading
    /// <paramref name="description"/>, in a dictionary of their own: 1300h and the SRDO records,
    /// written by the loader's own steps in the loader's order — the §8.4.2.2 defaults, the
    /// application objects a mapping can name, 1300h, the communication records with the SRDO
    /// deleted, the mapping records, the deferred creates — through the node's own write
    /// validator (CiA DSP 304 §8.4.2.2, §8.4.2.3), as in Pre-Operational. Findings are not
    /// kept; the node reports them when it loads the file. What
    /// <see cref="Safety.PeerSafetyConfiguration.FromDeviceDescription"/> reads, so that step D
    /// against a slave's own DCF expects what the slave holds.
    /// </summary>
    internal static ObjectDictionary LoadDescribedSafetyObjects(CanOpenDeviceDescription description, byte nodeId)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        CanOpenCobId.ValidateNodeId(nodeId);
        var od = new ObjectDictionary();
        int srdoCount = DescribedSrdoCount(description);
        PopulateSafetyObjects(od, nodeId, srdoCount, writable: true);
        od.WriteValidator = (index, subindex, value) => IsManagedSafetyObject(srdoCount, index)
            ? ValidateSafetyWrite(od, srdoCount, operational: false, index, subindex, value)
            : OdWriteDecision.Accept;
        // The node also removes the records the file does not declare. Not needed here: such a
        // record has no create pending and stays at direction 0, which is what the expectation
        // reads from a removed one too.

        var findings = new List<DeviceDescriptionFinding>();
        var pendingSrdoCreates = new List<(int Srdo, byte Direction, string Raw)>();
        var failedMappings = new HashSet<ushort>();
        var objects = description.Objects.Objects;
        // Rank 0 as in ApplyDeviceDescription: what a mapping names is data the loader declares
        // first (1000h–1FFFh cannot be mapped, so only the application area matters).
        foreach (var obj in objects.Values.Where(o => o.Index >= 0x2000).OrderBy(o => o.Index))
        {
            foreach (var entry in EntriesOf(obj)) DeclareDescribedEntry(od, nodeId, obj.Index, entry, findings);
        }
        // 1300h is managed only on a node with SRDOs; without, it is data like any other object.
        if (objects.TryGetValue(Co.GfcParameter, out var gfc))
        {
            if (srdoCount > 0) ApplyManagedVariable(od, nodeId, Co.GfcParameter, EntriesOf(gfc), findings);
            else foreach (var entry in EntriesOf(gfc)) DeclareDescribedEntry(od, nodeId, Co.GfcParameter, entry, findings);
        }
        foreach (var obj in objects.Values.Where(o => IsManagedSafetyObject(srdoCount, o.Index) && SrdoRecords.IsCommunicationRecord(o.Index)).OrderBy(o => o.Index))
            ApplySrdoCommunicationRecord(od, nodeId, srdoCount, obj.Index, EntriesOf(obj), findings, pendingSrdoCreates);
        foreach (var obj in objects.Values.Where(o => IsManagedSafetyObject(srdoCount, o.Index) && SrdoRecords.IsMappingRecord(o.Index)).OrderBy(o => o.Index))
            ApplySrdoMappingRecord(od, nodeId, obj.Index, EntriesOf(obj), findings, failedMappings);
        CreateDescribedSrdos(od, description, pendingSrdoCreates, failedMappings, findings);
        return od;
    }

    /// <summary>13FEh is applied after everything else: every validated write to a checksummed
    /// record, to 13FFh or the creation of an SRDO clears it (CiA DSP 304 §8.4.2.2).</summary>
    private const int ConfigurationValidRank = 4;

    private static int PdoRecordRank(ushort index) => index switch
    {
        // 1F81h and 1F89h before 1F80h, and 1F90h before both: the network list, the boot
        // timeout and the times are in the dictionary when bits 0 and 5 start the election.
        Co.SlaveAssignment or Co.BootTime => -2,
        Co.FlyingMasterTiming => -1,
        >= Co.RpdoComm and < Co.RpdoComm + 0x200 => 1,
        >= Co.TpdoComm and < Co.TpdoComm + 0x200 => 1,
        >= Co.RpdoMap and < Co.RpdoMap + 0x200 => 2,
        >= Co.TpdoMap and < Co.TpdoMap + 0x200 => 2,
        // SRDOs: communication records before mappings as for PDOs; then the checksums, which
        // only make sense next to the final records; 13FEh last.
        >= 0x1301 and <= 0x1340 => 1,
        >= 0x1381 and <= 0x13C0 => 2,
        0x13FF => 3,
        0x13FE => ConfigurationValidRank,
        _ => 0,
    };

    private void RemoveObject(ushort index) => RemoveObject(_od, index);

    private static void RemoveObject(ObjectDictionary od, ushort index)
    {
        foreach (var key in od.SnapshotKeys().Where(k => (ushort)(k >> 8) == index))
        {
            od.Remove(index, (byte)(key & 0xFF));
        }
    }

    /// <summary>One described entry, whatever section of the file it came from.</summary>
    private readonly struct DescribedEntry
    {
        public DescribedEntry(byte subindex, ushort? dataType, AccessType access, string? value, bool pdoMappable)
        {
            Subindex = subindex;
            DataType = dataType;
            Access = access;
            Value = value;
            PdoMappable = pdoMappable;
        }

        public byte Subindex { get; }
        public ushort? DataType { get; }
        public AccessType Access { get; }
        public string? Value { get; }
        public bool PdoMappable { get; }
    }

    private static List<DescribedEntry> EntriesOf(CanOpenObject obj)
    {
        var entries = new List<DescribedEntry>();
        if (obj.SubObjects.Count == 0)
        {
            entries.Add(new DescribedEntry(0, obj.DataType, obj.AccessType,
                string.IsNullOrEmpty(obj.ParameterValue) ? obj.DefaultValue : obj.ParameterValue, obj.PdoMapping));
            return entries;
        }
        foreach (var sub in obj.SubObjects.Values.OrderBy(s => s.SubIndex))
        {
            entries.Add(new DescribedEntry(sub.SubIndex, sub.DataType == 0 ? null : sub.DataType, sub.AccessType,
                string.IsNullOrEmpty(sub.ParameterValue) ? sub.DefaultValue : sub.ParameterValue, sub.PdoMapping));
        }
        return entries;
    }

    private int ApplyDescribedObject(CanOpenObject obj, CanOpenDeviceDescription description,
        List<DeviceDescriptionFinding> findings, List<(ushort, uint, string)> pendingPdoCreates,
        List<(int, byte, string)> pendingSrdoCreates, HashSet<ushort> failedMappings)
    {
        ushort index = obj.Index;
        // 0001h–0FFFh: data-type definitions and reserved space, nothing a device serves.
        if (index < 0x1000) return 0;
        var entries = EntriesOf(obj);

        switch (index)
        {
            case Co.SdoServer:
                return ApplyFixedRecord(index, entries, findings,
                    "the default SDO server is fixed at 600h/580h + node-id (1200h is const); the described value is not applied");
            case Co.StoreParameters:
            case Co.RestoreDefaults:
                return ApplyFixedRecord(index, entries, findings,
                    "only sub-index 01h (all parameters) is implemented; the described sub-index is not created");
            case Co.ConsumerHeartbeat:
                return ApplyConsumerHeartbeatArray(entries, findings);
            case Co.SyncCobId:
            case Co.CyclePeriod:
            case Co.EmcyCobId:
            case Co.ProducerHeartbeat:
            case Co.GuardTime:
            case Co.LifeTimeFactor:
            case Co.ErrorRegister:
            case Co.NmtStartup:
            case Co.BootTime:
                return ApplyManagedVariable(index, entries, findings);
            case Co.SlaveAssignment:
                return ApplySlaveAssignment(entries, findings);
            case Co.RequestNmt:
                return ApplyRequestNmt(entries, findings);
            case Co.FlyingMasterTiming:
                return ApplyFlyingMasterTiming(entries, findings);
        }
        if (index is >= Co.RpdoComm and < Co.RpdoComm + 0x200)
            return ApplyPdoCommunicationRecord(index, Co.RpdoComm, isTpdo: false, entries, findings, pendingPdoCreates);
        if (index is >= Co.TpdoComm and < Co.TpdoComm + 0x200)
            return ApplyPdoCommunicationRecord(index, Co.TpdoComm, isTpdo: true, entries, findings, pendingPdoCreates);
        if (index is >= Co.RpdoMap and < Co.RpdoMap + 0x200)
            return ApplyPdoMappingRecord(index, Co.RpdoMap, entries, findings, failedMappings);
        if (index is >= Co.TpdoMap and < Co.TpdoMap + 0x200)
            return ApplyPdoMappingRecord(index, Co.TpdoMap, entries, findings, failedMappings);

        // CiA DSP 304: only when the node has SRDOs; otherwise the objects are data like any other.
        if (IsManagedSafetyObject(index))
        {
            if (index == Co.GfcParameter || index == Co.SrdoConfigurationValid)
                return ApplyManagedVariable(index, entries, findings);
            if (index == Co.SrdoChecksum)
                return ApplySafetyChecksumArray(entries, findings);
            if (SrdoRecords.IsCommunicationRecord(index))
                return ApplySrdoCommunicationRecord(_od, _nodeId, _srdoCount, index, entries, findings, pendingSrdoCreates);
            if (SrdoRecords.IsMappingRecord(index))
                return ApplySrdoMappingRecord(_od, _nodeId, index, entries, findings, failedMappings);
        }

        // Everything else is data: created as described. Communication-profile objects the node
        // has no behaviour for are still created — a master reads and writes what the file
        // promised — and reported as such once, on their sub-index 0.
        int loaded = 0;
        foreach (var entry in entries)
        {
            loaded += DeclareDescribedEntry(index, entry, findings) ? 1 : 0;
        }
        if (IsCommunicationProfileArea(index) && index is not (Co.DeviceType or Co.Identity or 0x1002 or 0x1008 or 0x1009 or 0x100A))
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.NotImplemented,
                "created as data: the node implements no behaviour behind this communication-profile object"));
        }
        return loaded;
    }

    private int ApplyFixedRecord(ushort index, List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings, string reason)
    {
        foreach (var entry in entries)
        {
            if (!_od.TryGet(index, entry.Subindex, out var ours))
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted, reason, entry.Value));
                continue;
            }
            // 1200h names the default server's COB-IDs; a description that puts other values
            // there promises something this node does not do, and the node says so rather than
            // exposing the file's value. The capability words of 1010h/1011h carry no promise.
            if (index == Co.SdoServer && entry.Subindex != 0 && ParseUnsigned(entry, out var raw) is { } described
                && described != ObjectDictionary.DecodeU32(ours.GetRawValue()))
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.NotImplemented,
                    reason, raw));
            }
        }
        return 0;
    }

    /// <summary>
    /// <c>1F90h</c>: six UNSIGNED16 times. Sub-index <c>00h</c> stays the constant 6. Each other
    /// value goes through the checks an SDO download hits. Sub-indices <c>04h</c> and <c>05h</c>
    /// constrain each other, so they are written in whichever order keeps a currently valid pair
    /// valid between the two writes.
    /// </summary>
    private int ApplyFlyingMasterTiming(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        const ushort index = Co.FlyingMasterTiming;
        int loaded = 0;
        var described = new Dictionary<byte, DescribedEntry>();
        foreach (var entry in entries) described[entry.Subindex] = entry;

        if (described.TryGetValue(0, out var sub0))
        {
            if (ParseUnsigned(sub0, out _) == 6) loaded++;
            else
            {
                findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                    "1F90h sub-index 00h is constant 6; the described count is not applied", sub0.Value));
            }
        }
        foreach (var entry in entries.Where(e => e.Subindex > 6))
        {
            findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                "1F90h implements sub-indices 01h to 06h; this sub-index is not created", entry.Value));
        }

        void Take(byte sub)
        {
            if (!described.TryGetValue(sub, out var entry)) return;
            // 1F90h:01 to :06 are created with the node, so the described sub-index is present.
            _od.TryGet(index, sub, out var current);
            _od.Declare(index, sub, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            ApplyManagedValue(index, sub, current.DataType, entry, findings);
        }

        Take(0x01);
        Take(0x02);
        Take(0x03);
        Take(0x06);

        int currentDevice = (int)_od.ReadUnsigned(index, 0x05);
        int currentPrioritySlot = (int)_od.ReadUnsigned(index, 0x04);
        int nextDevice = DescribedU16(0x05) ?? currentDevice;
        int nextPrioritySlot = DescribedU16(0x04) ?? currentPrioritySlot;
        // A larger priority slot has to land before a larger device slot, and a smaller device
        // slot before a smaller priority slot. Otherwise the pair is rejected mid-way even when
        // the two described values together would be accepted.
        bool deviceFirst = nextPrioritySlot <= 127 * currentDevice && currentPrioritySlot > 127 * nextDevice;
        if (deviceFirst)
        {
            Take(0x05);
            Take(0x04);
        }
        else
        {
            Take(0x04);
            Take(0x05);
        }
        return loaded;

        int? DescribedU16(byte sub)
        {
            if (!described.TryGetValue(sub, out var entry)) return null;
            var parsed = ParseUnsigned(entry, out _);
            if (parsed is null || parsed > ushort.MaxValue) return null;
            return (int)parsed.Value;
        }
    }

    /// <summary><c>1F81h</c>: one UNSIGNED32 per node-id. Sub-index <c>00h</c> stays 127. A described
    /// entry is stored through the same checks an SDO download hits.</summary>
    private int ApplySlaveAssignment(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        const ushort index = Co.SlaveAssignment;
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0)
            {
                if (ParseUnsigned(entry, out _) == CanOpenCobId.MaxNodeId) loaded++;
                else
                {
                    findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                        "1F81h sub-index 00h is constant 127; the described count is not applied", entry.Value));
                }
                continue;
            }
            if (entry.Subindex > CanOpenCobId.MaxNodeId || !_od.TryGet(index, entry.Subindex, out var current))
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    "1F81h implements sub-indices 01h to 7Fh; this sub-index is not created", entry.Value));
                continue;
            }
            _od.Declare(index, entry.Subindex, OdDataType.Unsigned32, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            ApplyManagedValue(index, entry.Subindex, OdDataType.Unsigned32, entry, findings);
        }
        return loaded;
    }

    /// <summary><c>1F82h</c>: the tracked NMT state. A described value is stored as that state and
    /// is not executed as a request — a request is a write once the node is the active master.
    /// Sub-index <c>00h</c> stays 128.</summary>
    private int ApplyRequestNmt(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        const ushort index = Co.RequestNmt;
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0)
            {
                if (ParseUnsigned(entry, out _) == 0x80) loaded++;
                else
                {
                    findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                        "1F82h sub-index 00h is constant 128; the described count is not applied", entry.Value));
                }
                continue;
            }
            if (entry.Subindex > 0x80 || !_od.TryGet(index, entry.Subindex, out var current))
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    "1F82h implements sub-indices 01h to 80h; this sub-index is not created", entry.Value));
                continue;
            }
            _od.Declare(index, entry.Subindex, OdDataType.Unsigned8, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            var parsed = ParseUnsigned(entry, out var raw);
            if (parsed is null || parsed > byte.MaxValue)
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Corrected,
                    "1F82h holds the tracked NMT state as UNSIGNED8; the described value is not applied", raw));
                continue;
            }
            _od.WriteRawUnchecked(index, entry.Subindex, new[] { (byte)parsed.Value });
        }
        return loaded;
    }

    /// <summary>1005h, 1006h, 1014h, 1017h, 100Ch, 100Dh, 1001h, 1F80h, 1F89h: the node's type stays, the
    /// description's access and value are taken — the value through the validated path.</summary>
    private int ApplyManagedVariable(ushort index, List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
        => ApplyManagedVariable(_od, _nodeId, index, entries, findings);

    private static int ApplyManagedVariable(ObjectDictionary od, byte nodeId, ushort index, List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex != 0 || !od.TryGet(index, 0, out var current))
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    "this object is a VAR; only sub-index 00h exists", entry.Value));
                continue;
            }
            od.Declare(index, 0, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            ApplyManagedValue(od, nodeId, index, 0, current.DataType, entry, findings);
        }
        return loaded;
    }

    /// <summary>1016h: the array grows to the described size, every entry goes through the
    /// validated path (a duplicate node-id is rejected there with 0604 0043h).</summary>
    private int ApplyConsumerHeartbeatArray(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int loaded = 0;
        // §7.5.2.19: at most 127 entries — the count the node accepts in sub-index 00h and the
        // bound its loops over the array count to (Codex on #133). A described sub-index above
        // 7Fh cannot be an entry of this array and is reported below.
        const byte maxEntries = CanOpenCobId.MaxNodeId;
        byte count = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0)
            {
                count = (byte)Math.Min(maxEntries, ParseUnsigned(entry, out _) ?? (uint)entries.Count(e => e.Subindex != 0));
                continue;
            }
            if (entry.Subindex <= maxEntries) count = Math.Max(count, entry.Subindex);
        }
        for (int s = 1; s <= count; s++)
        {
            if (!_od.TryGet(Co.ConsumerHeartbeat, (byte)s, out _))
                _od.Declare(Co.ConsumerHeartbeat, (byte)s, OdDataType.Unsigned32, OdAccess.ReadWrite, new byte[4], pdoMappable: false);
        }
        _od.WriteUnsigned(Co.ConsumerHeartbeat, 0x00, count);
        loaded++;
        foreach (var entry in entries.Where(e => e.Subindex != 0))
        {
            if (entry.Subindex > count)
            {
                findings.Add(new DeviceDescriptionFinding(Co.ConsumerHeartbeat, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    entry.Subindex > maxEntries
                        ? "1016h holds at most 127 entries (CiA 301 §7.5.2.19); this sub-index is not created"
                        : "beyond the array size sub-index 00h announces", entry.Value));
                continue;
            }
            _od.Declare(Co.ConsumerHeartbeat, entry.Subindex, OdDataType.Unsigned32, MapAccess(entry.Access), new byte[4], pdoMappable: false);
            loaded++;
            ApplyManagedValue(Co.ConsumerHeartbeat, entry.Subindex, OdDataType.Unsigned32, entry, findings);
        }
        return loaded;
    }

    private int ApplyPdoCommunicationRecord(ushort index, ushort baseIndex, bool isTpdo, List<DescribedEntry> entries,
        List<DeviceDescriptionFinding> findings, List<(ushort, uint, string)> pendingPdoCreates)
    {
        int n = index - baseIndex + 1;
        if (n > Co.PdoCount)
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Omitted,
                $"the node implements {Co.PdoCount} {(isTpdo ? "TPDOs" : "RPDOs")}; this record is not created"));
            return 0;
        }
        int loaded = 0;
        var described = new Dictionary<byte, DescribedEntry>();
        foreach (var entry in entries) described[entry.Subindex] = entry;

        // The sub-indices the node implements for this record; the description says which of
        // them exist on this device (sub-index 00h announces the highest one).
        byte[] implemented = isTpdo ? new byte[] { 1, 2, 3, 5 } : new byte[] { 1, 2 };
        byte highest = 0;
        foreach (var sub in implemented)
        {
            if (!described.TryGetValue(sub, out var entry))
            {
                _od.Remove(index, sub);
                continue;
            }
            if (!_od.TryGet(index, sub, out var current)) continue;
            _od.Declare(index, sub, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            highest = Math.Max(highest, sub);
        }
        _od.WriteUnsigned(index, 0x00, highest);
        foreach (var entry in entries.Where(e => e.Subindex != 0 && Array.IndexOf(implemented, e.Subindex) < 0))
        {
            findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                entry.Subindex == 4
                    ? "sub-index 04h is reserved and shall not be implemented (CiA 301 §7.5.2.37)"
                    : "the node implements no behaviour behind this sub-index", entry.Value));
        }

        // Values, with the PDO still destroyed: transmission type, inhibit time, event timer.
        foreach (var sub in implemented.Where(s => s != 1 && described.ContainsKey(s) && _od.TryGet(index, s, out _)))
        {
            _od.TryGet(index, sub, out var current);
            ApplyManagedValue(index, sub, current!.DataType, described[sub], findings);
        }
        // The COB-ID word last, and only after every mapping record is in (step 5).
        bool hasCobId = described.TryGetValue(1, out var cobId);
        if (hasCobId && !string.IsNullOrEmpty(cobId.Value) && ParseUnsigned(cobId, out var unreadable) is null)
        {
            findings.Add(new DeviceDescriptionFinding(index, 0x01, DeviceDescriptionOutcome.PdoDisabled,
                "the COB-ID word could not be read as UNSIGNED32; the PDO stays destroyed on its default COB-ID", unreadable));
        }
        else if (hasCobId && ParseUnsigned(cobId, out var raw) is { } word)
        {
            if ((word & CanOpenCobId.InvalidBit) != 0)
            {
                // Described as destroyed: the CAN-ID part is still recorded (validated) so the
                // master reads what the file says.
                if (!_od.TryWriteRaw(index, 0x01, ObjectDictionary.EncodeU32(word), out var abort))
                    findings.Add(new DeviceDescriptionFinding(index, 0x01, DeviceDescriptionOutcome.Corrected,
                        "the COB-ID word was rejected; the default COB-ID is kept", raw, abort));
            }
            else
            {
                pendingPdoCreates.Add((index, word, raw ?? ""));
            }
        }
        return loaded;
    }

    /// <summary>1301h–1340h (CiA DSP 304 §8.4.2.2): sub-indices 1, 2, 3, 5 and 6 are mandatory, 4 is
    /// the constant 254. The values go in with the SRDO deleted — times first, then COB-ID 1
    /// before COB-ID 2 — and the direction is applied last, in a deferred step, because it
    /// creates the SRDO.</summary>
    private static int ApplySrdoCommunicationRecord(ObjectDictionary od, byte nodeId, int srdoCount, ushort index,
        List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings, List<(int Srdo, byte Direction, string Raw)> pendingSrdoCreates)
    {
        int n = index - SrdoRecords.CommunicationBase;
        if (n > srdoCount) return 0; // cannot happen: srdoCount covers every declared record
        int loaded = 0;
        var described = new Dictionary<byte, DescribedEntry>();
        foreach (var entry in entries) described[entry.Subindex] = entry;
        foreach (byte sub in new byte[] { 1, 2, 3, 5, 6 })
        {
            if (!described.TryGetValue(sub, out var entry))
            {
                findings.Add(new DeviceDescriptionFinding(index, sub, DeviceDescriptionOutcome.SuppliedDefault,
                    "CiA DSP 304 §8.4.2.2 makes this sub-index mandatory; the node keeps its default"));
                continue;
            }
            if (!od.TryGet(index, sub, out var current)) continue;
            od.Declare(index, sub, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
        }
        // Sub-index 4 is the constant 254: a description that says so promises nothing the node does not keep.
        foreach (var entry in entries.Where(e => e.Subindex is > 6 or 4 && e.Subindex != 0
                     && !(e.Subindex == 4 && ParseUnsigned(nodeId, e, out _) == SrdoRecords.TransmissionType)))
            findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                entry.Subindex == 4 ? "the transmission type is the constant 254 (CiA DSP 304 §8.4.2.2)" : "no such sub-index in an SRDO communication parameter record", entry.Value));
        // Values with the SRDO deleted: times first, then the ids (COB-ID 1 before COB-ID 2 — sub6 is validated against sub5).
        od.WriteUnsigned(index, 0x01, 0);
        foreach (byte sub in new byte[] { 2, 3, 5, 6 })
        {
            if (!described.TryGetValue(sub, out var entry) || !od.TryGet(index, sub, out var current)) continue;
            ApplyManagedValue(od, nodeId, index, sub, current.DataType, entry, findings);
        }
        if (described.TryGetValue(1, out var direction) && !string.IsNullOrEmpty(direction.Value))
        {
            var value = ParseUnsigned(nodeId, direction, out var raw);
            if (value is null)
                findings.Add(new DeviceDescriptionFinding(index, 0x01, DeviceDescriptionOutcome.Corrected, "the direction could not be read as UNSIGNED8; the SRDO stays deleted", raw));
            else if (value.Value > byte.MaxValue)
                findings.Add(new DeviceDescriptionFinding(index, 0x01, DeviceDescriptionOutcome.Corrected, "the direction does not fit UNSIGNED8; the SRDO stays deleted", raw));
            else if (value.Value != 0)
                pendingSrdoCreates.Add((n, (byte)value.Value, raw ?? ""));
        }
        return loaded;
    }

    /// <summary>1381h–13C0h: the entries go in with the SRDO deleted and the count at 0, then the
    /// count (CiA DSP 304 §8.4.2.3: plain and inverted object alternate, so at most 16 sub-indices).</summary>
    private static int ApplySrdoMappingRecord(ObjectDictionary od, byte nodeId, ushort index, List<DescribedEntry> entries,
        List<DeviceDescriptionFinding> findings, HashSet<ushort> failedMappings)
    {
        int n = index - SrdoRecords.MappingBase;
        // An SRDO exists only with both records (Table 6 footnote), and writing a mapping reads
        // and deletes the communication record's direction: without it the record is not created.
        if (!od.TryGet(SrdoRecords.CommIndex(n), 0x01, out _))
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Omitted,
                $"the description declares no communication record 0x{SrdoRecords.CommIndex(n):X4} for this SRDO; an SRDO exists only with both records (Table 6 footnote), so the mapping record is not created"));
            RemoveObject(od, index);
            failedMappings.Add(index);
            return 0;
        }
        int loaded = 0;
        var byIndex = entries.ToDictionary(e => e.Subindex);
        foreach (var entry in entries)
        {
            if (entry.Subindex > SrdoRecords.MappingSubindices)
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    $"a byte-aligned SRDO mapping holds at most {SrdoMapping.MaxEntries} objects (16 sub-indices); this sub-index is not created", entry.Value));
                continue;
            }
            if (!od.TryGet(index, entry.Subindex, out var current)) continue;
            od.Declare(index, entry.Subindex, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
        }
        od.WriteUnsigned(SrdoRecords.CommIndex(n), 0x01, 0); // the mapping is written with the SRDO deleted
        uint count = 0;
        bool failed = false;
        if (byIndex.TryGetValue(0, out var sub0) && !string.IsNullOrEmpty(sub0.Value))
        {
            if (ParseUnsigned(nodeId, sub0, out _) is { } parsed) count = parsed;
            else { findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected, "the mapping count could not be read as UNSIGNED8; the mapping stays disabled", sub0.Value)); failed = true; }
        }
        od.WriteUnsigned(index, 0x00, 0);
        for (byte s = 1; s <= Math.Min(count, (uint)SrdoRecords.MappingSubindices); s++)
        {
            if (!byIndex.TryGetValue(s, out var entry) || string.IsNullOrEmpty(entry.Value)) continue;
            var value = ParseUnsigned(nodeId, entry, out var raw);
            if (value is null) { findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected, "the mapping entry could not be read as UNSIGNED32; the slot stays empty and the mapping stays disabled", raw)); failed = true; continue; }
            if (!od.TryWriteRaw(index, s, ObjectDictionary.EncodeU32(value.Value), out var abort))
            { findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected, "the mapping entry was rejected; the slot stays empty and the mapping stays disabled", raw, abort)); failed = true; }
        }
        // The count is read as UNSIGNED32 and stored as UNSIGNED8: above 16 it is refused here,
        // before the cast could turn 0x104 into 4 (§8.4.2.3: at most 16 sub-indices).
        if (count > SrdoRecords.MappingSubindices)
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                $"an SRDO mapping has at most {SrdoRecords.MappingSubindices} sub-indices (CiA DSP 304 §8.4.2.3); the mapping stays disabled", sub0.Value));
            failed = true;
        }
        if (!failed && count > 0 && !od.TryWriteRaw(index, 0x00, new[] { (byte)count }, out var countAbort))
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected, "the mapping count was rejected; the mapping stays disabled", sub0.Value, countAbort));
            failed = true;
        }
        if (failed) failedMappings.Add(index);
        return loaded;
    }

    /// <summary>13FFh: one UNSIGNED16 signature per SRDO. Sub-index 00h stays the node's SRDO count;
    /// a sub-index beyond it cannot be an entry of this array.</summary>
    private int ApplySafetyChecksumArray(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0 || entry.Subindex > _srdoCount)
            {
                if (entry.Subindex != 0)
                    findings.Add(new DeviceDescriptionFinding(Co.SrdoChecksum, entry.Subindex, DeviceDescriptionOutcome.Omitted, $"the node holds {_srdoCount} SRDO checksum(s)", entry.Value));
                continue;
            }
            if (!_od.TryGet(Co.SrdoChecksum, entry.Subindex, out var current)) continue;
            _od.Declare(Co.SrdoChecksum, entry.Subindex, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            ApplyManagedValue(Co.SrdoChecksum, entry.Subindex, current.DataType, entry, findings);
        }
        return loaded;
    }

    private int ApplyPdoMappingRecord(ushort index, ushort baseIndex, List<DescribedEntry> entries,
        List<DeviceDescriptionFinding> findings, HashSet<ushort> failedMappings)
    {
        int n = index - baseIndex + 1;
        if (n > Co.PdoCount)
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Omitted,
                $"the node implements {Co.PdoCount} PDOs of each kind; this record is not created"));
            return 0;
        }
        // A PDO exists only with both records (objects overview, footnote *): a mapping record
        // whose communication record — or its COB-ID sub-index — the description does not
        // declare cannot form one, and the validation of a mapping write reads that COB-ID.
        // The record is not created; the finding says why (Bugbot on #135).
        var commIndex = (ushort)(index - 0x200);
        if (!_od.TryGet(commIndex, 0x01, out _))
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Omitted,
                $"the description declares no COB-ID (0x{commIndex:X4}:01) for this PDO; a PDO exists only with both records, so the mapping record is not created"));
            RemoveObject(index);
            failedMappings.Add(index);
            return 0;
        }
        int loaded = 0;
        var byIndex = entries.ToDictionary(e => e.Subindex);
        foreach (var entry in entries)
        {
            if (entry.Subindex > Pdo.PdoMapping.MaxEntries)
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    $"a byte-aligned mapping holds at most {Pdo.PdoMapping.MaxEntries} entries; this sub-index is not created", entry.Value));
                continue;
            }
            if (!_od.TryGet(index, entry.Subindex, out var current)) continue;
            _od.Declare(index, entry.Subindex, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
        }

        // The mapping itself, in the order of §7.5.2.38 steps 2 to 4 (the PDO is destroyed). A
        // value that cannot be read is a finding like a value that is rejected (Bugbot on #135):
        // an empty value is a 0, a garbled one leaves the slot empty and the mapping disabled.
        bool failed = false;
        uint count = 0;
        if (byIndex.TryGetValue(0, out var sub0) && !string.IsNullOrEmpty(sub0.Value))
        {
            if (ParseUnsigned(sub0, out _) is { } parsedCount) count = parsedCount;
            else
            {
                findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                    "the mapping count could not be read as UNSIGNED8; the mapping stays disabled", sub0.Value));
                failed = true;
            }
        }
        _od.WriteUnsigned(index, 0x00, 0);
        for (byte s = 1; s <= Math.Min(count, (uint)Pdo.PdoMapping.MaxEntries); s++)
        {
            if (!byIndex.TryGetValue(s, out var entry) || string.IsNullOrEmpty(entry.Value)) continue;
            var value = ParseUnsigned(entry, out var raw);
            if (value is null)
            {
                findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected,
                    "the mapping entry could not be read as UNSIGNED32; the slot stays empty and the mapping stays disabled", raw));
                failed = true;
                continue;
            }
            if (!_od.TryWriteRaw(index, s, ObjectDictionary.EncodeU32(value.Value), out var abort))
            {
                findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected,
                    "the mapping entry was rejected; the slot stays empty and the mapping stays disabled", raw, abort));
                failed = true;
            }
        }
        if (count > (uint)Pdo.PdoMapping.MaxEntries)
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                $"a byte-aligned mapping holds at most {Pdo.PdoMapping.MaxEntries} entries; the mapping stays disabled", sub0.Value));
            failed = true;
        }
        if (!failed && count > 0 && !_od.TryWriteRaw(index, 0x00, new[] { (byte)count }, out var countAbort))
        {
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected,
                "the mapping count was rejected; the mapping stays disabled", sub0.Value, countAbort));
            failed = true;
        }
        if (failed) failedMappings.Add(index);
        return loaded;
    }

    private void ApplyManagedValue(ushort index, byte subindex, OdDataType type, DescribedEntry entry,
        List<DeviceDescriptionFinding> findings)
        => ApplyManagedValue(_od, _nodeId, index, subindex, type, entry, findings);

    private static void ApplyManagedValue(ObjectDictionary od, byte nodeId, ushort index, byte subindex, OdDataType type, DescribedEntry entry,
        List<DeviceDescriptionFinding> findings)
    {
        if (string.IsNullOrEmpty(entry.Value)) return;
        byte[]? bytes;
        try
        {
            bytes = ToBytes(type, CanOpenValueConverter.Parse(entry.Value!, TypeCode(type), nodeId));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException or InvalidCastException)
        {
            findings.Add(new DeviceDescriptionFinding(index, subindex, DeviceDescriptionOutcome.Corrected,
                $"the value could not be read as {type}: {ex.Message}; the default is kept", entry.Value));
            return;
        }
        if (bytes is null) return;
        if (!od.TryWriteRaw(index, subindex, bytes, out var abort))
        {
            findings.Add(new DeviceDescriptionFinding(index, subindex, DeviceDescriptionOutcome.Corrected,
                "the value was rejected by the rule an SDO download would hit; the default is kept", entry.Value, abort));
        }
    }

    private bool DeclareDescribedEntry(ushort index, DescribedEntry entry, List<DeviceDescriptionFinding> findings)
        => DeclareDescribedEntry(_od, _nodeId, index, entry, findings);

    private static bool DeclareDescribedEntry(ObjectDictionary od, byte nodeId, ushort index, DescribedEntry entry, List<DeviceDescriptionFinding> findings)
    {
        var type = MapType(entry.DataType);
        if (type is null)
        {
            findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                entry.DataType is { } code
                    ? $"data type 0x{code:X4} ({CanOpenDataType.GetName(code) ?? "unknown"}) is not one the dictionary represents"
                    : "no data type is declared", entry.Value));
            return false;
        }
        byte[] value;
        if (string.IsNullOrEmpty(entry.Value) || type == OdDataType.Domain)
        {
            value = ZeroOf(type.Value);
        }
        else
        {
            try
            {
                value = ToBytes(type.Value, CanOpenValueConverter.Parse(entry.Value!, TypeCode(type.Value), nodeId)) ?? ZeroOf(type.Value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException or InvalidCastException)
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Corrected,
                    $"the value could not be read as {type}: {ex.Message}; the entry starts at zero", entry.Value));
                value = ZeroOf(type.Value);
            }
        }
        // Mandatory placeholders (1000h, 1018h) are replaced; everything else in the file is new.
        od.Declare(index, entry.Subindex, type.Value, MapAccess(entry.Access), value,
            pdoMappable: entry.PdoMappable && !IsCommunicationProfileArea(index));
        return true;
    }

    // -----------------------------------------------------------------------------------------
    // Conversions.
    // -----------------------------------------------------------------------------------------

    private uint? ParseUnsigned(DescribedEntry entry, out string? raw) => ParseUnsigned(_nodeId, entry, out raw);

    private static uint? ParseUnsigned(byte nodeId, DescribedEntry entry, out string? raw)
    {
        raw = entry.Value;
        if (string.IsNullOrEmpty(entry.Value)) return null;
        try
        {
            return Convert.ToUInt32(CanOpenValueConverter.Parse(entry.Value!, CanOpenDataType.Unsigned32, nodeId), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    private static OdAccess MapAccess(AccessType access) => access switch
    {
        AccessType.WriteOnly => OdAccess.WriteOnly,
        AccessType.ReadWrite or AccessType.ReadWriteInput or AccessType.ReadWriteOutput => OdAccess.ReadWrite,
        _ => OdAccess.ReadOnly, // ro and const are the same thing on the bus
    };

    private static OdDataType? MapType(ushort? code) => code switch
    {
        CanOpenDataType.Boolean => OdDataType.Boolean,
        CanOpenDataType.Integer8 => OdDataType.Integer8,
        CanOpenDataType.Integer16 => OdDataType.Integer16,
        CanOpenDataType.Integer32 => OdDataType.Integer32,
        CanOpenDataType.Unsigned8 => OdDataType.Unsigned8,
        CanOpenDataType.Unsigned16 => OdDataType.Unsigned16,
        CanOpenDataType.Unsigned32 => OdDataType.Unsigned32,
        CanOpenDataType.Real32 => OdDataType.Real32,
        CanOpenDataType.VisibleString => OdDataType.VisibleString,
        CanOpenDataType.OctetString => OdDataType.OctetString,
        CanOpenDataType.UnicodeString => OdDataType.UnicodeString,
        CanOpenDataType.Domain => OdDataType.Domain,
        CanOpenDataType.Real64 => OdDataType.Real64,
        CanOpenDataType.Integer64 => OdDataType.Integer64,
        CanOpenDataType.Unsigned64 => OdDataType.Unsigned64,
        _ => null,
    };

    private static ushort TypeCode(OdDataType type) => (ushort)type;

    private static byte[] ZeroOf(OdDataType type)
    {
        int size = OdEntryLayout.FixedSize(type);
        return size > 0 ? new byte[size] : Array.Empty<byte>();
    }

    private static byte[]? ToBytes(OdDataType type, object value)
    {
        switch (type)
        {
            case OdDataType.Boolean: return new[] { (byte)((bool)value ? 1 : 0) };
            case OdDataType.Integer8: return new[] { unchecked((byte)(sbyte)value) };
            case OdDataType.Unsigned8: return new[] { (byte)value };
            case OdDataType.Integer16: return LittleEndian(unchecked((ulong)(short)value), 2);
            case OdDataType.Unsigned16: return LittleEndian((ushort)value, 2);
            case OdDataType.Integer32: return LittleEndian(unchecked((ulong)(int)value), 4);
            case OdDataType.Unsigned32: return LittleEndian((uint)value, 4);
            case OdDataType.Integer64: return LittleEndian(unchecked((ulong)(long)value), 8);
            case OdDataType.Unsigned64: return LittleEndian((ulong)value, 8);
            case OdDataType.Real32: return LittleEndian(BitConverter.GetBytes((float)value));
            case OdDataType.Real64: return LittleEndian(BitConverter.GetBytes((double)value));
            case OdDataType.VisibleString: return Encoding.ASCII.GetBytes((string)value);
            case OdDataType.UnicodeString: return Encoding.Unicode.GetBytes((string)value);
            case OdDataType.OctetString: return (byte[])value;
            case OdDataType.Domain: return null;
            default: return null;
        }
    }

    private static byte[] LittleEndian(ulong value, int width)
    {
        var bytes = new byte[width];
        for (int i = 0; i < width; i++) bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    private static byte[] LittleEndian(byte[] hostOrder)
    {
        if (!BitConverter.IsLittleEndian) Array.Reverse(hostOrder);
        return hostOrder;
    }
}
