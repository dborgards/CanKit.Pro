using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.CANopen.Emcy;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen;

/// <summary>
/// The communication-profile objects (CiA 301 §7.5.2) of <see cref="CanOpenNode"/>: the node
/// creates them in its object dictionary at their CiA 301 defaults, validates every write to them
/// against the norm's value rules before the value is stored, and derives its runtime
/// configuration (SYNC, EMCY, heartbeat, guarding, PDOs) from them. The dictionary is the single
/// source of truth in both directions and on both write paths — an accepted SDO download and a
/// local <see cref="ObjectDictionary"/> write reach the service the same way, and the public
/// configuration methods are themselves writes to these objects.
/// </summary>
/// <remarks>
/// <para>
/// Objects the node manages: <c>1001h</c> (error register, mirrored by <c>SendEmcyAsync</c>),
/// <c>1005h</c>/<c>1006h</c> (SYNC), <c>100Ch</c>/<c>100Dh</c> (life guarding), <c>1010h</c>/
/// <c>1011h</c> (store / restore, in-memory), <c>1014h</c> (EMCY COB-ID), <c>1016h</c>/<c>1017h</c>
/// (heartbeat), <c>1200h</c> (the default SDO server, constant), and the PDO communication and
/// mapping records <c>1400h</c>–<c>1403h</c>, <c>1600h</c>–<c>1603h</c>, <c>1800h</c>–<c>1803h</c>,
/// <c>1A00h</c>–<c>1A03h</c>. <c>1000h</c> and <c>1018h</c> are created as placeholders the
/// application replaces; they drive no behaviour.
/// </para>
/// <para>
/// Power-on values (CiA 301 §7.3.2.2.1): an NMT reset restores the managed objects to the values
/// last stored through <c>1010h:01</c> / <see cref="StoreParameters"/>, or to the defaults the node
/// was created with if nothing was stored or <c>1011h:01</c> / <see cref="RestoreDefaultParameters"/>
/// preceded the reset. Reset Node and Reset Communication restore the same set here: without a
/// device description there is no source for the application objects' power-on values, so
/// the application restores those itself from <see cref="ICanOpenNode.ApplicationReset"/>,
/// which runs before the boot-up goes out.
/// </para>
/// </remarks>
internal sealed partial class CanOpenNode
{
    /// <summary>CiA 301 communication-profile object indices the node manages.</summary>
    private static class Co
    {
        public const ushort DeviceType = 0x1000;
        public const ushort ErrorRegister = 0x1001;
        public const ushort SyncCobId = 0x1005;
        public const ushort CyclePeriod = 0x1006;
        public const ushort GuardTime = 0x100C;
        public const ushort LifeTimeFactor = 0x100D;
        public const ushort StoreParameters = 0x1010;
        public const ushort RestoreDefaults = 0x1011;
        public const ushort EmcyCobId = 0x1014;
        public const ushort ConsumerHeartbeat = 0x1016;
        public const ushort ProducerHeartbeat = 0x1017;
        public const ushort Identity = 0x1018;
        public const ushort SdoServer = 0x1200;
        public const ushort NmtStartup = 0x1F80;
        public const ushort SlaveAssignment = 0x1F81;
        public const ushort RequestNmt = 0x1F82;
        public const ushort BootTime = 0x1F89;
        public const ushort FlyingMasterTiming = 0x1F90;
        public const ushort RpdoComm = 0x1400;
        public const ushort RpdoMap = 0x1600;
        public const ushort TpdoComm = 0x1800;
        public const ushort TpdoMap = 0x1A00;
        public const int PdoCount = 4;
    }

    // "save" and "load" as the UNSIGNED32 signatures of CiA 301 Figures 55 and 57, little-endian
    // on the wire: 0x65766173 ("e" "v" "a" "s" from MSB to LSB) and 0x64616F6C.
    private static readonly byte[] SaveSignature = { 0x73, 0x61, 0x76, 0x65 };
    private static readonly byte[] LoadSignature = { 0x6C, 0x6F, 0x61, 0x64 };

    // Actor-confined runtime configuration derived from the OD.
    private volatile uint _syncCobId = CanOpenCobId.Sync;
    private bool _syncGenerate;
    private uint _emcyCobId;
    private bool _emcyValid = true;
    private EmcyMessage? _pendingEmcy;
    private TimeSpan _guardTime;
    private byte _lifeTimeFactor;
    private IDeadline? _lifeGuardingDeadline;
    private bool _lifeGuardingOccurred;

    // Power-on values (CiA 301 §7.3.2.2.1): the factory defaults are the values the node was
    // created with; the power-on set is what an NMT reset restores. Both are keyed like the OD.
    private Dictionary<uint, byte[]> _factoryDefaults = new();
    private Dictionary<uint, byte[]> _powerOnValues = new();
    private bool _restoreDefaultsOnReset;

    // =========================================================================================
    // Creation.
    // =========================================================================================

    /// <summary>
    /// Creates the communication-profile objects at their CiA 301 defaults and installs the OD
    /// hooks. Runs in the constructor, before the node is on the bus.
    /// </summary>
    private void PopulateCommunicationProfile()
    {
        // Footnote * of the CiA 301 objects overview permits ro for "PDO communication parameter
        // and PDO mapping object entries" — and for nothing else. The SYNC, EMCY, heartbeat and
        // guarding objects are rw as their definitions prescribe.
        var pdo = _options.WritableCommunicationParameters ? OdAccess.ReadWrite : OdAccess.ReadOnly;
        const OdAccess ro = OdAccess.ReadOnly;
        const OdAccess rw = OdAccess.ReadWrite;
        const bool nm = false; // communication-profile objects are not PDO-mappable (CiA 301 §7.5.2)

        // §7.5.2.1 / §7.5.2.2 / §7.5.2.21: the three mandatory objects. 1000h = 0 is "a logical
        // device that does not follow a standardized profile"; 1018h with sub0 = 01h and
        // vendor-id 0 is "no vendor-ID assigned" — both defined values, not inventions. The
        // application replaces them with its own (Add* on 1000h / 1018h is permitted).
        _od.AddU32(Co.DeviceType, 0x00, 0, ro, nm);
        _od.AddU8(Co.ErrorRegister, 0x00, 0, ro, nm);
        _od.AddU8(Co.Identity, 0x00, 0x01, ro, nm);
        _od.AddU32(Co.Identity, 0x01, 0, ro, nm);

        // §7.5.2.5 / §7.5.2.6: SYNC. Default 0000 0080h — SYNC on 080h, not generated.
        _od.AddU32(Co.SyncCobId, 0x00, CanOpenCobId.Sync, rw, nm);
        _od.AddU32(Co.CyclePeriod, 0x00, 0, rw, nm);

        // §7.5.2.11 / §7.5.2.12: guard time and life time factor, 0 = life guarding disabled.
        _od.AddU16(Co.GuardTime, 0x00, 0, rw, nm);
        _od.AddU8(Co.LifeTimeFactor, 0x00, 0, rw, nm);

        // §7.5.2.13 / §7.5.2.14: store / restore, sub-index 01h ("all parameters"). The read value
        // 1 announces "saves parameters on command" / "restores default parameters".
        _od.AddU8(Co.StoreParameters, 0x00, 0x01, ro, nm);
        _od.AddU32(Co.StoreParameters, 0x01, 0x0000_0001, rw, nm);
        _od.AddU8(Co.RestoreDefaults, 0x00, 0x01, ro, nm);
        _od.AddU32(Co.RestoreDefaults, 0x01, 0x0000_0001, rw, nm);

        // §7.5.2.17: EMCY COB-ID, default 80h + node-id, valid.
        _od.AddU32(Co.EmcyCobId, 0x00, CanOpenCobId.Emcy(_nodeId), rw, nm);

        // §7.5.2.19 / §7.5.2.20: heartbeat. One consumer slot to begin with; AddHeartbeatConsumer
        // grows the array. Sub0 is const on the bus.
        _od.AddU8(Co.ConsumerHeartbeat, 0x00, 0x01, ro, nm);
        _od.AddU32(Co.ConsumerHeartbeat, 0x01, 0, rw, nm);
        _od.AddU16(Co.ProducerHeartbeat, 0x00, 0, rw, nm);

        // Flying master and boot-up (CiA 302-2 v4.1.0). Inactive until bits 0 and 5 of 1F80h
        // are set. 1F90h is milliseconds: timeout, negotiation delay, priority level, priority
        // time slot, device time slot, multiple-master detect cycle. The defaults separate the
        // three priority levels (1500 > 127 × 10). The detect cycle carries the node-id so two
        // masters do not poll in lockstep. 1F81h is the network list (one entry per node-id),
        // 1F82h the tracked NMT state and the request that sends a command, 1F89h the boot
        // timeout (0 = none). 1F81h's guard time and life time are stored and not acted on:
        // heartbeat is the keep-alive this node runs, and node guarding is not started from
        // those bytes. The suppress bits of 1F80h follow the node's profile: a device leaves
        // self-start and slave-start allowed, a tool suppresses both.
        _od.AddU32(Co.NmtStartup, 0x00, DefaultNmtStartup(), rw, nm);
        _od.AddU8(Co.SlaveAssignment, 0x00, CanOpenCobId.MaxNodeId, ro, nm);
        for (byte node = 1; node <= CanOpenCobId.MaxNodeId; node++)
            _od.AddU32(Co.SlaveAssignment, node, 0, rw, nm);
        _od.AddU8(Co.RequestNmt, 0x00, 0x80, ro, nm);
        for (byte node = 1; node <= 0x80; node++)
            _od.AddU8(Co.RequestNmt, node, 0, rw, nm);
        _od.AddU32(Co.BootTime, 0x00, 0, rw, nm);
        _od.AddU8(Co.FlyingMasterTiming, 0x00, 0x06, ro, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x01, 100, rw, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x02, 500, rw, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x03, 2, rw, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x04, 1500, rw, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x05, 10, rw, nm);
        _od.AddU16(Co.FlyingMasterTiming, 0x06, (ushort)(4000 + 10 * _nodeId), rw, nm);

        // §7.5.2.33: the default SDO server, all const — the node serves 600h/580h + node-id
        // and nothing else, and the record says so.
        _od.AddU8(Co.SdoServer, 0x00, 0x02, ro, nm);
        _od.AddU32(Co.SdoServer, 0x01, CanOpenCobId.SdoRx(_nodeId), ro, nm);
        _od.AddU32(Co.SdoServer, 0x02, CanOpenCobId.SdoTx(_nodeId), ro, nm);

        // §7.5.2.35–§7.5.2.38: four RPDOs and four TPDOs at their pre-defined connection set
        // CAN-IDs, "not valid" until configured. TPDO sub0 = 05h (inhibit time and event timer
        // supported); RPDO sub0 = 02h. Sub-index 04h is reserved and deliberately absent, so an
        // access yields 0609 0011h as §7.5.2.37 prescribes.
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            var rc = (ushort)(Co.RpdoComm + n - 1);
            var rm = (ushort)(Co.RpdoMap + n - 1);
            var tc = (ushort)(Co.TpdoComm + n - 1);
            var tm = (ushort)(Co.TpdoMap + n - 1);

            _od.AddU8(rc, 0x00, 0x02, ro, nm);
            _od.AddU32(rc, 0x01, CanOpenCobId.InvalidBit | CanOpenCobId.RpdoDefault(_nodeId, n), pdo, nm);
            _od.AddU8(rc, 0x02, CanOpenTransmissionType.EventDrivenManufacturer, pdo, nm);

            _od.AddU8(tc, 0x00, 0x05, ro, nm);
            _od.AddU32(tc, 0x01, CanOpenCobId.InvalidBit | CanOpenCobId.NoRtrBit | CanOpenCobId.TpdoDefault(_nodeId, n), pdo, nm);
            _od.AddU8(tc, 0x02, CanOpenTransmissionType.EventDrivenManufacturer, pdo, nm);
            _od.AddU16(tc, 0x03, 0, pdo, nm);
            _od.AddU16(tc, 0x05, 0, pdo, nm);

            _od.AddU8(rm, 0x00, 0, pdo, nm);
            _od.AddU8(tm, 0x00, 0, pdo, nm);
            for (byte s = 1; s <= PdoMapping.MaxEntries; s++)
            {
                _od.AddU32(rm, s, 0, pdo, nm);
                _od.AddU32(tm, s, 0, pdo, nm);
            }
        }

        _od.DeclareGuard = (index, _) => !IsManagedCommunicationObject(index);
        _od.WriteValidator = ValidateCommunicationWrite;
        _od.EntryWritten += OnOdEntryWrittenForCommunicationProfile;

        _factoryDefaults = SnapshotRestorableValues();
        _powerOnValues = _factoryDefaults;
    }

    /// <summary>Objects whose values drive the node and which the application therefore writes
    /// rather than re-declares.</summary>
    private static bool IsManagedCommunicationObject(ushort index) => index switch
    {
        Co.ErrorRegister or Co.SyncCobId or Co.CyclePeriod or Co.GuardTime or Co.LifeTimeFactor
            or Co.StoreParameters or Co.RestoreDefaults or Co.EmcyCobId or Co.ConsumerHeartbeat
            or Co.ProducerHeartbeat or Co.SdoServer or Co.NmtStartup or Co.SlaveAssignment
            or Co.RequestNmt or Co.BootTime or Co.FlyingMasterTiming => true,
        >= Co.RpdoComm and < Co.RpdoComm + Co.PdoCount => true,
        >= Co.RpdoMap and < Co.RpdoMap + Co.PdoCount => true,
        >= Co.TpdoComm and < Co.TpdoComm + Co.PdoCount => true,
        >= Co.TpdoMap and < Co.TpdoMap + Co.PdoCount => true,
        _ => false,
    };

    /// <summary>
    /// The objects an NMT reset restores: the managed communication objects — except status
    /// (1001h) and the constant records (1010h/1011h capabilities, 1200h) — and every object a
    /// device description declared, the placeholders 1000h/1018h and the application objects
    /// among them. Without a description the node has no power-on source for those: what the
    /// application put into 1000h, 1018h or its own objects stays across a reset, as FR-CO-019
    /// and the README say (Bugbot on #135). Reset Communication restores the communication
    /// profile area (1000h–1FFFh) only; Reset Node restores all of them (CiA 301 §7.3.2.2.1).
    /// </summary>
    private bool IsRestorableObject(ushort index)
        => (IsManagedCommunicationObject(index) || _describedObjects.Contains(index))
           && index is not (Co.ErrorRegister or Co.StoreParameters or Co.RestoreDefaults or Co.SdoServer);

    // The objects a device description declared and the loader created or took over; empty
    // without a description. Written once, on the constructing thread, before the node's actor
    // runs anything that reads it.
    private readonly HashSet<ushort> _describedObjects = new();

    private static bool IsCommunicationProfileArea(ushort index) => index is >= 0x1000 and <= 0x1FFF;

    // =========================================================================================
    // Validation — runs on the writing thread, before the value is stored.
    // =========================================================================================

    private OdWriteDecision ValidateCommunicationWrite(ushort index, byte subindex, byte[] value)
    {
        switch (index)
        {
            case Co.SyncCobId:
                return ValidateSyncCobIdWrite(value);
            case Co.EmcyCobId:
                return ValidateEmcyCobIdWrite(value);
            case Co.ConsumerHeartbeat:
                if (subindex == 0) return ValidateConsumerHeartbeatCountWrite(value[0]);
                return ValidateConsumerHeartbeatWrite(subindex, value);
            case Co.FlyingMasterTiming:
                return ValidateFlyingMasterTimingWrite(subindex, value);
            case Co.SlaveAssignment:
                return ValidateSlaveAssignmentWrite(subindex, value);
            case Co.RequestNmt:
                return ValidateRequestNmtWrite(subindex, value);
            case Co.BootTime:
                return ValidateBootTimeWrite(subindex, value);
            case Co.StoreParameters:
                return subindex == 1 ? HandleStoreCommand(value) : OdWriteDecision.Accept;
            case Co.RestoreDefaults:
                return subindex == 1 ? HandleRestoreCommand(value) : OdWriteDecision.Accept;
        }
        if (index is >= Co.RpdoComm and < Co.RpdoComm + Co.PdoCount)
            return ValidatePdoCommunicationWrite(index, subindex, value, isTpdo: false);
        if (index is >= Co.TpdoComm and < Co.TpdoComm + Co.PdoCount)
            return ValidatePdoCommunicationWrite(index, subindex, value, isTpdo: true);
        if (index is >= Co.RpdoMap and < Co.RpdoMap + Co.PdoCount)
            return ValidatePdoMappingWrite(index, subindex, value, isTpdo: false);
        if (index is >= Co.TpdoMap and < Co.TpdoMap + Co.PdoCount)
            return ValidatePdoMappingWrite(index, subindex, value, isTpdo: true);
        return OdWriteDecision.Accept;
    }

    // §7.5.2.5 Table 55: bit 31 do not care, bit 30 gen., bit 29 frame, bits 10..0 the CAN-ID.
    // "It is not allowed to change bits 0 to 29, while the object exists (bit 30 = 1b)."
    private OdWriteDecision ValidateSyncCobIdWrite(byte[] value)
    {
        uint v = ObjectDictionary.DecodeU32(value);
        if (!IsUsableCobIdWord(v)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        uint cur = _od.ReadUnsigned(Co.SyncCobId, 0x00);
        const uint lower30 = 0x3FFF_FFFF;
        if ((cur & CanOpenCobId.SyncGenerateBit) != 0 && (v & CanOpenCobId.SyncGenerateBit) != 0
            && (cur & lower30) != (v & lower30))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        // A CAN-ID carries one communication object of this node: a frame on the SYNC CAN-ID is
        // a SYNC and nothing else, so 1005h cannot move onto the CAN-ID of a PDO that exists or
        // of a valid EMCY (Codex on #133). The PDO and EMCY sides refuse the mirror image.
        uint canId = v & CanOpenCobId.CanIdMask;
        if (IsCanIdOfAValidPdo(canId) || IsCanIdOfTheValidEmcy(canId)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        return OdWriteDecision.Accept;
    }

    private bool IsCanIdOfTheValidEmcy(uint canId)
    {
        uint word = _od.ReadUnsigned(Co.EmcyCobId, 0x00);
        return (word & CanOpenCobId.InvalidBit) == 0 && (word & CanOpenCobId.CanIdMask) == canId;
    }

    private bool IsTheSyncCanId(uint canId) => (_od.ReadUnsigned(Co.SyncCobId, 0x00) & CanOpenCobId.CanIdMask) == canId;

    private bool IsCanIdOfAValidPdo(uint canId)
    {
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            if (IsValidPdoOn((ushort)(Co.RpdoComm + n - 1), canId) || IsValidPdoOn((ushort)(Co.TpdoComm + n - 1), canId))
                return true;
        }
        return false;
    }

    private bool IsValidPdoOn(ushort comm, uint canId)
        => _od.TryReadUnsigned(comm, 0x01, out var word)
           && (word & CanOpenCobId.InvalidBit) == 0
           && (word & CanOpenCobId.CanIdMask) == canId;

    // §7.5.2.17 Table 59: bit 31 valid, bit 30 reserved (always 0b), bit 29 frame. "The bits 0
    // to 29 shall not be changed, while the object exists and is valid (bit 31 = 0b)."
    private OdWriteDecision ValidateEmcyCobIdWrite(byte[] value)
    {
        uint v = ObjectDictionary.DecodeU32(value);
        if ((v & 0x4000_0000) != 0 || !IsUsableCobIdWord(v))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        uint cur = _od.ReadUnsigned(Co.EmcyCobId, 0x00);
        const uint lower30 = 0x3FFF_FFFF;
        if ((cur & CanOpenCobId.InvalidBit) == 0 && (v & CanOpenCobId.InvalidBit) == 0
            && (cur & lower30) != (v & lower30))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        // A valid EMCY cannot sit on the SYNC CAN-ID: every data frame there is a SYNC, and on
        // a bus that echoes the node's own EMCY would come back as one (Codex on #133).
        if ((v & CanOpenCobId.InvalidBit) == 0 && IsTheSyncCanId(v & CanOpenCobId.CanIdMask))
            return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        return OdWriteDecision.Accept;
    }

    // §7.5.2.19 Figure 62: bits 31..24 reserved (00h), 23..16 node-id, 15..0 heartbeat time in
    // ms. "An attempt to configure several heartbeat times unequal 0 for the same node-ID … shall
    // be responded with the SDO abort transfer service (abort code: 0604 0043h)."
    // §7.5.2.19: sub-index 00h holds at most 127 entries. It is ro on the bus, but a local write
    // reaches it: a count above 127 would send the loops over the array round the clock on the
    // actor (a byte 255 + 1 is 0), and a count above the declared sub-indices would announce
    // entries that cannot be read and make every further AddHeartbeatConsumer see the array as
    // full. The count may only name sub-indices that exist; the node declares one before it
    // raises the count (Codex on #133).
    private OdWriteDecision ValidateConsumerHeartbeatCountWrite(byte count)
    {
        if (count > CanOpenCobId.MaxNodeId) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        // A slot beyond the current count is out of the duplicate check while it is hidden, so
        // growing the array again brings its entry back into view: the proposed range may hold
        // one consumer per producer, as every single write is held to (Codex on #133).
        var producers = new HashSet<byte>();
        for (int s = 1; s <= count; s++)
        {
            if (!_od.TryReadUnsigned(Co.ConsumerHeartbeat, (byte)s, out var v)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
            byte nodeId = (byte)((v >> 16) & 0xFF);
            if ((ushort)(v & 0xFFFF) == 0 || nodeId is < CanOpenCobId.MinNodeId or > CanOpenCobId.MaxNodeId) continue;
            if (!producers.Add(nodeId)) return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
        }
        return OdWriteDecision.Accept;
    }

    private OdWriteDecision ValidateConsumerHeartbeatWrite(byte subindex, byte[] value)
    {
        uint v = ObjectDictionary.DecodeU32(value);
        if ((v & 0xFF00_0000) != 0) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
        byte nodeId = (byte)((v >> 16) & 0xFF);
        ushort time = (ushort)(v & 0xFFFF);
        if (time == 0 || nodeId is < CanOpenCobId.MinNodeId or > CanOpenCobId.MaxNodeId)
            return OdWriteDecision.Accept; // "the corresponding object entry shall be not used"
        byte count = (byte)_od.ReadUnsigned(Co.ConsumerHeartbeat, 0x00);
        for (int s = 1; s <= count; s++)
        {
            if (s == subindex || !_od.TryReadUnsigned(Co.ConsumerHeartbeat, (byte)s, out var other)) continue;
            if ((ushort)(other & 0xFFFF) != 0 && (byte)((other >> 16) & 0xFF) == nodeId)
                return OdWriteDecision.Reject(SdoAbortCode.GeneralParameterIncompatibility);
        }
        return OdWriteDecision.Accept;
    }

    // §7.5.2.13: storing happens only on the "save" signature; a wrong one is refused with
    // 0800 002xh. The stored value (the capability word) never changes.
    private OdWriteDecision HandleStoreCommand(byte[] value)
    {
        if (!SignatureMatches(value, SaveSignature))
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferred);
        // The values are taken here, on the writing thread under the dictionary's write gate: what
        // is stored is what the dictionary held at the "save", not what a writer changed while the
        // actor was still getting to it (Codex on #133). Only the actor-owned state moves to the loop.
        var stored = SnapshotRestorableValues();
        RunOnActor(() =>
        {
            _powerOnValues = stored;
            // A store after "load" is the newer instruction: the next reset restores what was
            // just stored, not the defaults the earlier "load" asked for.
            _restoreDefaultsOnReset = false;
        });
        return OdWriteDecision.Handled;
    }

    // §7.5.2.14: "The default values shall be set valid after the CANopen device is reset."
    private OdWriteDecision HandleRestoreCommand(byte[] value)
    {
        if (!SignatureMatches(value, LoadSignature))
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferred);
        RunOnActor(() => _restoreDefaultsOnReset = true);
        return OdWriteDecision.Handled;
    }

    private static bool SignatureMatches(byte[] value, byte[] signature)
    {
        if (value.Length != signature.Length) return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != signature[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// The checks every COB-ID word shares: this node speaks CAN base frames only, so bit 29
    /// (frame) is rejected with 0609 0030h as §7.5.2.5, §7.5.2.17, §7.5.2.35 and §7.5.2.37
    /// prescribe; bits 28..11 must be zero for an 11-bit CAN-ID; and the CAN-ID must not be one
    /// CiA 301 §7.3.5 restricts.
    /// </summary>
    private static bool IsUsableCobIdWord(uint word)
    {
        if ((word & CanOpenCobId.ExtendedFrameBit) != 0) return false;
        if ((word & 0x1FFF_F800) != 0) return false;
        return !CanOpenCobId.IsRestricted(word & CanOpenCobId.CanIdMask);
    }

    // =========================================================================================
    // Apply — the OD changed, bring the runtime in line. Runs on the actor loop.
    // =========================================================================================

    private void OnOdEntryWrittenForCommunicationProfile(ushort index, byte subindex)
    {
        if (!IsManagedCommunicationObject(index)) return;
        if (Volatile.Read(ref _disposed) != 0) return;
        // A TPDO record changed: the change-of-state pre-filter is refreshed here, on the writing
        // thread and still inside the write gate, so that the writer's next write — the first
        // value of an object it has just mapped — is already seen, before the actor has rebuilt
        // the runtime (Codex on #133).
        if (index is (>= Co.TpdoComm and < Co.TpdoComm + Co.PdoCount) or (>= Co.TpdoMap and < Co.TpdoMap + Co.PdoCount))
            RebuildCosRelevantEntries();
        RunOnActor(() => ApplyCommunicationObject(index, subindex));
    }

    private void RunOnActor(Action work)
    {
        if (_actor.IsOnCurrentActor)
        {
            work();
            return;
        }
        try
        {
            _actor.Post(work);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the post; the change no longer needs applying.
        }
    }

    /// <summary>Runs <paramref name="work"/> on the actor loop and returns once it has, rethrowing
    /// what it threw. A configuration that is several dictionary writes becomes one transaction
    /// there: the SDO server writes on the same loop, and another caller queues behind the whole
    /// sequence instead of interleaving with it. The applies the writes raise run inline on the
    /// loop, so the effect is in place when this returns. Inline when already on the loop.</summary>
    private void RunOnActorAndWait(Action work)
    {
        if (_actor.IsOnCurrentActor)
        {
            work();
            return;
        }
        _actor.PostAsync(work).GetAwaiter().GetResult();
    }

    /// <summary>Test seam: posts <paramref name="work"/> to the actor loop and returns its task.
    /// A test holds the loop with it to prove what must not wait for the loop — the values a
    /// "save" stores, a change-of-state write right after a direct configuration.</summary>
    internal System.Threading.Tasks.Task PostToActorAsync(Action work) => _actor.PostAsync(work);

    /// <summary>Blocks until every apply posted so far has run, so a configuration method
    /// returns with its effect in place. Skipped when already on the actor loop, where posted
    /// work cannot run until the current callback returns anyway.</summary>
    private void WaitForActor()
    {
        if (_actor.IsOnCurrentActor || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _actor.PostAsync(() => { }).GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
            // Nothing left to wait for.
        }
    }

    private void ApplyAllCommunicationObjects()
    {
        ApplySyncConfiguration();
        ApplyEmcyConfiguration();
        ApplyHeartbeatProducerConfiguration();
        RebuildHeartbeatConsumers();
        ApplyLifeGuardingConfiguration();
        for (int n = 1; n <= Co.PdoCount; n++)
        {
            RebuildRpdo(n);
            RebuildTpdo(n);
        }
    }

    private void ApplyCommunicationObject(ushort index, byte subindex)
    {
        if (_disposed != 0) return;
        switch (index)
        {
            case Co.SyncCobId:
            case Co.CyclePeriod:
                ApplySyncConfiguration();
                return;
            case Co.EmcyCobId:
                ApplyEmcyConfiguration();
                return;
            case Co.ConsumerHeartbeat:
                RebuildHeartbeatConsumers();
                return;
            case Co.ProducerHeartbeat:
                ApplyHeartbeatProducerConfiguration();
                return;
            case Co.NmtStartup:
                ApplyFlyingMasterStartup();
                return;
            case Co.SlaveAssignment:
                // A live edit is the network list the active master boots now. It is not a
                // power-on value: StartFlyingMaster and StoreParameters are what record one,
                // and a reset must come back to that rather than to whatever was written since.
                if (_flyingMasterRole == FlyingMasterRole.Active) BeginBootUp();
                return;
            case Co.BootTime:
                if (_flyingMasterRole == FlyingMasterRole.Active) BeginBootUp();
                return;
            case Co.GuardTime:
            case Co.LifeTimeFactor:
                ApplyLifeGuardingConfiguration();
                return;
        }
        if (index is >= Co.RpdoComm and < Co.RpdoComm + Co.PdoCount) RebuildRpdo(index - Co.RpdoComm + 1);
        else if (index is >= Co.RpdoMap and < Co.RpdoMap + Co.PdoCount) RebuildRpdo(index - Co.RpdoMap + 1);
        else if (index is >= Co.TpdoComm and < Co.TpdoComm + Co.PdoCount) RebuildTpdo(index - Co.TpdoComm + 1);
        else if (index is >= Co.TpdoMap and < Co.TpdoMap + Co.PdoCount) RebuildTpdo(index - Co.TpdoMap + 1);
    }

    // §7.5.2.5 / §7.5.2.6: the producer runs iff bit 30 of 1005h is set and 1006h > 0; "the
    // first transmission of SYNC object starts within 1 sync cycle after setting bit 30 to 1b".
    private void ApplySyncConfiguration()
    {
        uint word = _od.ReadUnsigned(Co.SyncCobId, 0x00);
        _syncCobId = word & CanOpenCobId.CanIdMask;
        _syncGenerate = (word & CanOpenCobId.SyncGenerateBit) != 0;
        uint periodMicroseconds = _od.ReadUnsigned(Co.CyclePeriod, 0x00);
        var interval = TimeSpan.FromTicks(periodMicroseconds * (TimeSpan.TicksPerMillisecond / 1000));

        if (!_syncGenerate || periodMicroseconds == 0)
        {
            _syncProducerHandle?.Dispose();
            _syncProducerHandle = null;
            _syncProducerInterval = TimeSpan.Zero;
            return;
        }
        if (_syncProducerHandle is not null && _syncProducerInterval == interval) return;
        _syncProducerHandle?.Dispose();
        _syncProducerInterval = interval;
        ScheduleSyncProducerTick();
    }

    // §7.5.2.17: bit 31 = "EMCY does not exist / is not valid"; bits 10..0 the CAN-ID.
    private void ApplyEmcyConfiguration()
    {
        uint word = _od.ReadUnsigned(Co.EmcyCobId, 0x00);
        _emcyCobId = word & CanOpenCobId.CanIdMask;
        _emcyValid = (word & CanOpenCobId.InvalidBit) == 0;
    }

    // §7.5.2.20: "The value 0 shall disable the producer heartbeat." The producer module follows
    // the object; it does not read the dictionary itself.
    private void ApplyHeartbeatProducerConfiguration()
    {
        var ms = (ushort)_od.ReadUnsigned(Co.ProducerHeartbeat, 0x00);
        var interval = ms == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ms);
        // §7.2.8.3.2.2: with 1017h ≠ 0 the heartbeat protocol is used, so guarding ends here
        // — a node life time still running from the last poll, and an event that occurred,
        // would otherwise outlive the switch and report a master that was told to stop polling.
        if (_heartbeatProducer.Apply(interval))
            ResetLifeGuardingState();
    }

    // §7.5.2.19: every sub-index with a node-id in 1..127 and a non-zero time is a consumer.
    private void RebuildHeartbeatConsumers()
    {
        var desired = new Dictionary<byte, TimeSpan>();
        byte count = (byte)_od.ReadUnsigned(Co.ConsumerHeartbeat, 0x00);
        for (int s = 1; s <= count; s++)
        {
            if (!_od.TryReadUnsigned(Co.ConsumerHeartbeat, (byte)s, out var v)) continue;
            byte nodeId = (byte)((v >> 16) & 0xFF);
            ushort ms = (ushort)(v & 0xFFFF);
            if (ms == 0 || nodeId is < CanOpenCobId.MinNodeId or > CanOpenCobId.MaxNodeId) continue;
            desired[nodeId] = TimeSpan.FromMilliseconds(ms);
        }

        _heartbeatConsumer.Replace(desired);
    }

    // =========================================================================================
    // NMT state transitions and resets (CiA 301 §7.3.2).
    // =========================================================================================

    /// <summary>Applies a Start / Stop / Enter Pre-Operational transition on the actor loop:
    /// PDOs exist only in Operational, SDO / SYNC / EMCY are not active in Stopped (Table 37),
    /// and a heartbeat announcing the new state goes out when the producer is active.</summary>
    private void ApplyNmtTransition(NmtState target)
    {
        var previous = _state;
        _state = target;

        if (previous == NmtState.Operational && target != NmtState.Operational) OnLeaveOperational();
        if (target == NmtState.Operational && previous != NmtState.Operational) OnEnterOperational();

        if (target == NmtState.Stopped)
        {
            // §7.3.2.2.4: "forced to stop the communication altogether (except node guarding and
            // heartbeat)". An SDO transfer in flight cannot continue; tell the client why.
            AbortServerSessions(SdoAbortCode.DataCannotBeTransferredDeviceState);
        }
        else if (previous == NmtState.Stopped && _pendingEmcy is { } pending)
        {
            // §7.3.2.2.4: "The most recent active EMCY reason may be transmitted after the
            // CANopen device transits into another NMT state."
            _pendingEmcy = null;
            if (_emcyValid) _ = EmitEmcy(pending);
        }

        // A state-change heartbeat is an early heartbeat, so it belongs to the heartbeat protocol
        // and goes out only while that protocol is in use (1017h ≠ 0). With the producer off the
        // node is in the configuration node guarding runs in, and an unsolicited data frame on
        // 0x700 + id is indistinguishable from a toggle-0 guarding reply (#43).
        if (_heartbeatProducer.Interval > TimeSpan.Zero)
            _ = EmitHeartbeat((byte)_state);
    }

    /// <summary>
    /// NMT Reset Node / Reset Communication (CiA 301 §7.3.2.2.1): the communication-profile
    /// objects return to their power-on values, the node passes NMT state Initialisation, sends
    /// boot-up and settles in Pre-Operational. See the class remarks for why both commands
    /// restore the same set here.
    /// </summary>
    private void PerformNmtReset(bool communicationOnly)
    {
        AbortServerSessions(SdoAbortCode.DataCannotBeTransferredDeviceState);
        _pendingEmcy = null;
        // §7.2.8.3.2.1: "The toggle bit in the guarding protocol shall be reset to 0 when the NMT
        // sub-state reset communication is passed."
        _nodeGuardingProducerToggle = false;
        ResetLifeGuardingState();
        // Before the dictionary is restored: a flying master that was mid-election must not keep
        // that deadline, and the restart the restored 1F80h performs is a warm boot.
        SuspendFlyingMasterForReset();
        if (_state == NmtState.Operational) OnLeaveOperational();
        _state = NmtState.Initializing;

        if (_restoreDefaultsOnReset)
        {
            _powerOnValues = _factoryDefaults;
            _restoreDefaultsOnReset = false;
        }
        RestoreValues(_powerOnValues, communicationOnly);
        // The application's turn, still in Initialisation: its objects are restored before the
        // node announces itself, so a master reacting to the boot-up never reads a value the
        // reset had not reached. With a description the node restored the described ones above;
        // the hook lets the application restore what the description does not hold.
        RaiseApplicationReset(communicationOnly ? NmtCommand.ResetCommunication : NmtCommand.ResetNode);

        _state = NmtState.PreOperational;
        // Boot-up (0x00) first; a heartbeat with the new state follows only when the producer is
        // active, in which case §7.2.8.3.2.2 regards the boot-up as its first heartbeat — and the
        // producer's cycle restarts from it: the restore armed the tick before the application's
        // hook ran, so a tick that became due meanwhile would otherwise fire right behind this.
        // Re-armed before the frames are ordered, for the same reason the guarding reply arms
        // its life time first (#141): a frame on the wire implies the timer behind it is set.
        _heartbeatProducer.RestartCycle();
        _ = EmitHeartbeat(0x00);
        if (_heartbeatProducer.Interval > TimeSpan.Zero) _ = EmitHeartbeat((byte)NmtState.PreOperational);
    }

    private void AbortServerSessions(SdoAbortCode code)
    {
        if (_sdoServer is { } classic)
        {
            _sdoServer = null;
            classic.Deadline?.Dispose();
            SendSdoServerAbort(classic.Index, classic.Subindex, code);
        }
        if (_sdoBlockServer is { } block)
        {
            _sdoBlockServer = null;
            block.Deadline?.Dispose();
            SendSdoServerAbort(block.Index, block.Subindex, code);
        }
    }

    // =========================================================================================
    // Power-on values.
    // =========================================================================================

    private Dictionary<uint, byte[]> SnapshotRestorableValues()
    {
        var snapshot = new Dictionary<uint, byte[]>();
        foreach (var key in _od.SnapshotKeys())
        {
            var index = (ushort)(key >> 8);
            if (!IsRestorableObject(index)) continue;
            snapshot[key] = _od.ReadRaw(index, (byte)(key & 0xFF));
        }
        return snapshot;
    }

    /// <summary>
    /// Restores the objects in <paramref name="values"/> — the communication profile area only
    /// when <paramref name="communicationOnly"/>. A managed communication sub-index that was
    /// added since (a grown <c>1016h</c>) goes to zero; an application object the snapshot does
    /// not hold has no power-on value and is left alone. Each write applies to the runtime
    /// through the ordinary hook, inline because this runs on the actor loop. One transaction
    /// under the dictionary's write gate, so the snapshot a "save" takes on another thread sees
    /// all restored values or all live ones, never a mix (Bugbot on #133).
    /// </summary>
    private void RestoreValues(Dictionary<uint, byte[]> values, bool communicationOnly)
    {
        // 1F80h sorts before 1F90h. Applying the startup bit as soon as 1F80h is written would
        // arm the election from the live timing, and the restored 1F90h would arrive too late
        // to move that deadline. Hold the startup until every restored value is in the dictionary.
        _suppressFlyingMasterStartup = true;
        try
        {
            _od.Transaction(() =>
            {
                foreach (var key in _od.SnapshotKeys())
                {
                    var index = (ushort)(key >> 8);
                    var subindex = (byte)(key & 0xFF);
                    if (!IsRestorableObject(index)) continue;
                    if (communicationOnly && !IsCommunicationProfileArea(index)) continue;
                    if (values.TryGetValue(key, out var stored))
                    {
                        if (_od.TryGet(index, subindex, out var current) && OdEntryLayout.FixedSize(current.DataType) is var size
                            && size > 0 && stored.Length != size)
                            continue; // re-declared with another width since the snapshot: no power-on value for it
                        _od.WriteRawUnchecked(index, subindex, stored);
                    }
                    else if (IsManagedCommunicationObject(index) && _od.TryGet(index, subindex, out var entry))
                    {
                        _od.WriteRawUnchecked(index, subindex, new byte[entry.Size]);
                    }
                }
            });
        }
        finally
        {
            _suppressFlyingMasterStartup = false;
        }
        ApplyFlyingMasterStartup();
    }

    /// <inheritdoc />
    public void StoreParameters()
    {
        ThrowIfDisposed();
        _od.WriteRaw(Co.StoreParameters, 0x01, SaveSignature);
        WaitForActor();
    }

    /// <inheritdoc />
    public void RestoreDefaultParameters()
    {
        ThrowIfDisposed();
        _od.WriteRaw(Co.RestoreDefaults, 0x01, LoadSignature);
        WaitForActor();
    }

    // =========================================================================================
    // Unit conversions for the public API — every bound is the object's UNSIGNED width.
    // =========================================================================================

    private static ushort ToMilliseconds16(TimeSpan value, string paramName, bool allowZero)
    {
        long ms = (long)Math.Round(value.TotalMilliseconds);
        if (ms < (allowZero ? 0 : 1) || ms > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Must be 1 ms .. 65535 ms: the object is an UNSIGNED16 in multiples of 1 ms (CiA 301).");
        return (ushort)ms;
    }

    private static ushort ToHundredMicroseconds16(TimeSpan value, string paramName)
    {
        long units = value.Ticks / (TimeSpan.TicksPerMillisecond / 10);
        if (units < 0 || units > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Must be 0 .. 6.5535 s: the object is an UNSIGNED16 in multiples of 100 µs (CiA 301 §7.5.2.37).");
        return (ushort)units;
    }

    private static uint ToMicroseconds32(TimeSpan value, string paramName)
    {
        long us = value.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        if (us < 1 || us > uint.MaxValue)
            throw new ArgumentOutOfRangeException(paramName, value,
                "Must be 1 µs .. 4294967295 µs: 1006h is an UNSIGNED32 in multiples of 1 µs (CiA 301 §7.5.2.6).");
        return (uint)us;
    }
}
