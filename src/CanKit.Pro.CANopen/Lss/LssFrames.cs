using System;
using System.Buffers.Binary;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Eight-byte LSS frames (CiA 305 §7.3). Byte 0 is the command specifier. Multi-byte values are
/// little-endian, as CiA 301 encodes them. Bytes the protocol marks reserved are 0.
/// </summary>
internal static class LssFrames
{
    internal const int Length = 8;

    internal static byte[] Command(byte commandSpecifier)
    {
        var frame = new byte[Length];
        frame[0] = commandSpecifier;
        return frame;
    }

    internal static byte[] SwitchGlobal(LssState state)
    {
        var frame = Command(LssCommand.SwitchStateGlobal);
        frame[1] = (byte)state;
        return frame;
    }

    internal static byte[] WithU32(byte commandSpecifier, uint value)
    {
        var frame = Command(commandSpecifier);
        WriteU32(frame, 1, value);
        return frame;
    }

    internal static byte[] ConfigureNodeId(byte nodeId)
    {
        var frame = Command(LssCommand.ConfigureNodeId);
        frame[1] = nodeId;
        return frame;
    }

    internal static byte[] ConfigureBitTiming(byte tableSelector, byte tableIndex)
    {
        var frame = Command(LssCommand.ConfigureBitTiming);
        frame[1] = tableSelector;
        frame[2] = tableIndex;
        return frame;
    }

    internal static byte[] ActivateBitTiming(ushort switchDelayMilliseconds)
    {
        var frame = Command(LssCommand.ActivateBitTiming);
        frame[1] = (byte)switchDelayMilliseconds;
        frame[2] = (byte)(switchDelayMilliseconds >> 8);
        return frame;
    }

    internal static byte[] Confirmed(byte commandSpecifier, byte errorCode, byte specError)
    {
        var frame = Command(commandSpecifier);
        frame[1] = errorCode;
        frame[2] = specError;
        return frame;
    }

    internal static byte[] InquireNodeId(byte nodeId)
    {
        var frame = Command(LssCommand.InquireNodeId);
        frame[1] = nodeId;
        return frame;
    }

    internal static byte[] Fastscan(uint idNumber, byte bitCheck, byte lssSub, byte lssNext)
    {
        var frame = Command(LssCommand.Fastscan);
        WriteU32(frame, 1, idNumber);
        frame[5] = bitCheck;
        frame[6] = lssSub;
        frame[7] = lssNext;
        return frame;
    }

    internal static byte[] Manufacturer(byte commandSpecifier, ReadOnlySpan<byte> parameters)
    {
        if (!LssCommand.IsManufacturerSpecific(commandSpecifier))
            throw new ArgumentOutOfRangeException(nameof(commandSpecifier), commandSpecifier,
                "Manufacturer-specific command specifiers are 128..255.");
        if (parameters.Length > 7)
            throw new ArgumentException("An LSS frame has at most 7 parameter bytes.", nameof(parameters));
        var frame = Command(commandSpecifier);
        parameters.CopyTo(frame.AsSpan(1));
        return frame;
    }

    internal static uint ReadU32(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(1));

    internal static ushort ReadSwitchDelay(ReadOnlySpan<byte> frame)
        => (ushort)(frame[1] | (frame[2] << 8));

    internal static void WriteU32(byte[] frame, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), value);
}
