using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>The conditions <c>6421h</c> selects for one analog input (CiA 401-1 v3.1.0 §8.6.2).
/// Bits 5 to 7 are reserved; a device refuses them.</summary>
[Flags]
internal enum Cia401AnalogInterruptTrigger : byte
{
    /// <summary>No condition.</summary>
    None = 0,

    /// <summary>Bit 0: the input exceeds the upper limit (<c>6424h</c>).</summary>
    UpperLimit = 1,

    /// <summary>Bit 1: the input falls below the lower limit (<c>6425h</c>).</summary>
    LowerLimit = 2,

    /// <summary>Bit 2: the input changed by more than the delta (<c>6426h</c>).</summary>
    Delta = 4,

    /// <summary>Bit 3: the input fell by more than the negative delta (<c>6427h</c>).</summary>
    NegativeDelta = 8,

    /// <summary>Bit 4: the input rose by more than the positive delta (<c>6428h</c>).</summary>
    PositiveDelta = 16,
}

/// <summary>
/// The analog inputs of a foreign CiA 401 device, 16-bit access, with their interrupt set-up (CiA
/// 401-1 v3.1.0 §8.4.2, §8.6.2 to §8.6.9), read and written over SDO by <see cref="Cia401Peer"/>. A
/// channel is a sub-index, 1 to 254; an interrupt source bank is a sub-index of <c>6422h</c>, 1 to 8
/// (§8.6.3). Numbers outside those ranges are refused with <see cref="ArgumentOutOfRangeException"/>
/// before anything is sent; the exceptions of a transfer are those of
/// <see cref="ICia401DigitalInputs"/>.
/// </summary>
/// <remarks>The limits <c>6424h</c> and <c>6425h</c> are INTEGER32 objects that stand for the 16-bit
/// input left adjusted (§8.4.3, §8.6.5, §8.6.6): the raw methods read and write the INTEGER32 as it
/// is, and the <c>…LeftAdjustedAsync</c> methods write a 16-bit threshold <c>v</c> as
/// <c>v · 2^16</c>. They have their own names rather than overloads: an <see cref="int"/> argument
/// would otherwise pick the raw method silently.</remarks>
internal interface ICia401AnalogInputs
{
    /// <summary>Sub-index <c>00h</c> of <c>6401h</c>: the number of analog inputs.</summary>
    Task<int> ReadChannelCountAsync(CancellationToken cancellationToken = default);

    /// <summary><c>6401h</c>: the value of <paramref name="channel"/> (§8.4.2).</summary>
    Task<short> ReadAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary><c>6421h</c>: the conditions that trigger a transmission for <paramref name="channel"/>
    /// (§8.6.2).</summary>
    Task<Cia401AnalogInterruptTrigger> ReadInterruptTriggerAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6421h</c> (§8.6.2).</summary>
    Task WriteInterruptTriggerAsync(int channel, Cia401AnalogInterruptTrigger trigger, CancellationToken cancellationToken = default);

    /// <summary><c>6422h</c>: interrupt source bank <paramref name="bank"/>, one bit per input that
    /// triggered (§8.6.3). Read-only.</summary>
    Task<uint> ReadInterruptSourceAsync(int bank, CancellationToken cancellationToken = default);

    /// <summary><c>6423h</c>: whether the analog inputs may trigger a transmission at all (§8.6.4).</summary>
    Task<bool> ReadGlobalInterruptEnableAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6423h</c> (§8.6.4).</summary>
    Task WriteGlobalInterruptEnableAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary><c>6424h</c>: the upper limit of <paramref name="channel"/>, the raw INTEGER32 (§8.6.5).</summary>
    Task<int> ReadInterruptUpperLimitAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6424h</c> raw (§8.6.5).</summary>
    Task WriteInterruptUpperLimitAsync(int channel, int limit, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6424h</c> as the 16-bit threshold <paramref name="threshold"/> left adjusted
    /// (§8.4.3, §8.6.5).</summary>
    Task WriteInterruptUpperLimitLeftAdjustedAsync(int channel, short threshold, CancellationToken cancellationToken = default);

    /// <summary><c>6425h</c>: the lower limit of <paramref name="channel"/>, the raw INTEGER32 (§8.6.6).</summary>
    Task<int> ReadInterruptLowerLimitAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6425h</c> raw (§8.6.6).</summary>
    Task WriteInterruptLowerLimitAsync(int channel, int limit, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6425h</c> as the 16-bit threshold <paramref name="threshold"/> left adjusted
    /// (§8.4.3, §8.6.6).</summary>
    Task WriteInterruptLowerLimitLeftAdjustedAsync(int channel, short threshold, CancellationToken cancellationToken = default);

    /// <summary><c>6426h</c>: the delta of <paramref name="channel"/> (§8.6.7).</summary>
    Task<uint> ReadInterruptDeltaAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6426h</c> (§8.6.7).</summary>
    Task WriteInterruptDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default);

    /// <summary><c>6427h</c>: the negative delta of <paramref name="channel"/> (§8.6.8).</summary>
    Task<uint> ReadInterruptNegativeDeltaAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6427h</c> (§8.6.8).</summary>
    Task WriteInterruptNegativeDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default);

    /// <summary><c>6428h</c>: the positive delta of <paramref name="channel"/> (§8.6.9).</summary>
    Task<uint> ReadInterruptPositiveDeltaAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6428h</c> (§8.6.9).</summary>
    Task WriteInterruptPositiveDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default);
}
