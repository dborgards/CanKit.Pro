namespace CanKit.Pro.CANopen.Application;

/// <summary>One object-dictionary entry, named by its index and sub-index.</summary>
internal readonly record struct CanOpenObjectAddress(ushort Index, byte Subindex);
