namespace CanKit.Pro.CANopen;

internal enum CanOpenWriteKind
{
    Dictionary,
    Sdo,
    Rpdo,
    Mpdo,
    Srdo,
}

/// <summary>The service that committed a dictionary write. For a command callback,
/// AnsweringRpdo is set only when the node will also tell this claimant of that reception.</summary>
internal readonly record struct CanOpenWriteSource(CanOpenWriteKind Kind, int? AnsweringRpdo = null);
