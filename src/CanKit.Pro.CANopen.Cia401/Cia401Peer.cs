using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Application;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen.Cia401;

/// <summary>One entry of an observed PDO of a CiA 401 device, named.</summary>
/// <param name="Index">The object index as the mapping names it: absolute, in the logical device it
/// belongs to.</param>
/// <param name="Subindex">The sub-index as the mapping names it.</param>
/// <param name="Definition">The catalogue object, when the index is one of the profile in the logical
/// device of the peer that interpreted it; <see langword="null"/> otherwise.</param>
/// <param name="Description">What the entry carries (<see cref="Cia401Objects.Describe"/>), or the
/// index and sub-index in hexadecimal, <c>2000h:01h</c>, for an entry without a definition.</param>
/// <param name="Value">The mapped bits as an integer: sign-extended for a signed data type of the
/// catalogue, zero-extended for every other one and for an entry without a definition. A REAL32
/// object is its bit pattern, not the real.</param>
internal readonly record struct Cia401PdoValue(
    ushort Index,
    byte Subindex,
    Cia401ObjectDefinition? Definition,
    string Description,
    long Value);

/// <summary>What <see cref="Cia401Peer.ObserveAsync"/> found: the core's result for the COB-ID and
/// the entries it decoded, named. <see cref="Values"/> is empty when nothing was decoded, and
/// <see cref="Result"/> says why.</summary>
/// <param name="Result">The result of <see cref="ICanOpenNode.ObserveForeignPdoAsync"/>.</param>
/// <param name="Values">The decoded entries in mapping order (<see cref="Cia401Peer.Interpret"/>).</param>
internal readonly record struct Cia401Observation(ForeignPdoObserveResult Result, IReadOnlyList<Cia401PdoValue> Values);

/// <summary>
/// The tool side of CiA 401-1 v3.1.0: typed access to one logical device of a foreign CiA 401 device,
/// made by <see cref="Cia401Profile.CreatePeer"/>. It talks to nothing but <see cref="ICanOpenNode"/>:
/// every read and write is one <see cref="ICanOpenNode.SdoUploadAsync(byte, ushort, byte, SdoTransferMode, CancellationToken)"/>
/// or <see cref="ICanOpenNode.SdoDownloadAsync(byte, ushort, byte, ReadOnlyMemory{byte}, SdoTransferMode, CancellationToken)"/>,
/// so it passes the SDO client's peer gate: without a description of the device bound in the node
/// (<see cref="ICanOpenNode.BindPeerDeviceDescription"/>) the profile objects are refused with
/// <see cref="PeerSdoAccessException"/> before a frame is sent.
/// </summary>
/// <remarks>
/// <para><b>Blocks.</b> <see cref="DigitalInputs"/>, <see cref="DigitalOutputs"/>,
/// <see cref="AnalogInputs"/> and <see cref="AnalogOutputs"/> exist when bit 16, 17, 18 or 19 of the
/// device type of the logical device is set, and are <see langword="null"/> otherwise (CiA 401-1
/// v3.1.0 §6.2.1, Table 2). The M bit (bit 23, a manufacturer-specific PDO mapping, CiA 401-1 v3.1.0 Table 3) changes nothing
/// here: the peer reads and writes over SDO, and <see cref="Interpret"/> names the entries a PDO
/// really carried, whatever its mapping.</para>
/// <para><b>Indices.</b> Every index comes from <see cref="Cia401Objects"/> and is shifted for the
/// logical device of the peer by <see cref="CanOpenLogicalDeviceAddressing"/> (<c>n·800h</c>, CiA 301
/// v4.2.0 §7.4.1).</para>
/// <para><b>Values.</b> They are encoded and decoded little-endian by the data type of the catalogue
/// (<see cref="Cia401Codec"/>); a value of another length than its type is an
/// <see cref="InvalidDataException"/>, not a truncation.</para>
/// <para><b>Argument errors.</b> A null argument, a group, channel or bank out of range, and a write
/// to an entry the catalogue marks read-only are thrown by the call itself, before a task exists, on
/// every method of the peer and its blocks; nothing is sent then. Everything that happens on the bus
/// ends in the returned task.</para>
/// <para><b>State.</b> The peer holds the node, the peer's node-id and the logical device, and nothing
/// else: it caches no value, and the block objects are views of the peer. It can be used from any
/// thread; the node's SDO client allows one transfer per server at a time and refuses a second with
/// <see cref="InvalidOperationException"/>.</para>
/// </remarks>
internal sealed class Cia401Peer : ICanOpenProfilePeer
{
    private readonly ICanOpenNode _node;

    internal Cia401Peer(ICanOpenProfile profile, ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice)
    {
        Profile = profile;
        _node = node;
        PeerNodeId = peerNodeId;
        LogicalDevice = logicalDevice;

        uint type = logicalDevice.DeviceType.Value;
        DigitalInputs = HasBit(type, Cia401Configuration.DigitalInputsBit) ? new DigitalInputBlock(this) : null;
        DigitalOutputs = HasBit(type, Cia401Configuration.DigitalOutputsBit) ? new DigitalOutputBlock(this) : null;
        AnalogInputs = HasBit(type, Cia401Configuration.AnalogInputsBit) ? new AnalogInputBlock(this) : null;
        AnalogOutputs = HasBit(type, Cia401Configuration.AnalogOutputsBit) ? new AnalogOutputBlock(this) : null;
    }

    /// <inheritdoc />
    public ICanOpenProfile Profile { get; }

    /// <inheritdoc />
    public byte PeerNodeId { get; }

    /// <inheritdoc />
    public CanOpenLogicalDevice LogicalDevice { get; }

    /// <summary>The digital inputs, when bit 16 of the device type is set (CiA 401-1 v3.1.0 §6.2.1).</summary>
    public ICia401DigitalInputs? DigitalInputs { get; }

    /// <summary>The digital outputs, when bit 17 of the device type is set (CiA 401-1 v3.1.0 §6.2.1).</summary>
    public ICia401DigitalOutputs? DigitalOutputs { get; }

    /// <summary>The analog inputs, when bit 18 of the device type is set (CiA 401-1 v3.1.0 §6.2.1).</summary>
    public ICia401AnalogInputs? AnalogInputs { get; }

    /// <summary>The analog outputs, when bit 19 of the device type is set (CiA 401-1 v3.1.0 §6.2.1).</summary>
    public ICia401AnalogOutputs? AnalogOutputs { get; }

    /// <summary>Uploads sub-index <paramref name="subindex"/> of any catalogue object, in the logical
    /// device of the peer, and returns its bytes as the device sent them. The device need not implement
    /// the object: the gate and the device decide.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the upload.</exception>
    public Task<byte[]> ReadAsync(Cia401ObjectDefinition definition, byte subindex, CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        return _node.SdoUploadAsync(PeerNodeId, Shift(definition), subindex, SdoTransferMode.Auto, cancellationToken);
    }

    /// <summary>Downloads <paramref name="value"/> to sub-index <paramref name="subindex"/> of any
    /// catalogue object, in the logical device of the peer, as given.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or
    /// <paramref name="value"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The catalogue marks the entry read-only — the object
    /// is <c>ro</c>, or the entry is sub-index <c>00h</c> of an array, which is const (CiA 301 v4.2.0
    /// §7.4.5) — and nothing was sent.</exception>
    /// <exception cref="PeerSdoAccessException">The bound description does not allow the entry.</exception>
    /// <exception cref="SdoAbortException">The device aborted the download.</exception>
    public Task WriteAsync(Cia401ObjectDefinition definition, byte subindex, byte[] value, CancellationToken cancellationToken = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (value is null) throw new ArgumentNullException(nameof(value));
        if ((definition.Access & OdAccess.WriteOnly) == 0 || (definition.IsArray && subindex == 0))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "{0} is read-only: it is not written.", Cia401Objects.Describe(definition.Index, subindex)));
        }

        return _node.SdoDownloadAsync(PeerNodeId, Shift(definition), subindex, value, SdoTransferMode.Auto, cancellationToken);
    }

    /// <summary>
    /// Names the entries of an observed PDO of this peer, in the order given: the order in which
    /// <see cref="ICanOpenNode.ObserveForeignPdoAsync"/> delivered them to its sink, which is the
    /// order of the mapping the PDO really had (the device's live mapping before the description's).
    /// Pure: no bus access.
    /// </summary>
    /// <remarks>
    /// <para>An entry whose index, shifted back by the logical device of this peer, is a catalogue
    /// object gets its definition, <see cref="Cia401Objects.Describe"/> for its description and its
    /// value signed or unsigned by the catalogue's data type, in the mapped width. Every other entry —
    /// an object outside the profile, a reserved index, an object of another logical device — is kept
    /// with no definition, its index and sub-index in hexadecimal and its value unsigned in the mapped
    /// width.</para>
    /// <para>A mapped REAL32 entry (<c>6403h</c>, <c>6413h</c> and their kin) is returned as its bit
    /// pattern, unsigned in the mapped width: <see cref="Cia401PdoValue.Value"/> is an integer, and the
    /// real is the caller's to make of it.</para>
    /// <para>Dummy entries (<c>0001h</c> to <c>0007h</c>, CiA 301 v4.2.0 §7.4.7.1) occupy their bits in
    /// the PDO, but the core delivers no signal for them, so they never reach this method and have no
    /// value here.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="signals"/> is null.</exception>
    /// <exception cref="ArgumentException">A signal comes from another node than this peer.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A signal has a bit length outside 1 to 64.</exception>
    /// <exception cref="InvalidDataException">A signal has fewer bytes than its bit length needs.</exception>
    public Cia401PdoValue[] Interpret(IReadOnlyList<ForeignPdoSignal> signals)
    {
        if (signals is null) throw new ArgumentNullException(nameof(signals));

        var values = new Cia401PdoValue[signals.Count];
        for (int i = 0; i < values.Length; i++)
        {
            var signal = signals[i];
            if (signal.PeerNodeId != PeerNodeId)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "Signal {0} comes from node 0x{1:X2}, not from this peer, node 0x{2:X2}.", i, signal.PeerNodeId, PeerNodeId),
                    nameof(signals));
            }

            values[i] = TryFindDefinition(signal.Index, out var definition)
                ? new Cia401PdoValue(signal.Index, signal.SubIndex, definition,
                    Cia401Objects.Describe(definition.Index, signal.SubIndex),
                    Cia401Codec.DecodeBits(signal.Value, signal.BitLength, Cia401Codec.IsSigned(definition.DataType)))
                : new Cia401PdoValue(signal.Index, signal.SubIndex, null,
                    string.Format(CultureInfo.InvariantCulture, "{0:X4}h:{1:X2}h", signal.Index, signal.SubIndex),
                    Cia401Codec.DecodeBits(signal.Value, signal.BitLength, signed: false));
        }

        return values;
    }

    /// <summary>
    /// Observes one PDO payload of this peer with <see cref="ICanOpenNode.ObserveForeignPdoAsync"/> —
    /// which reads the device's COB-IDs and mapping live over SDO, through the gate, and takes
    /// <paramref name="peerDescription"/> where a live read fails — and names what it carried with
    /// <see cref="Interpret"/>. When the core decodes nothing, <see cref="Cia401Observation.Values"/>
    /// is empty and <see cref="Cia401Observation.Result"/> says why; that is not an exception.
    /// </summary>
    /// <param name="cobId">The observed ID, with bit 29 set for an extended frame.</param>
    /// <param name="payload">The PDO data, 0 to 8 bytes.</param>
    /// <param name="peerDescription">EDS or DCF of the peer, as the core method takes it.</param>
    /// <param name="cancellationToken">Cancels an in-flight mapping upload.</param>
    /// <exception cref="ArgumentNullException"><paramref name="peerDescription"/> is null.</exception>
    /// <remarks>What the core delivered to the peer's sink before it threw — a cancellation during
    /// the live reads, a transport failure — is discarded: the exception propagates and nothing is
    /// returned.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">As for the core method, in the returned task:
    /// <paramref name="cobId"/> is not a valid PDO key, or <paramref name="payload"/> is longer than
    /// 8 bytes.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was
    /// cancelled, in the returned task, as the core reports it.</exception>
    public Task<Cia401Observation> ObserveAsync(uint cobId, ReadOnlyMemory<byte> payload,
        CanOpenDeviceDescription peerDescription, CancellationToken cancellationToken = default)
    {
        if (peerDescription is null) throw new ArgumentNullException(nameof(peerDescription));
        return ObserveCoreAsync(cobId, payload, peerDescription, cancellationToken);
    }

    private async Task<Cia401Observation> ObserveCoreAsync(uint cobId, ReadOnlyMemory<byte> payload,
        CanOpenDeviceDescription peerDescription, CancellationToken cancellationToken)
    {
        var sink = new ListSink();
        var result = await _node.ObserveForeignPdoAsync(PeerNodeId, cobId, payload, peerDescription, sink, cancellationToken)
            .ConfigureAwait(false);
        return new Cia401Observation(result, Interpret(sink.Signals));
    }

    // ---- shared by the blocks ---------------------------------------------------------------------

    private static bool HasBit(uint value, uint bit) => (value & bit) != 0;

    private ushort Shift(Cia401ObjectDefinition definition)
        => CanOpenLogicalDeviceAddressing.Index(LogicalDevice.Index, definition.Index);

    /// <summary>The catalogue object whose index, in the logical device of this peer, is
    /// <paramref name="absoluteIndex"/>.</summary>
    private bool TryFindDefinition(ushort absoluteIndex, out Cia401ObjectDefinition definition)
    {
        foreach (var candidate in Cia401Objects.All)
        {
            if (Shift(candidate) == absoluteIndex)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    /// <summary>The sub-index of element <paramref name="number"/> of an array: 1 to the highest
    /// sub-index the norm allows for it.</summary>
    private static byte Element(Cia401ObjectDefinition definition, int number, string name)
    {
        if (number < 1 || number > definition.MaxSubindex)
        {
            throw new ArgumentOutOfRangeException(name, number, string.Format(CultureInfo.InvariantCulture,
                "{0} of {1} lies in 1 to {2}.", name, definition, definition.MaxSubindex));
        }

        return (byte)number;
    }

    private async Task<long> ReadValueAsync(Cia401ObjectDefinition definition, byte subindex, CancellationToken cancellationToken)
        => Cia401Codec.Decode(definition.DataType, await ReadAsync(definition, subindex, cancellationToken).ConfigureAwait(false));

    /// <summary>Element <paramref name="number"/> of an array, converted. The number is checked here,
    /// before a task exists; the transfer and the decoding follow in the task.</summary>
    private Task<T> ReadElementAsync<T>(Cia401ObjectDefinition definition, int number, string name, Func<long, T> convert,
        CancellationToken cancellationToken)
    {
        byte subindex = Element(definition, number, name);
        return ConvertAsync(ReadValueAsync(definition, subindex, cancellationToken), convert);
    }

    private static async Task<T> ConvertAsync<T>(Task<long> value, Func<long, T> convert)
        => convert(await value.ConfigureAwait(false));

    private Task WriteValueAsync(Cia401ObjectDefinition definition, byte subindex, long value, CancellationToken cancellationToken)
        => WriteAsync(definition, subindex, Cia401Codec.Encode(definition.DataType, value), cancellationToken);

    private Task WriteElementAsync(Cia401ObjectDefinition definition, int number, string name, long value, CancellationToken cancellationToken)
        => WriteValueAsync(definition, Element(definition, number, name), value, cancellationToken);

    /// <summary>Sub-index <c>00h</c> of an array, the UNSIGNED8 "highest sub-index supported" (CiA 301
    /// v4.2.0 §7.4.5): how many elements the device has.</summary>
    private async Task<int> ReadCountAsync(Cia401ObjectDefinition definition, CancellationToken cancellationToken)
        => (int)Cia401Codec.Decode(OdDataType.Unsigned8, await ReadAsync(definition, 0, cancellationToken).ConfigureAwait(false));

    /// <summary>A value that is TRUE (1) or FALSE (0) and nothing else: a BOOLEAN (CiA 301 v4.2.0
    /// §7.1.4.3), or an UNSIGNED8 whose other values are reserved.</summary>
    private async Task<bool> ReadFlagAsync(Cia401ObjectDefinition definition, byte subindex, CancellationToken cancellationToken)
    {
        long value = await ReadValueAsync(definition, subindex, cancellationToken).ConfigureAwait(false);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                "{0} is {1}: only 0 and 1 are defined.", Cia401Objects.Describe(definition.Index, subindex), value)),
        };
    }

    private sealed class ListSink : IForeignPdoSink
    {
        internal List<ForeignPdoSignal> Signals { get; } = new();

        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }

    // ---- the blocks ---------------------------------------------------------------------------------

    private sealed class DigitalInputBlock : ICia401DigitalInputs
    {
        private const string Group = "group";
        private readonly Cia401Peer _peer;

        internal DigitalInputBlock(Cia401Peer peer) => _peer = peer;

        public Task<int> ReadGroupCountAsync(CancellationToken cancellationToken = default)
            => _peer.ReadCountAsync(Cia401Objects.ReadDigitalInput8, cancellationToken);

        public Task<byte> ReadAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.ReadDigitalInput8, group, Group, static value => (byte)value, cancellationToken);

        public Task<byte> ReadPolarityAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.PolarityDigitalInput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WritePolarityAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.PolarityDigitalInput8, group, Group, mask, cancellationToken);

        public Task<byte> ReadFilterEnableAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.DigitalFilterEnable8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteFilterEnableAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.DigitalFilterEnable8, group, Group, mask, cancellationToken);

        public Task<bool> ReadGlobalInterruptEnableAsync(CancellationToken cancellationToken = default)
            => _peer.ReadFlagAsync(Cia401Objects.GlobalInterruptEnableDigital8, 0, cancellationToken);

        public Task WriteGlobalInterruptEnableAsync(bool enabled, CancellationToken cancellationToken = default)
            => _peer.WriteValueAsync(Cia401Objects.GlobalInterruptEnableDigital8, 0, enabled ? 1 : 0, cancellationToken);

        public Task<byte> ReadInterruptMaskAnyChangeAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.DigitalInterruptMaskAnyChange8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteInterruptMaskAnyChangeAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.DigitalInterruptMaskAnyChange8, group, Group, mask, cancellationToken);

        public Task<byte> ReadInterruptMaskLowToHighAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.DigitalInterruptMaskLowToHigh8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteInterruptMaskLowToHighAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.DigitalInterruptMaskLowToHigh8, group, Group, mask, cancellationToken);

        public Task<byte> ReadInterruptMaskHighToLowAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.DigitalInterruptMaskHighToLow8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteInterruptMaskHighToLowAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.DigitalInterruptMaskHighToLow8, group, Group, mask, cancellationToken);
    }

    private sealed class DigitalOutputBlock : ICia401DigitalOutputs
    {
        private const string Group = "group";
        private readonly Cia401Peer _peer;

        internal DigitalOutputBlock(Cia401Peer peer) => _peer = peer;

        public Task<int> ReadGroupCountAsync(CancellationToken cancellationToken = default)
            => _peer.ReadCountAsync(Cia401Objects.WriteDigitalOutput8, cancellationToken);

        public Task<byte> ReadAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.WriteDigitalOutput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteAsync(int group, byte levels, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.WriteDigitalOutput8, group, Group, levels, cancellationToken);

        public Task<byte> ReadPolarityAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.ChangePolarityDigitalOutput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WritePolarityAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.ChangePolarityDigitalOutput8, group, Group, mask, cancellationToken);

        public Task<byte> ReadErrorModeAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.ErrorModeDigitalOutput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteErrorModeAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.ErrorModeDigitalOutput8, group, Group, mask, cancellationToken);

        public Task<byte> ReadErrorValueAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.ErrorValueDigitalOutput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteErrorValueAsync(int group, byte levels, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.ErrorValueDigitalOutput8, group, Group, levels, cancellationToken);

        public Task<byte> ReadFilterMaskAsync(int group, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.FilterMaskDigitalOutput8, group, Group, static value => (byte)value, cancellationToken);

        public Task WriteFilterMaskAsync(int group, byte mask, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.FilterMaskDigitalOutput8, group, Group, mask, cancellationToken);
    }

    private sealed class AnalogInputBlock : ICia401AnalogInputs
    {
        private const string Channel = "channel";
        private const string Bank = "bank";
        private readonly Cia401Peer _peer;

        internal AnalogInputBlock(Cia401Peer peer) => _peer = peer;

        public Task<int> ReadChannelCountAsync(CancellationToken cancellationToken = default)
            => _peer.ReadCountAsync(Cia401Objects.ReadAnalogInput16, cancellationToken);

        public Task<short> ReadAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.ReadAnalogInput16, channel, Channel, static value => (short)value, cancellationToken);

        public Task<Cia401AnalogInterruptTrigger> ReadInterruptTriggerAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptTriggerSelection, channel, Channel, static value => (Cia401AnalogInterruptTrigger)value, cancellationToken);

        public Task WriteInterruptTriggerAsync(int channel, Cia401AnalogInterruptTrigger trigger, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptTriggerSelection, channel, Channel, (byte)trigger, cancellationToken);

        public Task<uint> ReadInterruptSourceAsync(int bank, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptSource, bank, Bank, static value => (uint)value, cancellationToken);

        public Task<bool> ReadGlobalInterruptEnableAsync(CancellationToken cancellationToken = default)
            => _peer.ReadFlagAsync(Cia401Objects.AnalogInputGlobalInterruptEnable, 0, cancellationToken);

        public Task WriteGlobalInterruptEnableAsync(bool enabled, CancellationToken cancellationToken = default)
            => _peer.WriteValueAsync(Cia401Objects.AnalogInputGlobalInterruptEnable, 0, enabled ? 1 : 0, cancellationToken);

        public Task<int> ReadInterruptUpperLimitAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptUpperLimitInteger, channel, Channel, static value => (int)value, cancellationToken);

        public Task WriteInterruptUpperLimitAsync(int channel, int limit, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptUpperLimitInteger, channel, Channel, limit, cancellationToken);

        public Task WriteInterruptUpperLimitLeftAdjustedAsync(int channel, short threshold, CancellationToken cancellationToken = default)
            => WriteInterruptUpperLimitAsync(channel, Cia401LeftAdjusted.Widen(threshold), cancellationToken);

        public Task<int> ReadInterruptLowerLimitAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptLowerLimitInteger, channel, Channel, static value => (int)value, cancellationToken);

        public Task WriteInterruptLowerLimitAsync(int channel, int limit, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptLowerLimitInteger, channel, Channel, limit, cancellationToken);

        public Task WriteInterruptLowerLimitLeftAdjustedAsync(int channel, short threshold, CancellationToken cancellationToken = default)
            => WriteInterruptLowerLimitAsync(channel, Cia401LeftAdjusted.Widen(threshold), cancellationToken);

        public Task<uint> ReadInterruptDeltaAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptDeltaUnsigned, channel, Channel, static value => (uint)value, cancellationToken);

        public Task WriteInterruptDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptDeltaUnsigned, channel, Channel, delta, cancellationToken);

        public Task<uint> ReadInterruptNegativeDeltaAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptNegativeDeltaUnsigned, channel, Channel, static value => (uint)value, cancellationToken);

        public Task WriteInterruptNegativeDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptNegativeDeltaUnsigned, channel, Channel, delta, cancellationToken);

        public Task<uint> ReadInterruptPositiveDeltaAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogInputInterruptPositiveDeltaUnsigned, channel, Channel, static value => (uint)value, cancellationToken);

        public Task WriteInterruptPositiveDeltaAsync(int channel, uint delta, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogInputInterruptPositiveDeltaUnsigned, channel, Channel, delta, cancellationToken);
    }

    private sealed class AnalogOutputBlock : ICia401AnalogOutputs
    {
        private const string Channel = "channel";
        private readonly Cia401Peer _peer;

        internal AnalogOutputBlock(Cia401Peer peer) => _peer = peer;

        public Task<int> ReadChannelCountAsync(CancellationToken cancellationToken = default)
            => _peer.ReadCountAsync(Cia401Objects.WriteAnalogOutput16, cancellationToken);

        public Task<short> ReadAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.WriteAnalogOutput16, channel, Channel, static value => (short)value, cancellationToken);

        public Task WriteAsync(int channel, short value, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.WriteAnalogOutput16, channel, Channel, value, cancellationToken);

        public Task<bool> ReadErrorModeAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadFlagAsync(Cia401Objects.AnalogOutputErrorMode,
                Element(Cia401Objects.AnalogOutputErrorMode, channel, Channel), cancellationToken);

        public Task WriteErrorModeAsync(int channel, bool useErrorValue, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogOutputErrorMode, channel, Channel, useErrorValue ? 1 : 0, cancellationToken);

        public Task<int> ReadErrorValueAsync(int channel, CancellationToken cancellationToken = default)
            => _peer.ReadElementAsync(Cia401Objects.AnalogOutputErrorValueInteger, channel, Channel, static value => (int)value, cancellationToken);

        public Task WriteErrorValueAsync(int channel, int value, CancellationToken cancellationToken = default)
            => _peer.WriteElementAsync(Cia401Objects.AnalogOutputErrorValueInteger, channel, Channel, value, cancellationToken);

        public Task WriteErrorValueLeftAdjustedAsync(int channel, short value, CancellationToken cancellationToken = default)
            => WriteErrorValueAsync(channel, Cia401LeftAdjusted.Widen(value), cancellationToken);
    }
}
