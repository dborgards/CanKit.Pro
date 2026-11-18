using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>
/// The digital inputs of a foreign CiA 401 device, 8-bit access (CiA 401-1 v3.1.0 §8.2.1 to §8.2.7),
/// read and written over SDO by <see cref="Cia401Peer"/>. A group is a sub-index: group <c>g</c>
/// carries inputs <c>8g − 7</c> to <c>8g</c> (§7.2.1.3).
/// </summary>
/// <remarks>
/// <para>A group lies in 1 to 254 (sub-indices <c>01h</c> to <c>FEh</c>, §8.2.1); a number outside
/// that range is refused with <see cref="ArgumentOutOfRangeException"/> before anything is sent. How
/// many groups the device has is the device's business (<see cref="ReadGroupCountAsync"/>): a group
/// it does not have is refused by the SDO client's peer gate, when the bound description does not
/// declare it, or aborted by the device.</para>
/// <para>Every call is one SDO transfer through the node's SDO client, with its exceptions: a
/// <c>PeerSdoAccessException</c> when the bound description does not allow the entry, an
/// <c>SdoAbortException</c> when the device aborts, and an <see cref="System.IO.InvalidDataException"/>
/// when the device answers with a value of another length than the data type's.</para>
/// </remarks>
internal interface ICia401DigitalInputs
{
    /// <summary>Sub-index <c>00h</c> of <c>6000h</c>: the number of groups of eight inputs.</summary>
    Task<int> ReadGroupCountAsync(CancellationToken cancellationToken = default);

    /// <summary><c>6000h</c>: the inputs of <paramref name="group"/>, after the polarity (§8.2.1).</summary>
    Task<byte> ReadAsync(int group, CancellationToken cancellationToken = default);

    /// <summary><c>6002h</c>: which inputs of <paramref name="group"/> are inverted (§8.2.2).</summary>
    Task<byte> ReadPolarityAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6002h</c> (§8.2.2).</summary>
    Task WritePolarityAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6003h</c>: which inputs of <paramref name="group"/> are filtered (§8.2.3).</summary>
    Task<byte> ReadFilterEnableAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6003h</c> (§8.2.3).</summary>
    Task WriteFilterEnableAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6005h</c>: whether the inputs may trigger a transmission at all (§8.2.4).</summary>
    Task<bool> ReadGlobalInterruptEnableAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6005h</c> (§8.2.4).</summary>
    Task WriteGlobalInterruptEnableAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary><c>6006h</c>: the inputs of <paramref name="group"/> whose any change triggers a
    /// transmission (§8.2.5).</summary>
    Task<byte> ReadInterruptMaskAnyChangeAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6006h</c> (§8.2.5).</summary>
    Task WriteInterruptMaskAnyChangeAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6007h</c>: the inputs of <paramref name="group"/> whose rising edge triggers a
    /// transmission (§8.2.6).</summary>
    Task<byte> ReadInterruptMaskLowToHighAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6007h</c> (§8.2.6).</summary>
    Task WriteInterruptMaskLowToHighAsync(int group, byte mask, CancellationToken cancellationToken = default);

    /// <summary><c>6008h</c>: the inputs of <paramref name="group"/> whose falling edge triggers a
    /// transmission (§8.2.7).</summary>
    Task<byte> ReadInterruptMaskHighToLowAsync(int group, CancellationToken cancellationToken = default);

    /// <summary>Writes <c>6008h</c> (§8.2.7).</summary>
    Task WriteInterruptMaskHighToLowAsync(int group, byte mask, CancellationToken cancellationToken = default);
}
