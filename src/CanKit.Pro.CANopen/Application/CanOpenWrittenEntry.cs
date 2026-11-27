namespace CanKit.Pro.CANopen.Application;

/// <summary>A claimed entry and the kinds of write coalesced for it in one notification round.</summary>
internal readonly record struct CanOpenWrittenEntry(CanOpenObjectAddress Address, CanOpenWriteOrigins Origins);

/// <summary>The set of services that committed a reported change. Its default value is the empty set.</summary>
internal readonly struct CanOpenWriteOrigins
{
    private readonly int _bits;

    private CanOpenWriteOrigins(int bits) => _bits = bits;

    public bool Contains(CanOpenWriteKind kind) => (_bits & (1 << (int)kind)) != 0;

    internal CanOpenWriteOrigins Including(CanOpenWriteKind kind) => new(_bits | (1 << (int)kind));
}
