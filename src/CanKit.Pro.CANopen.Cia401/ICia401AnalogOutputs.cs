using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The analog outputs of a foreign CiA 401 device, 16-bit access, with error mode and error value (CiA
/// 401-1 v3.1.0 §8.5.2, §8.7.4, §8.7.5), read and written over SDO by <see cref="Cia401Peer"/>. A
/// channel is a sub-index, 1 to 254; channels and exceptions are as for
/// <see cref="ICia401AnalogInputs"/>.
/// </summary>
/// <remarks>The error value <c>6444h</c> is an INTEGER32 that stands for the 16-bit output left
/// adjusted (Figure 7's legend): <see cref="WriteErrorValueAsync"/> writes the INTEGER32 raw,
/// <see cref="WriteErrorValueLeftAdjustedAsync"/> a 16-bit value <c>v</c> as <c>v · 2^16</c>.</remarks>
internal interface ICia401AnalogOutputs
{
    /// <summary>Sub-index <c>00h</c> of <c>6411h</c>: the number of analog outputs.</summary>
    Task<int> ReadChannelCountAsync(CancellationToken cancellationToken = default);

    /// <summary><c>6411h</c>: the value of <paramref name="channel"/> as last written (§8.5.2).</summary>
    Task<short> ReadAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6411h</c>: sets <paramref name="channel"/> (§8.5.2).</summary>
    Task WriteAsync(int channel, short value, CancellationToken cancellationToken = default);

    /// <summary><c>6443h</c>: whether <paramref name="channel"/> takes its error value in the error
    /// state (§8.7.4: 1h does, 0h keeps the last value).</summary>
    /// <exception cref="System.IO.InvalidDataException">The device answers with a reserved value,
    /// neither 0h nor 1h.</exception>
    Task<bool> ReadErrorModeAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6443h</c> as 1h or 0h (§8.7.4).</summary>
    Task WriteErrorModeAsync(int channel, bool useErrorValue, CancellationToken cancellationToken = default);

    /// <summary><c>6444h</c>: the error value of <paramref name="channel"/>, the raw INTEGER32 (§8.7.5).</summary>
    Task<int> ReadErrorValueAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6444h</c> raw (§8.7.5).</summary>
    Task WriteErrorValueAsync(int channel, int value, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6444h</c> as the 16-bit value <paramref name="value"/> left adjusted (§8.7.5,
    /// Figure 7).</summary>
    Task WriteErrorValueLeftAdjustedAsync(int channel, short value, CancellationToken cancellationToken = default);
}
