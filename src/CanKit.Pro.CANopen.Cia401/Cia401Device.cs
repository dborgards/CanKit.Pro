using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// A CiA 401-1 v3.1.0 generic I/O device on a CANopen node (<see cref="ICanOpenNode"/>): the profile objects of one
/// logical device, its device type and default PDOs, and the behaviour that links the process values
/// the application sets to the objects and PDOs the master sees. Handed to the node with
/// <see cref="AddTo"/>; it talks to the node through <see cref="ICanOpenApplicationHost"/> only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Function blocks.</b> Each I/O function of clause 8 lives in a partial of this class, with its
/// state in a nested block object that exists when the configuration gives the function channels and
/// is <see langword="null"/> otherwise. This class does what is common to all of them: attach (each
/// block declares its objects; then the device type and the default PDOs are set), the dispatch of
/// written entries and of the change to Operational to the blocks, and one
/// <see cref="ICanOpenApplicationHost.RequestTransmission"/> per callback for what the blocks ask to
/// send, so that one event sends one frame per PDO, not one per entry.
/// </para>
/// <para>
/// <b>Threads.</b> The state of every block lives on the node's actor loop: it is changed only in
/// the device's callbacks and in work posted with <see cref="ICanOpenApplicationHost.Post"/>, which
/// is a callback of the device too. The application-side methods (<see cref="SetDigitalInput"/> and
/// the others) can be called from any thread and from inside the device's callbacks: they check
/// their arguments on the calling thread and hand the change to <see cref="ICanOpenApplicationHost.Post"/>,
/// which applies it at once inside a callback and queues it otherwise. The readers read the
/// dictionary, which is safe from any thread, and so see the last value applied on the actor loop.
/// The device keeps no reference to the node, only to its host.
/// </para>
/// <para>
/// <b>Life cycle.</b> Before the node has attached the device, the application-side methods throw
/// <see cref="InvalidOperationException"/>: there is nothing to set yet. A device attaches to one
/// node; handed to a second, it fails that node's open. Once the node is disposed a set does
/// nothing (the post is dropped) and a reader shows the last value, from the dictionary the device
/// still holds.
/// </para>
/// <para>
/// <b>What is built.</b> The four function blocks of CiA 401-1 with their default access, each
/// described where it is used:
/// </para>
/// <list type="bullet">
/// <item><description>the digital inputs, 8-bit access, with polarity and the interrupt masks
/// (<see cref="SetDigitalInput"/>);</description></item>
/// <item><description>the digital outputs, 8-bit access, through the filter mask, the error state
/// and the polarity (<see cref="ReadDigitalOutputs"/>);</description></item>
/// <item><description>the analog inputs, 16-bit access, with their interrupt conditions, the
/// interrupt source and the warning of §5.3 (<see cref="SetAnalogInput"/>);</description></item>
/// <item><description>the analog outputs, 16-bit access, with error mode and error value
/// (<see cref="ReadAnalogOutput"/>).</description></item>
/// </list>
/// <para>
/// The outputs share one error state, with its causes, on <see cref="OutputsInErrorState"/> and
/// <see cref="ReportDeviceFailure"/>. The application is told when an output moves, and an optional
/// plant model (<see cref="ICia401Process"/>) is called then too (<see cref="OutputsChanged"/>).
/// What an NMT reset and a <c>1010h</c> save do to the device is on <see cref="OnReset"/>.
/// </para>
/// <para>
/// <b>What is not built, and where the device departs from the norm.</b>
/// </para>
/// <list type="bullet">
/// <item><description>NOTE 1 of CiA 401-1 §7.2.3 and of §7.2.5: outputs are accepted only once the
/// first heartbeat of the device that sets them has been received, or the master has guarded the
/// node. The device accepts them from the start.</description></item>
/// <item><description>The sub-indices 02h and 03h of <c>1029h</c> (§6.2.4). A device failure
/// follows sub-index 01h.</description></item>
/// <item><description>The digital input filter <c>6003h</c> is stored and has no effect: its type
/// and constant are manufacturer-specific (§8.2.3).</description></item>
/// <item><description><c>6422h</c> is not cleared after an SDO read or a transmission, as §8.6.3
/// has it, but when <c>6423h</c> is written, on entering Operational and on every reset: the
/// application seam reports neither moment (see <see cref="SetAnalogInput"/>).</description></item>
/// <item><description>How the analog interrupt conditions combine. The limits are OR'd and the
/// deltas are OR'd, and the two groups are ANDed. This is a reading of Figure 6, §8.6.5 and
/// §8.6.6 against §6.2.8 (see <see cref="SetAnalogInput"/>).</description></item>
/// <item><description>Access other than 8-bit digital and 16-bit analog (16- and 32-bit digital;
/// 8-bit, 32-bit, float and manufacturer-specific analog), the float limits and deltas of the analog
/// inputs, offset, scaling and SI unit. The device does not declare them.</description></item>
/// </list>
/// </remarks>
internal sealed partial class Cia401Device : ICanOpenApplication
{
    private readonly int _logicalDevice;
    private readonly CanOpenDeviceType _deviceType;
    private readonly IReadOnlyList<Cia401DefaultPdo> _defaultPdos;
    private readonly int _requiredPdoCount;
    private readonly CanOpenIndexRange[] _claims;

    // The plant model, or null.
    private readonly ICia401Process? _process;

    // The host of the node the device is attached to; null until Attach has declared everything.
    // Written once, on the thread that opens the node; read from any thread.
    private ICanOpenApplicationHost? _host;

    // The managed thread id of the device's own validated write while it runs, else 0: the write
    // the device makes to an entry it declared read-only (6000h) passes its own ValidateWrite, and
    // no other writer's does. A thread id rather than a flag, since a local write on another thread
    // can be validated while the actor makes the device's write.
    private int _ownWriteThread;

    // True while RaiseOutputsChanged runs: a change made then (by a subscriber or the plant model)
    // is left pending, and the running call reports it. Actor loop.
    private bool _raisingOutputsChanged;

    /// <summary>A device with what <paramref name="configuration"/> says, and the plant model
    /// <paramref name="process"/> when one is given. The configuration is read once, here.</summary>
    /// <param name="configuration">The function blocks and the logical device.</param>
    /// <param name="process">The plant model the device calls when its outputs changed, after it was
    /// attached and after every NMT reset (see <see cref="OutputsChanged"/>); null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or the logical device is out of range
    /// (<see cref="Cia401Configuration.Validate"/>).</exception>
    /// <exception cref="ArgumentException">The configuration has no I/O function
    /// (<see cref="Cia401Configuration.Validate"/>).</exception>
    public Cia401Device(Cia401Configuration configuration, ICia401Process? process = null)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        configuration.Validate();

        _process = process;
        _logicalDevice = configuration.LogicalDevice;
        _deviceType = configuration.DeviceType;
        _defaultPdos = configuration.DefaultPdos;
        _requiredPdoCount = configuration.RequiredPdoCount;
        _claims = new[] { CanOpenIndexRange.ForLogicalDevice(_logicalDevice) };
        Requirements = new CanOpenApplicationRequirements
        {
            PdoCount = _requiredPdoCount,
            CommunicationFailureMonitoring = true,
        };
        Name = string.Format(CultureInfo.InvariantCulture, "CiA 401 device (logical device {0})", _logicalDevice);

        _digitalInputs = configuration.DigitalInputs > 0
            ? new DigitalInputBlock(configuration.DigitalInputs, _logicalDevice)
            : null;
        _digitalOutputs = configuration.DigitalOutputs > 0
            ? new DigitalOutputBlock(configuration.DigitalOutputs, _logicalDevice)
            : null;
        _analogInputs = configuration.AnalogInputs > 0
            ? new AnalogInputBlock(configuration.AnalogInputs, _logicalDevice)
            : null;
        _analogOutputs = configuration.AnalogOutputs > 0
            ? new AnalogOutputBlock(configuration.AnalogOutputs, _logicalDevice)
            : null;
    }

    /// <summary>Returns <paramref name="options"/> with <paramref name="device"/> appended to the
    /// applications, the PDO count raised to what the device's default PDOs need (never lowered), and
    /// error behaviour switched on, which the device's failure monitoring needs. Every other option
    /// is kept.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static CanOpenNodeOptions AddTo(CanOpenNodeOptions options, Cia401Device device)
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
    /// (<see cref="Cia401Configuration.RequiredPdoCount"/>), and failure monitoring.</summary>
    public CanOpenApplicationRequirements Requirements { get; }

    /// <summary>Declares the objects of every function block, sets the device type and the default
    /// PDOs (CiA 401-1 §6.1, §6.2.1, §6.2.5 to §6.2.12), and only then makes the device usable from
    /// the application side. With a plant model, posts its first call, which the node holds until it
    /// has opened (see <see cref="OutputsChanged"/>).</summary>
    /// <exception cref="InvalidOperationException">The device is attached to a node already.</exception>
    public void Attach(ICanOpenApplicationHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (Volatile.Read(ref _host) is not null) throw AttachedAlready();

        _digitalInputs?.Declare(host.ObjectDictionary);
        _digitalOutputs?.Declare(host.ObjectDictionary);
        _analogInputs?.Declare(host.ObjectDictionary);
        _analogOutputs?.Declare(host.ObjectDictionary);

        host.SetDeviceType(_logicalDevice, _deviceType);
        foreach (var pdo in _defaultPdos)
        {
            var mapping = new PdoMapping();
            foreach (var (index, subindex, bits) in pdo.Entries) mapping.Add(index, subindex, bits);
            if (pdo.IsTpdo) host.SetDefaultTpdo(pdo.Number, mapping, pdo.Enabled);
            else host.SetDefaultRpdo(pdo.Number, mapping);
        }

        if (Interlocked.CompareExchange(ref _host, host, null) is not null) throw AttachedAlready();
        if (_process is not null) host.Post(() => RaiseOutputsChanged(callProcess: true));
    }

    private InvalidOperationException AttachedAlready()
        => new($"{Name} is attached to a node already: one device, one node.");

    /// <summary>Refuses a write to an entry the device declared read-only, with the abort code an SDO
    /// download gets for it (<c>06010002h</c>, CiA 301 v4.2.0 Table 22): the SDO server refuses such a
    /// download before the device is asked, but a local write through the dictionary asks the device.
    /// Refuses a value other than 0 and 1 for a BOOLEAN entry (<c>6005h</c>), which is TRUE or FALSE
    /// and nothing else (CiA 301 v4.2.0 §7.1.4.3), with <c>06090030h</c>, "invalid value for
    /// parameter" (Table 22). Refuses an analog interrupt trigger selection (<c>6421h</c>) with a
    /// reserved bit set (CiA 401-1 §8.6.2), and an analog output error mode (<c>6443h</c>) other than
    /// 0h and 1h (§8.7.4: "others = reserved"), with the same code. The device's own writes pass.
    /// Every other write is accepted.</summary>
    public OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value)
    {
        // Nobody writes a claimed entry before Attach has declared it.
        var host = Volatile.Read(ref _host);
        if (host is null || IsOwnWrite) return OdWriteDecision.Accept;
        if (!host.ObjectDictionary.TryGet(index, subindex, out var entry)) return OdWriteDecision.Accept;
        if ((entry.Access & OdAccess.WriteOnly) == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (entry.DataType == OdDataType.Boolean && value.Length == 1 && value[0] > 1)
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        if (IsReservedTriggerSelection(index, subindex, value) || IsReservedAnalogErrorMode(index, subindex, value))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        return OdWriteDecision.Accept;
    }

    /// <summary>Hands each written entry to the block that owns it, then asks for one transmission
    /// of whatever the blocks found worth sending, then tells the application of the outputs that
    /// changed. Actor loop.</summary>
    public void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries)
    {
        var host = AttachedHost();
        var send = new List<CanOpenObjectAddress>();
        foreach (var entry in entries)
        {
            DigitalInputsWritten(host, entry, send);
            DigitalOutputsWritten(host, entry);
            AnalogInputsWritten(host, entry);
            AnalogOutputsWritten(host, entry);
        }
        RefreshDigitalOutputs(host);
        RefreshAnalogOutputs();
        RequestTransmission(host, send);
        RaiseOutputsChanged();
    }

    /// <summary>Stopped puts the outputs in the error state while it lasts (CiA 401-1 §8.3.3).
    /// Entering Operational sends every TPDO of transmission type <c>FFh</c> (§6.1): the device asks
    /// for every entry its blocks transmit, in one request, whatever the interrupt masks and the
    /// global interrupt enables say; the analog inputs also start their interrupt state afresh and
    /// raise the warning of §5.3 when <c>6423h</c> is FALSE, which leaving Operational ends (see
    /// <see cref="SetAnalogInput"/>). Actor loop.</summary>
    public void OnNmtStateChanged(NmtState previous, NmtState current)
    {
        var host = AttachedHost();
        _stopped = current == NmtState.Stopped;
        UpdateErrorState(host);
        if (current == NmtState.Operational)
        {
            var send = new List<CanOpenObjectAddress>();
            DigitalInputsToSendOnStart(send);
            AnalogInputsEnterOperational(host, send);
            RequestTransmission(host, send);
        }
        else if (previous == NmtState.Operational)
        {
            AnalogInputsLeaveOperational(host);
        }
        RaiseOutputsChanged();
    }

    /// <summary>
    /// Brings the device to what the reset leaves, then tells the application and the plant model.
    /// Actor loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reset ends the communication failure and Stopped as causes of the error state: the node has
    /// told both by now (Initializing, and the end of the failure before this call), and the device
    /// keeps the levels it was told rather than overriding them here; a device failure the
    /// application reported outlasts the reset.
    /// </para>
    /// <para>
    /// Reset Node, the application reset, has the node restore the device's objects to their
    /// power-on or stored values before this call (<see cref="ICanOpenApplication.OnReset"/>). The
    /// device then makes the dictionary true again. The inputs first: <c>6000h</c> is rewritten from
    /// the physical levels the application set under the restored polarity of <c>6002h</c>, and
    /// <c>6401h</c> from the value the application set last. The application's levels are the
    /// truth; what the node restored, a default or what <c>1010h</c> stored, is stale. Then the
    /// outputs: the held values are dropped for what <c>6200h</c> and <c>6411h</c> hold now, a
    /// default or a stored value (CiA 401-1 §6.2.5 NOTE, §6.2.7 NOTE), and a device failure that
    /// persists takes the error values again. All of it happens in Initialisation, before the node
    /// can enter Operational, so the TPDOs sent on entering it carry the application's levels.
    /// </para>
    /// <para>
    /// <b>Stored values.</b> A save through <c>1010h</c> stores every entry the device declared, the
    /// inputs and <c>6422h</c> among them: the application seam offers no way to keep an entry out
    /// of it. The rewrite above means a stored input value is never seen after Reset Node, and
    /// <c>6422h</c> is cleared. The stored output values are what §6.2.5 NOTE and §6.2.7 NOTE mean by
    /// "stored values overwrite default values": after a save and Reset Node the outputs come up with
    /// them. §7.2.5 NOTE 1 has the analog outputs "in default state" after an application reset; the
    /// device follows §6.2.7 NOTE, which names the stored values, and reads NOTE 1 as the case
    /// without them.
    /// </para>
    /// <para>
    /// Reset Communication restores <c>1000h</c> to <c>1FFFh</c> only, the default PDO records with
    /// them, and resets no application: the device's objects and outputs stay as they are.
    /// </para>
    /// <para>
    /// Every reset starts the analog interrupt state afresh: <c>6422h</c> is cleared (see
    /// <see cref="SetAnalogInput"/>), each channel's last communicated value is the value the
    /// application set, and <c>6423h</c> is read again. Last, the events for the outputs the reset
    /// moved are raised and the plant model is called once, whether or not an output moved.
    /// </para>
    /// </remarks>
    public void OnReset(NmtCommand command)
    {
        var host = AttachedHost();
        bool resetNode = command == NmtCommand.ResetNode;
        if (resetNode) DigitalInputsReset(host);
        AnalogInputsReset(host, resetNode);
        if (resetNode)
        {
            DigitalOutputsReset(host);
            AnalogOutputsReset(host);
        }

        RaiseOutputsChanged(callProcess: true);
    }

    /// <summary>A communication failure puts the outputs in the error state while it lasts (CiA 401-1
    /// §5.2, §8.3.3). The node acts on <c>1029h</c> itself, after this. Actor loop.</summary>
    public void OnCommunicationFailureChanged(bool active)
    {
        var host = AttachedHost();
        _communicationFailure = active;
        UpdateErrorState(host);
        RaiseOutputsChanged();
    }

    // ---- what the application and the plant model are told ---------------------------------------

    /// <summary>The level at the terminal of a digital or an analog output changed. Raised on the
    /// node's actor loop, at the end of the callback that changed it, once the device's state is
    /// consistent, and only when a level actually changed: a write of the value already there raises
    /// nothing.</summary>
    /// <remarks>
    /// <para>
    /// One event per output block per callback, however many entries, groups and channels changed
    /// in it, the digital outputs before the analog ones: a callback that moves both, such as
    /// entering the error state, raises two events, in that order.
    /// <see cref="Cia401OutputsChangedEventArgs.Block"/> names the block,
    /// <see cref="Cia401OutputsChangedEventArgs.Group"/> and <see cref="Cia401OutputsChangedEventArgs.Channel"/>
    /// the lowest of it that changed, and each event carries the terminals of both blocks as they are
    /// after the callback (<see cref="Cia401OutputsChangedEventArgs.Levels"/>,
    /// <see cref="Cia401OutputsChangedEventArgs.AnalogLevels"/>), so a subscriber that reads every
    /// event ends on the latest levels. The device works out a callback's levels before it publishes
    /// any: the levels after the callback are compared with those before it, a callback that leaves
    /// every level where it was publishes and raises nothing, and no level a callback would pass
    /// through on the way is ever visible to <see cref="ReadDigitalOutputs"/> or
    /// <see cref="ReadAnalogOutput"/>.
    /// </para>
    /// <para>
    /// The handler runs inside a callback of the device, so the device's methods called from it apply
    /// at once (<see cref="ICanOpenApplicationHost.Post"/>): a handler that loops an output back to an
    /// input with <see cref="SetDigitalInputs"/> sees it in <see cref="ReadDigitalInputs"/> before it
    /// returns, and one that calls <see cref="ReportDeviceFailure"/> changes the outputs there and
    /// then. Such a change is not raised inside the running event: every subscriber is given the
    /// events in hand first, and then the change, as events of its own, so each subscriber sees the
    /// changes in the order they happened and ends on the latest levels. It must not block: it holds
    /// up the node.
    /// </para>
    /// <para>
    /// Each subscriber is called on its own, so one that throws keeps no other from the event, nor
    /// the plant model from its call. What they throw is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> once every subscriber and the model
    /// have been called, as anything thrown out of an application's callback is, and the device
    /// carries on (several exceptions as one <see cref="AggregateException"/>); when the event is
    /// raised inside a <see cref="ReportDeviceFailure"/> that was itself called from a callback of
    /// the device, the exception reaches that caller instead. Either way the device's state is
    /// complete before the first subscriber runs.
    /// </para>
    /// <para>
    /// <b>The plant model</b> (<see cref="ICia401Process"/>, handed to the constructor) is called
    /// on the actor loop, inside the same callback of the device, so its setters apply at once too:
    /// </para>
    /// <list type="bullet">
    /// <item><description>after every callback in which a level changed, once, after the subscribers
    /// have had that callback's events;</description></item>
    /// <item><description>once after the device was attached: the call is posted from
    /// <see cref="Attach"/>, which the node holds until it has opened and then queues behind its
    /// start-up (<see cref="ICanOpenApplicationHost.Post"/>). It runs as the first actor job after
    /// the start-up: the node is Pre-operational, its boot-up has been ordered and the device told
    /// of it, and no frame received has been handled yet. On an LSS slave still waiting for its
    /// node-id it runs in Initialisation instead;</description></item>
    /// <item><description>once after every NMT reset, at the end of <see cref="OnReset"/>, once the
    /// device's state is what the reset leaves, whether or not an output moved; the node is in
    /// Initialisation then and its boot-up not yet ordered.</description></item>
    /// </list>
    /// <para>
    /// The inputs the model sets never call it again: the model is told of outputs, and a set input
    /// changes none. Outputs the model changes itself (by <see cref="ReportDeviceFailure"/>) are
    /// raised to the subscribers once the model returns, but do not call the model again: it sees
    /// them in the readers as soon as the call it made returns, and a model that answers each call
    /// with a change of the outputs would otherwise keep the actor in a loop. A model that throws is
    /// reported as a throwing subscriber is, and is called again for the next change.
    /// </para>
    /// </remarks>
    public event EventHandler<Cia401OutputsChangedEventArgs>? OutputsChanged;

    /// <summary>
    /// Raises <see cref="OutputsChanged"/> for what changed since the running callback first changed
    /// a level, if anything did, then calls the plant model when a level changed or
    /// <paramref name="callProcess"/> asks for it, then raises what the model changed. Called last in
    /// each callback, so the device's state is complete when the subscribers and the model run.
    /// Called while it runs already (a subscriber or the model changed the outputs), it leaves the
    /// change pending for the running call. What the subscribers and the model throw is thrown from
    /// here once nothing is pending. Actor loop.
    /// </summary>
    private void RaiseOutputsChanged(bool callProcess = false)
    {
        if (_raisingOutputsChanged) return;

        List<Exception>? failures = null;
        _raisingOutputsChanged = true;
        try
        {
            bool changed = RaisePendingOutputChanges(ref failures);
            if ((changed || callProcess) && _process is { } process)
            {
                try
                {
                    process.OnOutputsChanged(this);
                }
                // Everything, deliberately: the model's failure must not leave what it changed
                // unreported; it is thrown with the subscribers' once nothing is pending.
                catch (Exception ex)
                {
                    (failures ??= new List<Exception>()).Add(ex);
                }

                RaisePendingOutputChanges(ref failures);
            }
        }
        finally
        {
            _raisingOutputsChanged = false;
        }

        if (failures is null) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    /// <summary>Gives every subscriber the events for what is pending, digital before analog, and
    /// again for what they changed meanwhile, until nothing is pending. Whether any level changed.
    /// Actor loop, inside <see cref="RaiseOutputsChanged"/>.</summary>
    private bool RaisePendingOutputChanges(ref List<Exception>? failures)
    {
        bool changed = false;
        while (_outputsBefore is not null || _analogOutputsBefore is not null)
        {
            var digitalBefore = _outputsBefore;
            var analogBefore = _analogOutputsBefore;
            _outputsBefore = null;
            _analogOutputsBefore = null;
            var digitalAfter = _digitalOutputs?.Terminal;
            var analogAfter = _analogOutputs?.Terminal;
            IReadOnlyList<byte> digital = digitalAfter is null ? Array.Empty<byte>() : new ReadOnlyCollection<byte>(digitalAfter);
            IReadOnlyList<short> analog = analogAfter is null ? Array.Empty<short>() : new ReadOnlyCollection<short>(analogAfter);
            var events = new List<Cia401OutputsChangedEventArgs>(2);
            if (digitalBefore is not null && FirstDigitalChange(digitalBefore, digitalAfter!) is { } d)
            {
                events.Add(new Cia401OutputsChangedEventArgs(Cia401FunctionBlock.DigitalOutput, d.Group, d.Channel,
                    digital, analog));
            }

            if (analogBefore is not null && FirstAnalogChange(analogBefore, analogAfter!) is { } c)
            {
                // An analog output's group is its channel: the sub-index of 6411h that addresses it.
                events.Add(new Cia401OutputsChangedEventArgs(Cia401FunctionBlock.AnalogOutput, c, c, digital, analog));
            }

            if (events.Count == 0) continue;
            changed = true;
            if (OutputsChanged is not { } handler) continue;
            foreach (var args in events)
            {
                foreach (var subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<Cia401OutputsChangedEventArgs>)subscriber)(this, args);
                    }
                    // Everything, deliberately: one subscriber's failure must not keep the event from
                    // the others; it is thrown once they all have it.
                    catch (Exception ex)
                    {
                        (failures ??= new List<Exception>()).Add(ex);
                    }
                }
            }
        }

        return changed;
    }

    // ---- shared by the blocks -------------------------------------------------------------------

    /// <summary>The host, for the actor-loop callbacks: the node calls them only once
    /// <see cref="Attach"/> has returned.</summary>
    private ICanOpenApplicationHost AttachedHost()
        => Volatile.Read(ref _host) ?? throw new InvalidOperationException($"{Name} is called before it was attached.");

    /// <summary>The host, for the application side.</summary>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    private ICanOpenApplicationHost RequireHost()
        => Volatile.Read(ref _host) ?? throw new InvalidOperationException(
            $"{Name} is not attached to a node yet: open a node with Cia401Device.AddTo first.");

    private bool IsOwnWrite => Volatile.Read(ref _ownWriteThread) == Environment.CurrentManagedThreadId;

    /// <summary>The device's own write of one byte. Actor loop, inside a callback of the device, so
    /// it is not reported back (<see cref="ICanOpenApplication.OnEntriesWritten"/>).</summary>
    private void WriteOwn(ICanOpenApplicationHost host, ushort index, byte subindex, byte value)
        => WriteOwn(host, index, subindex, new[] { value });

    /// <summary>The device's own write of an entry's raw little-endian bytes, as
    /// <see cref="WriteOwn(ICanOpenApplicationHost, ushort, byte, byte)"/>.</summary>
    private void WriteOwn(ICanOpenApplicationHost host, ushort index, byte subindex, byte[] value)
    {
        Volatile.Write(ref _ownWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            host.ObjectDictionary.WriteRaw(index, subindex, value);
        }
        finally
        {
            Volatile.Write(ref _ownWriteThread, 0);
        }
    }

    private static byte ReadByte(ICanOpenApplicationHost host, ushort index, byte subindex)
        => host.ObjectDictionary.ReadRaw(index, subindex)[0];

    /// <summary>The index of a catalogue object in logical device <paramref name="logicalDevice"/>.</summary>
    private static ushort Shift(int logicalDevice, Cia401ObjectDefinition definition)
        => CanOpenLogicalDeviceAddressing.Index(logicalDevice, definition.Index);

    /// <summary>The bits of group <paramref name="g"/> (0-based) that are channels of a digital
    /// block with <paramref name="channels"/> channels: all eight but in a last group that is not
    /// full.</summary>
    private static byte GroupChannelMask(int channels, int g)
    {
        int inGroup = Math.Min(ChannelsPerGroup, channels - (g * ChannelsPerGroup));
        return (byte)((1 << inGroup) - 1);
    }

    /// <summary>Declares an array with the data type, access, PDO mapping and default the catalogue
    /// gives it: sub-index 0, the const UNSIGNED8 "highest sub-index supported" (CiA 301 §7.4.5)
    /// holding <paramref name="elements"/>, then one element per group of eight digital channels,
    /// per analog channel or per interrupt source bank; an object without a default ("No", CiA 401-1
    /// §8.2.1, §8.4.2) starts at 0. Constructing thread, in Attach.</summary>
    private static void DeclareArray(ObjectDictionary od, int logicalDevice, int elements, Cia401ObjectDefinition definition)
    {
        var index = Shift(logicalDevice, definition);
        od.AddU8(index, 0x00, (byte)elements, OdAccess.ReadOnly, pdoMappable: false);
        var initial = Encode(definition.DataType, definition.DefaultValue ?? 0);
        for (int sub = 1; sub <= elements; sub++)
        {
            od.AddRaw(index, (byte)sub, definition.DataType, initial, definition.Access,
                definition.PdoMapping != Cia401PdoMapping.No);
        }
    }

    /// <summary>The little-endian bytes of <paramref name="value"/> in the width of
    /// <paramref name="type"/>, one of the integer types of the catalogue's 8-, 16- and 32-bit
    /// objects (CiA 301 v4.2.0 §7.1.4.4, §7.1.4.5: two's complement for the signed ones).</summary>
    private static byte[] Encode(OdDataType type, long value)
    {
        int width = type switch
        {
            OdDataType.Boolean or OdDataType.Unsigned8 or OdDataType.Integer8 => 1,
            OdDataType.Unsigned16 or OdDataType.Integer16 => 2,
            OdDataType.Unsigned32 or OdDataType.Integer32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an integer type of the catalogue."),
        };
        var bytes = new byte[width];
        for (int i = 0; i < width; i++) bytes[i] = unchecked((byte)(value >> (8 * i)));
        return bytes;
    }

    /// <summary>Declares a one-byte variable with what the catalogue gives it. Constructing thread,
    /// in Attach.</summary>
    private static void DeclareVariable(ObjectDictionary od, int logicalDevice, Cia401ObjectDefinition definition)
        => od.AddRaw(Shift(logicalDevice, definition), 0x00, definition.DataType,
            new[] { (byte)(definition.DefaultValue ?? 0) }, definition.Access,
            definition.PdoMapping != Cia401PdoMapping.No);

    /// <summary>One request for everything collected: the node sends each TPDO that maps one of the
    /// entries once, however many of them it maps.</summary>
    private static void RequestTransmission(ICanOpenApplicationHost host, List<CanOpenObjectAddress> send)
    {
        if (send.Count > 0) host.RequestTransmission(send);
    }
}
