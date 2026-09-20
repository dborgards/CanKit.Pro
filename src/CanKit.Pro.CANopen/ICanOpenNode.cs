using System;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// A single CANopen node running on top of the CanKit.Pro L2 demux (arc42 §5.3, ADR-5;
/// FR-CO-012). Combines an NMT slave state machine, a local Object Dictionary, an SDO server
/// (for the local OD) and an SDO client (for remote nodes on the same bus), plus the SYNC,
/// EMCY, heartbeat producer/consumer, node-guarding and PDO plumbing of CiA 301 §7.
/// </summary>
/// <remarks>
/// <para>
/// One instance represents one CANopen node identity (1..127) on one physical bus, and serves
/// both roles a CANopen application takes: a <b>device</b> (its own object dictionary, PDOs it
/// produces and consumes, configured by a master over SDO) and a <b>tool or master</b> (the
/// SDO client, the NMT master, heartbeat and node-guarding consumers, the SYNC producer). Two or
/// more nodes may share the same underlying <see cref="CanKit.Pro.RawCan.ICanBusService"/> so
/// a process-hosted master and one or more simulated slaves can coexist on a virtual bus.
/// </para>
/// <para>
/// The object dictionary is the single source of truth for the node's communication
/// parameters. The methods below that configure a service — <see cref="StartHeartbeatProducer"/>,
/// <see cref="AddHeartbeatConsumer"/>, <see cref="StartSyncProducer"/>,
/// <see cref="ConfigureTpdo"/>, <see cref="ConfigureRpdo"/> — are writes to the
/// corresponding CiA 301 objects (<c>1017h</c>, <c>1016h</c>, <c>1005h</c>/<c>1006h</c>,
/// <c>1800h</c>/<c>1A00h</c>, <c>1400h</c>/<c>1600h</c>), and a value those objects accept from
/// a master over SDO, or from the application through <see cref="ObjectDictionary"/>, takes effect
/// the same way. A value that is not implementable is rejected before it is stored, so the
/// dictionary never describes behaviour the node does not have.
/// </para>
/// </remarks>
public interface ICanOpenNode : IDisposable
{
    /// <summary>Node identifier (1..127) this instance answers as on the bus.</summary>
    byte NodeId { get; }

    /// <summary>Options this node was constructed with.</summary>
    CanOpenNodeOptions Options { get; }

    /// <summary>
    /// The local Object Dictionary (FR-CO-001). Shared by SDO server, PDO mapping and application
    /// code. Created with the communication-profile objects CiA 301 requires of a device that
    /// supports SDO, PDO, SYNC, EMCY, heartbeat and guarding (<c>1000h</c>, <c>1001h</c>,
    /// <c>1005h</c>, <c>1006h</c>, <c>100Ch</c>, <c>100Dh</c>, <c>1010h</c>, <c>1011h</c>,
    /// <c>1014h</c>, <c>1016h</c>, <c>1017h</c>, <c>1018h</c>, <c>1200h</c>, <c>1400h</c>–<c>1403h</c>,
    /// <c>1600h</c>–<c>1603h</c>, <c>1800h</c>–<c>1803h</c>, <c>1A00h</c>–<c>1A03h</c>), each at its
    /// CiA 301 default. <c>1000h</c> and <c>1018h</c> carry placeholder values (device type 0,
    /// vendor-id 0 = "no vendor-id assigned") that the application replaces with its own; the
    /// remaining objects are managed by the node and take values, not re-declarations.
    /// </summary>
    ObjectDictionary ObjectDictionary { get; }

    /// <summary>Current NMT slave state of this node.</summary>
    NmtState State { get; }

    // -----------------------------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------------------------

    /// <summary>Raised when a heartbeat or bootup frame is received from any node. Data frames on
    /// <c>0x700 + node-id</c> are heartbeats; a node-guarding response is reported through
    /// <see cref="NodeGuardingReceived"/> instead when a guarding consumer is registered for the
    /// producer.</summary>
    event EventHandler<HeartbeatReceivedEventArgs>? HeartbeatReceived;

    /// <summary>Raised when a configured heartbeat consumer detects a missing heartbeat.</summary>
    event EventHandler<HeartbeatTimeoutEventArgs>? HeartbeatTimeout;

    /// <summary>Raised when an EMCY frame is received on the bus (FR-CO-011).</summary>
    event EventHandler<EmcyReceivedEventArgs>? EmcyReceived;

    /// <summary>Raised whenever a SYNC frame is received on the configured SYNC COB-ID
    /// (<c>1005h</c>), either from a remote producer or from this node's own producer if echo is
    /// on. Not raised in NMT state Stopped, where SYNC is not active (CiA 301 Table 37).</summary>
    event EventHandler<SyncReceivedEventArgs>? SyncReceived;

    /// <summary>Raised after an RPDO the local node has mapped is received and unpacked into
    /// the OD — immediately for an event-driven RPDO, with the next SYNC for a synchronous one.</summary>
    event EventHandler<RpdoReceivedEventArgs>? RpdoReceived;

    /// <summary>Raised on the actor loop for every NMT master command whose target matches this
    /// node (or the broadcast target 0).</summary>
    event EventHandler<NmtCommandReceivedEventArgs>? NmtCommandReceived;

    /// <summary>Raised whenever a node-guarding response (data frame on <c>0x700 + producer</c>
    /// with the toggle bit in bit 7 and the NMT state in bits 0..6) is received for a
    /// configured node-guarding consumer (FR-CO-009 / CiA 301 §7.2.8.3.2.1).</summary>
    event EventHandler<NodeGuardingReceivedEventArgs>? NodeGuardingReceived;

    /// <summary>Raised when a configured node-guarding consumer's life-time
    /// (<c>guardTime × lifeTimeFactor</c>) elapses without seeing an answer to the RTR poll
    /// (FR-CO-009).</summary>
    event EventHandler<NodeGuardingTimeoutEventArgs>? NodeGuardingTimeout;

    /// <summary>
    /// Producer-side life guarding (CiA 301 §7.2.8.2.2.2 / §7.2.8.3.2.1): raised with
    /// <see cref="LifeGuardingState.Occurred"/> when this node, being guarded by a master, was
    /// not polled within its node life time (<c>100Ch</c> guard time × <c>100Dh</c> life time
    /// factor, both from the object dictionary), and with <see cref="LifeGuardingState.Resolved"/>
    /// when a poll arrives again afterwards. Guarding starts with the first RTR received; both
    /// objects at 0 (the default) disable it.
    /// </summary>
    event EventHandler<LifeGuardingEventArgs>? LifeGuardingEvent;

    /// <summary>Raised on background exceptions from the actor loop / subscription reader.</summary>
    event EventHandler<Exception>? BackgroundExceptionOccurred;

    // -----------------------------------------------------------------------------------------
    // NMT master (FR-CO-007)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Sends an NMT master command (COB-ID <c>0x000</c>). <paramref name="targetNodeId"/>
    /// zero means broadcast to all nodes. Applying the command to the local node (either
    /// broadcast or targeting this node's own id) is expected to be handled by the receiver
    /// via <see cref="NmtCommandReceived"/>.
    /// </summary>
    Task SendNmtCommandAsync(NmtCommand command, byte targetNodeId,
        CancellationToken cancellationToken = default);

    // -----------------------------------------------------------------------------------------
    // Heartbeat producer / consumer (FR-CO-008)
    // -----------------------------------------------------------------------------------------

    /// <summary>Starts (or replaces) the local heartbeat producer with
    /// <paramref name="interval"/>: writes <c>1017h</c> (producer heartbeat time, 1..65535 ms).
    /// While the producer is active the node uses the heartbeat protocol and does not answer
    /// node-guarding RTRs (CiA 301 §7.2.8.3.2.2).</summary>
    void StartHeartbeatProducer(TimeSpan interval);

    /// <summary>Stops the local heartbeat producer: writes <c>1017h</c> = 0.</summary>
    void StopHeartbeatProducer();

    /// <summary>Registers (or replaces) a heartbeat consumer for
    /// <paramref name="producerNodeId"/> in a free sub-index of <c>1016h</c> (consumer heartbeat
    /// time; the array grows as consumers are added): if no heartbeat is received within
    /// <paramref name="timeout"/> (1..65535 ms), the node raises <see cref="HeartbeatTimeout"/>.
    /// The timeout is armed immediately, so a producer that never appears is reported too.</summary>
    void AddHeartbeatConsumer(byte producerNodeId, TimeSpan timeout);

    /// <summary>Removes a previously-registered heartbeat consumer for
    /// <paramref name="producerNodeId"/> (clears its <c>1016h</c> entry). No-op if none was
    /// registered.</summary>
    void RemoveHeartbeatConsumer(byte producerNodeId);

    // -----------------------------------------------------------------------------------------
    // SYNC (FR-CO-010)
    // -----------------------------------------------------------------------------------------

    /// <summary>Starts a periodic SYNC producer with the given interval: writes <c>1006h</c>
    /// (communication cycle period, in µs) and sets bit 30 of <c>1005h</c> ("device generates
    /// SYNC"). SYNC is not transmitted in NMT state Stopped (CiA 301 Table 37).</summary>
    void StartSyncProducer(TimeSpan interval);

    /// <summary>Stops the periodic SYNC producer: clears bit 30 of <c>1005h</c>.</summary>
    void StopSyncProducer();

    /// <summary>Transmits a single SYNC frame (payload-less) on the SYNC COB-ID configured in
    /// <c>1005h</c>.</summary>
    Task SendSyncAsync(CancellationToken cancellationToken = default);

    // -----------------------------------------------------------------------------------------
    // EMCY (FR-CO-011)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Transmits an EMCY frame from this node on the COB-ID configured in <c>1014h</c>, after
    /// writing <paramref name="errorRegister"/> to <c>1001h</c> so the error register a master
    /// reads over SDO is the one the EMCY carried. In NMT state Stopped the EMCY is held and the
    /// most recent one is transmitted when the node leaves Stopped (CiA 301 §7.3.2.2.4). The
    /// returned task faults with <see cref="InvalidOperationException"/> when EMCY is disabled
    /// (bit 31 of <c>1014h</c> set).
    /// </summary>
    Task SendEmcyAsync(ushort errorCode, byte errorRegister,
        ReadOnlyMemory<byte> manufacturerSpecific = default,
        CancellationToken cancellationToken = default);

    // -----------------------------------------------------------------------------------------
    // SDO client (FR-CO-002 / FR-CO-003)
    // -----------------------------------------------------------------------------------------

    /// <summary>Reads an object from <paramref name="serverNodeId"/>'s OD using
    /// <see cref="SdoTransferMode.Auto"/> (the server's initiate response decides expedited vs.
    /// segmented; the size is unknown up front so Auto does not select block upload — pass
    /// <see cref="SdoTransferMode.Block"/> explicitly).
    /// Source-compatible overload for callers that pass a positional
    /// <see cref="CancellationToken"/>.</summary>
    Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex,
        CancellationToken cancellationToken);

    /// <summary>Reads an object from <paramref name="serverNodeId"/>'s OD.
    /// <see cref="SdoTransferMode.Auto"/> uses the classic client because the payload length is
    /// unknown until the server replies — whether that reply is expedited or segmented is the
    /// server's choice and is handled transparently. Pass <see cref="SdoTransferMode.Block"/> to
    /// force block transfer (CiA 301 §7.2.4.3.15).
    /// (FR-CO-002 / FR-CO-003 / FR-CO-004).</summary>
    Task<byte[]> SdoUploadAsync(byte serverNodeId, ushort index, byte subindex,
        SdoTransferMode mode = SdoTransferMode.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>Writes an object to <paramref name="serverNodeId"/>'s OD using
    /// <see cref="SdoTransferMode.Auto"/>. Source-compatible overload for callers that pass a
    /// positional <see cref="CancellationToken"/> after <paramref name="data"/>.</summary>
    Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken);

    /// <summary>Writes an object to <paramref name="serverNodeId"/>'s OD.
    /// <see cref="SdoTransferMode.Auto"/> selects the transport from the <paramref name="data"/>
    /// length: at or above <see cref="CanOpenNodeOptions.SdoBlockThresholdBytes"/> block transfer,
    /// and below it the CiA 301 split — 1..4 bytes expedited, 5 bytes and up segmented. The
    /// threshold is checked first, so it also decides the expedited range: a threshold of 1..4
    /// (which the options permit) sends a short payload by block transfer rather than expedited.
    /// Pass
    /// <see cref="SdoTransferMode.Block"/> to force block transfer below that threshold; the
    /// expedited/segmented split itself is dictated by CiA 301 and is not selectable.</summary>
    Task SdoDownloadAsync(byte serverNodeId, ushort index, byte subindex,
        ReadOnlyMemory<byte> data,
        SdoTransferMode mode = SdoTransferMode.Auto,
        CancellationToken cancellationToken = default);

    // -----------------------------------------------------------------------------------------
    // PDO (FR-CO-005 / FR-CO-006)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Configures (or replaces) TPDO <paramref name="pdoIndex"/> (1..4) by writing its
    /// communication record <c>1800h + n − 1</c> and mapping record <c>1A00h + n − 1</c>, in the
    /// CiA 301 §7.5.2.38 re-mapping order (PDO invalidated, mapping rewritten, parameters set,
    /// PDO validated). Values not provided use CiA 301 defaults: the COB-ID is the pre-defined
    /// connection set entry, the inhibit time is 0 (off), and the event timer is
    /// <see cref="CanOpenNodeOptions.DefaultTpdoEventTimerInterval"/> for
    /// <see cref="TpdoTransmission.EventTimer"/> and 0 otherwise. The writes are one transaction
    /// on the node's actor loop: the method returns with the configuration applied, and neither
    /// another caller nor an SDO download interleaves with the sequence.
    /// </summary>
    /// <param name="pdoIndex">TPDO number 1..4.</param>
    /// <param name="mapping">The mapped objects; copied into the mapping record. Every target
    /// must exist in the OD, be PDO-mappable and readable, and have the mapped bit length (dummy
    /// entries <c>0002h</c>–<c>0007h</c> excepted).</param>
    /// <param name="transmission">Trigger mode; see <see cref="TpdoTransmission"/>. Cyclic
    /// "every n-th SYNC" is configured by writing the raw byte to <c>1800h:02</c> instead.</param>
    /// <param name="cobId">The <c>1800h:01</c> word: an 11-bit CAN-ID plus the CiA 301 control
    /// bits — bit 31 set configures the PDO but leaves it disabled ("PDO does not exist"), bit 30
    /// set forbids RTR on it. Bit 29 (29-bit CAN-ID) is not supported and throws.</param>
    /// <param name="eventTimerInterval">Event timer for <see cref="TpdoTransmission.EventTimer"/>,
    /// 1..65535 ms; ignored for the other modes.</param>
    /// <param name="inhibitTime">Minimum interval between two transmissions of an event-driven
    /// TPDO (<c>1800h:03</c>, in multiples of 100 µs, at most 6.5535 s). Has no effect on the
    /// synchronous and RTR-only modes, per CiA 301 §7.5.2.37.</param>
    /// <exception cref="ArgumentException">A value was rejected; the message names the CiA 301
    /// abort code the same write would have produced over SDO. The PDO is left disabled.</exception>
    void ConfigureTpdo(int pdoIndex, PdoMapping mapping,
        TpdoTransmission transmission = TpdoTransmission.EventDriven,
        uint? cobId = null,
        TimeSpan? eventTimerInterval = null,
        TimeSpan? inhibitTime = null);

    /// <summary>
    /// Configures (or replaces) RPDO <paramref name="pdoIndex"/> (1..4) by writing its
    /// communication record <c>1400h + n − 1</c> and mapping record <c>1600h + n − 1</c>. Incoming
    /// frames matching the COB-ID unpack into the local OD and raise <see cref="RpdoReceived"/> —
    /// immediately, or with the next SYNC for <see cref="RpdoTransmission.Synchronous"/>.
    /// <paramref name="cobId"/> is the <c>1400h:01</c> word (bit 31 set = configured but disabled;
    /// bit 29 is not supported and throws); the CAN-ID must not be one CiA 301 §7.3.5 restricts.
    /// The writes are one transaction on the actor loop, as for <see cref="ConfigureTpdo"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A value was rejected; see <see cref="ConfigureTpdo"/>.</exception>
    void ConfigureRpdo(int pdoIndex, PdoMapping mapping, uint? cobId = null,
        RpdoTransmission transmission = RpdoTransmission.EventDriven);

    /// <summary>Requests a TPDO transmission from the application (CiA 301 "internal event").
    /// Event-driven TPDOs transmit subject to their inhibit time; a synchronous-acyclic TPDO
    /// transmits after the next SYNC; the other modes transmit immediately. The node still
    /// respects the current NMT state (only fires in <see cref="NmtState.Operational"/>).</summary>
    Task TriggerTpdoAsync(int pdoIndex, CancellationToken cancellationToken = default);

    // -----------------------------------------------------------------------------------------
    // Parameter storage (CiA 301 §7.5.2.13 / §7.5.2.14)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the current values of the node's communication-profile objects as the power-on
    /// values an NMT reset restores — the local equivalent of a master writing "save" to
    /// <c>1010h:01</c>. Without a store, a reset restores the CiA 301 defaults the node was
    /// created with, so a device configured through <see cref="ConfigureTpdo"/> /
    /// <see cref="ConfigureRpdo"/> calls this once its configuration is complete.
    /// </summary>
    void StoreParameters();

    /// <summary>
    /// Marks the stored power-on values for discarding: the next NMT reset restores the CiA 301
    /// defaults the node was created with (the local equivalent of a master writing "load" to
    /// <c>1011h:01</c>; per §7.5.2.14 the defaults become valid with the reset, not before).
    /// </summary>
    void RestoreDefaultParameters();

    // -----------------------------------------------------------------------------------------
    // Node-Guarding (FR-CO-009, CiA 301 §7.2.8.3.2.1)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Starts the node-guarding consumer for <paramref name="producerNodeId"/>: sends an RTR
    /// on <c>0x700 + producerNodeId</c> every <paramref name="guardTime"/> and expects the peer
    /// to reply with a one-byte data frame (toggle bit in bit 7, NMT state in bits 0..6). If
    /// <c>guardTime × lifeTimeFactor</c> elapses without a valid response the node raises
    /// <see cref="NodeGuardingTimeout"/>. Replaces any existing consumer for the same peer.
    /// </summary>
    void StartNodeGuardingConsumer(byte producerNodeId, TimeSpan guardTime, byte lifeTimeFactor);

    /// <summary>Stops the node-guarding consumer for <paramref name="producerNodeId"/>. No-op
    /// when none is running.</summary>
    void StopNodeGuardingConsumer(byte producerNodeId);
}
