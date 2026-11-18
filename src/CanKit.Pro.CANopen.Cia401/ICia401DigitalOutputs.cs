using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The digital outputs of a foreign CiA 401 device, 8-bit access (CiA 401-1 v3.1.0 §8.3.1 to §8.3.5),
/// read and written over SDO by <see cref="Cia401Peer"/>. A group is a sub-index: group <c>g</c>
/// carries outputs <c>8g − 7</c> to <c>8g</c> (CiA 401-1 v3.1.0 §7.2.1.3). Groups and exceptions are as for
/// <see cref="ICia401DigitalInputs"/>.
/// </summary>
internal interface ICia401DigitalOutputs
{
    /// <summary>Sub-index <c>00h</c> of <c>6200h</c>: the number of groups of eight outputs.</summary>
    Task<int> ReadGroupCountAsync(CancellationToken cancellationToken = default);

    /// <summary><c>6200h</c>: the outputs of <paramref name="group"/> as last written (CiA 401-1 v3.1.0 §8.3.1).</summary>
    Task<byte> ReadAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6200h</c>: sets the outputs of <paramref name="group"/> (CiA 401-1 v3.1.0 §8.3.1).</summary>
    Task WriteAsync(int group, byte levels, CancellationToken cancellationToken = default);

    /// <summary><c>6202h</c>: which outputs of <paramref name="group"/> are inverted (CiA 401-1 v3.1.0 §8.3.2).</summary>
    Task<byte> ReadPolarityAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6202h</c> (CiA 401-1 v3.1.0 §8.3.2).</summary>
    Task WritePolarityAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6206h</c>: which outputs of <paramref name="group"/> take their error value in the
    /// error state (CiA 401-1 v3.1.0 §8.3.3).</summary>
    Task<byte> ReadErrorModeAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6206h</c> (CiA 401-1 v3.1.0 §8.3.3).</summary>
    Task WriteErrorModeAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6207h</c>: the error values of the outputs of <paramref name="group"/> (CiA 401-1 v3.1.0 §8.3.4).</summary>
    Task<byte> ReadErrorValueAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6207h</c> (CiA 401-1 v3.1.0 §8.3.4).</summary>
    Task WriteErrorValueAsync(int group, byte levels, CancellationToken cancellationToken = default);

    /// <summary><c>6208h</c>: which outputs of <paramref name="group"/> follow a write (CiA 401-1 v3.1.0 §8.3.5).</summary>
    Task<byte> ReadFilterMaskAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6208h</c> (CiA 401-1 v3.1.0 §8.3.5).</summary>
    Task WriteFilterMaskAsync(int group, byte mask, CancellationToken cancellationToken = default);
}
