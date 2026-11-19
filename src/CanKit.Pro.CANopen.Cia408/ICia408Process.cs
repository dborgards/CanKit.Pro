namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The process model of a CiA 408 device: the plant behind the valve or drive, which turns the
/// effective setpoint into actual values and status bits. Handed to
/// <see cref="Cia408Device(Cia408Configuration, ICia408Process?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Step"/> is called on the node's actor loop: every
/// <see cref="Cia408Configuration.ProcessInterval"/> of the node's clock, and once after every
/// callback of the device in which the state machine changed state, after the subscribers of
/// <see cref="Cia408Device.StateChanged"/>. It is not called in NOT_READY.
/// </para>
/// <para>
/// Which setpoint is effective in which state is the model's business (CiA 408 v2.2.0 §7.6,
/// Table 5): in HOLD the hold setpoint, in ACTIVE the setpoint of the device mode; the context tells
/// the state and both modes. What the model throws is reported on
/// <see cref="ICanOpenNode.BackgroundExceptionOccurred"/> and stops neither the device nor the next
/// step (see <see cref="Cia408Device"/>).
/// </para>
/// </remarks>
internal interface ICia408Process
{
    /// <summary>One step of the model. The context is valid during the call only.</summary>
    void Step(Cia408ProcessContext context);
}
