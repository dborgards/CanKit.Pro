namespace CanKit.Pro.CANopen.Lss;

/// <summary>The two states of the LSS slave finite state automaton (CiA 305 §5.2).</summary>
public enum LssState : byte
{
    /// <summary>
    /// LSS waiting. The slave may be identified. Node-id and bit timing are not changed from here.
    /// </summary>
    Waiting = LssCommand.ModeWaiting,

    /// <summary>
    /// LSS configuration. Pending node-id and bit timing may be changed. At most one slave in the
    /// network may be here when a service returns a value that is not identical for every slave.
    /// </summary>
    Configuration = LssCommand.ModeConfiguration,
}
