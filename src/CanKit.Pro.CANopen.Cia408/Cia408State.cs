namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The states of the CiA 408 device state machine (CiA 408 v2.2.0 §7.6, Figure 1 and
/// Table 4). <see cref="FaultReaction"/> is passed through within one call of
/// <see cref="Cia408StateMachine.Fault"/> and is never a state the machine rests in.</summary>
internal enum Cia408State
{
    /// <summary>Entered after power on (D0); self test and initialisation may run.</summary>
    NotReady,

    /// <summary>Initialised; parameters may be set, the valve spool position controller is
    /// disabled.</summary>
    Init,

    /// <summary>Parameters may be set; actual values are available.</summary>
    Disabled,

    /// <summary>The preset hold setpoint is effective; the setpoint is not.</summary>
    Hold,

    /// <summary>The setpoint defined by the device mode is effective; the device mode cannot be
    /// changed.</summary>
    Active,

    /// <summary>A fault state in which the device function is disabled.</summary>
    FaultInit,

    /// <summary>A fault state in which the device function is disabled and actual values are
    /// available.</summary>
    FaultDisabled,

    /// <summary>A fault state in which the actual value is held or the preset hold setpoint is
    /// effective.</summary>
    FaultHold,

    /// <summary>The evaluation of a detected fault, which ends in one of the three fault states
    /// (Table 9).</summary>
    FaultReaction,
}
