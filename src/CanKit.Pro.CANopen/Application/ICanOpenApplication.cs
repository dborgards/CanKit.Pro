using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Nmt;

namespace CanKit.Pro.CANopen.Application;

/// <summary>What an application asks of the node it is attached to, checked when the node opens.
/// Asking for more than the node was opened with fails the open.</summary>
internal sealed class CanOpenApplicationRequirements
{
    /// <summary>Asks for nothing.</summary>
    public static CanOpenApplicationRequirements None { get; } = new();

    /// <summary>The highest PDO number the application configures; <c>0</c> for none. Must not
    /// exceed the node's PDO count.</summary>
    public int PdoCount { get; init; }

    /// <summary>The application wants <see cref="ICanOpenApplication.OnCommunicationFailureChanged"/>.
    /// Needs <see cref="CanOpenNodeOptions.EnableErrorBehavior"/>.</summary>
    public bool CommunicationFailureMonitoring { get; init; }

    /// <summary>The error behaviour record <c>1029h</c> the application's profile defines;
    /// <see langword="null"/> for the node's generic record (sub-index 00h = 01h, the communication
    /// error at 01h with default 00h). Needs <see cref="CanOpenNodeOptions.EnableErrorBehavior"/>,
    /// without which the node has no <c>1029h</c>.</summary>
    /// <remarks>
    /// <para>
    /// Read with the rest of the requirements, before the node builds its dictionary, so the record
    /// exists as laid out from the first frame and a device description is loaded against it.
    /// Values outside <c>00h</c> to <c>02h</c>, more than 253 additional classes, two applications
    /// asking for layouts that differ in any value, and a layout without
    /// <see cref="CanOpenNodeOptions.EnableErrorBehavior"/> fail the open with an
    /// <see cref="System.ArgumentException"/> for <c>options</c>. Two applications asking for the
    /// same layout share the record. When one application asks for a layout and another for none, the
    /// record is the layout, for both: its defaults hold for the application that asked for none too —
    /// on a node with a CiA 401 and a CiA 408 device, <c>1029h:01</c> powers on as CiA 408's 01h, not as
    /// the generic 00h, so a communication failure leaves the node Operational.
    /// </para>
    /// <para>
    /// A device description that declares <c>1029h</c> wins, as for every object it declares: the
    /// node keeps the access it gives each sub-index and the values it gives them, also where they
    /// differ from the layout's defaults, and corrects a value outside <c>00h</c> to <c>02h</c> to
    /// the default, as it does for its own record. The file must declare every sub-index the layout
    /// names, or the open fails naming those it lacks; sub-index 00h stays the constant the layout
    /// gives, and a sub-index above the layout's is not created, as without a layout. A description
    /// that does not declare <c>1029h</c> leaves the record as laid out.
    /// </para>
    /// <para>
    /// The node acts on the communication error, sub-index 01h, as it does with its generic record
    /// (CiA 301 v4.2.0 §7.5.2.32): only from Operational, whatever the value. The further classes are
    /// the profile's to act on; <see cref="ICanOpenApplicationHost.ErrorBehaviorTarget"/> says what
    /// one of them asks for in the present state. Reset Node and Reset Communication restore the
    /// record to its power-on values, <c>1010h</c> stores it and <c>1011h</c> returns it to the
    /// layout's defaults (or the file's), as for the generic record.
    /// </para>
    /// </remarks>
    public CanOpenErrorBehaviorLayout? ErrorBehavior { get; init; }
}

/// <summary>
/// What a device profile gives the node: the object indices it owns, and the callbacks the node
/// makes into it. Handed to the node in <see cref="CanOpenNodeOptions.ApplicationFactories"/> and attached
/// when the node opens.
/// </summary>
/// <remarks>
/// <para>
/// Every callback runs on the node's actor loop, except <see cref="Attach"/> (the thread that opens
/// the node) and <see cref="ValidateWrite"/> (the writing thread, under the dictionary's write
/// gate). The actor-loop callbacks of all applications run one at a time, in attach order where
/// several are told the same thing. An exception from an actor-loop callback is reported on
/// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>; it does not stop the node or the next
/// application.
/// </para>
/// <para>
/// Options can register a factory to create an application for each node. Registering a specific
/// instance passes that instance to every open. Profiles that bind to one node, including CiA 401
/// and CiA 408 devices, require a fresh instance on each open.
/// The node owns only the resource returned by a successful <see cref="Attach"/>.
/// </para>
/// </remarks>
internal interface ICanOpenApplication
{
    /// <summary>The name that error messages use for this application.</summary>
    string Name { get; }

    /// <summary>The object indices the application owns, within <c>2000h</c> to <c>BFFFh</c>: the
    /// manufacturer-specific area, the standardized profile areas of the eight logical devices, and
    /// the standardized network and system variable areas (CiA 301 v4.2.0 §7.4.1, Table 41). Below
    /// lie the node's own areas, above it <c>C000h</c> to <c>FFFFh</c> are reserved; a range that
    /// reaches either, by one index included, fails the open with an <see cref="System.ArgumentException"/>
    /// for <c>options</c>. The ranges of one node's applications must not overlap. Read when the node
    /// opens, once.</summary>
    IReadOnlyList<CanOpenIndexRange> ClaimedRanges { get; }

    /// <summary>What the application needs from the node. Read when the node opens, once.</summary>
    CanOpenApplicationRequirements Requirements { get; }

    /// <summary>Startup transmissions requested by this profile. Null opts out. Any list, including
    /// an empty one, requests every valid profile-event TPDO (type FFh) on entry to Operational.
    /// The listed entries also trigger their usual mapped transmissions as in
    /// <see cref="ICanOpenApplicationHost.RequestTransmission"/>. Each must belong to the application.
    /// The node copies and combines the lists after each application's <see cref="Attach"/> and
    /// acts once, after the state callbacks, using the current PDO mappings.</summary>
    IReadOnlyList<CanOpenObjectAddress>? OperationalTransmissionEntries { get; }

    /// <summary>The claimed entries whose every committed write the application is told of, one by
    /// one and with the value written (<see cref="OnCommandWritten"/>), instead of coalesced through
    /// <see cref="OnEntriesWritten"/>: a command word, whose edges a profile reacts to, such as the
    /// fault-reset edge of the CiA 408 v2.2.0 control word (§7.7, Table 7). Empty for none.</summary>
    /// <remarks>
    /// <para>
    /// Read once, when the node opens, right after <see cref="Attach"/> returned, so it can name
    /// what the application declared there. Each entry must lie in a range the application claims,
    /// exist by then, and be of a fixed-width data type of at most 8 bytes; a string or a domain is
    /// not a command. Anything else, and a <see langword="null"/> list, fails the open with an
    /// <see cref="System.ArgumentException"/> for <c>options</c>.
    /// </para>
    /// <para>
    /// A write to a command entry that the queue of <see cref="OnCommandWritten"/> has no room for
    /// is refused (see there). A declaration through <c>Add*</c> that replaces a command entry is no
    /// written value: it is reported through <see cref="OnEntriesWritten"/>, as for any claimed entry.
    /// Reset Node drops the command writes not yet told, whose values it restores; Reset
    /// Communication, which leaves them in place, does not (see <see cref="OnCommandWritten"/>).
    /// </para>
    /// </remarks>
    IReadOnlyList<CanOpenObjectAddress> CommandEntries { get; }

    /// <summary>The node is opening: declare the objects, set the device type and the default PDOs.
    /// Runs on the constructing thread, before the node boots; what the application declared here is
    /// what an NMT reset restores and what <c>1010h</c> stores.</summary>
    /// <remarks>A write the application makes here to an entry it claims is validated
    /// (<see cref="ValidateWrite"/>, on this thread) but not reported through
    /// <see cref="OnEntriesWritten"/>. The host's actor-loop members, <see cref="ICanOpenApplicationHost.RaiseError"/>
    /// among them, cannot be called from here.</remarks>
    /// <returns>An optional resource for this attachment. The node disposes resources once in reverse
    /// attach order on shutdown or a later opening failure. A throwing Attach cleans up its own
    /// partial work. Cleanup runs on the closing thread when no callback is active. Otherwise it runs on the thread pool
    /// after the last callback returns, including one that disposed the node itself. A failure during deferred cleanup is reported through
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>. No further callbacks from this node
    /// enter the application after cleanup starts. NMT resets keep the attachment.</returns>
    IDisposable? Attach(ICanOpenApplicationHost host);

    /// <summary>Decides a write to an entry the application claims.</summary>
    /// <remarks>
    /// <para>
    /// Asked for every validated write to a claimed entry, whoever makes it — an SDO download, an
    /// RPDO, MPDO or SRDO, the local user through <see cref="ObjectDictionary"/>, and the
    /// application itself. <paramref name="isOwnWrite"/> identifies an explicit call to this application's
    /// <see cref="ICanOpenApplicationHost.WriteOwn"/>. A direct dictionary write, including one
    /// nested in the same callback or made by an event subscriber, is validated as external.
    /// Not asked for a write through
    /// <see cref="ObjectDictionary.WriteRawUnchecked"/>, nor for the values an NMT reset restores.
    /// </para>
    /// <para>
    /// Runs on the writing thread, under the dictionary's write gate, which on an SDO download or a
    /// PDO is the actor loop: read and decide only. Do not block, do not write the dictionary, do not
    /// wait for the actor loop. A throw is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> and the write is refused with
    /// <see cref="Sdo.SdoAbortCode.General"/>.
    /// </para>
    /// <para>
    /// Accepting a write does not make it stored. A write to a command entry
    /// (<see cref="CommandEntries"/>) that this accepted is still refused when the queue of
    /// <see cref="OnCommandWritten"/> is full, with <c>0800 0022h</c> (see there), and the
    /// dictionary refuses a write whose entry is gone by the time it is stored. Neither is told
    /// back; decide from what is in the dictionary, not from what was accepted here.
    /// </para>
    /// </remarks>
    OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value, bool isOwnWrite);

    /// <summary>Entries the application claims were written and stored.</summary>
    /// <remarks>
    /// <para>
    /// Every stored write counts, whether or not it changed the value, except a written value of a
    /// command entry (<see cref="CommandEntries"/>), which goes to <see cref="OnCommandWritten"/>,
    /// one call per write, instead. A write that <see cref="ValidateWrite"/> refused, or answered with <see cref="OdWriteDecision.Handled"/>,
    /// stored nothing and is not reported. Coalesced: the entries written since the last call arrive
    /// in one list, each once, sorted by index and then sub-index. The call comes one actor job after
    /// the write, so a change of NMT state that followed the write can have been reported first.
    /// </para>
    /// <para>
    /// Not reported: what the application writes on the actor loop from inside one of its own
    /// callbacks, deadline callbacks (<see cref="ICanOpenApplicationHost.Arm"/>) and the work it
    /// posts (<see cref="ICanOpenApplicationHost.Post"/>) included; the values
    /// an NMT reset restores; writes made during <see cref="Attach"/>. What another application
    /// writes to these entries from its callbacks is reported.
    /// </para>
    /// <para>
    /// The rule for its own writes goes by thread and moment, not by origin: every write on the actor
    /// loop while one of this application's callbacks is the innermost callback running counts as its
    /// own. A callback of another application that runs nested inside one of this application's (its
    /// <see cref="OnNmtStateChanged"/>, run by a <see cref="ICanOpenApplicationHost.RequestNmtState"/>
    /// this application made) is the innermost one then, and what it writes here is reported. A profile that raises a
    /// .NET event synchronously from a callback, and whose subscriber sets a value through the
    /// profile's API, has that write suppressed as well.
    /// </para>
    /// </remarks>
    void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries);

    /// <summary>A command entry (<see cref="CommandEntries"/>) was written: one call per committed
    /// write, in the order the writes were committed, with a copy of the value each stored.</summary>
    /// <remarks>
    /// <para>
    /// <paramref name="source"/> identifies the service at commit, even for a nested dictionary write.
    /// Its answering RPDO number is present when this claimant will receive the corresponding
    /// <see cref="OnRpdoReceived"/> callback. Reset Communication cancels that reception.
    /// Every write that stored a value counts, from wherever it came — an SDO download, an RPDO, the
    /// local user through <see cref="ObjectDictionary"/> — also when it stored the value the entry
    /// held already, and also when a second write followed before the actor loop came round: two
    /// writes, two calls, each with its own value. The value is copied under the dictionary's write
    /// gate as the write is committed, so a later write cannot change what this call carries. A
    /// write that was refused — by <see cref="ValidateWrite"/>, by the dictionary for its size, by
    /// the SDO server for the access — stored nothing and is not reported, and neither is one that
    /// <see cref="ValidateWrite"/> answered with <see cref="OdWriteDecision.Handled"/>. The rule for
    /// the application's own writes is that of <see cref="OnEntriesWritten"/>: what it writes from
    /// inside one of its callbacks is not reported to it, nor what it writes in <see cref="Attach"/>,
    /// nor the values an NMT reset restores.
    /// </para>
    /// <para>
    /// The calls come on the actor loop, one actor job after the writes, so a change of NMT state that
    /// followed a write can have been told first (<see cref="OnNmtStateChanged"/>); the state the
    /// write was made in is not carried. They come in the job that tells
    /// <see cref="OnEntriesWritten"/> too: first every command write committed since the last such
    /// job, in commit order, then the coalesced <see cref="OnEntriesWritten"/>, then
    /// <see cref="OnRpdoReceived"/>. A command entry is never reported through
    /// <see cref="OnEntriesWritten"/> for a written value. A call that throws
    /// is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>, and the next write is still told.
    /// </para>
    /// <para>
    /// The writes wait in a queue of the node's, which holds at most
    /// <see cref="CanOpenNode.CommandQueueCapacity"/> of them, for all applications together: the
    /// actor's mailbox is unbounded, and a master that writes a command
    /// entry faster than the actor loop comes round must not make the node hold an unbounded number
    /// of values for it. A write that finds the queue full is refused before it is stored, so no
    /// committed write is lost: an SDO download is aborted with <c>0800 0022h</c>, "data cannot be
    /// transferred or stored to the application because of the present device state" (CiA 301
    /// v4.2.0 §7.2.4.3.17, Table 22); a local write throws the <see cref="System.ArgumentException"/>
    /// of <see cref="ObjectDictionary.WriteRaw"/> carrying that code; an RPDO's entry is not
    /// written, the rest of the RPDO is, and the node reports the refusal on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>, as for every RPDO entry the
    /// dictionary refuses. The application has seen <see cref="ValidateWrite"/> accept such a write
    /// by then: the queue is checked after it, so a value the application refuses is refused with
    /// the application's code whatever the queue holds.
    /// </para>
    /// <para>
    /// Reset Node drops the writes still queued, before the applications are told of the reset
    /// (<see cref="OnNmtStateChanged"/> into Initializing), and so does the disposal of the node. A
    /// write committed after that and before the reset restores the dictionary — by another thread,
    /// or by another application while the applications are told of the reset — is not told either.
    /// Reset Node restores the application's objects, command entries included (see
    /// <see cref="OnReset"/>), so such a write belongs to the device the reset ended and its value is
    /// gone. What is committed after the restore, from <see cref="OnReset"/> on, is told.
    /// </para>
    /// <para>
    /// Reset Communication drops nothing of this: it restores <c>1000h</c> to <c>1FFFh</c> only, and a
    /// command entry, which lies in a claimed range from <c>2000h</c> up, keeps every value written to
    /// it, so the application must be told of each. A write queued before the reset, and one
    /// committed while the reset runs, is told in commit order in the first round after the reset
    /// job, so after the notifications of the reset (<see cref="OnNmtStateChanged"/> into
    /// Initializing, <see cref="OnReset"/>, <see cref="OnNmtStateChanged"/> into Pre-Operational) when
    /// that job came first; a round already queued ahead of the reset tells its writes before it.
    /// </para>
    /// </remarks>
    void OnCommandWritten(CanOpenObjectAddress entry, byte[] value, CanOpenWriteSource source);

    /// <summary>RPDO <paramref name="pdoNumber"/> was received and its data written: once per frame
    /// the node took, also when it carried the data of the last one.</summary>
    /// <remarks>
    /// <para>
    /// Told for the RPDOs of the logical devices the application set a device type for
    /// (<see cref="ICanOpenApplicationHost.SetDeviceType"/>): logical device <c>n</c> uses the PDO
    /// numbers <c>n·64 + 1</c> to <c>n·64 + 64</c> (CiA 301 v4.2.0 §7.2.2.1, Table 3;
    /// <see cref="CanOpenLogicalDeviceAddressing.PdoNumber"/>). An RPDO outside every such range
    /// tells no application. A profile answers a received RPDO with this, as CiA 408 v2.2.0 §8.4.1
    /// asks of its TPDOs: "A TPDO with transmission type 255 shall be transmitted immediately after
    /// receiving the corresponding RPDO"; it sends the answer with
    /// <see cref="ICanOpenApplicationHost.RequestTransmissionOfTpdo"/>.
    /// </para>
    /// <para>
    /// A frame counts when the node takes it: the RPDO exists (valid COB-ID), the node is
    /// Operational (CiA 301 v4.2.0 §7.3.2.2.5, Table 37), and the frame carries at least the mapped
    /// length (§7.5.2.36). An entry of the mapping that the dictionary or the application refused is
    /// still part of a frame the node took. An SDO download or a local write of the same entries is
    /// no reception and tells nothing here, whatever values it writes; neither does an MPDO, which
    /// is addressed by its object, not by an RPDO of the application's.
    /// </para>
    /// <para>
    /// A synchronous RPDO (transmission type <c>00h</c> to <c>F0h</c>) passes its data to the
    /// application with the SYNC that follows it (§7.2.2.2), and is told then, once for the frame
    /// whose data the SYNC applied: the node keeps the last frame received before the SYNC, so a frame
    /// that a later one replaced before the SYNC is not told.
    /// </para>
    /// <para>
    /// Actor loop, one actor job after the frame, so a change of NMT state that followed the frame
    /// can have been told first (<see cref="OnNmtStateChanged"/>): the node can have left Operational
    /// by the time it is told of an RPDO it took there. It comes in the job that tells the writes: after
    /// <see cref="OnCommandWritten"/> and <see cref="OnEntriesWritten"/>, so the application has been
    /// told the RPDO's own writes when it hears of the RPDO. Several RPDOs taken before that job are
    /// told in the order taken, after every write of the job, including writes committed between the
    /// frames. An NMT reset, either command, drops the receptions not yet told: the PDOs of the
    /// device before the reset are gone with it, whatever becomes of their command writes (see
    /// <see cref="OnCommandWritten"/>).
    /// </para>
    /// </remarks>
    void OnRpdoReceived(int pdoNumber);

    /// <summary>The node's NMT state changed from <paramref name="previous"/> to
    /// <paramref name="current"/>.</summary>
    /// <remarks>
    /// <para>
    /// Every application is told every change, in the order the changes happened. A change made
    /// while the applications are being told of an earlier one (a
    /// <see cref="ICanOpenApplicationHost.RequestNmtState"/> from this callback, say) is queued and
    /// told after this callback returns, so <paramref name="current"/> need not equal
    /// <see cref="ICanOpenApplicationHost.State"/>.
    /// </para>
    /// <para>
    /// At start-up the application is told Initializing to PreOperational once, after the boot-up has
    /// been ordered; an LSS slave waiting for its node-id is told when it has been given one and
    /// boots. On a reset see <see cref="OnReset"/>.
    /// </para>
    /// </remarks>
    void OnNmtStateChanged(NmtState previous, NmtState current);

    /// <summary>An NMT reset (<paramref name="command"/>) is under way: the node is in Initialisation,
    /// has restored what the command restores, and has not announced itself yet.</summary>
    /// <remarks>
    /// <para>
    /// Reset Node restores the application's declared objects with the rest; Reset Communication
    /// restores <c>1000h</c> to <c>1FFFh</c> only. The restored values are not reported through
    /// <see cref="OnEntriesWritten"/>. Every error the application raised has ended, without an EMCY:
    /// raise again what persists; its EMCY follows the boot-up.
    /// <see cref="ICanOpenApplicationHost.RequestNmtState"/> does nothing here.
    /// </para>
    /// <para>
    /// The order on a reset: <see cref="OnNmtStateChanged"/> from the state before to Initializing,
    /// <see cref="OnCommunicationFailureChanged"/>(<see langword="false"/>) to an application that
    /// was last told a failure, this call, <see cref="ICanOpenNode.ApplicationReset"/>, and
    /// <see cref="OnNmtStateChanged"/> from Initializing to PreOperational once the boot-up has been
    /// ordered — for an LSS slave left without a node-id, only once it has been given one. The first
    /// <see cref="OnNmtStateChanged"/> of that list is not made when the node was in Initialisation
    /// already: a reset taken while an LSS slave waits for its node-id.
    /// </para>
    /// <para>
    /// When a cause of the communication failure persists, <see cref="OnCommunicationFailureChanged"/>
    /// (<see langword="true"/>) can follow once the node detects it anew (see there): for bus-off at
    /// the earliest right after the notification from Initializing to PreOperational; on an LSS slave
    /// left without a node-id, a heartbeat timeout can report it while the node still waits in
    /// Initialisation.
    /// </para>
    /// </remarks>
    void OnReset(NmtCommand command);

    /// <summary>A communication failure began (<see langword="true"/>) or ended: bus-off, a heartbeat
    /// timeout or life guarding, the condition the error behaviour of <c>1029h</c> acts on. Only with
    /// <see cref="CanOpenApplicationRequirements.CommunicationFailureMonitoring"/>.</summary>
    /// <remarks>
    /// <para>
    /// Told before the node acts on the failure, so outputs can go to their fault value before the
    /// node leaves Operational. The failure is a level: each application is told when the condition
    /// differs from what it was last told, so it hears each change once, and never a state that was
    /// withdrawn before its turn came.
    /// </para>
    /// <para>
    /// An NMT reset ends the failure as part of its clearing and tells that once the clearing is
    /// done, before <see cref="OnReset"/>. A cause that persists is reported again once the node
    /// detects it anew: a controller still bus-off when the node samples it afresh, right after the
    /// boot-up; a silent heartbeat producer when its watch next times out, at the latest one consumer
    /// heartbeat time later; life guarding when the master has polled again, which starts guarding
    /// anew, and the node life time then lapses without a poll.
    /// </para>
    /// <para>
    /// A write from this callback that changes the condition (<c>1016h</c>, <c>100Ch</c>,
    /// <c>100Dh</c>) is not reported into it: the node looks again after it returns. The same write
    /// from another of the application's actor-loop callbacks reports the change at once, inside the
    /// write, before the write returns.
    /// </para>
    /// </remarks>
    void OnCommunicationFailureChanged(bool active);
}
