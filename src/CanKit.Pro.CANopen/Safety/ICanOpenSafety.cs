using System;
using System.Threading;
using System.Threading.Tasks;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// CANopen Safety (CiA DSP 304 V1.0) on a node: the SRDOs it produces and consumes, the global
/// failsafe command, and — for a master or tool — the configuration and verification of a
/// peer's safety parameters. Reached through <see cref="CanOpenSafetyExtensions.Safety"/>.
/// A node has SRDOs only when opened with <see cref="CanOpenNodeOptions.SrdoCount"/> &gt; 0 or
/// a device description that declares them.
/// </summary>
/// <remarks>
/// This library is not developed to IEC 61508 / DIN V VDE 0801 and claims no safety integrity
/// level. It implements the data transport of DSP 304 V1.0 — the frame pair, the timing checks,
/// the objects, the checksum — and leaves the diverse redundancy of §9.5 ("built by two
/// different ways", "compared … in the application") and the safe state to the device.
/// </remarks>
public interface ICanOpenSafety
{
    /// <summary>The number of SRDO records this node holds (0..64).</summary>
    int SrdoCount { get; }

    /// <summary>Writes record <paramref name="srdoNumber"/> as a producer: direction tx,
    /// refresh time, the mapping (plain and inverted sub-indices), the COB-IDs — the
    /// pre-defined pair of Table 4 for SRDO 1 of a node-id ≤ 64 when none is given. The SRDO
    /// is deleted first and created last, as §8.4.2.2 requires for a mapping change. Writes
    /// <c>13FEh</c> to 0; call <see cref="CommitSafetyConfiguration"/> afterwards. The writes are
    /// checked whole before the first one: a configuration the dictionary would refuse part-way
    /// changes nothing.</summary>
    /// <exception cref="InvalidOperationException">The node has no SRDOs, or is Operational (0800 0022h).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The number is not 1..<see cref="SrdoCount"/>, or a time is out of range.</exception>
    /// <exception cref="ArgumentException">A value the dictionary refuses (the abort code is in the message; nothing is written), or no COB-ID for a node-id above 64.</exception>
    void ConfigureSrdoProducer(int srdoNumber, SrdoMapping mapping, TimeSpan refreshTime, uint? cobId1 = null, uint? cobId2 = null);

    /// <summary>Writes record <paramref name="srdoNumber"/> as a consumer: direction rx, SCT,
    /// SRVT, mapping and COB-IDs. See <see cref="ConfigureSrdoProducer"/> for the rest.</summary>
    void ConfigureSrdoConsumer(int srdoNumber, SrdoMapping mapping, TimeSpan safeguardCycleTime, TimeSpan validationTime, uint? cobId1 = null, uint? cobId2 = null);

    /// <summary>Sets the direction of the record to 0: the SRDO does not exist.</summary>
    void DeleteSrdo(int srdoNumber);

    /// <summary>§9.2 for a locally configured node: writes <c>13FFh:n</c> for every record and
    /// then <c>13FEh</c> = A5h. Until this is called the configuration is not valid and no SRDO
    /// runs in Operational.</summary>
    void CommitSafetyConfiguration();

    /// <summary>Transmits producer <paramref name="srdoNumber"/> now (§8.1, event-driven) and
    /// restarts its refresh cycle. Nothing happens outside Operational.</summary>
    Task TriggerSrdoAsync(int srdoNumber, CancellationToken cancellationToken = default);

    /// <summary>The current validity of an SRDO: a snapshot of the actor's view. Right after
    /// the node is opened its records are taken on the actor's first turn, so a read before any
    /// call that round-trips the actor (<see cref="ICanOpenNode.State"/>, say) has returned may
    /// still show <see cref="SrdoDirection.None"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is not 1..<see cref="SrdoCount"/> — on a node without SRDOs, every number.</exception>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    SrdoState GetSrdoState(int srdoNumber);

    /// <summary>Sends the global failsafe command (§8.2: COB-ID 001h, DLC 0). Requires
    /// <c>1300h</c> = 1 and Operational.</summary>
    /// <exception cref="InvalidOperationException">1300h is 0 or the node is not Operational.</exception>
    Task SendGlobalFailsafeCommandAsync(CancellationToken cancellationToken = default);

    /// <summary>§9.2 Figure 9: downloads 1300h, every record (deleted first, created last), the
    /// checksums 13FFh:n, reads everything back and compares byte for byte, and only then writes
    /// 13FEh = A5h and reads it back. Every transfer passes the peer-SDO gate. One operation per
    /// peer at a time. An SDO abort, a timeout or a gate refusal propagates as from
    /// SdoDownloadAsync. The peer's 13FEh is 0 once at least one parameter write has been
    /// accepted, because every such write clears it; an abort before that — for example
    /// 0800 0022h from an Operational peer, which refuses the first write — leaves it unchanged.</summary>
    /// <exception cref="InvalidOperationException">The peer returned no count from 13FFh:00, or a
    /// count above 64 (§8.4.2.2) — refused before any further frame.</exception>
    /// <exception cref="ArgumentException">The configuration names an SRDO above the peer's count.</exception>
    Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>§8.3.1 step D: uploads 13FEh (must be A5h), 13FFh:n of every expected SRDO (must
    /// equal the checksum of the expected record) and the records themselves, and compares. An
    /// SRDO the expectation does not name must be deleted; its checksum is not compared, because
    /// the device checks only those of existing SRDOs (§9.5). Nothing is written.</summary>
    /// <exception cref="InvalidOperationException">The peer returned no count from 13FFh:00, or a
    /// count above 64 (§8.4.2.2) — refused before any further frame.</exception>
    /// <exception cref="ArgumentException">The expectation names an SRDO above the peer's count.</exception>
    Task<PeerSafetyResult> VerifyPeerSafetyConfigurationAsync(byte peerNodeId, PeerSafetyConfiguration expected, CancellationToken cancellationToken = default);

    /// <summary>Splits an SRDO pair of another node into <paramref name="sink"/>, like
    /// <see cref="ICanOpenNode.ObserveForeignPdoAsync"/>: the existing record whose live 1301h–1340h:05
    /// equals <paramref name="cobId1"/> (the file is the fallback when the upload fails; a record
    /// whose direction is 0, live or in the file, does not match: it may have kept the ids another SRDO now uses), the
    /// pair checked for equal length and bitwise inversion, the mapping read live from
    /// 1381h–13C0h (odd sub-indices) or from the file. Signals carry
    /// <see cref="ForeignPdoKind.Srdo"/> and the SRDO number. SRVT and SCT are the caller's to
    /// judge: it holds the timestamps. Nothing is written to this node's dictionary.
    /// The live mapping is read in full whenever its count is: the peer-SDO gate lets through
    /// every slot of an SRDO record the bound file implies, declared or not. A record whose
    /// COB-ID 1 word has a bit above bit 10 set, or a mapping with a dummy entry, is not decoded.</summary>
    Task<ForeignSrdoObserveResult> ObserveForeignSrdoAsync(byte peerNodeId, uint cobId1, ReadOnlyMemory<byte> frame1,
        ReadOnlyMemory<byte> frame2, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken = default);

    /// <summary>A consumer SRDO received a valid pair and wrote it to the mapped objects.</summary>
    event EventHandler<SrdoReceivedEventArgs>? SrdoReceived;

    /// <summary>An SRDO became valid or invalid (transitions only; never dropped).</summary>
    event EventHandler<SrdoStateChangedEventArgs>? SrdoStateChanged;

    /// <summary>A GFC arrived while <c>1300h</c> = 1 (including the node's own, on a bus that echoes).</summary>
    event EventHandler<GlobalFailsafeCommandReceivedEventArgs>? GlobalFailsafeCommandReceived;
}
