using System;
using System.Collections.Generic;
using System.Linq;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// FR-CO-062, #434: the pre-defined error field 1003h (CiA 301 §7.5.2.4; transitions 1 and 3 of
// Figure 37). The object is optional: a node has it when its options or its device description ask.
//
// It holds "the errors that occurred on the CANopen device and were signaled via the emergency
// object", so an error is entered in the step of the EMCY chain that transmits it: the application's
// and the node's own alike, not one that is switched off, cancelled or replaced while held, not
// 0000h, which reports that an error ended, and not the EMCY of another node. The entry is the
// frame's: the code low, the first two manufacturer-specific bytes high.
//
// Status like 1001h: 1010h does not store it, 1011h does not restore it, and an NMT reset leaves it,
// which departs from §7.3.2.2.1 on purpose, by the maintainer's decision on #434, so that a master
// that resets a node to recover it can still read why it had to.
internal sealed partial class CanOpenNode
{
    private readonly int _errorHistoryCapacity;

    private const int MaxErrorHistoryCapacity = 0xFE;

    /// <summary>Opt-in: without it 1003h is not the node's, and an application may declare it.</summary>
    private bool IsManagedErrorHistory(ushort index) => index == Co.ErrorHistory && _errorHistoryCapacity > 0;

    /// <summary>The highest entry a description declares; at least one, as §7.5.2.4 requires of an
    /// object that exists. 0 without the object.</summary>
    private static int DescribedErrorHistoryCapacity(CanOpenDeviceDescription? description)
    {
        if (description is null || !description.Objects.Objects.TryGetValue(Co.ErrorHistory, out var declared)) return 0;
        int highest = declared.SubObjects.Keys.Where(sub => sub <= MaxErrorHistoryCapacity).Select(sub => (int)sub).DefaultIfEmpty(0).Max();
        return Math.Max(highest, 1);
    }

    private void PopulateErrorHistory()
    {
        _od.AddU8(Co.ErrorHistory, 0x00, 0, OdAccess.ReadWrite, pdoMappable: false);
        for (int n = 1; n <= _errorHistoryCapacity; n++) _od.AddU32(Co.ErrorHistory, (byte)n, 0, OdAccess.ReadOnly, pdoMappable: false);
    }

    /// <summary>§7.5.2.4: writing 00h empties the array, any other value is refused, and the entries
    /// are ro for a local writer as well: only the node's own unchecked writes reach them.</summary>
    private static OdWriteDecision ValidateErrorHistoryWrite(byte subindex, byte[] value)
    {
        if (subindex != 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        return value[0] == 0 ? OdWriteDecision.Accept : OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
    }

    /// <summary>The count was written. When it became 0 the entries are emptied too, under the same
    /// write gate. EntryWritten, on the writer's thread.</summary>
    private void OnErrorHistoryWritten(byte subindex)
    {
        if (subindex != 0 || _od.ReadUnsigned(Co.ErrorHistory, 0x00) != 0) return;
        var empty = new byte[4];
        for (int n = 1; n <= _errorHistoryCapacity; n++) _od.WriteRawUnchecked(Co.ErrorHistory, (byte)n, empty);
    }

    /// <summary>The EMCY in <paramref name="frame"/> is being transmitted: <c>1001h</c> gets the
    /// register the frame carries and <c>1003h</c> its error, as one write. Actor loop only.</summary>
    private void NoteTransmittedEmcy(byte[] frame, byte? applicationErrorRegister, byte nodeBits)
        => _od.Transaction(() =>
        {
            frame[2] = UpdateErrorRegister(applicationErrorRegister, nodeBits);
            EnterInErrorHistory(frame);
        });

    /// <summary>The EMCY in <paramref name="frame"/> is being transmitted: its error goes in at
    /// sub-index 01h, the older ones move up and the oldest of a full history falls off. Under the
    /// dictionary's write gate, on the actor.</summary>
    private void EnterInErrorHistory(byte[] frame)
    {
        if (_errorHistoryCapacity == 0 || (frame[0] == 0 && frame[1] == 0)) return;
        int kept = Math.Min((int)_od.ReadUnsigned(Co.ErrorHistory, 0x00), _errorHistoryCapacity - 1);
        for (int n = kept; n >= 1; n--) _od.WriteRawUnchecked(Co.ErrorHistory, (byte)(n + 1), _od.ReadRaw(Co.ErrorHistory, (byte)n));
        _od.WriteRawUnchecked(Co.ErrorHistory, 0x01, new[] { frame[0], frame[1], frame[3], frame[4] });
        _od.WriteRawUnchecked(Co.ErrorHistory, 0x00, new[] { (byte)(kept + 1) });
    }

    /// <summary>What an SDO upload serves: <paramref name="value"/>, or the abort code instead.
    /// §7.5.2.4 NOTE: with no error present an upload of sub-index 01h aborts, read here for every
    /// entry above the count. Count and entry are read together under the write gate: an application
    /// emptying the history from its own thread could otherwise land between them, and the upload
    /// would serve a zero that was never an error (Codex on #510).</summary>
    private SdoAbortCode? SnapshotForUpload(ushort index, byte subindex, out byte[] value)
    {
        if (!IsManagedErrorHistory(index))
            return _od.TryReadRaw(index, subindex, out value!) ? null : SdoAbortCode.ObjectDoesNotExist;
        byte[]? entry = null;
        _od.Transaction(() =>
        {
            if (subindex <= _od.ReadUnsigned(Co.ErrorHistory, 0x00)) entry = _od.ReadRaw(Co.ErrorHistory, subindex);
        });
        value = entry ?? Array.Empty<byte>();
        return entry is null ? SdoAbortCode.NoDataAvailable : null;
    }

    /// <summary>The count starts at 00h whatever the file says, the entries stay ro: the node writes
    /// them. The access of the count is the file's, so that a device can refuse to be cleared.</summary>
    private int ApplyErrorHistory(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0)
            {
                _od.TryGet(Co.ErrorHistory, 0x00, out var count);
                _od.Declare(Co.ErrorHistory, 0x00, count!.DataType, MapAccess(entry.Access), count.GetRawValue(), pdoMappable: false);
                if (ParseUnsigned(entry, out _) is { } described && described != 0)
                    findings.Add(new DeviceDescriptionFinding(Co.ErrorHistory, 0x00, DeviceDescriptionOutcome.Corrected,
                        "the history starts empty: the node counts what it enters", entry.Value));
                loaded++;
            }
            else if (entry.Subindex <= _errorHistoryCapacity)
            {
                if ((MapAccess(entry.Access) & OdAccess.WriteOnly) != 0)
                    findings.Add(new DeviceDescriptionFinding(Co.ErrorHistory, entry.Subindex, DeviceDescriptionOutcome.Corrected,
                        "an entry of the error history is read-only: the node writes it", entry.Value));
                loaded++;
            }
            else
            {
                findings.Add(new DeviceDescriptionFinding(Co.ErrorHistory, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    "the entries of the error history end at sub-index FEh", entry.Value));
            }
        }
        if (entries.All(entry => entry.Subindex != 1))
            findings.Add(new DeviceDescriptionFinding(Co.ErrorHistory, 0x01, DeviceDescriptionOutcome.SuppliedDefault,
                "CiA 301 §7.5.2.4 requires at least one entry and the description declares none; the node provides it"));
        return loaded;
    }
}
