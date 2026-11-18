namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// A plant model for a <see cref="Cia401Device"/>: what the process behind the terminals does with
/// the outputs, answered by setting the inputs. Handed to the device's constructor.
/// </summary>
/// <remarks>
/// <para>
/// The device calls <see cref="OnOutputsChanged"/> on the node's actor loop, inside one of its own
/// callbacks, so the device's setters (<see cref="Cia401Device.SetDigitalInputs"/>,
/// <see cref="Cia401Device.SetAnalogInputs"/> and the others) apply at once, before they return; a
/// model that sets several analog inputs uses <see cref="Cia401Device.SetAnalogInputs"/>, so that a
/// TPDO that maps them goes out once. The call must not block: it holds up the node.
/// </para>
/// <para>
/// When it is called is described on <see cref="Cia401Device.OutputsChanged"/>: after every callback
/// in which a level at the terminals changed, once after the device was attached, and once after
/// every NMT reset.
/// </para>
/// </remarks>
internal interface ICia401Process
{
    /// <summary>The outputs of <paramref name="device"/> may have changed: read them
    /// (<see cref="Cia401Device.ReadDigitalOutputs"/>, <see cref="Cia401Device.ReadAnalogOutput"/>)
    /// and set the inputs the process answers with. Actor loop.</summary>
    void OnOutputsChanged(Cia401Device device);
}
