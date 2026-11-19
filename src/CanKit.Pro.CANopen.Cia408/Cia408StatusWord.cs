namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The bits of the CiA 408 device status word, <c>6041h</c> (16 bits, CiA 408 v2.2.0 §9.3.3,
/// Figure 7, Table 76) and <c>6045h</c> (32 bits, §9.3.7, Figure 9, Table 88), that the device sets,
/// the one place they are written. Bits 3..0 (R M H D) encode the state of the device state machine
/// (§7.6, Table 4) and come from <see cref="Cia408StateMachine.StatusBits"/>.
/// </summary>
/// <remarks>Bits 7 to 12 (control mode specific, limit touched, control error, reached target) and
/// the manufacturer-specific ones are the process model's to set and are clear in this slice; bit 6
/// is reserved, always 0, and bits 24 to 31 of the 32-bit word likewise.</remarks>
internal static class Cia408StatusWord
{
    /// <summary>Bit 4, L: "local operation active" when set, remote (bus) control when clear. It
    /// mirrors <c>604Fh</c> (§9.3.9).</summary>
    public const uint Local = 1u << 4;

    /// <summary>Bit 5, W: a collective manufacturer-specific warning is active; <c>604Eh</c> gives the
    /// code (§9.3.8).</summary>
    public const uint Warning = 1u << 5;
}
