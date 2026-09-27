namespace CanKit.Pro.CANopen;

/// <summary>
/// Which role a node is opened as. One process can host a device and a tool at once; each
/// node keeps the profile it was constructed with, and the default of <c>1F80h</c> is chosen
/// from that profile when the communication objects are created.
/// </summary>
public enum CanOpenNodeProfile
{
    /// <summary>
    /// A device. Bits 2 and 3 of <c>1F80h</c> start clear, so an active flying master may
    /// enter Operational and may boot the slaves assigned in <c>1F81h</c>.
    /// </summary>
    Device = 0,

    /// <summary>
    /// A tool or NMT master. Bits 2 and 3 of <c>1F80h</c> start set, so self-start and
    /// slave-start stay suppressed until the application clears them.
    /// </summary>
    Tool = 1,
}
