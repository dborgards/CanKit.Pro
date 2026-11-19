namespace CanKit.Pro.CANopen.Cia408;

/// <summary>A value record of CiA 408 v2.2.0 as a device reports it (§8.7.1): the value at
/// sub-index <c>01h</c>, the SI unit at <c>02h</c> and the prefix at <c>03h</c>, each as read from
/// the device. Unit and prefix may be configurable or fixed by the manufacturer (§8.3), so the
/// catalogue's defaults (<see cref="Cia408ObjectDefinition.DefaultUnit"/>,
/// <see cref="Cia408ObjectDefinition.DefaultPrefix"/>) say what the norm suggests, and this what the
/// device uses.</summary>
/// <param name="Value">The value, sign-extended for a signed value type of the catalogue.</param>
/// <param name="Unit">The SI unit as its CiA 303-2 code (UNSIGNED8; <c>00h</c> dimensionless).</param>
/// <param name="Prefix">The prefix as the power of ten of CiA 303-2 (INTEGER8; <c>FDh</c> = −3).</param>
internal readonly record struct Cia408ValueRecord(long Value, byte Unit, sbyte Prefix);
