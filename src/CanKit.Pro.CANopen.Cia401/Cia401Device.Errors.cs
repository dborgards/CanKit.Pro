using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia401;

// The error state of the outputs, digital and analog (CiA 401-1 §5.2, §6.2.4, §8.3.3, §8.3.4,
// §8.7.4, §8.7.5), and the device failure the application reports.
internal sealed partial class Cia401Device
{
    /// <summary>The error class of <c>1029h</c>, error behavior (CiA 301 v4.2.0 §7.5.2.32), that CiA
    /// 401-1 §6.2.4 gives the values for a device failure in: sub-index 01h.</summary>
    private const byte DeviceFailureErrorClass = 0x01;

    // The causes of the error state, each a level. Actor loop.
    private bool _communicationFailure;
    private bool _stopped;
    private bool _deviceFailure;

    // Whether any cause is active: written on the actor loop, read from any thread.
    private volatile bool _inErrorState;

    /// <summary>Whether the outputs, digital and analog, are in the error state. Any thread.</summary>
    /// <remarks>
    /// <para>
    /// One error state for both output blocks. It has three causes, and lasts while any of them
    /// does: a communication failure (bus-off, a heartbeat or life guarding event: §5.2, the node's
    /// <see cref="ICanOpenApplication.OnCommunicationFailureChanged"/>); the NMT state Stopped, the
    /// "'Stop Remote Node' indication" of §8.3.3 and §8.7.4, however the node got there; and a
    /// device failure the application reports with <see cref="ReportDeviceFailure"/>. A failure
    /// that begins while the node is Stopped keeps the outputs in the error state when the node is
    /// started again.
    /// </para>
    /// <para>
    /// Entering it, each digital output with <c>6206h</c> = 1 takes the value of <c>6207h</c> and
    /// each with <c>6206h</c> = 0 keeps its value (§8.3.3, §8.3.4); each analog output with
    /// <c>6443h</c> = 1h takes the upper word of <c>6444h</c> and each with 0h keeps its value
    /// (§8.7.4, §8.7.5; see <see cref="ReadAnalogOutput"/>). <see cref="ReadDigitalOutputs"/> and
    /// <see cref="ReadAnalogOutput"/> show the result. <c>6200h</c> and <c>6411h</c> keep the value
    /// last written: Figure 5 (§7.2.3) and Figure 7 (§7.2.5) have the received value enter the write
    /// object before the error switch, which does not write back into it. So a master reads what it
    /// wrote, and the application reads what the terminals drive.
    /// </para>
    /// <para>
    /// While it lasts, a write to <c>6200h</c> or <c>6411h</c> is stored there and reaches no
    /// terminal, not even one whose error mode keeps its value: in both figures the error switch
    /// takes the write object off the output path, and the branch of error mode 0 feeds back the
    /// output's own value, which is the "kept" of §8.3.3 and the "actual value rest" of §8.7.4.
    /// Leaving the error state changes no output by itself (the figures would switch the write
    /// object back at once; the device does not, so that an output the master never wrote does not
    /// move when a failure ends): the next accepted write does, whatever value it carries. What
    /// decides is the state when the device applies a write, which is one actor job after the write
    /// is stored (<see cref="ICanOpenApplication.OnEntriesWritten"/>): a write stored just before
    /// the error state begins can be applied inside it, and so not reach the terminal, and one stored
    /// just before it ends can be applied after it, and so reach the terminal.
    /// </para>
    /// <para>
    /// An NMT reset ends the communication failure and Stopped; a device failure outlasts it,
    /// since it is the application's statement about its hardware, until
    /// <see cref="ReportDeviceFailure"/>(<see langword="false"/>). Reset Node also returns the
    /// outputs to what <c>6200h</c> and <c>6411h</c> hold after it, the application being reset
    /// (§6.2.5 NOTE, §6.2.7 NOTE); Reset Communication leaves them where they are.
    /// </para>
    /// <para>
    /// What this slice leaves out is listed in the remarks on <see cref="Cia401Device"/>.
    /// </para>
    /// <para>
    /// <see langword="false"/> before the device is attached, and for a device without outputs.
    /// </para>
    /// </remarks>
    public bool OutputsInErrorState => (_digitalOutputs is not null || _analogOutputs is not null) && _inErrorState;

    /// <summary>Reports that a device failure began (<see langword="true"/>) or ended: one of the
    /// "device internal failures" of CiA 401-1 §5.2, which the application detects in its hardware.
    /// Any thread; applied on the node's actor loop, at once when called from inside one of the
    /// device's callbacks.</summary>
    /// <remarks>
    /// <para>
    /// A level: reporting what is reported already does nothing. While it lasts the outputs are in
    /// the error state (<see cref="OutputsInErrorState"/>), across NMT resets too.
    /// </para>
    /// <para>
    /// When it begins, the device asks the node for the NMT state <c>1029h:01</c> names (§5.2,
    /// §6.2.4): 00h Pre-operational, only when the node is Operational; 01h no change; 02h Stopped,
    /// from Pre-operational as from Operational, since §6.2.4 makes 02h unconditional and only 00h
    /// conditional on Operational. In Initialisation the node takes no request
    /// (<see cref="ICanOpenApplicationHost.RequestNmtState"/>), and a failure can begin there: in
    /// the plant model's call after a reset, in its first call on an LSS slave still waiting for its
    /// node-id, in an <see cref="OutputsChanged"/> handler during a reset. Such a failure, and one
    /// that outlasts a reset, makes its request when the node leaves Initialisation, after the
    /// boot-up: with 02h the node boots and then enters Stopped; with 00h, Pre-operational being
    /// where the boot-up leaves it, nothing more happens. The node itself acts on its own
    /// communication errors only from Operational, for 02h too; for a device failure the device
    /// follows §6.2.4 instead. The case of the §6.2.4 NOTE, a node without <c>1029h</c>, does not
    /// arise: the device requires <see cref="CanOpenNodeOptions.EnableErrorBehavior"/> (it monitors
    /// communication failures), with which the node always has <c>1029h:01</c>. On a node that also
    /// carries a CiA 408 device the record is CiA 408's (§8.6.8), whose <c>1029h:01</c> powers on as
    /// 01h, no change: there a device failure, and a communication failure too, leaves the node in
    /// Operational unless the master writes 00h. The reserved values
    /// change nothing, as the node treats them for a communication failure. The
    /// communication failures themselves are the node's to act on, and the device asks nothing for
    /// them.
    /// </para>
    /// <para>
    /// That is all. A master that starts the node while the failure lasts is not refused, and the
    /// outputs stay in the error state: the node redirects a Start for its own communication failure,
    /// but the application seam offers a profile no way to take part in that, which is recorded for
    /// the seam's publication review (issue #548). When the failure ends, the node stays where it is.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">No node has attached the device yet.</exception>
    public void ReportDeviceFailure(bool active)
    {
        var host = RequireHost();
        host.Post(() => ApplyDeviceFailure(host, active));
    }

    /// <summary>Actor loop, in a callback of the device: the state first, then the NMT request
    /// (which can tell this device the change at once, nested), then the event.</summary>
    private void ApplyDeviceFailure(ICanOpenApplicationHost host, bool active)
    {
        if (active == _deviceFailure) return;
        _deviceFailure = active;
        UpdateErrorState(host);
        if (active) RequestDeviceFailureState(host);
        RaiseOutputsChanged();
    }

    /// <summary>Enters the error state when a cause is active and the state is not; leaves it, and
    /// changes no output, when none is. Actor loop.</summary>
    private void UpdateErrorState(ICanOpenApplicationHost host)
    {
        bool active = _communicationFailure || _stopped || _deviceFailure;
        if (active == _inErrorState) return;
        _inErrorState = active;
        if (!active) return;
        DigitalOutputsEnterErrorState(host);
        AnalogOutputsEnterErrorState(host);
    }

    /// <summary>The NMT state <c>1029h:01</c> names for a device failure (CiA 401-1 §6.2.4), by the
    /// host's reading of the error class (<see cref="ICanOpenApplicationHost.ErrorBehaviorTarget"/>:
    /// 00h Pre-operational from Operational only, 02h Stopped from any state but Stopped and
    /// Initialisation, anything else no change). Actor loop.</summary>
    private static void RequestDeviceFailureState(ICanOpenApplicationHost host)
    {
        if (host.ErrorBehaviorTarget(DeviceFailureErrorClass) is { } target) host.RequestNmtState(target);
    }
}
