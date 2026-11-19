using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// A CiA 408 v2.2.0 device (fluid power: proportional valves and hydrostatic drives) on a CANopen
/// node (<see cref="ICanOpenNode"/>): the device-control objects of §9.3 for one logical device, its
/// device type and default PDOs, the device state machine of §7.6 driven by the control word, the
/// status word that shows it, and the faults the application reports. Handed to the node with
/// <see cref="AddTo"/>; it talks to the node through <see cref="ICanOpenApplicationHost"/> only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drives power on in local control.</b> <c>604Fh</c>, Device local, defaults to 1 in a drive
/// (§9.3.9, Table 95): the control word then comes from the application
/// (<see cref="SetLocalControlWord"/>), and the control word a master writes over the bus is stored
/// and ignored. A master that wants to drive a drive's state machine writes <c>604Fh</c> = 0 first.
/// A valve (or pump) defaults to 0, bus control. Which kind the device is decides the first control
/// mode of the configuration (<see cref="Cia408Configuration.ControlModes"/>), as it decides the
/// default PDOs: a drive mode (6 to 12 of Table 82) makes a drive.
/// </para>
/// <para>
/// <b>Objects.</b> The eight objects of §9.3: both control words (<c>6040h</c>, <c>6044h</c>), both
/// status words (<c>6041h</c>, <c>6045h</c>), device mode (<c>6042h</c>), control mode
/// (<c>6043h</c>), device error code (<c>604Eh</c>) and device local (<c>604Fh</c>), with the data
/// type, access and PDO mappability the catalogue gives them (<see cref="Cia408Objects"/>). The norm
/// calls both word pairs Mandatory, although a device has one resolution (§8.6.2, Table 53, NOTE 2);
/// the device has both, and they are one state: a write to either control word drives the machine,
/// and both status words show it (see <see cref="ControlWord"/> and <see cref="ComposeStatus"/>). The default PDOs use the pair the
/// configuration's width uses (<see cref="Cia408Configuration.DefaultPdos"/>).
/// </para>
/// <para>
/// <b>Threads.</b> The machine and every cause the device keeps live on the node's actor loop: they
/// change only in the device's callbacks and in work posted with
/// <see cref="ICanOpenApplicationHost.Post"/>, which is a callback of the device too. The
/// application-side methods (<see cref="ReportFault"/>, <see cref="ClearFaultCause"/>,
/// <see cref="SetWarning"/>, <see cref="SetLocalControlWord"/>) can be called from any thread and
/// from inside the device's callbacks: they check their arguments on the calling thread and hand
/// the change to <see cref="ICanOpenApplicationHost.Post"/>, which applies it at once inside a
/// callback and queues it otherwise. <see cref="State"/> and <see cref="ControlWord"/> can be read
/// from any thread and show what the actor loop applied last.
/// </para>
/// <para>
/// <b>Life cycle.</b> Before a node has attached the device, the application-side methods throw
/// <see cref="InvalidOperationException"/>. A device attaches to one node; handed to a second, it
/// fails that node's open. Once the node is disposed a call does nothing (the post is dropped).
/// </para>
/// <para>
/// <b>NMT.</b> Stopped, Reset Communication and a communication failure force the fault reaction
/// without an EMCY; Reset Node restarts the machine and gives back the device that powered on, with
/// the faults the application still reports entered again (§7.8, Table 10; §7.6, Table 6; see
/// <see cref="OnNmtStateChanged"/>, <see cref="OnReset"/>, <see cref="OnCommunicationFailureChanged"/>).
/// The device is an NMT slave and asks NMT for nothing on these events, which are NMT's own; a fault
/// the application reports asks for what <c>1029h:02</c> names (<see cref="ReportFault"/>).
/// </para>
/// <para>
/// <b>What is not built in this slice.</b> The hardware enable signal (§7.7, Table 8). The behaviour
/// of device mode and control mode and their value ranges, the setpoints, actual values and the
/// process model, and the answer to a received RPDO (§8.4.1). The default PDO 2, which maps the setpoints and actual values of the default control
/// mode: the device does not declare them yet, and sets PDO 1, the control and status word, only (see
/// <see cref="Attach"/>). A device description that declares the device's objects.
/// </para>
/// <para>
/// <b>Readings where the norm is silent.</b> Each is argued where it is used:
/// </para>
/// <list type="bullet">
/// <item><description>A fault reset is refused while a cause is still active — a fault the
/// application reported, or a communication failure — and the refused edge is consumed
/// (<see cref="ReportFault"/>). Stopped and Reset Communication force the fault and are no cause
/// (<see cref="OnNmtStateChanged"/>).</description></item>
/// <item><description>Reset Node follows Table 6 over Table 10 and Figure 2, and enters again the
/// faults the application still reports (<see cref="OnReset"/>).</description></item>
/// <item><description>The control word has one history across both objects and both sources, and
/// is not mirrored from one object into the other (<see cref="ControlWord"/>).</description></item>
/// <item><description><c>604Eh</c> keeps the code of the warning or fault that occurred last
/// (<see cref="SetWarning"/>, <see cref="ReportFault"/>); a warning sends no EMCY.</description></item>
/// <item><description><c>604Fh</c> takes 0 and 1 only; the manufacturer-specific values are not
/// built (<see cref="ValidateWrite"/>).</description></item>
/// <item><description>The default of <c>6042h</c> is 1 where the configuration supports it, as
/// §9.3.4 gives it, and the first supported device mode otherwise (<see cref="Attach"/>).</description></item>
/// </list>
/// </remarks>
internal sealed partial class Cia408Device : ICanOpenApplication
{
    /// <summary>The error class of <c>1029h</c> for an internal device error (CiA 408 v2.2.0 §8.6.8,
    /// Table 56): sub-index 02h, which "shall be valid when the device is in error state".</summary>
    internal const byte InternalDeviceErrorClass = 0x02;

    private readonly int _logicalDevice;
    private readonly CanOpenDeviceType _deviceType;
    private readonly IReadOnlyList<Cia408DefaultPdo> _defaultPdos;
    private readonly int _requiredPdoCount;
    private readonly CanOpenIndexRange[] _claims;
    private readonly sbyte _deviceModeDefault;
    private readonly sbyte _controlModeDefault;
    private readonly sbyte _localDefault;

    // The indices of the eight objects in the device's logical device.
    private readonly ushort _controlWord16;
    private readonly ushort _statusWord16;
    private readonly ushort _deviceMode;
    private readonly ushort _controlMode;
    private readonly ushort _controlWord32;
    private readonly ushort _statusWord32;
    private readonly ushort _errorCode;
    private readonly ushort _local;
    private readonly CanOpenObjectAddress[] _commandEntries;

    // The PDO number of the default setpoint and actual value PDO (PDO 2 of the logical device).
    private readonly int _modePdo;

    // The state machine. Actor loop, and the constructing thread in Attach before the host is
    // published.
    private readonly Cia408StateMachine _machine = new();

    // The machine's state as last applied: written on the actor loop, read from any thread.
    private volatile Cia408State _state = Cia408State.NotReady;

    // The host of the node the device is attached to; null until Attach has declared everything.
    // Written once, on the thread that opens the node; read from any thread.
    private ICanOpenApplicationHost? _host;

    // 1 from the moment an Attach begins until it fails; an Attach that succeeds keeps it.
    private int _attaching;

    // The managed thread id of the device's own write while it runs, else 0: the device writes the
    // read-only status words and 604Eh, which no other writer may (ValidateWrite).
    private int _ownWriteThread;

    // The state changes not yet raised, and whether StateChanged is being raised. Actor loop.
    private readonly Queue<Cia408StateChangedEventArgs> _pendingChanges = new();
    private bool _raising;

    // A fault asked for the NMT state 1029h:02 names; the request waits until the changes have been
    // raised (FinishChanges). Actor loop.
    private bool _nmtRequestPending;

    /// <summary>A device with what <paramref name="configuration"/> says. The configuration is read
    /// once, here.</summary>
    /// <remarks>The process model is not built in this slice; its parameter arrives with it.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> or one of its lists is
    /// null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value of the configuration is out of range
    /// (<see cref="Cia408Configuration.Validate"/>).</exception>
    /// <exception cref="ArgumentException">The configuration describes no device the norm allows
    /// (<see cref="Cia408Configuration.Validate"/>).</exception>
    public Cia408Device(Cia408Configuration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        configuration.Validate();

        _logicalDevice = configuration.LogicalDevice;
        _deviceType = configuration.DeviceType;
        _defaultPdos = configuration.DefaultPdos;
        _requiredPdoCount = configuration.RequiredPdoCount;
        _claims = new[] { CanOpenIndexRange.ForLogicalDevice(_logicalDevice) };
        _modePdo = CanOpenLogicalDeviceAddressing.PdoNumber(_logicalDevice, 2);

        var firstControlMode = configuration.ControlModes[0];
        _controlModeDefault = (sbyte)firstControlMode;
        _deviceModeDefault = (sbyte)(Contains(configuration.DeviceModes, Cia408DeviceMode.SetpointViaBus)
            ? Cia408DeviceMode.SetpointViaBus
            : configuration.DeviceModes[0]);
        _localDefault = Cia408Configuration.IsDriveMode(firstControlMode) ? (sbyte)1 : (sbyte)0;

        _controlWord16 = Shift(Cia408Objects.DeviceControlWord16);
        _statusWord16 = Shift(Cia408Objects.DeviceStatusWord16);
        _deviceMode = Shift(Cia408Objects.DeviceMode);
        _controlMode = Shift(Cia408Objects.DeviceControlMode);
        _controlWord32 = Shift(Cia408Objects.DeviceControlWord32);
        _statusWord32 = Shift(Cia408Objects.DeviceStatusWord32);
        _errorCode = Shift(Cia408Objects.DeviceErrorCode);
        _local = Shift(Cia408Objects.DeviceLocal);
        _commandEntries = new[]
        {
            new CanOpenObjectAddress(_controlWord16, 0x00),
            new CanOpenObjectAddress(_controlWord32, 0x00),
            new CanOpenObjectAddress(_local, 0x00),
        };

        Requirements = new CanOpenApplicationRequirements
        {
            PdoCount = _requiredPdoCount,
            CommunicationFailureMonitoring = true,
            ErrorBehavior = ErrorBehaviorLayout,
        };
        Name = string.Format(CultureInfo.InvariantCulture, "CiA 408 device (logical device {0})", _logicalDevice);
    }

    /// <summary>The error behaviour record <c>1029h</c> of CiA 408 v2.2.0 (§8.6.8, Table 56): the
    /// communication error at sub-index 01h and the internal device error at
    /// <see cref="InternalDeviceErrorClass"/>, both defaulting to 01h, no change of state.</summary>
    internal static CanOpenErrorBehaviorLayout ErrorBehaviorLayout { get; } = new()
    {
        CommunicationErrorDefault = 0x01,
        AdditionalDefaults = new byte[] { 0x01 },
    };

    /// <summary>Returns <paramref name="options"/> with <paramref name="device"/> appended to the
    /// applications, the PDO count raised to what the device's default PDOs need (never lowered), and
    /// error behaviour switched on, which the device's <c>1029h</c> layout and its failure
    /// monitoring need. Every other option is kept.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static CanOpenNodeOptions AddTo(CanOpenNodeOptions options, Cia408Device device)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (device is null) throw new ArgumentNullException(nameof(device));

        var result = options.WithApplication(device).WithErrorBehavior(true);
        return result.PdoCount >= device._requiredPdoCount ? result : result.With(pdoCount: device._requiredPdoCount);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The objects of the device's logical device, <c>6000h</c> to <c>67FFh</c> shifted by
    /// <c>n·800h</c> (CiA 301 v4.2.0 §7.4.1).</summary>
    public IReadOnlyList<CanOpenIndexRange> ClaimedRanges => _claims;

    /// <summary>The highest PDO number of the default PDOs
    /// (<see cref="Cia408Configuration.RequiredPdoCount"/>), failure monitoring, and the CiA 408
    /// error behaviour record (<see cref="ErrorBehaviorLayout"/>).</summary>
    public CanOpenApplicationRequirements Requirements { get; }

    /// <summary>The two control words, <c>6040h:00</c> and <c>6044h:00</c>, and the source of the
    /// control word, <c>604Fh:00</c>: the fault reset is an edge between two written words (§7.7,
    /// Table 7), so the device needs every write in order, not the coalesced last value; and whether a
    /// control word counts depends on the source it was written under, so the switches of the source
    /// must come in the same order (<see cref="OnCommandWritten"/>).</summary>
    public IReadOnlyList<CanOpenObjectAddress> CommandEntries => _commandEntries;

    /// <summary>The state the device state machine rests in. Any thread: the state last applied on
    /// the actor loop. <see cref="Cia408State.NotReady"/> until the node has attached the device,
    /// which starts the machine (<see cref="Attach"/>).</summary>
    public Cia408State State => _state;

    /// <summary>Declares the eight objects of §9.3, starts the state machine (D0, D1 into INIT, so
    /// the status words show INIT from the first frame), sets the device type and the default
    /// PDO 1, and only then makes the device usable from the application side. The start is queued
    /// as a change before that, and raised on <see cref="StateChanged"/> from work posted here, which
    /// the node holds until it has opened (<see cref="ICanOpenApplicationHost.Post"/>): it runs as the
    /// first actor job after the start-up, unless a fault the application reported from another
    /// thread in the meantime came first, which then raises it, ahead of its own change.</summary>
    /// <remarks>
    /// The defaults: <c>6040h</c> and <c>6044h</c> 0 (§9.3.2, §9.3.6); <c>6042h</c> 1, "setpoint via
    /// bus", where the configuration supports it, and its first device mode otherwise — §9.3.4 gives
    /// 01h for a writable <c>6042h</c> and the norm does not say what a device without that mode
    /// holds; <c>6043h</c> the first control mode (<see cref="Cia408Configuration.ControlModes"/>,
    /// §9.3.5 leaves it to the manufacturer); <c>604Eh</c> 0, no fault yet (§9.3.8 gives no default);
    /// <c>604Fh</c> by device kind (§9.3.9, see the remarks on the class); the status words composed
    /// from the state (<see cref="ComposeStatus"/>).
    /// <para>
    /// The default PDO 2 maps the setpoint and actual value of the default control mode
    /// (<see cref="Cia408Configuration.DefaultPdos"/>), objects the device does not declare in this
    /// slice; it is left without a default until they arrive with the process model. Every other
    /// default PDO is set, and one the node's PDO records refuse fails the open
    /// (<see cref="ICanOpenApplicationHost.SetDefaultTpdo"/>).
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is attached to a node already.</exception>
    public void Attach(ICanOpenApplicationHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (Interlocked.CompareExchange(ref _attaching, 1, 0) != 0)
            throw new InvalidOperationException($"{Name} is attached to a node already: one device, one node.");

        try
        {
            var od = host.ObjectDictionary;
            _machine.Start();
            _state = _machine.State;
            Declare(od, Cia408Objects.DeviceControlWord16, Encode(0, 2));
            Declare(od, Cia408Objects.DeviceControlWord32, Encode(0, 4));
            Declare(od, Cia408Objects.DeviceMode, new[] { unchecked((byte)_deviceModeDefault) });
            Declare(od, Cia408Objects.DeviceControlMode, new[] { unchecked((byte)_controlModeDefault) });
            Declare(od, Cia408Objects.DeviceErrorCode, Encode(0, 2));
            Declare(od, Cia408Objects.DeviceLocal, new[] { unchecked((byte)_localDefault) });
            _localSource = _localDefault == 1;
            var status = ComposeStatus();
            Declare(od, Cia408Objects.DeviceStatusWord16, Encode(status, 2));
            Declare(od, Cia408Objects.DeviceStatusWord32, Encode(status, 4));

            host.SetDeviceType(_logicalDevice, _deviceType);
            foreach (var pdo in _defaultPdos)
            {
                // The setpoint and actual value PDO waits for the objects it maps (see the remarks).
                if (pdo.Number == _modePdo) continue;
                var mapping = new PdoMapping();
                foreach (var (index, subindex, bits) in pdo.Entries) mapping.Add(index, subindex, bits);
                if (pdo.IsTpdo) host.SetDefaultTpdo(pdo.Number, mapping);
                else host.SetDefaultRpdo(pdo.Number, mapping);
            }
        }
        catch
        {
            Volatile.Write(ref _attaching, 0);
            throw;
        }

        // Queued before the host is published: a fault reported from another thread as soon as it is
        // queues its change behind the start, whichever posted work runs first.
        _pendingChanges.Enqueue(new Cia408StateChangedEventArgs(Cia408State.NotReady, Cia408State.Init, new[] { 0, 1 }));
        Volatile.Write(ref _host, host);
        host.Post(() => FinishChanges(host));
    }

    /// <summary>Refuses a control word with a reserved bit set, and a value of <c>604Fh</c> other than
    /// 0 and 1, with <c>0609 0030h</c>, "value range of parameter exceeded" (CiA 301 v4.2.0 §7.2.4.3.17,
    /// Table 22). Refuses a write to a read-only entry with <c>0601 0002h</c>: the SDO server refuses
    /// such a download before the device is asked, but a local write through the dictionary asks the
    /// device. The device's own writes pass; every other write is accepted.</summary>
    /// <remarks>
    /// <para>The reserved bits of the control word are 4, 5 and 12, and in <c>6044h</c> also 24 to
    /// 31, all "always 0" (§9.3.2, Table 73; §9.3.6, Table 85; <see cref="Cia408ControlWord"/>).</para>
    /// <para><c>604Fh</c>: Table 93 gives 0 (control word over the bus) and 1 (local), reserves 2 to
    /// 127 and leaves −128 to −1 to the manufacturer; the device implements no manufacturer-specific
    /// source, so it refuses those too.</para>
    /// </remarks>
    public OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value)
    {
        // Nobody writes a claimed entry before Attach has declared it.
        var host = Volatile.Read(ref _host);
        if (host is null || IsOwnWrite) return OdWriteDecision.Accept;
        if (!host.ObjectDictionary.TryGet(index, subindex, out var entry)) return OdWriteDecision.Accept;
        if ((entry.Access & OdAccess.WriteOnly) == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (subindex != 0x00) return OdWriteDecision.Accept;

        if ((index == _controlWord16 && (Decode(value) & Cia408ControlWord.Reserved16) != 0)
            || (index == _controlWord32 && (Decode(value) & Cia408ControlWord.Reserved32) != 0)
            || (index == _local && value.Length == 1 && value[0] > 1))
        {
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        }

        return OdWriteDecision.Accept;
    }

    /// <summary>Nothing in this slice: the entries reported here (<c>6042h</c>, <c>6043h</c>) are
    /// stored without behaviour. The command entries, <c>604Fh</c> among them, come through
    /// <see cref="OnCommandWritten"/>. Actor loop.</summary>
    public void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries)
    {
    }

    /// <summary>Nothing yet: the answer to a received RPDO (§8.4.1) arrives with the process model.
    /// The control word an RPDO carries reaches the device as a command write
    /// (<see cref="OnCommandWritten"/>).</summary>
    public void OnRpdoReceived(int pdoNumber)
    {
    }

    // ---- the event ------------------------------------------------------------------------------

    /// <summary>The device state machine changed state. Raised on the node's actor loop, once per
    /// change, in the order the changes happened, with the D-numbers taken; never for a call that
    /// leaves the state where it was (a fault in FAULT_INIT takes D8 and D13 back into FAULT_INIT and
    /// raises nothing) — except the restart of Reset Node, D255, D0 and D1, which is raised also from
    /// INIT: the machine was left and entered again (<see cref="OnReset"/>).</summary>
    /// <remarks>
    /// <para>
    /// Raised once the device's state is complete: the status words are rewritten and their
    /// transmission requested before the first subscriber runs. The first event is the start, D0 and
    /// D1 from NOT_READY into INIT, raised in the first actor job after the node's start-up.
    /// </para>
    /// <para>
    /// The handler runs inside a callback of the device, so the device's methods called from it apply
    /// at once (<see cref="ICanOpenApplicationHost.Post"/>). The change such a call makes is not raised
    /// inside the running event: every subscriber is given the event in hand first, and then the
    /// change, so each subscriber sees the changes in the order they happened. It must not block: it
    /// holds up the node.
    /// </para>
    /// <para>
    /// Each subscriber is called on its own, so one that throws keeps no other from the event. What
    /// they throw is reported on <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> once every
    /// subscriber has been called and the device has finished what the call was doing, and the device
    /// carries on (several exceptions as one <see cref="AggregateException"/>).
    /// </para>
    /// </remarks>
    public event EventHandler<Cia408StateChangedEventArgs>? StateChanged;

    /// <summary>Queues the change the machine just made from <paramref name="previous"/> by
    /// <paramref name="taken"/>, if the state moved or <paramref name="evenWithoutMove"/> says so (the
    /// restart, see <see cref="OnReset"/>), and records the new state. Actor loop.</summary>
    private void Changed(Cia408State previous, IReadOnlyList<int> taken, bool evenWithoutMove = false)
    {
        var current = _machine.State;
        _state = current;
        if (current != previous || evenWithoutMove)
            _pendingChanges.Enqueue(new Cia408StateChangedEventArgs(previous, current, taken));
    }

    /// <summary>Gives every subscriber every pending change, in order, and again what they changed
    /// meanwhile, until nothing is pending. Only from <see cref="FinishChanges"/>, which leaves a
    /// change made while this runs to the running call. Returns what the subscribers threw, for the
    /// caller to throw once it has finished. Actor loop.</summary>
    private List<Exception>? RaisePendingChanges()
    {
        List<Exception>? failures = null;
        _raising = true;
        try
        {
            while (_pendingChanges.Count > 0)
            {
                var args = _pendingChanges.Dequeue();
                if (StateChanged is not { } handler) continue;
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<Cia408StateChangedEventArgs>)subscriber)(this, args);
                    }
                    // Everything, deliberately: a subscriber is application code, and whatever it
                    // throws must not break the device or keep the event from the other subscribers.
                    // Collected, and thrown by the caller once it has finished (one as itself,
                    // several as an AggregateException), out of the device's callback, so the node
                    // reports it on BackgroundExceptionOccurred.
                    catch (Exception ex)
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
            }
        }
        finally
        {
            _raising = false;
        }

        return failures;
    }

    /// <summary>Raises every pending change (<see cref="RaisePendingChanges"/>), then makes the NMT
    /// request a fault asked for (<see cref="ReportFault"/>), then throws what the subscribers threw.
    /// Called from inside a running raise (a subscriber made a change), it does neither: the running
    /// call raises the change and then makes the request, so the NMT state changes only once every
    /// subscriber has seen the fault. Actor loop.</summary>
    private void FinishChanges(ICanOpenApplicationHost host)
    {
        if (_raising) return;
        var failures = RaisePendingChanges();
        if (_nmtRequestPending)
        {
            _nmtRequestPending = false;
            if (host.ErrorBehaviorTarget(InternalDeviceErrorClass) is { } target) host.RequestNmtState(target);
        }

        ThrowIfAny(failures);
    }

    private static void ThrowIfAny(List<Exception>? failures)
    {
        if (failures is null) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    // ---- shared ---------------------------------------------------------------------------------

    /// <summary>The host, for the actor-loop callbacks: the node calls them only once
    /// <see cref="Attach"/> has returned.</summary>
    private ICanOpenApplicationHost AttachedHost()
        => Volatile.Read(ref _host) ?? throw new InvalidOperationException($"{Name} is called before it was attached.");

    /// <summary>The host, for the application side.</summary>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    private ICanOpenApplicationHost RequireHost()
        => Volatile.Read(ref _host) ?? throw new InvalidOperationException(
            $"{Name} is not attached to a node yet: open a node with Cia408Device.AddTo first.");

    private bool IsOwnWrite => Volatile.Read(ref _ownWriteThread) == Environment.CurrentManagedThreadId;

    /// <summary>The device's own write of an entry's raw little-endian bytes. Actor loop, inside a
    /// callback of the device, so it is not reported back
    /// (<see cref="ICanOpenApplication.OnEntriesWritten"/>).</summary>
    private void WriteOwn(ICanOpenApplicationHost host, ushort index, byte[] value)
    {
        Volatile.Write(ref _ownWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            host.ObjectDictionary.WriteRaw(index, 0x00, value);
        }
        finally
        {
            Volatile.Write(ref _ownWriteThread, 0);
        }
    }

    /// <summary>Declares a variable of §9.3 at sub-index 00h with the data type, access and PDO
    /// mappability the catalogue gives it. Constructing thread, in Attach.</summary>
    private void Declare(ObjectDictionary od, Cia408ObjectDefinition definition, byte[] initial)
        => od.AddRaw(Shift(definition), 0x00, definition.ValueType, initial, definition.Access,
            definition.PdoAccess != Cia408PdoAccess.No);

    /// <summary>The index of a catalogue object in the device's logical device.</summary>
    private ushort Shift(Cia408ObjectDefinition definition)
        => CanOpenLogicalDeviceAddressing.Index(_logicalDevice, definition.Index);

    /// <summary>The <paramref name="length"/> little-endian bytes of <paramref name="value"/>.</summary>
    private static byte[] Encode(uint value, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>The unsigned value of up to four little-endian bytes.</summary>
    private static uint Decode(byte[] value)
    {
        uint result = 0;
        for (int i = 0; i < value.Length && i < 4; i++) result |= (uint)value[i] << (8 * i);
        return result;
    }

    private static bool Contains(IReadOnlyList<Cia408DeviceMode> modes, Cia408DeviceMode mode)
    {
        foreach (var candidate in modes)
        {
            if (candidate == mode) return true;
        }

        return false;
    }
}
