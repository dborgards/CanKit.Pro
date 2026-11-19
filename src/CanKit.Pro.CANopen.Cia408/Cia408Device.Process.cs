using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Application;

namespace CanKit.Pro.CANopen.Cia408;

// The setpoint and actual value records (CiA 408 v2.2.0 §8.4 Table 13, §8.5 Table 32, §8.7.1) and the
// process model that steps on the node's clock.
internal sealed partial class Cia408Device
{
    // The process model, or null, and its period.
    private readonly ICia408Process? _process;
    private readonly TimeSpan _interval;

    // The setpoint and actual value records of the supported control modes, by index in the device's
    // logical device. Built in the constructor; read from any thread.
    private readonly SortedDictionary<ushort, Cia408ObjectDefinition> _setpoints = new();
    private readonly SortedDictionary<ushort, Cia408ObjectDefinition> _actuals = new();

    // The actual values as the device holds them, by index: what Reset Node rewrites. Actor loop.
    private readonly Dictionary<ushort, long> _actualValues = new();

    // The status bits the process model set (7 to 23). Actor loop.
    private uint _processStatusBits;

    // The time of the last step, and when the next periodic one is due, on the node's clock; whether
    // the model has been started (StartProcess). Actor loop.
    private TimeSpan _lastStep;
    private TimeSpan _nextDue;
    private bool _processStarted;

    // True while the model steps; the actual values it set, to transmit once it has returned. Actor
    // loop.
    private bool _stepping;
    private readonly List<CanOpenObjectAddress> _stepWrites = new();

    /// <summary>Sets the actual value (sub-index <c>01h</c>) of <paramref name="definition"/> and
    /// transmits what maps it: how the application drives the actual values of a device without a
    /// process model, or alongside one. Any thread; applied on the node's actor loop, at once when
    /// called from inside one of the device's callbacks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The device declares no such actual value: it is no actual
    /// value of a supported control mode in the width the device uses.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> does not fit the
    /// record's value type.</exception>
    /// <exception cref="InvalidOperationException">No node has attached the device yet.</exception>
    public void SetActualValue(Cia408ObjectDefinition definition, long value)
    {
        var index = Record(_actuals, definition, "actual value");
        CheckRange(definition, value);
        var host = RequireHost();
        host.Post(() => WriteActual(host, index, definition, value));
    }

    /// <summary>For <see cref="Cia408ProcessContext.Setpoint"/>: the value of a declared setpoint
    /// record. Actor loop, inside a step.</summary>
    internal long ReadSetpoint(Cia408ObjectDefinition definition)
    {
        var index = Record(_setpoints, definition, "setpoint");
        return CanOpenValueCodec.Decode(definition.ValueType, AttachedHost().ObjectDictionary.ReadRaw(index, 0x01));
    }

    /// <summary>For <see cref="Cia408ProcessContext.SetActual"/>: writes the actual value now and
    /// transmits it once the step has returned. Actor loop, inside a step.</summary>
    internal void WriteActualFromStep(Cia408ObjectDefinition definition, long value)
    {
        var index = Record(_actuals, definition, "actual value");
        CheckRange(definition, value);
        WriteOwn(AttachedHost(), index, CanOpenValueCodec.Encode(definition.ValueType, value), 0x01);
        _actualValues[index] = value;
        _stepWrites.Add(new CanOpenObjectAddress(index, 0x01));
    }

    private void WriteActual(ICanOpenApplicationHost host, ushort index, Cia408ObjectDefinition definition, long value)
    {
        WriteOwn(host, index, CanOpenValueCodec.Encode(definition.ValueType, value), 0x01);
        _actualValues[index] = value;
        Publish(host, errorCodeChanged: false, new[] { new CanOpenObjectAddress(index, 0x01) });
    }

    /// <summary>For <see cref="Cia408ProcessContext.SetStatusBit"/>: the process bits 7 to 23 of the
    /// status word (§9.3.3, Table 76; §9.3.7, Table 88). Actor loop, inside a step.</summary>
    internal void SetProcessStatusBit(int bit, bool on)
    {
        if (bit is < 7 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit,
                "The process sets status bits 7 to 23; 0 to 5 are the device's, 6 and 24 to 31 are reserved (CiA 408 Tables 76 and 88).");
        }

        if (on) _processStatusBits |= 1u << bit;
        else _processStatusBits &= ~(1u << bit);
    }

    /// <summary>
    /// Starts the process model: the first periodic step is due one
    /// <see cref="Cia408Configuration.ProcessInterval"/> after this. Runs in the work
    /// <see cref="Attach"/> posts, after the node has opened — not in <see cref="Attach"/> itself
    /// (issue #561). Actor loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deadline is the device's (<see cref="ICanOpenApplicationHost.Arm"/>): no NMT reset cancels
    /// it, so the model keeps its period across Reset Node and Reset Communication, and a reset's
    /// state change adds a step (<see cref="FinishChanges"/>). Each periodic step re-arms first, for the
    /// next due time read from <see cref="ICanOpenApplicationHost.Now"/>, so the period does not drift
    /// by the handling time; due times the clock has passed already are skipped rather than caught up
    /// in a burst — the model is told the elapsed time.
    /// </para>
    /// <para>
    /// The model does not run in NOT_READY. The device leaves NOT_READY in <see cref="Attach"/>,
    /// before the model is started, and Reset Node passes through it within one call
    /// (<see cref="Cia408StateMachine.Restart"/>), so the rule holds by construction and no step
    /// checks it.
    /// </para>
    /// <para>
    /// Nothing steps the model before this has run. A fault the application reported from another
    /// thread can run ahead of the posted start and raise its change first; its step is left out, and
    /// the start then steps once, in the state the device rests in, with no time elapsed.
    /// </para>
    /// <para>
    /// <b>A throwing model.</b> What the model throws is reported on
    /// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/>, as for any callback of an application,
    /// and nothing else happens: no fault of the device, no EMCY, no state change — the model stands
    /// for the plant, and a fault of the plant is the application's to report
    /// (<see cref="ReportFault"/>). The deadline was re-armed before the step, so the next step comes on
    /// time; what the model wrote before it threw stays written and is transmitted.
    /// </para>
    /// </remarks>
    private void StartProcess(ICanOpenApplicationHost host)
    {
        if (_process is null) return;
        _processStarted = true;
        _lastStep = host.Now;
        _nextDue = _lastStep + _interval;
        host.Arm(_interval, () => OnProcessDue(host));
    }

    private void OnProcessDue(ICanOpenApplicationHost host)
    {
        var now = host.Now;
        do
        {
            _nextDue += _interval;
        }
        while (_nextDue <= now);

        host.Arm(_nextDue - now, () => OnProcessDue(host));
        List<Exception>? failures = null;
        StepProcess(host, ref failures);
        ThrowIfAny(failures);
    }

    /// <summary>One step of the model, if there is one, it has been started and no step is running
    /// already (a step whose model reports a fault does not step again); then the status words
    /// and the actual values it set are transmitted in one request. What the model throws is added to
    /// <paramref name="failures"/>. Actor loop.</summary>
    private void StepProcess(ICanOpenApplicationHost host, ref List<Exception>? failures)
    {
        if (_process is null || !_processStarted || _stepping) return;

        var od = host.ObjectDictionary;
        var now = host.Now;
        var context = new Cia408ProcessContext(this, _machine.State,
            (Cia408DeviceMode)unchecked((sbyte)od.ReadRaw(_deviceMode, 0x00)[0]),
            (Cia408ControlMode)(_hasControlMode ? unchecked((sbyte)od.ReadRaw(_controlMode, 0x00)[0]) : _controlModeDefault),
            now - _lastStep);
        _lastStep = now;
        _stepping = true;
        _stepWrites.Clear();
        try
        {
            _process.Step(context);
        }
        // Everything, deliberately: the model is application code, and whatever it throws must not
        // break the device or stop the next step. Collected, and thrown by the caller once it has
        // finished, out of the device's callback, so the node reports it on
        // BackgroundExceptionOccurred.
        catch (Exception ex)
        {
            (failures ??= new List<Exception>()).Add(ex);
        }
        finally
        {
            context.Invalidate();
            _stepping = false;
        }

        Publish(host, errorCodeChanged: false, _stepWrites.ToArray());
    }

    /// <summary>Writes every actual value again from what the device holds: after Reset Node, which
    /// restored the records to their defaults or to what <c>1010h</c> stored. Actor loop.</summary>
    private void RewriteActualValues(ICanOpenApplicationHost host)
    {
        foreach (var pair in _actuals)
        {
            _actualValues.TryGetValue(pair.Key, out var value);
            WriteOwn(host, pair.Key, CanOpenValueCodec.Encode(pair.Value.ValueType, value), 0x01);
        }
    }

    /// <summary>Declares a value record (§8.7.1); see <see cref="Attach"/>. Constructing thread.</summary>
    private void DeclareRecord(ObjectDictionary od, Cia408ObjectDefinition definition)
    {
        var index = Shift(definition);
        od.AddU8(index, 0x00, 0x03, OdAccess.ReadOnly, pdoMappable: false);
        od.AddRaw(index, 0x01, definition.ValueType, CanOpenValueCodec.Encode(definition.ValueType, definition.DefaultValue ?? 0),
            definition.Access, definition.PdoAccess != Cia408PdoAccess.No);
        od.AddU8(index, 0x02, definition.DefaultUnitKind == Cia408DefaultKind.Value ? definition.DefaultUnit ?? 0 : (byte)0,
            OdAccess.ReadOnly, pdoMappable: false);
        od.AddI8(index, 0x03, definition.DefaultPrefixKind == Cia408DefaultKind.Value ? definition.DefaultPrefix ?? 0 : (sbyte)0,
            OdAccess.ReadOnly, pdoMappable: false);
    }

    private void AddRecord(SortedDictionary<ushort, Cia408ObjectDefinition> records, Cia408ObjectDefinition definition)
        => records[Shift(definition)] = definition;

    /// <summary>The index of <paramref name="definition"/> among <paramref name="records"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">It is not one of them.</exception>
    private ushort Record(SortedDictionary<ushort, Cia408ObjectDefinition> records, Cia408ObjectDefinition definition, string what)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        var index = Shift(definition);
        if (!records.TryGetValue(index, out var declared) || !ReferenceEquals(declared, definition))
        {
            throw new ArgumentException(
                $"{definition} is no {what} of {Name}: the device declares the {what}s of its supported control modes only.",
                nameof(definition));
        }

        return index;
    }

    /// <summary>Refuses a value outside the value range of the record's type
    /// (<see cref="CanOpenValueCodec.IsInRange"/>).</summary>
    private static void CheckRange(Cia408ObjectDefinition definition, long value)
    {
        if (!CanOpenValueCodec.IsInRange(definition.ValueType, value))
            throw new ArgumentOutOfRangeException(nameof(value), value, $"{definition} is {definition.ValueType}: the value does not fit.");
    }
}
