using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Lss;

/// <summary>
/// Persistent node-id and bit timing of an LSS slave (CiA 305 §8.2.2). The object dictionary's
/// <c>1010h</c> store does not touch these values, and a successful LSS store does not touch the
/// object dictionary (§8.6).
/// </summary>
public interface ILssConfigurationStore
{
    /// <summary>
    /// Reads the last stored node-id and bit timing. Returns <see langword="false"/> when nothing
    /// has been stored; the node then keeps the node-id it was opened with and table 0 index 2
    /// (500 kbit/s) as its in-memory default.
    /// </summary>
    bool TryLoad(out byte nodeId, out byte tableSelector, out byte tableIndex);

    /// <summary>
    /// Writes the pending node-id and bit timing. Returns <see langword="false"/> when the write
    /// failed. <paramref name="specError"/> <c>0</c> is reported as a media error
    /// (<see cref="LssCommand.StoreMediaError"/>); any other value is reported as
    /// manufacturer-specific (<see cref="LssCommand.ManufacturerSpecific"/>).
    /// </summary>
    bool TryStore(byte nodeId, byte tableSelector, byte tableIndex, out byte specError);
}

/// <summary>
/// Layer setting services on a <see cref="CanOpenNode"/> (CiA 305). Absent options leave LSS off,
/// unless the device description says <c>LSS_Supported</c>. An explicit instance wins over the
/// file, including <see cref="Enabled"/> <see langword="false"/>.
/// </summary>
public sealed class LssSlaveOptions
{
    /// <summary>
    /// When <see langword="false"/>, the node does not answer <c>7E5h</c> and rejects an
    /// unconfigured node-id. Defaults to <see langword="true"/>: passing an instance turns LSS on.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When <see langword="false"/>, configure node-id is ignored and draws no response (§7.5.1
    /// is then not implemented). Defaults to <see langword="true"/>.
    /// </summary>
    public bool NodeIdConfigurationSupported { get; init; } = true;

    /// <summary>
    /// When <see langword="false"/>, configure bit timing and activate bit timing are ignored.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool BitTimingSupported { get; init; } = true;

    /// <summary>
    /// Where a store command persists the pending node-id and bit timing. <see langword="null"/>
    /// (the default) answers store with <see cref="LssCommand.StoreNotSupported"/>: the service
    /// is recognized, and there is no medium.
    /// </summary>
    public ILssConfigurationStore? Store { get; init; }

    /// <summary>
    /// Handles a manufacturer-specific command (specifier 128..255, §7.3) while the slave is in
    /// configuration. The memory is the seven parameter bytes. Returns the response frame (at
    /// most 8 bytes; shorter frames are padded with zeros) or <see langword="null"/> to stay silent.
    /// <see langword="null"/> (the default) stays silent for every such command. Unlike
    /// <see cref="BitRateSwitch"/>, the handler runs on the node's actor: while it blocks, every
    /// other CANopen service of the node waits, so it must return promptly.
    /// </summary>
    public Func<byte, ReadOnlyMemory<byte>, byte[]?>? Manufacturer { get; init; }

    /// <summary>
    /// Called when activate bit timing copies the pending timing into the active one, with the
    /// bit rate in bits per second when the table names one. Runs off the node's actor. The
    /// open channel is not reconfigured: adapters generally cannot change rate while they are
    /// open, so the callback reopens the channel or records the rate. <see langword="null"/>
    /// (the default) only updates the slave's active table entry. Transmission resumes only
    /// after both the second switch delay and this task complete successfully, including for
    /// a zero delay. While the node is active, a failure is reported on its background exception
    /// event and keeps transmission suppressed until a later successful activation. Switch
    /// callbacks are serialized.
    /// The token requests cancellation asynchronously when another activation supersedes the
    /// switch or the node is disposed. A replacement callback starts only after the previous
    /// callback finishes, with a fresh token. The callback
    /// must cooperate with cancellation or provide its own timeout; a task that ignores the
    /// token cannot be forcibly stopped, and does not block disposal. Pending callbacks do not
    /// start after cancellation. The node never resumes transmission merely because a hook is slow.
    /// </summary>
    public Func<int, CancellationToken, Task>? BitRateSwitch { get; init; }

    /// <summary>
    /// Bit rates of manufacturer tables <c>80h</c>..<c>FEh</c>, keyed by table selector and
    /// index. A configure command for a pair that is absent is answered as not supported.
    /// Table 0 is not looked up here.
    /// </summary>
    public IReadOnlyDictionary<(byte Selector, byte Index), int>? ManufacturerRates { get; init; }
}
