using System;
using System.Collections.Generic;
using System.Globalization;
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
/// <b>Outputs.</b> The digital outputs follow <c>6200h</c> through the filter mask, the error state
/// and the polarity, and the application reads the levels at the terminals and is told when they
/// change (<see cref="ReadDigitalOutputs"/>, <see cref="OutputsChanged"/>); the error state and its
/// causes are described on <see cref="OutputsInErrorState"/>. Not built in this slice: NOTE 1 of
/// CiA 401-1 §7.2.3, under which outputs are accepted only once the first heartbeat of the device
/// that sets them has been received, or the master has guarded the node; the device accepts them
/// from the start. Nor the sub-indices 02h and 03h of <c>1029h</c> (§6.2.4): a device failure follows
/// sub-index 01h.
/// </para>
/// </remarks>
internal sealed partial class Cia401Device : ICanOpenApplication
{
    private readonly int _logicalDevice;
    private readonly CanOpenDeviceType _deviceType;
    private readonly IReadOnlyList<Cia401DefaultPdo> _defaultPdos;
    private readonly int _requiredPdoCount;
    private readonly CanOpenIndexRange[] _claims;

    // The host of the node the device is attached to; null until Attach has declared everything.
    // Written once, on the thread that opens the node; read from any thread.
    private ICanOpenApplicationHost? _host;

    // The managed thread id of the device's own validated write while it runs, else 0: the write
    // the device makes to an entry it declared read-only (6000h) passes its own ValidateWrite, and
    // no other writer's does. A thread id rather than a flag, since a local write on another thread
    // can be validated while the actor makes the device's write.
    private int _ownWriteThread;

    /// <summary>A device with what <paramref name="configuration"/> says. The configuration is read
    /// once, here.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or the logical device is out of range
    /// (<see cref="Cia401Configuration.Validate"/>).</exception>
    /// <exception cref="ArgumentException">The configuration has no I/O function
    /// (<see cref="Cia401Configuration.Validate"/>), or one this device does not implement yet:
    /// analog inputs and analog outputs arrive with their own partials.</exception>
    public Cia401Device(Cia401Configuration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        configuration.Validate();
        if (configuration.AnalogInputs > 0 || configuration.AnalogOutputs > 0)
        {
            throw new ArgumentException(
                "The CiA 401 device implements digital inputs and outputs only so far: analog inputs and analog "
                + "outputs are not built yet, and a device that declared their default PDOs without their objects "
                + "could not open.", nameof(configuration));
        }

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
    /// the application side.</summary>
    /// <exception cref="InvalidOperationException">The device is attached to a node already.</exception>
    public void Attach(ICanOpenApplicationHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (Volatile.Read(ref _host) is not null) throw AttachedAlready();

        _digitalInputs?.Declare(host.ObjectDictionary);
        _digitalOutputs?.Declare(host.ObjectDictionary);

        host.SetDeviceType(_logicalDevice, _deviceType);
        foreach (var pdo in _defaultPdos)
        {
            var mapping = new PdoMapping();
            foreach (var (index, subindex, bits) in pdo.Entries) mapping.Add(index, subindex, bits);
            if (pdo.IsTpdo) host.SetDefaultTpdo(pdo.Number, mapping, pdo.Enabled);
            else host.SetDefaultRpdo(pdo.Number, mapping);
        }

        if (Interlocked.CompareExchange(ref _host, host, null) is not null) throw AttachedAlready();
    }

    private InvalidOperationException AttachedAlready()
        => new($"{Name} is attached to a node already: one device, one node.");

    /// <summary>Refuses a write to an entry the device declared read-only, with the abort code an SDO
    /// download gets for it (<c>06010002h</c>, CiA 301 v4.2.0 Table 22): the SDO server refuses such a
    /// download before the device is asked, but a local write through the dictionary asks the device.
    /// Refuses a value other than 0 and 1 for a BOOLEAN entry (<c>6005h</c>), which is TRUE or FALSE
    /// and nothing else (CiA 301 v4.2.0 §7.1.4.3), with <c>06090030h</c>, "invalid value for
    /// parameter" (Table 22). The device's own writes pass. Every other write is accepted.</summary>
    public OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value)
    {
        // Nobody writes a claimed entry before Attach has declared it.
        var host = Volatile.Read(ref _host);
        if (host is null || IsOwnWrite) return OdWriteDecision.Accept;
        if (!host.ObjectDictionary.TryGet(index, subindex, out var entry)) return OdWriteDecision.Accept;
        if ((entry.Access & OdAccess.WriteOnly) == 0) return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
        if (entry.DataType == OdDataType.Boolean && value.Length == 1 && value[0] > 1)
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
        }
        RequestTransmission(host, send);
        RaiseOutputsChanged();
    }

    /// <summary>Stopped puts the outputs in the error state while it lasts (CiA 401-1 §8.3.3).
    /// Entering Operational sends every TPDO of transmission type <c>FFh</c> (§6.1): the device asks
    /// for every entry its blocks transmit, in one request, whatever the interrupt masks say. Actor
    /// loop.</summary>
    public void OnNmtStateChanged(NmtState previous, NmtState current)
    {
        var host = AttachedHost();
        _stopped = current == NmtState.Stopped;
        UpdateErrorState(host);
        if (current == NmtState.Operational)
        {
            var send = new List<CanOpenObjectAddress>();
            DigitalInputsToSendOnStart(send);
            RequestTransmission(host, send);
        }
        RaiseOutputsChanged();
    }

    /// <summary>A reset ends the communication failure and Stopped as causes of the error state
    /// (the node has told both by now); a device failure the application reported outlasts it. Reset
    /// Node, the application reset, also drops the held output values for what <c>6200h</c> holds
    /// after it (CiA 401-1 §6.2.5 NOTE); Reset Communication resets no application and leaves the
    /// outputs as they are. The physical input levels the application set are kept. Actor
    /// loop.</summary>
    public void OnReset(NmtCommand command)
    {
        var host = AttachedHost();
        _communicationFailure = false;
        _stopped = false;
        UpdateErrorState(host);
        if (command == NmtCommand.ResetNode) DigitalOutputsReset(host);
        RaiseOutputsChanged();
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
    {
        Volatile.Write(ref _ownWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            host.ObjectDictionary.WriteRaw(index, subindex, new[] { value });
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

    /// <summary>Declares an 8-bit digital array with the data type, access, PDO mapping and default
    /// the catalogue gives it: sub-index 0, the const UNSIGNED8 "highest sub-index supported"
    /// (CiA 301 §7.4.5) holding <paramref name="groups"/>, then one element per group; an object
    /// without a default ("No", §8.2.1) starts at 0. Constructing thread, in Attach.</summary>
    private static void DeclareGroupArray(ObjectDictionary od, int logicalDevice, int groups, Cia401ObjectDefinition definition)
    {
        var index = Shift(logicalDevice, definition);
        od.AddU8(index, 0x00, (byte)groups, OdAccess.ReadOnly, pdoMappable: false);
        var initial = (byte)(definition.DefaultValue ?? 0);
        for (int sub = 1; sub <= groups; sub++)
        {
            od.AddRaw(index, (byte)sub, definition.DataType, new[] { initial }, definition.Access,
                definition.PdoMapping != Cia401PdoMapping.No);
        }
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
