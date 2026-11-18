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
/// <b>Two ways to the objects.</b> A device built from a <see cref="Cia401Configuration"/> creates
/// its objects with the catalogue's defaults; one built with <see cref="FromDescription"/> adopts
/// those the node's device description declared and derives its function blocks from them, the
/// file winning. Behind either, the behaviour is the same code.
/// </para>
/// <para>
/// <b>Function blocks.</b> Each I/O function of clause 8 lives in a partial of this class, with its
/// state in a nested block object that exists when the configuration gives the function channels, or
/// the description declares its mandatory object, and is <see langword="null"/> otherwise. This class
/// does what is common to all of them: attach (each block declares its objects or adopts the file's;
/// then the device type and the default PDOs are set), the dispatch of
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
/// which applies it at once inside a callback and queues it otherwise. The readers can be called
/// from any thread and see the last value applied on the actor loop: the input readers
/// (<see cref="ReadDigitalInputs"/>, <see cref="ReadAnalogInput"/>) read the dictionary, the output
/// readers (<see cref="ReadDigitalOutputs"/>, <see cref="ReadDigitalOutput"/>,
/// <see cref="ReadAnalogOutput"/>) the terminal levels, which the device publishes whole once a
/// callback has worked them out (see <see cref="OutputsChanged"/>). The device keeps no reference to
/// the node, only to its host.
/// </para>
/// <para>
/// <b>Life cycle.</b> Before the node has attached the device, the application-side methods throw
/// <see cref="InvalidOperationException"/>: there is nothing to set yet. A device attaches to one
/// node; handed to a second, it fails that node's open. Once the node is disposed a set does
/// nothing (the post is dropped) and a reader shows the last value: an input reader from the
/// dictionary the device still holds, an output reader the levels last published.
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
/// <b>What is not built.</b>
/// </para>
/// <list type="bullet">
/// <item><description>NOTE 1 of CiA 401-1 §7.2.3 and of §7.2.5: outputs are accepted only once the
/// first heartbeat of the device that sets them has been received, or the master has guarded the
/// node. The device accepts them from the start.</description></item>
/// <item><description>The sub-indices 02h and 03h of <c>1029h</c> (§6.2.4). A device failure
/// follows sub-index 01h.</description></item>
/// <item><description>The digital input filter <c>6003h</c> is stored and has no effect: its type
/// and constant are manufacturer-specific (§8.2.3).</description></item>
/// <item><description>Access other than 8-bit digital and 16-bit analog: 16- and 32-bit digital;
/// 8-bit, 32-bit, float and manufacturer-specific analog. Also not built: the float limits and deltas
/// of the analog inputs (<c>6429h</c> to <c>642Dh</c>), and every offset, scaling and SI unit object
/// (<c>642Eh</c> to <c>6432h</c>; <c>6441h</c>, <c>6442h</c>, <c>6446h</c>, <c>6447h</c>,
/// <c>6450h</c>). The same goes for the float error value <c>6445h</c>, which §8.7.5 and §8.7.8
/// couple to <c>6444h</c>. The device does not declare any of them.</description></item>
/// </list>
/// <para>
/// <b>Where the device departs from the norm.</b>
/// </para>
/// <list type="bullet">
/// <item><description>Leaving the error state does not put the write object (<c>6200h</c>,
/// <c>6411h</c>) back onto the terminal, as the plain reading of Figures 5 and 7 would. The next
/// accepted write does. This keeps an output the master never wrote from moving when a failure
/// ends (see <see cref="OutputsInErrorState"/>).</description></item>
/// <item><description><c>6422h</c> is not cleared after an SDO read or a transmission, as §8.6.3
/// has it. It is cleared when <c>6423h</c> is written, on entering Operational and on every reset,
/// because the application seam reports neither an SDO read nor a transmission (see
/// <see cref="SetAnalogInput"/>).</description></item>
/// <item><description>A device built with <see cref="FromDescription"/> keeps the objects of its
/// logical device that the first slice does not build, as the file declares them, and does not act
/// on them: the accesses of one I/O do not "access a single database" (§7.2.1.2) for them, and a
/// bit or 16-bit input object does not follow the 8-bit one (see
/// <see cref="FromDescription"/>).</description></item>
/// <item><description>A device built with <see cref="FromDescription"/> on a file without PDO
/// records has no PDOs, the default ones included, where §6.1 has a device that supports an I/O
/// function "support the related default PDOs": the file wins (see
/// <see cref="FromDescription"/>).</description></item>
/// </list>
/// <para>
/// Which objects a description must declare per function block: see <see cref="FromDescription"/>.
/// </para>
/// <para>
/// <b>Readings and choices where the norm is silent or not of one voice.</b> Each is argued where
/// it is used:
/// </para>
/// <list type="bullet">
/// <item><description><b>Digital inputs</b> (<see cref="SetDigitalInput"/>, the remarks on its
/// interrupt evaluation): the directions of <c>6007h</c> (low to high) and <c>6008h</c> (high to
/// low) follow §8.2.6 and §8.2.7, not the reversed labels of Figure 4.</description></item>
/// <item><description><b>Digital outputs</b> (<see cref="ReadDigitalOutputs"/>):
/// <list type="bullet">
/// <item><description>The filter mask acts on received values, as §8.3.5 and §7.2.1.1 have it,
/// not nearest the output as Figure 5 draws it.</description></item>
/// <item><description>The error value <c>6207h</c> is a logical value that the polarity
/// inverts.</description></item>
/// </list>
/// </description></item>
/// <item><description><b>The error state</b> (<see cref="OutputsInErrorState"/>):
/// <list type="bullet">
/// <item><description>Writes received while it lasts are stored and reach no terminal, even
/// with error mode 0, as Figures 5 and 7 have it.</description></item>
/// <item><description>Stopped is a cause however the node got there.</description></item>
/// <item><description>Figure 7's "Switch if Device Failure" gives way to §8.7.4, which adds the
/// 'Stop remote node' indication.</description></item>
/// <item><description>A device failure the application reports outlasts NMT resets. A master's
/// Start during it is not refused.</description></item>
/// <item><description>For a device failure, <c>1029h:01</c> = 02h requests Stopped from
/// Pre-operational and Operational alike (§6.2.4). A failure reported in Initialisation, where the
/// node takes no request, or one that outlasts a reset, makes its request when the node leaves
/// Initialisation (see <see cref="ReportDeviceFailure"/>).</description></item>
/// </list>
/// </description></item>
/// <item><description><b>Analog inputs</b> (<see cref="SetAnalogInput"/>):
/// <list type="bullet">
/// <item><description>How the interrupt conditions combine: the limits are OR'd, the deltas are
/// OR'd, and the two groups are ANDed. This follows Figure 6, §8.6.5 and §8.6.6 against §6.2.8.
/// The limits are OR'd where the figure draws an XOR, and <c>6421h</c> = 0 never
/// interrupts.</description></item>
/// <item><description>The limits are left adjusted, compared with the input times 2^16, after
/// §8.6.5 and §8.6.6 and over Figure 6.</description></item>
/// <item><description>The deltas are plain counts of the 16-bit value, a choice where §8.6.7 to
/// §8.6.9 say nothing.</description></item>
/// <item><description>"By more than" means strictly greater.</description></item>
/// <item><description>The last communicated value is reset at an interrupt, on entering
/// Operational, on every reset, and when <c>6423h</c> goes from FALSE to TRUE.</description></item>
/// <item><description>The warning of §5.3 ends when <c>6423h</c> is written TRUE or the node
/// leaves Operational. The norm does not say when it ends.</description></item>
/// </list>
/// </description></item>
/// <item><description><b>Analog outputs</b> (<see cref="ReadAnalogOutput"/>):
/// <list type="bullet">
/// <item><description><c>6444h</c> is read as left adjusted. Figure 7 marks it so, and §8.7.5
/// is silent. The output takes the upper word and ignores the lower.</description></item>
/// <item><description>Figure 7 draws offset and scaling after the error switch, against the
/// NOTE of §8.7.5. Since neither offset nor scaling is built, nothing turns on
/// it.</description></item>
/// </list>
/// </description></item>
/// <item><description><b>Left adjustment of values narrower than 16 bits</b> (§8.4.2, §8.5.2) is
/// the writer's business. The device stores and drives the 16-bit value it is
/// given.</description></item>
/// <item><description><b>Entering Operational</b> sends every TPDO of transmission type
/// <c>FFh</c> (§6.1), whatever <c>6005h</c> and <c>6423h</c> say (see
/// <see cref="OnNmtStateChanged"/>). TPDOs 2 to 4 are disabled by default (see
/// <see cref="Cia401Configuration.DefaultPdos"/>).</description></item>
/// <item><description><b>Value ranges</b> (see <see cref="ValidateWrite"/>):
/// <list type="bullet">
/// <item><description>A BOOLEAN takes 0 and 1 only.</description></item>
/// <item><description>The reserved bits of <c>6421h</c> are refused.</description></item>
/// <item><description><c>6443h</c> takes 0h and 1h only.</description></item>
/// </list>
/// </description></item>
/// <item><description><b>Resets and stored values</b> (see <see cref="OnReset"/>):
/// <list type="bullet">
/// <item><description>Only Reset Node, the application reset, returns the outputs to the write
/// objects (§6.2.5 NOTE). Reset Communication leaves them where they are.</description></item>
/// <item><description>The stored output values come up after a <c>1010h</c> save and Reset Node.
/// This follows §6.2.5 NOTE and §6.2.7 NOTE ("stored values overwrite default values"), against
/// the "default state" of §7.2.3 NOTE 1 and §7.2.5 NOTE 1, which is read as the case without
/// stored values.</description></item>
/// <item><description>A save stores the input objects too, <c>6000h</c>, <c>6401h</c> and
/// <c>6422h</c>, because the seam cannot exclude an entry. At Reset Node the device rewrites the
/// inputs from the application's levels and clears <c>6422h</c>, so no stored input value is
/// read over SDO after the reset.</description></item>
/// </list>
/// </description></item>
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

    // Whether the device takes its blocks from the node's device description (FromDescription)
    // rather than from a configuration.
    private readonly bool _fromDescription;

    // The host of the node the device is attached to; null until Attach has declared everything.
    // Written once, on the thread that opens the node; read from any thread.
    private ICanOpenApplicationHost? _host;

    // 1 from the moment an Attach begins until it fails; an Attach that succeeds keeps it. One device,
    // one node: a second Attach, concurrent or later, is refused before it touches the blocks.
    private int _attaching;

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
    /// is kept. A device built with <see cref="FromDescription"/> needs no PDO count: the
    /// description gives the node its PDOs, and the count is left as it is.</summary>
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
    /// (<see cref="Cia401Configuration.RequiredPdoCount"/>), none for a device built with
    /// <see cref="FromDescription"/>, and failure monitoring.</summary>
    public CanOpenApplicationRequirements Requirements { get; }

    /// <summary>Declares the objects of every function block, or adopts those the node's device
    /// description declared (<see cref="FromDescription"/>); brings the inputs to the application's
    /// levels and the outputs to what their write objects hold; sets the device type and the default
    /// PDOs (CiA 401-1 §6.1, §6.2.1, §6.2.5 to §6.2.12); and only then makes the device usable from
    /// the application side. With a plant model, posts its first call, which the node holds until it
    /// has opened (see <see cref="OutputsChanged"/>).</summary>
    /// <remarks>
    /// The inputs are brought to the application's levels as Reset Node brings them
    /// (<see cref="OnReset"/>): <c>6000h</c> is the physical level, 0 until the application sets
    /// one, under the polarity of <c>6002h</c>, <c>6401h</c> the value the application set, 0 until
    /// it sets one, and <c>6422h</c> is clear. A value a description gives an input object is
    /// replaced, so that the device that powers on is the device a reset restores. The outputs take
    /// the value their write objects hold, a description's included, through the error mode and the
    /// polarity as always (§6.2.5 NOTE, §6.2.7 NOTE).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The device is attached to a node already.</exception>
    /// <exception cref="ArgumentException">The node's device description and the device do not fit
    /// (<see cref="FromDescription"/>; for a device built from a configuration, the description
    /// declares an object of its logical device). The parameter is <c>options</c>, as for every
    /// application that fails the open.</exception>
    public void Attach(ICanOpenApplicationHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (Interlocked.CompareExchange(ref _attaching, 1, 0) != 0) throw AttachedAlready();

        try
        {
            var od = host.ObjectDictionary;
            CanOpenDeviceType deviceType;
            if (_fromDescription)
            {
                deviceType = AdoptDescription(od);
            }
            else
            {
                RefuseDescribedObjects(od);
                _digitalInputs?.Declare(od);
                _digitalOutputs?.Declare(od);
                _analogInputs?.Declare(od);
                _analogOutputs?.Declare(od);
                deviceType = _deviceType;
            }

            DigitalInputsReset(host);
            AnalogInputsReset(host, resetNode: true);
            _digitalOutputs?.Start(od);
            _analogOutputs?.Start(od);

            host.SetDeviceType(_logicalDevice, deviceType);
            foreach (var pdo in _defaultPdos)
            {
                var mapping = new PdoMapping();
                foreach (var (index, subindex, bits) in pdo.Entries) mapping.Add(index, subindex, bits);
                if (pdo.IsTpdo) host.SetDefaultTpdo(pdo.Number, mapping, pdo.Enabled);
                else host.SetDefaultRpdo(pdo.Number, mapping);
            }
        }
        catch
        {
            Volatile.Write(ref _attaching, 0);
            throw;
        }

        Volatile.Write(ref _host, host);
        if (_process is not null) host.Post(() => RaiseOutputsChanged(callProcess: true));
    }

    private InvalidOperationException AttachedAlready()
        => new($"{Name} is attached to a node already: one device, one node.");

    /// <summary>Refuses a write to an entry whose own access is read-only, the catalogue's on a
    /// device built from a configuration and the file's on one built with
    /// <see cref="FromDescription"/>, with the abort code an SDO download gets for it
    /// (<c>06010002h</c>, CiA 301 v4.2.0 Table 22): the SDO server refuses such a download before the
    /// device is asked, but a local write through the dictionary asks the device. Refuses a value
    /// other than 0 and 1 for every writable BOOLEAN entry in the claimed range, whatever block it
    /// belongs to: <c>6005h</c> and <c>6423h</c>, and any BOOLEAN a description declares there. A
    /// BOOLEAN is TRUE or FALSE and nothing else (CiA 301 v4.2.0 §7.1.4.3); the code is
    /// <c>06090030h</c>, "invalid value for parameter" (Table 22).
    /// Refuses an analog interrupt trigger selection (<c>6421h</c>) with a reserved bit set (CiA
    /// 401-1 §8.6.2), and an analog output error mode (<c>6443h</c>) other than 0h and 1h (§8.7.4:
    /// "others = reserved"), with the same code; these two rules hold for the blocks the device has.
    /// An index in the claimed range the catalogue does not know, and a parameter object of a block
    /// the device does not have, both of which only a description can declare, are stored as their
    /// access allows, without the reserved-value rules. The device's own writes pass. Every other
    /// write is accepted.</summary>
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
    /// <see cref="SetAnalogInput"/>). Leaving Initialisation with a device failure active asks the
    /// node for the state <c>1029h:01</c> names (§6.2.4; see <see cref="ReportDeviceFailure"/>).
    /// Actor loop.</summary>
    public void OnNmtStateChanged(NmtState previous, NmtState current)
    {
        var host = AttachedHost();
        _stopped = current == NmtState.Stopped;
        UpdateErrorState(host);

        // The node takes no state request in Initialisation: a device failure reported there, or
        // one that outlasted a reset, asks for its state as the node leaves it (see
        // ReportDeviceFailure). Asked from here, the change follows once this round is told.
        if (previous == NmtState.Initializing && _deviceFailure) RequestDeviceFailureState(host);

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
    /// The node restores and calls this in the same actor job, so no SDO transfer comes between;
    /// but a reader on another thread (<see cref="ReadDigitalInputs"/>, <see cref="ReadAnalogInput"/>)
    /// can, in that window, see the restored value, a default or a stored one.
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
    /// carries on (several exceptions as one <see cref="AggregateException"/>). The device's state is
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
    /// changes none. From the model the outputs can move only by <see cref="ReportDeviceFailure"/>
    /// entering the error state. Such a change is raised to the subscribers once the model returns,
    /// but does not call the model again: the model made the change itself, and sees its effect in
    /// the readers as soon as the call it made returns, the call applying inline. A model that
    /// throws is reported as a throwing subscriber is, and is called again for the next change.
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

    /// <summary>A block for the application side. A device built from a configuration has its blocks
    /// from the start and answers a channel of a block it lacks with the
    /// <see cref="ArgumentOutOfRangeException"/> of the caller, as for any channel it does not have.
    /// One built with <see cref="FromDescription"/> knows its blocks only once attached: before, and
    /// for a block the description does not declare, this throws.</summary>
    /// <exception cref="InvalidOperationException">A device built from a description is not attached
    /// yet, or its description declares no such block.</exception>
    private TBlock? ApplicationBlock<TBlock>(ref TBlock? block, string function)
        where TBlock : class
    {
        if (!_fromDescription) return block;
        RequireHost();
        return Volatile.Read(ref block) ?? throw new InvalidOperationException(
            $"{Name} has no {function}: the node's device description declares none.");
    }

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
        int inGroup = Math.Min(Cia401Configuration.ChannelsPerDigitalGroup, channels - (g * Cia401Configuration.ChannelsPerDigitalGroup));
        return (byte)((1 << inGroup) - 1);
    }

    /// <summary>Declares an array with the data type, access, PDO mapping and default the catalogue
    /// gives it: sub-index 0, the UNSIGNED8 "highest sub-index supported" (CiA 301 v4.2.0 §7.4.3,
    /// Table 42), const in the entry descriptions of CiA 401-1 (Table 39 for <c>6000h</c>), holding
    /// <paramref name="elements"/>, then one element per group of eight digital channels,
    /// per analog channel or per interrupt source bank; an object without a default ("No", CiA 401-1
    /// §8.2.1, §8.4.2) starts at 0. Constructing thread, in Attach.</summary>
    private static void DeclareArray(ObjectDictionary od, ProfileObject profileObject, int elements)
    {
        var definition = profileObject.Definition;
        od.AddU8(profileObject.Index, 0x00, (byte)elements, OdAccess.ReadOnly, pdoMappable: false);
        var initial = Cia401Codec.Encode(definition.DataType, definition.DefaultValue ?? 0);
        for (int sub = 1; sub <= elements; sub++)
        {
            od.AddRaw(profileObject.Index, (byte)sub, definition.DataType, initial, definition.Access,
                definition.PdoMapping != Cia401PdoMapping.No);
        }
    }

    /// <summary>Declares a one-byte variable with what the catalogue gives it. Constructing thread,
    /// in Attach.</summary>
    private static void DeclareVariable(ObjectDictionary od, ProfileObject profileObject)
    {
        var definition = profileObject.Definition;
        od.AddRaw(profileObject.Index, 0x00, definition.DataType,
            new[] { (byte)(definition.DefaultValue ?? 0) }, definition.Access,
            definition.PdoMapping != Cia401PdoMapping.No);
    }

    /// <summary>One request for everything collected: the node sends each TPDO that maps one of the
    /// entries once, however many of them it maps.</summary>
    private static void RequestTransmission(ICanOpenApplicationHost host, List<CanOpenObjectAddress> send)
    {
        if (send.Count > 0) host.RequestTransmission(send);
    }

    /// <summary>A catalogue object at its index in the device's logical device: what a block keeps of
    /// each of its objects, the definition with the index, so that a parameter is read with the
    /// default the catalogue gives it.</summary>
    private readonly struct ProfileObject
    {
        internal ProfileObject(int logicalDevice, Cia401ObjectDefinition definition)
        {
            Definition = definition;
            Index = Shift(logicalDevice, definition);
        }

        internal Cia401ObjectDefinition Definition { get; }

        /// <summary>The index in the device's logical device.</summary>
        internal ushort Index { get; }

        /// <summary>
        /// The value of sub-index <paramref name="subindex"/> of a parameter object, as the device
        /// acts on it: what the dictionary holds, or the catalogue default where the dictionary has no
        /// such entry. The one place a parameter is read.
        /// </summary>
        /// <remarks>
        /// "If an optional parameter is not implemented, the device shall behave as specified in the
        /// Default value attribute" (CiA 401-1 §8.1, repeated in each optional object of clause 8). A
        /// device built from a configuration declares every parameter of its blocks with the
        /// catalogue's data type, so the default is never taken there. One built with
        /// <see cref="FromDescription"/> adds no parameter the file leaves out, and a parameter
        /// array shorter than its block leaves the channels beyond it at the default. An object
        /// without a default ("No") reads 0. The value is decoded with the catalogue's data type,
        /// which an adopted entry has been checked to have.
        /// </remarks>
        internal long Read(ObjectDictionary od, byte subindex)
            => od.TryReadRaw(Index, subindex, out var raw)
                ? Cia401Codec.Decode(Definition.DataType, raw)
                : Definition.DefaultValue ?? 0;

        /// <summary><see cref="Read"/> of a one-byte parameter.</summary>
        internal byte ReadByte(ObjectDictionary od, byte subindex) => unchecked((byte)Read(od, subindex));
    }
}
