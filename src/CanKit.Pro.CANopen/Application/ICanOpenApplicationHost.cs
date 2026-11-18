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
    /// <remarks>
    /// <para>
    /// The deadline is the application's: an NMT reset does not cancel it, and it fires in every NMT
    /// state. For a period, arm again from <paramref name="onExpired"/> with the delay to the next due
    /// time as read from <see cref="Now"/>, so the period does not drift by the handling time.
    /// </para>
    /// <para>
    /// Armed before the node has opened — from <see cref="ICanOpenApplication.Attach"/>, or from
    /// another thread while the constructor is still running — the deadline counts from this call,
    /// but a callback that comes due before the node has ordered its start-up is held as a
    /// <see cref="Post"/> is: it runs behind the start-up job, in Pre-Operational, or for an LSS slave
    /// still waiting for its node-id in Initialisation, never while the node is still being built,
    /// and it is dropped when the open fails (#561). Such a deadline has expired when it came due
    /// (<see cref="IDeadline.IsExpired"/>): disposing it while its callback is held does not stop the
    /// callback. Its EMCYs, like every EMCY of the application, do not overtake a boot-up whose send
    /// is still pending (<see cref="RaiseError"/>).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onExpired"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The node's actor has ended.</exception>
    IDeadline Arm(TimeSpan delay, Action onExpired);

    /// <summary>Runs <paramref name="action"/> on the actor loop as a callback of this application:
    /// what it writes there counts as the application's own (see
    /// <see cref="ICanOpenApplication.OnEntriesWritten"/>), and the actor-loop members of this host
    /// can be called from it. Any thread.</summary>
    /// <remarks>
    /// <para>
    /// Called on the actor loop while one of this application's callbacks is the innermost callback
    /// running (the rule of <see cref="ICanOpenApplication.OnEntriesWritten"/>), the action runs at
    /// once, before this call returns, and what it throws reaches the caller as from any other call.
    /// So an application's API that is called from inside its own callback applies in the order it
    /// is called, with nothing deferred behind work the actor holds.
    /// </para>
    /// <para>
    /// Called anywhere else — another thread, or the actor loop outside this application's
    /// callbacks: in <see cref="ICanOpenApplication.ValidateWrite"/>, also when the write it decides
    /// was made from inside one of this application's callbacks (the innermost callback is then
    /// <see cref="ICanOpenApplication.ValidateWrite"/>, under the dictionary's write gate), or in a
    /// callback of another application — the action is queued behind the work the actor has been handed already, and
    /// runs later as a callback of this application; what it throws is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>. The actions one thread queues run in
    /// the order it queued them.
    /// </para>
    /// <para>
    /// Called before the node has opened — from <see cref="ICanOpenApplication.Attach"/>, or from
    /// another thread while the constructor is still running — the action is held, and queued once
    /// the node has ordered its start-up, behind it: it runs in Pre-Operational, or for an LSS slave
    /// still waiting for its node-id in Initialisation, and not while the node is still being built.
    /// When the open fails, a held action is dropped. A deadline that comes due before the node has
    /// opened is held the same way (<see cref="Arm"/>), so no callback of the application runs while
    /// the node is still being built.
    /// </para>
    /// <para>
    /// Once the node is disposed, or after an open that failed, the action is dropped without being
    /// run, and nothing is thrown: an application's producer thread that races the node's disposal
    /// has nothing to catch. An action queued before the disposal and not yet started is dropped as
    /// well. Unlike <see cref="Arm"/>, which throws <see cref="ObjectDisposedException"/>, since a
    /// deadline is a handle the caller keeps.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="action"/> is null.</exception>
    void Post(Action action);

    /// <summary>Sets the device type (<c>1000h</c>) of a logical device. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <remarks>
    /// <para>
    /// Once every application has attached, the node composes what was set (CiA 301 v4.2.0
    /// §7.5.2.1): one logical device, and its word is <c>1000h</c>; several, numbered from 0 without a
    /// gap, and <c>1000h</c> holds the profile number of device 0 with <c>FFFFh</c> above it, while
    /// every device's word is the read-only UNSIGNED32 <c>67FFh + n·800h</c>. The additional
    /// information is the profile's and is kept as set, with two exceptions that fail the open,
    /// before the node boots: a word with profile number <c>0000h</c> takes additional information
    /// <c>0000h</c> or <c>FFFFh</c> only, and the word of a node's only logical device does not carry
    /// <c>FFFFh</c>, which announces a further logical device (both §7.5.2.1).
    /// </para>
    /// <para>
    /// A device description that declares <c>1000h</c> or a chain object wins: the node writes
    /// nothing there, and the open fails when the file declares the object as anything but a read-only
    /// UNSIGNED32 (§7.5.2.1; a type of the same width, or a write access, included), when it holds
    /// another profile number, when the file's <c>1000h</c> does not hold <c>FFFFh</c> in its upper half on a node with several
    /// logical devices (a reader of <c>1000h</c> would stop at the first), or when it holds
    /// <c>FFFFh</c> on a node with one and no <c>67FFh</c> exists for a reader to find. A <c>67FFh</c>
    /// the application declared itself is then the object a reader takes for the device's type, and
    /// it is checked as a described one: a read-only UNSIGNED32 with the profile number set. The
    /// message names the object and the application.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="logicalDevice"/> is not a
    /// logical device number.</exception>
    /// <exception cref="ArgumentException">The application does not claim the device-type object of
    /// <paramref name="logicalDevice"/>. The checks of the composed words and of a described
    /// <c>1000h</c> fail the open with an <see cref="ArgumentException"/> for <c>options</c> once every
    /// application has attached, so that an application that catches the exception cannot keep the
    /// open from failing.</exception>
    void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType);

    /// <summary>Sets the default of TPDO <paramref name="pdoNumber"/>: its mapping, the transmission
    /// type <c>FFh</c> and its COB-ID. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <remarks>
    /// <para>
    /// The node writes the default into the PDO records once every application has attached, through
    /// the re-mapping procedure <see cref="ICanOpenNode.ConfigureTpdo"/> runs (CiA 301 v4.2.0
    /// §7.5.2.38), so the mapping is checked as it would be over SDO and the PDO engine runs with
    /// what the records hold. The transmission type is <c>FFh</c>, event-driven and device-profile
    /// specific (CiA 301 v4.2.0 §7.5.2.37, Table 72), the default CiA 401-1 v3.1.0 §6.2.6 gives a
    /// TPDO; inhibit time and event timer are 0. The mapping is copied when this is called.
    /// </para>
    /// <para>
    /// The COB-ID of TPDO 1 to 4 is the one the generic pre-defined connection set gives the node-id
    /// (CiA 301 v4.2.0 §7.3.3, Table 39), with bit 31 set when <paramref name="enabled"/> is
    /// <see langword="false"/>. That set covers at most four TPDOs (§7.3.3): above TPDO 4 the record
    /// gets the mapping and keeps the invalid COB-ID the node writes for an unassigned TPDO,
    /// <c>C0000000h</c> (no CAN-ID, no RTR), whatever <paramref name="enabled"/> says, for the master
    /// to assign one.
    /// </para>
    /// <para>
    /// The default is part of the power-on values: an NMT reset returns the records to it, and
    /// <c>1011h</c> restores it. When the node's active node-id changes, the node moves a COB-ID that
    /// still names the pre-defined connection set of the node-id it leaves to the new one, as it does
    /// for its own default COB-IDs. Under LSS the active node-id changes when Reset Communication
    /// copies the pending one over, and on a slave without a node-id as soon as it is given one
    /// (CiA 305 v3.0.0 §4.2).
    /// </para>
    /// <para>
    /// On an LSS slave opened without a node-id the COB-ID names node-id <c>FFh</c> and is written even
    /// where that is a restricted CAN-ID (CiA 301 v4.2.0 §7.3.5, Table 40), as for RPDO 4 (<c>5FFh</c>).
    /// Such a slave stays in Initialisation until it is given a node-id (CiA 305 v3.0.0 §4.2), and PDOs
    /// exist only in Operational (CiA 301 v4.2.0 §7.3.2.2.5, Table 37); meanwhile an RPDO on a
    /// restricted CAN-ID takes no frame, every other RPDO takes its own as on a node with a node-id,
    /// and once the slave is given one the node moves the COB-ID to it. Only this call writes such a
    /// COB-ID; a write to the record keeps its checks.
    /// </para>
    /// <para>
    /// The device description wins. A PDO it declares is left as the file has it, and a PDO it leaves
    /// out, which the node then does not have (CiA 301 v4.2.0 §7.3.3: CAN-IDs only for the supported
    /// communication objects), stays out: for either the call has no effect.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pdoNumber"/> is below 1 or above
    /// the <see cref="CanOpenApplicationRequirements.PdoCount"/> the application asks for.</exception>
    /// <exception cref="ArgumentException">An entry of <paramref name="mapping"/> other than a dummy
    /// entry lies in no range the application claims: a profile maps the objects it owns. Two
    /// applications setting the same TPDO, one application setting it twice, and a mapping the
    /// records refuse fail the open with an <see cref="ArgumentException"/> for <c>options</c> once
    /// every application has attached.</exception>
    void SetDefaultTpdo(int pdoNumber, PdoMapping mapping, bool enabled = true);

    /// <summary>Sets the default of RPDO <paramref name="pdoNumber"/>: its mapping, the transmission
    /// type <c>FFh</c> and its COB-ID. <see cref="ICanOpenApplication.Attach"/> only.</summary>
    /// <remarks>As <see cref="SetDefaultTpdo"/>, with <see cref="ICanOpenNode.ConfigureRpdo"/>'s
    /// procedure (CiA 301 v4.2.0 §7.5.2.36), the transmission type of §7.5.2.35 Table 68 that
    /// CiA 401-1 v3.1.0 §6.2.5 gives an RPDO, and the RPDO always enabled up to RPDO 4. Above RPDO 4
    /// the record keeps the invalid COB-ID the node writes for an unassigned RPDO,
    /// <c>80000000h</c>. On an LSS slave without a node-id RPDO 4's COB-ID is <c>5FFh</c>, a
    /// restricted CAN-ID (CiA 301 v4.2.0 §7.3.5, Table 40), and is written all the same (see
    /// <see cref="SetDefaultTpdo"/>); until the slave has a node-id that RPDO takes no frame, so
    /// node 127's SDO response on <c>5FFh</c> still reaches the node's SDO client.</remarks>
    /// <exception cref="InvalidOperationException">Called outside the application's own
    /// <see cref="ICanOpenApplication.Attach"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mapping"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pdoNumber"/> is below 1 or above
    /// the <see cref="CanOpenApplicationRequirements.PdoCount"/> the application asks for.</exception>
    /// <exception cref="ArgumentException">An entry of <paramref name="mapping"/> other than a dummy
    /// entry lies in no range the application claims. Two applications setting the same RPDO, one
    /// setting it twice, and a mapping the records refuse fail the open as for
    /// <see cref="SetDefaultTpdo"/>.</exception>
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
    /// bits. <c>1001h</c> shows the error from this call until <see cref="ClearError"/> or a reset ends
    /// it, in every NMT state and whatever its EMCY still waits for — the inhibit time of
    /// <c>1015h</c>, Stopped, the boot-up: "If a specific error occurs the corresponding bit shall be
    /// set" (CiA 301 v4.2.0 §7.5.2.2, Table 54). No EMCY transmitted later, an older one included,
    /// writes an ended error back. The EMCY carries the node's part of the register as of the moment
    /// the EMCY is ordered (for a held one, its release) and the application's part as of its
    /// transmission, and follows the node's rules for its frame: the inhibit time spaces it as any
    /// other. With the EMCY switched off (bit 31 of <c>1014h</c>) the error is still recorded and
    /// shown in <c>1001h</c>, without a frame. In Stopped the EMCY is held, and only the most
    /// recent EMCY the node holds goes out when it leaves Stopped. In Initialisation the EMCY is held
    /// until the boot-up has been ordered, and every one held there goes out behind it in the order
    /// raised. Such an EMCY is sent once the boot-up's send has completed, and so is every other EMCY
    /// of the application ordered while that send is still pending — raised or cleared in
    /// Pre-Operational (by work held behind the start-up job, say), or held in Stopped and released
    /// then; when its turn comes and the node has been stopped meanwhile, it waits until the node leaves
    /// Stopped and goes out then, whatever the options (CiA 301 v4.2.0 §7.3.2.2.4, Table 37: no EMCY
    /// in Stopped).
    /// </para>
    /// <para>An NMT reset ends every error without an EMCY, and <c>1001h</c> loses its bits at once,
    /// before the reset's <see cref="ICanOpenApplication.OnNmtStateChanged"/> into Initialisation,
    /// whatever EMCYs are still on their way. An EMCY of
    /// the application, raise or <see cref="ClearError"/>, that has not been transmitted when the reset
    /// comes is dropped, frame and <c>1001h</c> write alike: one held in Initialisation, one already
    /// released behind the boot-up but still waiting for its turn (a master that resets the node as
    /// soon as it sees the boot-up), one held in Stopped, one waiting for the node to leave Stopped or
    /// for its inhibit time, and one queued behind any of these: every EMCY of the application takes
    /// the gated path of the node's EMCY chain, an EMCY held in Stopped included once it is released,
    /// whatever the options.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is <c>0000h</c>,
    /// the error reset, which <see cref="ClearError"/> sends; or <paramref name="errorRegisterBits"/>
    /// sets bit 6, which is reserved and always 0 (CiA 301 v4.2.0 §7.5.2.2, Table 54). Nothing is
    /// recorded then. Every other bit is the profile's to set: the categories (bits 1 to 5), the
    /// manufacturer-specific bit 7, and bit 0, which the node sets with any error anyway.</exception>
    /// <exception cref="ArgumentException"><paramref name="manufacturerSpecific"/> is longer than
    /// five bytes.</exception>
    /// <exception cref="InvalidOperationException">Called off the node's actor loop.</exception>
    void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default);

    /// <summary>Ends an error raised with <see cref="RaiseError"/>. Actor loop.</summary>
    /// <remarks>Nothing when the error is not active. Otherwise <c>1001h</c> loses the error's bits
    /// at once, the generic bit with them unless another error keeps it (CiA 301 v4.2.0 §7.5.2.2),
    /// and the node orders error code <c>0000h</c>, carrying what the register still holds, under
    /// the same rules as the EMCY of <see cref="RaiseError"/>: in Stopped it is held like any other
    /// (and replaces an EMCY held there), and with the EMCY switched off there is no frame. In
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
