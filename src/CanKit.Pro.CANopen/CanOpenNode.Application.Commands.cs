using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

// Command entries (ICanOpenApplication.CommandEntries, #557): every committed write of one reaches
// its claimant with the value it stored, in commit order, where OnEntriesWritten coalesces by
// address and leaves the application the latest value only. Nothing here runs on a node without
// applications: the entries are declared by an application, and every path into this file starts
// from a claimed index or from the application round.
internal sealed partial class CanOpenNode
{
    /// <summary>
    /// The most committed command writes the node holds for its applications, all applications
    /// together, between the write and the actor job that delivers them. The actor's mailbox is
    /// unbounded and a master can write a command entry faster than the actor comes round; without a
    /// bound every such write would be held. The bound is far above what a command word sees between
    /// two actor turns, so it is reached only by a flood, which it then refuses visibly
    /// (<see cref="ReserveCommandWrite"/>) instead of dropping a committed write. The one place the
    /// figure is stated; the seam's documentation and the tests refer to it.
    /// </summary>
    internal const int CommandQueueCapacity = 256;

    // The command entries of every application, as CosKey values; null when no application declares
    // one. Assigned once, on the constructing thread, once every application has attached; read by
    // the writers' validation and by EntryWritten, which see null until then.
    private HashSet<uint>? _commandEntries;

    // The committed command writes not yet delivered, oldest first, under _applicationGate. One job
    // delivers them and the dirty set together (EvaluateApplicationEntriesOnActor), so they share
    // _applicationEvaluationPosted. Each carries the restore generation it was committed in.
    private List<(ApplicationSlot Slot, CanOpenObjectAddress Entry, byte[] Value, int Generation)>? _queuedCommands;

    // Counts the restores of Reset Node (EndCommandWritesBeforeRestore). Changed under the
    // dictionary's write gate, on the actor loop; read under the gate where a write is queued and on
    // the actor loop where the queue is delivered. A queued write of an earlier generation was
    // committed before a Reset Node restored the dictionary and is not delivered.
    private int _commandWriteGeneration;

    // The command write that passed its validation and is about to be stored, as its CosKey; null for
    // none. Under the dictionary's write gate: set at the end of the validation
    // (ReserveCommandWrite) and taken by the notification that follows it in the same hold of the
    // gate (OnOdEntryWrittenForApplications, on ObjectDictionary.EntryStored), which every other
    // notification clears. No other write can come between the two: the gate serialises writers, and
    // a write nested inside this one by a subscriber of EntryWritten (the gate is re-entrant on the
    // writing thread) starts only after EntryStored has run, which is raised ahead of every
    // EntryWritten subscriber. So at most one is outstanding, and one whose write was not stored after
    // all is cleared by the next write, having taken no place.
    private uint? _reservedCommand;

    /// <summary>The command entries <paramref name="slot"/>'s application names, checked: in its
    /// claims, declared by now, and of a fixed width of at most 8 bytes. Constructing thread, right
    /// after the application's <c>Attach</c> returned, so the list can name what it declared.</summary>
    /// <exception cref="System.ArgumentException">An entry fails a check, or the list is null; the
    /// parameter is <c>options</c> and the message names the application.</exception>
    private IEnumerable<uint> ValidatedCommandEntries(ApplicationSlot slot)
    {
        var application = slot.Application;
        var listed = application.CommandEntries
            ?? throw OptionsError($"Application '{application.Name}' returns null for CommandEntries: " +
                "an application without command entries returns an empty list.");
        var keys = new List<uint>(listed.Count);
        foreach (var entry in listed)
        {
            var name = $"{entry.Index:X4}:{entry.Subindex:X2}";
            if (!slot.Claims.Any(range => range.Contains(entry.Index)))
            {
                throw OptionsError($"Application '{application.Name}' names {name} a command entry, " +
                    "which lies in no range it claims.");
            }
            if (!_od.TryGet(entry.Index, entry.Subindex, out var declared))
            {
                throw OptionsError($"Application '{application.Name}' names {name} a command entry, " +
                    "which does not exist when its Attach has returned.");
            }
            int size = OdEntryLayout.FixedSize(declared.DataType);
            if (size is < 1 or > 8)
            {
                throw OptionsError($"Application '{application.Name}' names {name} a command entry, which is " +
                    $"{declared.DataType}: a command entry is of a fixed-width data type of at most 8 bytes, whose " +
                    "every written value the node copies.");
            }
            keys.Add(CosKey(entry.Index, entry.Subindex));
        }
        return keys;
    }

    /// <summary>
    /// The application accepted a write to an entry it claims: when the entry is a command entry and
    /// the write is not the application's own, the write takes its place in the queue now, or is
    /// refused with <c>0800 0022h</c> when the queue is full (CiA 301 v4.2.0 §7.2.4.3.17, Table 22).
    /// Writing thread, under the dictionary's write gate, at the end of the validation: nothing
    /// refuses the write after this but the dictionary losing the entry, and the queue grows only
    /// under this gate, so the room found here is there when the write is stored.
    /// </summary>
    private OdWriteDecision ReserveCommandWrite(ushort index, byte subindex, bool own)
    {
        var key = CosKey(index, subindex);
        if (own || _commandEntries is not { } commands || !commands.Contains(key)) return OdWriteDecision.Accept;
        lock (_applicationGate)
        {
            if (_queuedCommands is { Count: >= CommandQueueCapacity })
                return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);
        }
        _reservedCommand = key;
        return OdWriteDecision.Accept;
    }

    /// <summary>
    /// Tells the command writes queued since the last round, in commit order, each through the
    /// dispatcher: one that throws is reported, and the next is still told. Actor loop, at the head
    /// of the application round. Returns <see langword="false"/> when a callback disposed the node,
    /// which ends the round. A write committed before a Reset Node restored the dictionary is passed
    /// over (<see cref="EndCommandWritesBeforeRestore"/>).
    /// </summary>
    private bool DeliverCommandWrites(List<(ApplicationSlot Slot, CanOpenObjectAddress Entry, byte[] Value, int Generation)> commands)
    {
        foreach (var (slot, entry, value, generation) in commands)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (generation != _commandWriteGeneration) continue;
            InvokeApplication(slot, application => application.OnCommandWritten(entry, value));
        }
        return Volatile.Read(ref _disposed) == 0;
    }

    /// <summary>
    /// Reset Node restores the application's objects, command entries included: every command write
    /// committed before this belongs to the device the reset ended, and its value is overwritten.
    /// <see cref="DropQueuedCommandWritesForReset"/> dropped what was queued when the reset began, but
    /// a write can still be committed between that and the restore — by another thread, or by another
    /// application's callback while the applications are told of the reset. So the generation moves
    /// here, inside the restore's hold of the write gate, and such a write, queued with the old one,
    /// is not delivered. Not for Reset Communication (<paramref name="communicationOnly"/>), which
    /// restores <c>1000h</c> to <c>1FFFh</c> only: a command entry, claimed from <c>2000h</c> up, keeps
    /// the value written, so the application is told of it. Actor loop, under the write gate. Nothing
    /// on a node without applications.
    /// </summary>
    private void EndCommandWritesBeforeRestore(bool communicationOnly)
    {
        if (_applications.Length == 0 || communicationOnly) return;
        _commandWriteGeneration++;
    }

    /// <summary>An NMT reset drops the RPDO receptions still queued, and Reset Node the command writes
    /// too, before the applications are told of the reset. Reset Communication keeps the command
    /// writes: it leaves the application's objects as they are, so each written value is still the
    /// entry's or was overwritten by a later write that is queued behind it, and the application is
    /// told of them after the reset (<see cref="EndCommandWritesBeforeRestore"/>). Actor loop. Nothing
    /// on a node without applications.</summary>
    /// <remarks>The drop under Reset Node is not redundant with the generation that
    /// <see cref="EndCommandWritesBeforeRestore"/> moves. The generation keeps a stale write from being
    /// told, but only when the next round reaches it; until then the stale writes would still count
    /// against <see cref="CommandQueueCapacity"/>. A queue filled before the reset would then refuse,
    /// with <c>0800 0022h</c>, a command write committed after the restore — by another thread, or by
    /// another application from its <see cref="ICanOpenApplication.OnReset"/> — which belongs to the
    /// device the reset started and must be told.</remarks>
    private void DropQueuedCommandWritesForReset(bool communicationOnly)
    {
        if (_applications.Length == 0) return;
        lock (_applicationGate)
        {
            if (!communicationOnly) _queuedCommands = null;
            _queuedReceptions = null;
        }
    }
}
