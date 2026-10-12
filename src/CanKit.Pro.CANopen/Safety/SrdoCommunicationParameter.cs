using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>The SRDO communication parameter record, data type 26h (CiA DSP 304 V1.0 §8.4.1.1
/// Table 5): direction (sub1), refresh time for a producer or SCT for a consumer (sub2), SRVT
/// (sub3, consumer only), and the two COB-IDs (sub5, sub6). Sub4, the transmission type, is
/// the constant 254 and is not part of this record.</summary>
/// <param name="Direction">Sub-index 1.</param>
/// <param name="RefreshOrSafeguardCycleTime">Sub-index 2, whole milliseconds 1..65535.</param>
/// <param name="ValidationTime">Sub-index 3, whole milliseconds 1..255. Written and part of the checksum
/// for every direction; a producer does not act on it (§8.4.2.2 "tx: not used").</param>
/// <param name="CobId1">Sub-index 5, the odd CAN-ID of the plain-data frame (257, 259..383).</param>
/// <param name="CobId2">Sub-index 6, the even CAN-ID of the inverted frame (258, 260..384).</param>
public readonly record struct SrdoCommunicationParameter(
    SrdoDirection Direction,
    TimeSpan RefreshOrSafeguardCycleTime,
    TimeSpan ValidationTime,
    uint CobId1,
    uint CobId2);
