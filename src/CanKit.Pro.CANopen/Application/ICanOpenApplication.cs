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
/// Every callback runs on the node's actor loop, except <see cref="Attach"/> (the thread that opens
/// the node) and <see cref="ValidateWrite"/> (the writing thread, under the dictionary's write
/// gate). An exception from an actor-loop callback is reported on
/// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>; it does not stop the node or the next
/// application.
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
    void Attach(ICanOpenApplicationHost host);

    /// <summary>Decides a write to an object. Runs on the writing thread under the dictionary's write
    /// gate: do not block, do not call back into the dictionary's writers.</summary>
    OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value);

    /// <summary>Entries were written (accepted by <see cref="ValidateWrite"/> and stored).</summary>
    void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries);

    /// <summary>The node's NMT state changed.</summary>
    void OnNmtStateChanged(NmtState previous, NmtState current);

    /// <summary>An NMT reset ran, after the node restored its dictionary and before it announces itself.</summary>
    void OnReset(NmtCommand command);

    /// <summary>A communication failure began (<see langword="true"/>) or ended; see
    /// <see cref="CanOpenApplicationRequirements.CommunicationFailureMonitoring"/>.</summary>
    void OnCommunicationFailureChanged(bool active);
}
