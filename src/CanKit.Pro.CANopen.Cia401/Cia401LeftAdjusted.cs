namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The one reading of "left adjusted" for the INTEGER32 objects that stand for a 16-bit analog value
/// (CiA 401-1 §8.4.2, §8.4.3, §8.6.5, §8.6.6, and Figure 7's legend for <c>6444h</c>): the 16-bit
/// value <c>v</c> is the INTEGER32 <c>v · 2^16</c>, its low 16 bits zero, as §8.4.3 defines a
/// left-adjusted 32-bit input. Used by the device and the peer alike.
/// </summary>
/// <remarks>What the low 16 bits of an INTEGER32 do depends on the use. An analog input limit is
/// compared with the widened input as an INTEGER32, so its low bits take part
/// (<see cref="Cia401Device.SetAnalogInput"/>). A value that has to become a 16-bit output
/// (<c>6444h</c>, the analog output error value; <see cref="Cia401Device.ReadAnalogOutput"/>) is
/// narrowed to its upper word, and the low bits, which a 16-bit value cannot carry, are dropped.
/// §8.7.5 does not call <c>6444h</c> left adjusted; Figure 7 does, and the figure is
/// followed.</remarks>
internal static class Cia401LeftAdjusted
{
    private const int Shift = 16;

    /// <summary><paramref name="value"/> · 2^16: the 16-bit value left adjusted in 32 bits.</summary>
    internal static int Widen(short value) => value << Shift;

    /// <summary>The upper word of <paramref name="value"/>: the 16-bit value a left-adjusted
    /// INTEGER32 stands for, its low 16 bits dropped.</summary>
    internal static short Narrow(int value) => (short)(value >> Shift);
}
