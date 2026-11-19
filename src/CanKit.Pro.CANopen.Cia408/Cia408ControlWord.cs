namespace CanKit.Pro.CANopen.Cia408;

/// <summary>
/// The bits of the CiA 408 device control word, <c>6040h</c> (16 bits, CiA 408 v2.2.0 §9.3.2,
/// Figure 6, Table 73) and <c>6044h</c> (32 bits, §9.3.6, Figure 8, Table 85), the one place they are
/// written. The two words share bits 0 to 15; the 32-bit word extends the manufacturer-specific
/// field to bit 23 and reserves bits 24 to 31.
/// </summary>
/// <remarks>Bits 3..0 (R M H D) drive the state machine (§7.7, Table 7; see
/// <see cref="Cia408StateMachine"/>). Bits 6 to 11 (parameter set, control mode and device mode
/// specific) and 13 to 15 (13 to 23 in the 32-bit word, manufacturer-specific) have no behaviour in
/// this slice: they are stored and handed on (<see cref="Cia408Device.ControlWord"/>).</remarks>
internal static class Cia408ControlWord
{
    /// <summary>Bit 0, D: "Enable device function" when set.</summary>
    public const uint Disabled = 1u << 0;

    /// <summary>Bit 1, H: "Enable hold" when set.</summary>
    public const uint HoldEnable = 1u << 1;

    /// <summary>Bit 2, M: "Enable device mode active enable" when set.</summary>
    public const uint DeviceModeActiveEnable = 1u << 2;

    /// <summary>Bit 3, R: "Reset fault", taken on its edge (Table 7, NOTE 2).</summary>
    public const uint ResetFault = 1u << 3;

    /// <summary>Bits 3..0, R M H D: the bits the state machine reads. Bits 4 and up never move
    /// it.</summary>
    public const uint MachineBits = ResetFault | DeviceModeActiveEnable | HoldEnable | Disabled;

    /// <summary>The bits of <c>6040h</c> that are "Reserved (always 00b)" or "(always 0)" (Table 73):
    /// 4, 5 and 12.</summary>
    public const uint Reserved16 = (1u << 4) | (1u << 5) | (1u << 12);

    /// <summary>The bits of <c>6044h</c> that are reserved (Table 85): 4, 5 and 12 as in the 16-bit
    /// word, and 24 to 31, "Reserved (always 00000000b)".</summary>
    public const uint Reserved32 = Reserved16 | 0xFF00_0000u;
}
