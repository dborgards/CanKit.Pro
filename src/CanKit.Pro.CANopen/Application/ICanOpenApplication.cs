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
}

/// <summary>
/// What a device profile gives the node: the object indices it owns, and the callbacks the node
/// makes into it. Handed to the node in <see cref="CanOpenNodeOptions.Applications"/> and attached
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
/// The end of an application's life is not part of the contract yet, and is an open question for
/// the publication of this interface. As things stand the application is told nothing when the
/// node is disposed, and nothing when the open fails after its <see cref="Attach"/> returned (a
/// later application's <see cref="Attach"/> throws, or a check made after every application has
/// attached refuses the options). The same instance handed to a second node is attached to that
/// node too, with nothing refused or shared between the two.
/// </para>
/// </remarks>
internal interface ICanOpenApplication
{
    /// <summary>The name that error messages use for this application.</summary>
    string Name { get; }

    /// <summary>The object indices the application owns, within <c>2000h</c> to <c>FFFFh</c>. The
    /// ranges of one node's applications must not overlap. Read when the node opens, once.</summary>
    IReadOnlyList<CanOpenIndexRange> ClaimedRanges { get; }

    /// <summary>What the application needs from the node. Read when the node opens, once.</summary>
    CanOpenApplicationRequirements Requirements { get; }

    /// <summary>The node is opening: declare the objects, set the device type. Runs on the constructing
    /// thread, before the node boots; what the application declared here is what an NMT reset
    /// restores and what <c>1010h</c> stores.</summary>
    /// <remarks>A write the application makes here to an entry it claims is validated
    /// (<see cref="ValidateWrite"/>, on this thread) but not reported through
    /// <see cref="OnEntriesWritten"/>. The host's actor-loop members, <see cref="ICanOpenApplicationHost.RaiseError"/>
    /// among them, cannot be called from here.</remarks>
    void Attach(ICanOpenApplicationHost host);

    /// <summary>Decides a write to an entry the application claims.</summary>
    /// <remarks>
    /// <para>
    /// Asked for every validated write to a claimed entry, whoever makes it — an SDO download, an
    /// RPDO, MPDO or SRDO, the local user through <see cref="ObjectDictionary"/>, and the
    /// application itself — and told nothing about where it came from. Not asked for a write through
    /// <see cref="ObjectDictionary.WriteRawUnchecked"/>, nor for the values an NMT reset restores.
    /// </para>
    /// <para>
    /// Runs on the writing thread, under the dictionary's write gate, which on an SDO download or a
    /// PDO is the actor loop: read and decide only. Do not block, do not write the dictionary, do not
    /// wait for the actor loop. A throw is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> and the write is refused with
    /// <see cref="Sdo.SdoAbortCode.General"/>.
    /// </para>
    /// </remarks>
    OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value);

    /// <summary>Entries the application claims were written and stored.</summary>
    /// <remarks>
    /// <para>
    /// Every stored write counts, whether or not it changed the value. A write that
    /// <see cref="ValidateWrite"/> refused, or answered with <see cref="OdWriteDecision.Handled"/>,
    /// stored nothing and is not reported. Coalesced: the entries written since the last call arrive
    /// in one list, each once, sorted by index and then sub-index. The call comes one actor job after
    /// the write, so a change of NMT state that followed the write can have been reported first.
    /// </para>
    /// <para>
    /// Not reported: what the application writes on the actor loop from inside one of its own
    /// callbacks, deadline callbacks (<see cref="ICanOpenApplicationHost.Arm"/>) included; the values
    /// an NMT reset restores; writes made during <see cref="Attach"/>. What another application
    /// writes to these entries from its callbacks is reported.
    /// </para>
    /// <para>
    /// The rule for its own writes goes by thread and moment, not by origin: every write on the actor
    /// loop while one of this application's callbacks runs counts as its own. A profile that raises a
    /// .NET event synchronously from a callback, and whose subscriber sets a value through the
    /// profile's API, has that write suppressed as well.
    /// </para>
    /// </remarks>
    void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries);

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
    /// ordered — for an LSS slave left without a node-id, only once it has been given one.
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
    /// withdrawn before its turn came. An NMT reset ends the failure as part of its clearing and tells
    /// it once that is done, before <see cref="OnReset"/>.
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
