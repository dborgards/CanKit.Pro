namespace CanKit.Pro.CANopen.Application;

/// <summary>How a device profile reads a parameter it acts on, for every profile alike: what the
/// dictionary holds, or the profile's default where the dictionary has no such entry.</summary>
internal static class CanOpenProfileParameter
{
    /// <summary>
    /// The value of <paramref name="index"/>:<paramref name="subindex"/> as the device acts on it:
    /// the dictionary's value decoded as <paramref name="type"/>
    /// (<see cref="CanOpenValueCodec.Decode"/>), or <paramref name="fallback"/> where the dictionary
    /// has no such entry.
    /// </summary>
    /// <remarks>
    /// A device built from a device description adds no optional parameter the file leaves out, and
    /// then behaves as the parameter's default: CiA 401-1 v3.1.0 §8.1 says so; a profile whose norm
    /// is silent says where it takes the same reading. The fallback is that default, the profile's to
    /// give: a catalogue default, or a value the profile derives where its catalogue gives none. The
    /// entry, when present, must have
    /// <paramref name="type"/>: a profile checks the declared types against its catalogue before it
    /// reads.
    /// </remarks>
    internal static long Read(ObjectDictionary od, ushort index, byte subindex, OdDataType type, long fallback)
        => od.TryReadRaw(index, subindex, out var raw) ? CanOpenValueCodec.Decode(type, raw) : fallback;
}
