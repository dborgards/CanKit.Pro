using System;

namespace CanKit.Pro.CANopen;

/// <summary>
/// CiA 301 pre-defined connection set: helpers that translate CANopen node identifiers into the
/// 11-bit COB-IDs used for NMT, SYNC, EMCY, PDO, SDO and heartbeat traffic.
/// </summary>
/// <remarks>
/// Constants and helpers only — no state, no allocation. The layout mirrors CiA 301 §7.2:
/// <list type="bullet">
///   <item><description>NMT master command frame — <c>0x000</c></description></item>
///   <item><description>SYNC — <c>0x080</c></description></item>
///   <item><description>EMCY — <c>0x080 + node-id</c></description></item>
///   <item><description>TPDO1..4 — <c>0x180/0x280/0x380/0x480 + node-id</c></description></item>
///   <item><description>RPDO1..4 — <c>0x200/0x300/0x400/0x500 + node-id</c></description></item>
///   <item><description>SDO server → client — <c>0x580 + node-id</c></description></item>
///   <item><description>SDO client → server — <c>0x600 + node-id</c></description></item>
///   <item><description>NMT error control (heartbeat / bootup) — <c>0x700 + node-id</c></description></item>
/// </list>
/// </remarks>
public static class CanOpenCobId
{
    /// <summary>Smallest legal CANopen node identifier (CiA 301 §7.2.4).</summary>
    public const byte MinNodeId = 1;

    /// <summary>Largest legal CANopen node identifier (CiA 301 §7.2.4).</summary>
    public const byte MaxNodeId = 127;

    /// <summary>NMT master command COB-ID (single-broadcast, no node offset).</summary>
    public const uint NmtCommand = 0x000;

    /// <summary>SYNC COB-ID.</summary>
    public const uint Sync = 0x080;

    /// <summary>Base COB-ID for EMCY frames: <c>0x080 + node-id</c>.</summary>
    public const uint EmcyBase = 0x080;

    /// <summary>Base COB-ID for TPDO1: <c>0x180 + node-id</c>.</summary>
    public const uint Tpdo1Base = 0x180;

    /// <summary>Base COB-ID for RPDO1: <c>0x200 + node-id</c>.</summary>
    public const uint Rpdo1Base = 0x200;

    /// <summary>Base COB-ID for TPDO2: <c>0x280 + node-id</c>.</summary>
    public const uint Tpdo2Base = 0x280;

    /// <summary>Base COB-ID for RPDO2: <c>0x300 + node-id</c>.</summary>
    public const uint Rpdo2Base = 0x300;

    /// <summary>Base COB-ID for TPDO3: <c>0x380 + node-id</c>.</summary>
    public const uint Tpdo3Base = 0x380;

    /// <summary>Base COB-ID for RPDO3: <c>0x400 + node-id</c>.</summary>
    public const uint Rpdo3Base = 0x400;

    /// <summary>Base COB-ID for TPDO4: <c>0x480 + node-id</c>.</summary>
    public const uint Tpdo4Base = 0x480;

    /// <summary>Base COB-ID for RPDO4: <c>0x500 + node-id</c>.</summary>
    public const uint Rpdo4Base = 0x500;

    /// <summary>Base COB-ID for SDO server → client responses: <c>0x580 + node-id</c>.</summary>
    public const uint SdoTxBase = 0x580;

    /// <summary>Base COB-ID for SDO client → server requests: <c>0x600 + node-id</c>.</summary>
    public const uint SdoRxBase = 0x600;

    /// <summary>Base COB-ID for NMT error control (heartbeat + bootup): <c>0x700 + node-id</c>.</summary>
    public const uint HeartbeatBase = 0x700;

    /// <summary>Bit 31 of a COB-ID object (<c>1014h</c>, <c>1200h</c>, <c>1400h:01</c>,
    /// <c>1800h:01</c>): set means the communication object "does not exist / is not valid"
    /// (CiA 301 Tables 59, 64, 66, 70).</summary>
    public const uint InvalidBit = 0x8000_0000;

    /// <summary>Bit 30 of a TPDO COB-ID (<c>1800h:01</c>): set means "no RTR allowed on this PDO"
    /// (CiA 301 Table 70).</summary>
    public const uint NoRtrBit = 0x4000_0000;

    /// <summary>Bit 30 of the SYNC COB-ID (<c>1005h</c>): set means "CANopen device generates
    /// SYNC message" (CiA 301 Table 55).</summary>
    public const uint SyncGenerateBit = 0x4000_0000;

    /// <summary>Bit 29 of a COB-ID object: set means the CAN-ID is a 29-bit extended identifier.
    /// This node supports CAN base frames only and rejects the bit with SDO abort
    /// <c>0609 0030h</c>, as CiA 301 prescribes.</summary>
    public const uint ExtendedFrameBit = 0x2000_0000;

    /// <summary>Mask of the 11-bit CAN-ID in a COB-ID object.</summary>
    public const uint CanIdMask = 0x7FF;

    /// <summary>
    /// Whether <paramref name="canId"/> is one of the restricted CAN-IDs of CiA 301 §7.3.5
    /// Table 40, which no configurable communication object (SYNC, TIME, EMCY, PDO, SDO) may use:
    /// <c>000h</c> (NMT), <c>001h</c>–<c>07Fh</c>, <c>101h</c>–<c>180h</c>, <c>581h</c>–<c>5FFh</c>
    /// (default SDO tx), <c>601h</c>–<c>67Fh</c> (default SDO rx), <c>6E0h</c>–<c>6FFh</c>,
    /// <c>701h</c>–<c>77Fh</c> (NMT error control) and <c>780h</c>–<c>7FFh</c>.
    /// </summary>
    public static bool IsRestricted(uint canId) => canId switch
    {
        0x000 => true,
        >= 0x001 and <= 0x07F => true,
        >= 0x101 and <= 0x180 => true,
        >= 0x581 and <= 0x5FF => true,
        >= 0x601 and <= 0x67F => true,
        >= 0x6E0 and <= 0x6FF => true,
        >= 0x701 and <= 0x77F => true,
        >= 0x780 => true,
        _ => false,
    };

    /// <summary>Validates that <paramref name="nodeId"/> falls in the legal CiA 301 range.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="nodeId"/> is
    /// outside <c>[1, 127]</c>.</exception>
    public static void ValidateNodeId(byte nodeId)
    {
        if (nodeId < MinNodeId || nodeId > MaxNodeId)
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId,
                $"CANopen node-id must be in [{MinNodeId}, {MaxNodeId}].");
    }

    /// <summary>Returns <c>0x580 + node-id</c>, the SDO server → client (SDO Tx) COB-ID.</summary>
    public static uint SdoTx(byte nodeId) => SdoTxBase + nodeId;

    /// <summary>Returns <c>0x600 + node-id</c>, the SDO client → server (SDO Rx) COB-ID.</summary>
    public static uint SdoRx(byte nodeId) => SdoRxBase + nodeId;

    /// <summary>Returns <c>0x700 + node-id</c>, the heartbeat / bootup COB-ID.</summary>
    public static uint Heartbeat(byte nodeId) => HeartbeatBase + nodeId;

    /// <summary>Returns <c>0x080 + node-id</c>, the EMCY COB-ID.</summary>
    public static uint Emcy(byte nodeId) => EmcyBase + nodeId;

    /// <summary>Returns the default TPDO COB-ID for <paramref name="pdoIndex"/> (1..4) and
    /// <paramref name="nodeId"/> according to the CiA 301 pre-defined connection set.</summary>
    public static uint TpdoDefault(byte nodeId, int pdoIndex) => pdoIndex switch
    {
        1 => Tpdo1Base + nodeId,
        2 => Tpdo2Base + nodeId,
        3 => Tpdo3Base + nodeId,
        4 => Tpdo4Base + nodeId,
        _ => throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, "PDO index must be 1..4."),
    };

    /// <summary>Returns the default RPDO COB-ID for <paramref name="pdoIndex"/> (1..4) and
    /// <paramref name="nodeId"/> according to the CiA 301 pre-defined connection set.</summary>
    public static uint RpdoDefault(byte nodeId, int pdoIndex) => pdoIndex switch
    {
        1 => Rpdo1Base + nodeId,
        2 => Rpdo2Base + nodeId,
        3 => Rpdo3Base + nodeId,
        4 => Rpdo4Base + nodeId,
        _ => throw new ArgumentOutOfRangeException(nameof(pdoIndex), pdoIndex, "PDO index must be 1..4."),
    };
}
