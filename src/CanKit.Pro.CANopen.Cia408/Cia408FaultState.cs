namespace CanKit.Pro.CANopen.Cia408;

/// <summary>The fault state a fault reaction is asked to end in (CiA 408 v2.2.0 §7.7, Table 9).
/// The members are declared from the lowest to the highest: a request that Table 9 does not allow
/// from the state the fault came from is lowered, never raised (see
/// <see cref="Cia408StateMachine.Fault"/>).</summary>
internal enum Cia408FaultState
{
    /// <summary>FAULT_INIT, reached by D13.</summary>
    FaultInit,

    /// <summary>FAULT_DISABLED, reached by D12.</summary>
    FaultDisabled,

    /// <summary>FAULT_HOLD, reached by D9.</summary>
    FaultHold,
}
