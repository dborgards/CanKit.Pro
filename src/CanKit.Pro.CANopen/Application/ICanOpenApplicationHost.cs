using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen.Application;

/// <summary>What the node gives an attached application: its dictionary, its clock and state, and the
/// ways to ask it to transmit, report an error or change state. The node implements it, one host
/// per application. The thread each member may be called on is given on the member.</summary>
internal interface ICanOpenApplicationHost
{
    /// <summary>The node's object dictionary. Any thread.</summary>
    ObjectDictionary ObjectDictionary { get; }

    /// <summary>The node's NMT state. Actor loop, or <see cref="ICanOpenApplication.Attach"/>.</summary>
    NmtState State { get; }

    /// <summary>The node's monotonic clock, the time source its deadlines run on. Any thread.</summary>
    TimeSpan Now { get; }

    /// <summary>Whether a communication failure is active: bus-off, a heartbeat timeout or life
    /// guarding, the condition <see cref="ICanOpenApplication.OnCommunicationFailureChanged"/> reports.
    /// Actor loop.</summary>
    /// <remarks>The whole condition is tracked only on a node opened with
    /// <see cref="CanOpenNodeOptions.EnableErrorBehavior"/>, which
    /// <see cref="CanOpenApplicationRequirements.CommunicationFailureMonitoring"/> requires. On another
    /// node only the parts that other options need are tracked (heartbeat timeouts with
    /// <see cref="CanOpenNodeOptions.EmitGuardingErrorEmcy"/>, for one), so this can read
    /// <see langword="false"/> through a failure.</remarks>
    bool CommunicationFailureActive { get; }

    /// <summary>Arms a one-shot deadline on the node's clock (<see cref="Now"/>);
    /// <paramref name="onExpired"/> runs on the actor loop as a callback of this application: what it
    /// writes there counts as the application's own, and what it throws is reported. Any thread.</summary>
    /// <remarks>The deadline is the application's: an NMT reset does not cancel it, and it fires in
    /// every NMT state. For a period, arm again from <paramref name="onExpired"/> with the delay to
    /// the next due time as read from <see cref="Now"/>, so the period does not drift by the
    /// handling time.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onExpired"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The node's actor has ended.</exception>
    IDeadline Arm(TimeSpan delay, Action onExpired);

    /// <summary>Sets the device type (<c>1000h</c>) of a logical device. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is not a
    /// logical device number.</exception>
    /// <exception cref="ArgumentException">The application does not claim the device-type object of
    /// <paramref name="logicalDevice"/>.</exception>
    void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType);

    /// <summary>Sets the default of TPDO <paramref name="pdoNumber"/>: its mapping, the transmission
    /// type <c>FFh</c> and its COB-ID. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <remarks>
    /// <para>
    /// The node writes the default into the PDO records once every application has attached, through
    /// the re-mapping procedure <see cref="ICanOpenNode.ConfigureTpdo"/> runs (CiA 301 §7.5.2.38),
    /// so the mapping is checked as it would be over SDO and the PDO engine runs with what the
    /// records hold. The transmission type is <c>FFh</c>, event-driven and profile-specific (CiA 301
    /// Table 72), the type CiA 401-1 §6.2.6 gives a default TPDO; inhibit time and event timer are
    /// 0. The mapping is copied when this is called.
    /// </para>
    /// <para>
    /// The COB-ID of TPDO 1 to 4 is the pre-defined connection set of the node-id, with bit 31 set
    /// when <paramref name="enabled"/> is <see langword="false"/>. Above TPDO 4 CiA 301 pre-defines no
    /// COB-ID: the record gets the mapping and keeps the invalid COB-ID the node writes for an
    /// unassigned TPDO, <c>C0000000h</c> (no CAN-ID, no RTR), whatever <paramref name="enabled"/>
    /// says, for the master to assign one.
    /// </para>
    /// <para>
    /// The default is part of the power-on values: an NMT reset returns the records to it, and
    /// <c>1011h</c> restores it. When LSS gives the node another node-id, the node moves a COB-ID that
    /// still names the pre-defined connection set of the node-id it leaves to the new one, as it does
    /// for its own default COB-IDs. On an LSS slave opened without a node-id the COB-ID names node-id
    /// <c>FFh</c>, and is written even where that is a restricted CAN-ID (RPDO 4: <c>5FFh</c>): the
    /// node has no PDOs on the bus until it is given a node-id, and then moves the COB-ID to it. Only
    /// this call does so; a write to the record keeps its checks. A PDO a device description declares is left as the file has it:
    /// the call has no effect on it. A PDO the description leaves out does not exist (CiA 301 §7.3.3),
    /// and a default for it fails the open.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pdoNumber"/> is below 1 or above
    /// the <see cref="CanOpenApplicationRequirements.PdoCount"/> the application asks for.</exception>
    /// <exception cref="ArgumentException">An entry of <paramref name="mapping"/> other than a dummy
    /// entry lies in no range the application claims: a profile maps the objects it owns. Two
    /// applications setting the same TPDO, and a mapping the records refuse, fail the open with an
    /// <see cref="ArgumentException"/> for <c>options</c> once every application has attached.</exception>
    void SetDefaultTpdo(int pdoNumber, PdoMapping mapping, bool enabled = true);

    /// <summary>Sets the default of RPDO <paramref name="pdoNumber"/>: its mapping, the transmission
    /// type <c>FFh</c> and its COB-ID. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <remarks>As <see cref="SetDefaultTpdo"/>, with <see cref="ICanOpenNode.ConfigureRpdo"/>'s
    /// procedure, the type CiA 401-1 §6.2.5 gives a default RPDO, and the RPDO always enabled up to
    /// RPDO 4. Above RPDO 4 the record keeps the invalid COB-ID the node writes for an unassigned
    /// RPDO, <c>80000000h</c>. On an LSS slave without a node-id RPDO 4's COB-ID is <c>5FFh</c>, a
    /// restricted CAN-ID, and is written all the same (see <see cref="SetDefaultTpdo"/>).</remarks>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pdoNumber"/> is below 1 or above
    /// the <see cref="CanOpenApplicationRequirements.PdoCount"/> the application asks for.</exception>
    /// <exception cref="ArgumentException">An entry of <paramref name="mapping"/> other than a dummy
    /// entry lies in no range the application claims. Two applications setting the same RPDO, and a
    /// mapping the records refuse, fail the open as for <see cref="SetDefaultTpdo"/>.</exception>
    void SetDefaultRpdo(int pdoNumber, PdoMapping mapping);

    /// <summary>Asks the node to transmit what maps <paramref name="entries"/>. Actor loop.</summary>
    /// <remarks>
    /// <para>
    /// A write to a claimed entry triggers no transmission by itself: the application asks with this.
    /// What follows is what a write does to an unclaimed entry: the event-driven TPDOs and source
    /// MPDOs that map one of the entries transmit, subject to their inhibit time; the
    /// synchronous-acyclic TPDOs latch for the next SYNC; the transmit SRDOs trigger. Whatever
    /// <see cref="CanOpenNodeOptions.EnableChangeOfStateTpdo"/> and
    /// <see cref="CanOpenNodeOptions.EnableChangeOfStateSrdo"/> say: they switch off that automatism,
    /// not a request.
    /// </para>
    /// <para>
    /// Acts only in Operational; in any other state nothing happens and nothing is kept for later.
    /// Every entry must be claimed by this application, and all are checked before any is
    /// triggered: a list with one foreign entry triggers nothing. An empty list does nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Called off the node's actor loop.</exception>
    /// <exception cref="ArgumentException">An entry is not claimed by this application.</exception>
    void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries);

    /// <summary>Raises an error of the application: records <paramref name="errorRegisterBits"/> and
    /// the generic bit (bit 0) as part of the node's share of <c>1001h</c>, and orders the EMCY.
    /// Actor loop; not from <see cref="ICanOpenApplication.Attach"/>.</summary>
    /// <remarks>
    /// <para>
    /// Once per error event: raising an error code that is active already does nothing, whatever the
    /// bits. The EMCY carries the node's part of the register as of the moment the EMCY is ordered
    /// (for a held one, its release) and the application's part as of its transmission, and follows
    /// the node's rules. With the EMCY switched off (bit 31 of <c>1014h</c>) the error is still recorded and
    /// written to <c>1001h</c>, without a frame. In Stopped the EMCY is held, and only the most
    /// recent EMCY the node holds goes out when it leaves Stopped. In Initialisation the EMCY is held
    /// until the boot-up has been ordered, and every one held there goes out behind it in the order
    /// raised; <c>1001h</c> shows the error from that EMCY on.
    /// </para>
    /// <para>An NMT reset ends every error without an EMCY. An EMCY held in Initialisation that has
    /// not been transmitted when the reset comes is dropped with its error, frame and <c>1001h</c>
    /// alike: one still held, and one already released behind the boot-up but still waiting for its
    /// turn (a master that resets the node as soon as it sees the boot-up).</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is <c>0000h</c>,
    /// the error reset, which <see cref="ClearError"/> sends.</exception>
    /// <exception cref="ArgumentException"><paramref name="manufacturerSpecific"/> is longer than
    /// five bytes.</exception>
    /// <exception cref="InvalidOperationException">Called off the node's actor loop.</exception>
    void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default);

    /// <summary>Ends an error raised with <see cref="RaiseError"/>. Actor loop.</summary>
    /// <remarks>Nothing when the error is not active. Otherwise the node orders error code
    /// <c>0000h</c>, carrying what the register still holds, under the same rules as the EMCY of
    /// <see cref="RaiseError"/>: in Stopped it is held like any other (and replaces an EMCY held
    /// there), and with the EMCY switched off it updates <c>1001h</c> without a frame. In
    /// Initialisation the error's own EMCY is still held: it is dropped instead, and nothing is
    /// sent.</remarks>
    /// <exception cref="InvalidOperationException">Called off the node's actor loop.</exception>
    void ClearError(ushort errorCode);

    /// <summary>Asks the node to enter Pre-Operational or Stopped: to leave Operational, or to go
    /// where the application's own fault calls for. Only the master starts the node. Actor loop.</summary>
    /// <remarks>
    /// <para>
    /// Does nothing in Initialisation (a reset or a start-up, which ends in Pre-Operational by
    /// itself) and nothing when the node is in <paramref name="target"/> already. Otherwise every
    /// application, this one included, is told through
    /// <see cref="ICanOpenApplication.OnNmtStateChanged"/>. Asked from that callback, the change is
    /// queued and told after the callback returns. Asked from any other callback while no round of
    /// <see cref="ICanOpenApplication.OnNmtStateChanged"/> is in progress, it is told at once: this
    /// application's <see cref="ICanOpenApplication.OnNmtStateChanged"/> runs before this call
    /// returns. Asked from a callback that itself runs inside such a round (an
    /// <see cref="ICanOpenApplication.OnCommunicationFailureChanged"/> reported inside a write made
    /// from <see cref="ICanOpenApplication.OnNmtStateChanged"/>, say), it is queued like one asked
    /// from that callback.
    /// </para>
    /// <para>
    /// The change being told is complete by then, its state-change heartbeat (with <c>1017h</c> ≠ 0)
    /// included, so the master sees each state in the order the node passed through it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is neither
    /// Pre-Operational nor Stopped.</exception>
    /// <exception cref="InvalidOperationException">Called off the node's actor loop.</exception>
    void RequestNmtState(NmtState target);
}
