using System;

namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// What a step of the process model (<see cref="ICia408Process.Step"/>) sees and may change: the
/// state of the device state machine, the device and control modes, the time since the previous
/// step, the setpoints, and the actual values and status bits it writes. Valid during the step
/// only, and on the thread of the step, the node's actor loop; afterwards, and from another
/// thread, every member throws <see cref="InvalidOperationException"/>. The application seam has no
/// query for the actor loop, so the context checks the thread its step was given on: a model that
/// hands the context to a worker thread during the step is refused there, rather than writing the
/// dictionary and the device's state off the loop.
/// </summary>
internal sealed class Cia408ProcessContext
{
    private readonly Cia408Device _device;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _valid = true;

    internal Cia408ProcessContext(Cia408Device device, Cia408State state, Cia408DeviceMode deviceMode,
        Cia408ControlMode controlMode, TimeSpan elapsed)
    {
        _device = device;
        _state = state;
        _deviceMode = deviceMode;
        _controlMode = controlMode;
        _elapsed = elapsed;
    }

    private readonly Cia408State _state;
    private readonly Cia408DeviceMode _deviceMode;
    private readonly Cia408ControlMode _controlMode;
    private readonly TimeSpan _elapsed;

    /// <summary>The state the device state machine rests in (CiA 408 v2.2.0 §7.6).</summary>
    public Cia408State State => Valid(_state);

    /// <summary>The device mode, <c>6042h</c>, as it holds now (§9.3.4).</summary>
    public Cia408DeviceMode DeviceMode => Valid(_deviceMode);

    /// <summary>The control mode, <c>6043h</c>, as it holds now (§9.3.5).</summary>
    public Cia408ControlMode ControlMode => Valid(_controlMode);

    /// <summary>The time on the node's clock since the previous step; for the first step, since the
    /// model was started after the node opened.</summary>
    public TimeSpan Elapsed => Valid(_elapsed);

    /// <summary>The value (sub-index <c>01h</c>) of the setpoint record <paramref name="definition"/>
    /// as the dictionary holds it: what the master last wrote, or the default.</summary>
    /// <exception cref="ArgumentException">The device declares no such setpoint: it is no setpoint
    /// of a supported control mode in the width the device uses, or the device description does not
    /// declare it (<see cref="Cia408Device.FromDescription"/>).</exception>
    public long Setpoint(Cia408ObjectDefinition definition)
    {
        Valid(0);
        return _device.ReadSetpoint(definition);
    }

    /// <summary>Sets the value (sub-index <c>01h</c>) of the actual value record
    /// <paramref name="definition"/>; it is transmitted once the step has returned, with whatever
    /// else the step changed.</summary>
    /// <exception cref="ArgumentException">The device declares no such actual value: it is no actual
    /// value of a supported control mode in the width the device uses, or the device description does
    /// not declare it (<see cref="Cia408Device.FromDescription"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> does not fit the
    /// record's value type.</exception>
    public void SetActual(Cia408ObjectDefinition definition, long value)
    {
        Valid(0);
        _device.WriteActualFromStep(definition, value);
    }

    /// <summary>Sets or clears a status bit that belongs to the process (§9.3.3, Table 76; §9.3.7,
    /// Table 88): 7 to 15, and 16 to 23, which only the 32-bit status word <c>6045h</c> carries. The
    /// status words are rewritten and transmitted once the step has returned, if they changed.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bit"/> is one of the device's
    /// own (0 to 3 the state, 4 local, 5 warning), the reserved bit 6, or above 23 (reserved in
    /// <c>6045h</c>).</exception>
    public void SetStatusBit(int bit, bool on)
    {
        Valid(0);
        _device.SetProcessStatusBit(bit, on);
    }

    /// <summary>Ends the context: the step has returned.</summary>
    internal void Invalidate() => _valid = false;

    private T Valid<T>(T value)
    {
        if (!_valid) throw new InvalidOperationException("The process context is valid during its step only.");
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The process context is used on the thread of its step only, the node's actor loop.");
        return value;
    }
}
