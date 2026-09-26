using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen;

/// <summary>Whether a decoded foreign PDO is one the peer transmits or one the peer receives.</summary>
public enum ForeignPdoKind
{
    /// <summary>The peer's TPDO. Its mapping record is <c>1A00h</c> + <c>n</c> − 1.</summary>
    Tpdo = 0,

    /// <summary>The peer's RPDO. Its mapping record is <c>1600h</c> + <c>n</c> − 1.</summary>
    Rpdo = 1,
}

/// <summary>Where the mapping that split a foreign PDO came from.</summary>
public enum ForeignPdoMappingOrigin
{
    /// <summary>The peer's live mapping record, read over SDO.</summary>
    LiveMapping = 0,

    /// <summary>The mapping declared in the peer EDS or DCF. Used when the live record could not be read.</summary>
    DeviceDescription = 1,
}

/// <summary>
/// One mapped object taken out of a foreign PDO payload. The bytes are the slice the mapping
/// assigns to <see cref="Index"/>:<see cref="SubIndex"/>, little-endian, the same order a PDO
/// carries. A dummy mapping (<c>0002h</c>–<c>0007h</c>) occupies bytes in the payload and is
/// not delivered.
/// </summary>
public readonly struct ForeignPdoSignal
{
    /// <summary>Constructs a signal. <paramref name="value"/> is kept as given; the observe path
    /// copies the slice out of the payload before it calls the sink.</summary>
    public ForeignPdoSignal(byte peerNodeId, ForeignPdoKind kind, int pdoNumber, uint cobId,
        ushort index, byte subIndex, byte[] value, ForeignPdoMappingOrigin origin)
    {
        PeerNodeId = peerNodeId;
        Kind = kind;
        PdoNumber = pdoNumber;
        CobId = cobId;
        Index = index;
        SubIndex = subIndex;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Origin = origin;
    }

    /// <summary>Node-id of the peer whose PDO this signal came from.</summary>
    public byte PeerNodeId { get; }

    /// <summary>Whether the PDO is a TPDO or an RPDO of <see cref="PeerNodeId"/>.</summary>
    public ForeignPdoKind Kind { get; }

    /// <summary>PDO number, 1..4.</summary>
    public int PdoNumber { get; }

    /// <summary>11-bit COB-ID the payload was observed on.</summary>
    public uint CobId { get; }

    /// <summary>Object index the mapping entry names.</summary>
    public ushort Index { get; }

    /// <summary>Object sub-index the mapping entry names.</summary>
    public byte SubIndex { get; }

    /// <summary>The mapped bytes, little-endian.</summary>
    public byte[] Value { get; }

    /// <summary>Whether <see cref="Value"/> was split with the live mapping record or the peer file.</summary>
    public ForeignPdoMappingOrigin Origin { get; }
}

/// <summary>
/// Receives the objects decoded from a foreign PDO. The observe path writes here and nowhere
/// else: the local object dictionary is not a sink.
/// </summary>
public interface IForeignPdoSink
{
    /// <summary>Accepts one mapped object. Called in mapping order, on the thread that is
    /// finishing <c>ObserveForeignPdoAsync</c>, once the whole payload has been checked against
    /// the mapping — a payload that is shorter than the mapping writes nothing.</summary>
    void Write(ForeignPdoSignal signal);
}

/// <summary>What one peer PDO (one mapping record) did with an observed payload.</summary>
public sealed class ForeignPdoObservation
{
    internal ForeignPdoObservation(ForeignPdoKind kind, int pdoNumber, bool decoded,
        ForeignPdoMappingOrigin? origin, int signalsWritten, string? reason)
    {
        Kind = kind;
        PdoNumber = pdoNumber;
        Decoded = decoded;
        Origin = origin;
        SignalsWritten = signalsWritten;
        Reason = reason;
    }

    /// <summary>Whether the PDO is a TPDO or an RPDO of the peer.</summary>
    public ForeignPdoKind Kind { get; }

    /// <summary>PDO number, 1..4.</summary>
    public int PdoNumber { get; }

    /// <summary>Whether the payload was split. A short payload, a mapping that could not be
    /// read, and a record the peer file does not list are not decoded, and the sink is not called
    /// for them.</summary>
    public bool Decoded { get; }

    /// <summary>Where the mapping came from, when <see cref="Decoded"/> is true.</summary>
    public ForeignPdoMappingOrigin? Origin { get; }

    /// <summary>How many signals were written. Dummy entries are not counted. Zero when the
    /// mapping itself is empty.</summary>
    public int SignalsWritten { get; }

    /// <summary>Why the payload was not decoded, when <see cref="Decoded"/> is false.</summary>
    public string? Reason { get; }
}

/// <summary>
/// The outcome of <c>ObserveForeignPdoAsync</c> for one COB-ID. A COB-ID the peer file does not
/// assign to a PDO has no <see cref="Observations"/> and a <see cref="Reason"/>. A COB-ID the
/// file assigns to more than one PDO has one observation per PDO, in transmit-record order
/// (TPDO 1..4) and then receive-record order (RPDO 1..4).
/// </summary>
public sealed class ForeignPdoObserveResult
{
    internal ForeignPdoObserveResult(uint cobId, IReadOnlyList<ForeignPdoObservation> observations, string? reason)
    {
        CobId = cobId;
        Observations = observations;
        Reason = reason;
    }

    /// <summary>The 11-bit COB-ID that was observed.</summary>
    public uint CobId { get; }

    /// <summary>One entry per PDO the peer file assigns to <see cref="CobId"/>. Empty when none do.</summary>
    public IReadOnlyList<ForeignPdoObservation> Observations { get; }

    /// <summary>Why nothing was decoded, when <see cref="Observations"/> is empty.</summary>
    public string? Reason { get; }

    /// <summary>Whether at least one PDO was decoded into the sink.</summary>
    public bool Decoded
    {
        get
        {
            foreach (var observation in Observations)
            {
                if (observation.Decoded) return true;
            }
            return false;
        }
    }
}
