# CANopen Safety (CiA 304) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add CiA DSP 304 V1.0 safety-relevant communication (SRDO, GFC, the `1300h`–`13FFh` objects, the §9.2 peer configuration, boot-up step D and foreign-SRDO observation) to `CanKit.Pro.CANopen`, opt-in, behind an `ICanOpenSafety` facade reached by an extension method.

**Architecture:** The safety objects are managed communication-profile objects of `CanOpenNode`, validated on the writing thread like the PDO records. An internal `SrdoEngine` (own class, injected actor / deadline scheduler / object dictionary / host callbacks, like `HeartbeatProducer`) derives producer and consumer runtimes from those records. The node wires the engine into its frame dispatch, NMT transitions and event pump, and exposes `ICanOpenSafety`; `CanOpenSafetyExtensions.Safety(this ICanOpenNode)` returns it so `ICanOpenNode` is not widened. Master-side operations are SDO-client flows through the existing peer gate.

**Tech Stack:** C# 14, `netstandard2.0;net10.0`, xunit + AwesomeAssertions, `CanKit.Adapter.Virtual` loopback, `ManualTimeSource` for every timed assertion.

**Spec:** `docs/reviews/2026-10-09-canopen-safety-scope.md` (the record of decisions, the design and the item table). Norm text: `dsp304.pdf` in the maintainer's collection, CiA DSP 304 V1.0. The reconciliation against V1.1 / EN 50325-5 is #289.

## Global Constraints

- Every change lands through one pull request on branch `feat/canopen-safety`; every commit is a Conventional Commit without `!` (`CLAUDE.md` § Pull requests). No `BREAKING CHANGE` footer anywhere.
- `ICanOpenNode` gains no member (spec decision 7). New public API lives in namespace `CanKit.Pro.CANopen.Safety` and on `ICanOpenSafety`; `CanOpenCobId`, `CanOpenNodeOptions`, `FlyingMasterSignal` and `ForeignPdoKind` gain additive members only.
- `tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.approved.txt` is replaced by the generated `.received.txt`, never hand-edited.
- No test asserts against wall-clock time. Timers run on `ManualTimeSource`; "no frame was sent" is shown with an ordering witness, as `CanOpenPdoEngineTests` does.
- The local gate before every push: `dotnet build CanKit.Pro.sln -c Release -p:CI=true`, `dotnet test CanKit.Pro.sln -c Release --no-build --framework net10.0`, `dotnet format CanKit.Pro.sln --verify-no-changes`, `dotnet pack` into a fresh directory + `python3 eng/verify-packages.py <dir>` (`EXPECTED_PACKAGE_COUNT` stays 9).
- Every rule cites its DSP 304 V1.0 section in a code comment, as the PDO engine cites CiA 301.
- Without `CanOpenNodeOptions.SrdoCount > 0` (or a description declaring SRDO records) the node creates no safety object and its behaviour is byte-for-byte what it is today; the existing test suite must stay green unchanged.
- Abort codes, defaults and value ranges are those in the spec's validator table; the CRC is CRC-16/XMODEM over the canonical byte sequence of spec § "CRC 13FFh:n", multi-byte fields MSB-first.

## Review Focus

1. A device opened with `SrdoCount = 0` must reject nothing new and send nothing new: the GFC filter bit is harmless and `TryHandleFrame` returns false immediately. Pinned in Task 7 (`Node_Without_Srdos_Is_Unchanged`).
2. The second frame of a pair arriving on an **echo** bus for a *transmit* SRDO must not be taken as an out-of-order reception of a receive SRDO on the same node that happens to share the id range (Task 6, `Frames_On_Own_Transmit_CobIds_Are_Ignored`).
3. A `13FFh` write by the tool must clear `13FEh`, and `CommitSafetyConfiguration` must write the CRCs **before** `A5h`, otherwise the commit un-validates itself (Task 4, `Checksum_Write_Clears_Configuration_Valid`; Task 7, `Commit_Writes_Checksums_Then_Valid`).
4. A consumer whose SCT expired and then receives a valid pair must re-arm the SCT, or the second expiry is never detected (Task 6, `Valid_Pair_After_Expiry_Rearms_Sct`).
5. A mandatory safety slave whose verification fails must halt the boot (no self-start, no broadcast), and a non-mandatory one must only be skipped (Task 10, both tests).

---

## File structure

**Create** (`src/CanKit.Pro.CANopen/Safety/`):
- `SrdoDirection.cs` — the `sub1` values.
- `SrdoMapping.cs` — public mapping builder (≤ 8 byte-aligned objects), reuses `PdoMappingEntry`.
- `SrdoCommunicationParameter.cs` — the typed `26h` record.
- `SrdoCrc.cs` — CRC-16/XMODEM primitive + canonical sequence of §8.4.2.2.
- `SrdoFrames.cs` — inversion, pair check, COB-ID rules of Figure 7 / Table 4.
- `SrdoRecords.cs` — internal OD helpers: indices, reading a record into typed values.
- `SrdoState.cs`, `SrdoInvalidReason.cs` — public state snapshot.
- `SafetyEvents.cs` — the three event-args types.
- `ISrdoEngineHost.cs`, `SrdoEngine.cs` — the runtime.
- `ICanOpenSafety.cs`, `CanOpenSafetyExtensions.cs` — the facade.
- `PeerSafety.cs` — `PeerSafetyConfiguration`, `PeerSafetyResult`, `PeerSafetyMismatch`, `ForeignSrdoObserveResult`.

**Create** (`src/CanKit.Pro.CANopen/`): `CanOpenNode.Safety.cs` (device wiring, facade implementation, validator), `CanOpenNode.PeerSafety.cs` (§9.2, verification), `CanOpenNode.ForeignSrdo.cs`.

**Modify:** `CanOpenCobId.cs`, `CanOpenNodeOptions.cs`, `CanOpenNode.cs` (fields, constructor filter, `HandleIncoming`, `CleanUpOnActor`, `EventKey`), `CanOpenNode.CommunicationProfile.cs` (`Co`, populate, `IsManagedCommunicationObject`, `ValidateCommunicationWrite`, `OnOdEntryWrittenForCommunicationProfile`, `ApplyCommunicationObject`, `ApplyAllCommunicationObjects`, `ApplyNmtTransition`, `PerformNmtReset`), `CanOpenNode.Pdo.cs` (CoS pre-filter and evaluation), `CanOpenNode.DeviceDescription.cs` (loader), `CanOpenNode.BootUp.cs` (step D), `Nmt/FlyingMaster.cs` (signal), `ForeignPdo.cs` (`ForeignPdoKind.Srdo`), `Sdo/SdoBlockFrames.cs` (delegate CRC), README, csproj, SRS, arc42, `docs/packages/index.md`, root README.

**Tests** (`tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/`): `SafetyTypesTests.cs`, `SrdoCrcTests.cs`, `SrdoFramesTests.cs`, `SrdoRecordsTests.cs`, `CanOpenSafetyCommunicationProfileTests.cs`, `SrdoEngineProducerTests.cs`, `SrdoEngineConsumerTests.cs`, `CanOpenSafetyNodeTests.cs`, `CanOpenSafetyDeviceDescriptionTests.cs`, `CanOpenPeerSafetyTests.cs`, `CanOpenSafetyBootUpTests.cs`, `CanOpenForeignSrdoTests.cs`; fixture `TestCases/CANopen/Fixtures/safety.dcf` (the csproj already copies `Fixtures/*.dcf`).

Run one test class: `dotnet test tests/CanKit.Pro.Tests/CanKit.Pro.Tests.csproj -c Release --framework net10.0 --filter "FullyQualifiedName~<ClassName>"`. The project uses `InternalsVisibleTo("CanKit.Pro.Tests")`, so internal types are reachable from tests.

---

### Task 1: Public value types, COB-ID constants, options

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/SrdoDirection.cs`, `Safety/SrdoMapping.cs`, `Safety/SrdoCommunicationParameter.cs`, `Safety/SrdoInvalidReason.cs`, `Safety/SrdoState.cs`, `Safety/SafetyEvents.cs`
- Modify: `src/CanKit.Pro.CANopen/CanOpenCobId.cs` (after `HeartbeatBase`, line 86), `src/CanKit.Pro.CANopen/CanOpenNodeOptions.cs` (properties after `Profile`, `With`, `Validate`)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SafetyTypesTests.cs`

**Interfaces:**
- Produces: `enum SrdoDirection : byte { None = 0, Transmit = 1, Receive = 2 }`; `sealed class SrdoMapping { const int MaxEntries = 8; SrdoMapping Add(ushort index, byte subindex, byte bitLength); SrdoMapping Add(PdoMappingEntry); IReadOnlyList<PdoMappingEntry> Entries; int TotalBytes; PdoMappingEntry[] ToArray(); internal static SrdoMapping FromEntries(PdoMappingEntry[]) }`; `readonly record struct SrdoCommunicationParameter(SrdoDirection Direction, TimeSpan RefreshOrSafeguardCycleTime, TimeSpan ValidationTime, uint CobId1, uint CobId2)`; `enum SrdoInvalidReason { NotReceived, NotOperational, ConfigurationInvalid, SafeguardCycleExpired, ValidationTimeExpired, OutOfOrder, Mismatch }`; `sealed class SrdoState { int SrdoNumber; SrdoDirection Direction; bool IsValid; SrdoInvalidReason? Reason; DateTime? LastValidAt; internal ctor }`; `SrdoReceivedEventArgs(int srdoNumber, uint cobId, byte[] payload, DateTime timestamp)`, `SrdoStateChangedEventArgs(int srdoNumber, bool isValid, SrdoInvalidReason? reason, DateTime timestamp)`, `GlobalFailsafeCommandReceivedEventArgs(DateTime timestamp)`; `CanOpenCobId.GlobalFailsafeCommand = 0x001`, `SrdoFirstCobId = 0x101`, `SrdoLastCobId = 0x180`, `uint SrdoDefaultCobId1(byte nodeId)`, `uint SrdoDefaultCobId2(byte nodeId)`; `CanOpenNodeOptions.SrdoCount` (int, 0..64, default 0), `EnableChangeOfStateSrdo` (bool, default true), both in `With(...)` and `Validate()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SafetyTypesTests.cs
using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The value types of CiA DSP 304 V1.0: the pre-defined connection set (§8.3.3
/// Table 4), the mapping builder (§8.4.2.2, 8 byte-aligned objects) and the node options.</summary>
public class SafetyTypesTests
{
    [Theory]
    [InlineData(1, 0x101u, 0x102u)]
    [InlineData(32, 0x13Fu, 0x140u)]
    [InlineData(33, 0x141u, 0x142u)]
    [InlineData(64, 0x17Fu, 0x180u)]
    public void Default_CobIds_Follow_Table_4(byte nodeId, uint cobId1, uint cobId2)
    {
        CanOpenCobId.SrdoDefaultCobId1(nodeId).Should().Be(cobId1, "COB-ID 1 = FFh + 2 × node-id");
        CanOpenCobId.SrdoDefaultCobId2(nodeId).Should().Be(cobId2, "COB-ID 2 = 100h + 2 × node-id");
    }

    [Theory]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(0)]
    public void Node_Ids_Above_64_Have_No_Default_CobId(byte nodeId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanOpenCobId.SrdoDefaultCobId1(nodeId));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanOpenCobId.SrdoDefaultCobId2(nodeId));
    }

    [Fact]
    public void Mapping_Holds_At_Most_Eight_Byte_Aligned_Objects()
    {
        var mapping = new SrdoMapping();
        for (ushort i = 0; i < 8; i++) mapping.Add((ushort)(0x2000 + i), 0x00, 8);
        mapping.TotalBytes.Should().Be(8);
        Assert.Throws<InvalidOperationException>(() => mapping.Add(0x2008, 0x00, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SrdoMapping().Add(0x2000, 0x00, 12));
        Assert.Throws<InvalidOperationException>(() => new SrdoMapping().Add(0x2000, 0x00, 64).Add(0x2001, 0x00, 8));
    }

    [Fact]
    public void Options_Validate_SrdoCount()
    {
        new CanOpenNodeOptions().SrdoCount.Should().Be(0);
        new CanOpenNodeOptions().EnableChangeOfStateSrdo.Should().BeTrue();
        new CanOpenNodeOptions { SrdoCount = 64 }.With(enableChangeOfStateSrdo: false).SrdoCount.Should().Be(64);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanOpenNodeOptions { SrdoCount = 65 }.With());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanOpenNodeOptions { SrdoCount = -1 }.With());
    }

    [Fact]
    public void State_And_Events_Carry_Their_Values()
    {
        var ts = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var received = new SrdoReceivedEventArgs(3, 0x105, new byte[] { 1, 2 }, ts);
        received.SrdoNumber.Should().Be(3);
        received.CobId.Should().Be(0x105u);
        received.Payload.Should().Equal(1, 2);
        var changed = new SrdoStateChangedEventArgs(3, false, SrdoInvalidReason.Mismatch, ts);
        changed.IsValid.Should().BeFalse();
        changed.Reason.Should().Be(SrdoInvalidReason.Mismatch);
        new GlobalFailsafeCommandReceivedEventArgs(ts).Timestamp.Should().Be(ts);
    }
}
```

Note: `With()` must call `Validate()` for the option test to work — see Step 3.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CanKit.Pro.Tests/CanKit.Pro.Tests.csproj -c Release --framework net10.0 --filter "FullyQualifiedName~SafetyTypesTests"`
Expected: build error — `SrdoMapping`, `SrdoDefaultCobId1`, `SrdoCount` do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoDirection.cs
namespace CanKit.Pro.CANopen.Safety;

/// <summary>Information direction of an SRDO, the value of sub-index 1 of its communication
/// parameter record (CiA DSP 304 V1.0 §8.4.2.2, object 1301h–1340h).</summary>
public enum SrdoDirection : byte
{
    /// <summary>0: the SRDO does not exist / is not valid.</summary>
    None = 0,
    /// <summary>1: exists, this node is its producer (tx).</summary>
    Transmit = 1,
    /// <summary>2: exists, this node is a consumer (rx).</summary>
    Receive = 2,
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoMapping.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The application objects an SRDO carries (CiA DSP 304 V1.0 §8.4.2.2, object 1381h–13C0h):
/// up to <see cref="MaxEntries"/> byte-aligned entries whose widths sum to at most 8 bytes —
/// the plain-data frame. The record in the dictionary holds each entry twice (odd sub-index
/// plain, even sub-index inverted); this type names the objects once.
/// </summary>
public sealed class SrdoMapping
{
    /// <summary>8 one-byte objects fill the frame; the record therefore has 16 sub-indices.</summary>
    public const int MaxEntries = 8;

    private readonly List<PdoMappingEntry> _entries = new();
    private ReadOnlyCollection<PdoMappingEntry>? _view;

    /// <summary>An empty mapping.</summary>
    public SrdoMapping() { }

    /// <summary>The entries, in frame order.</summary>
    public IReadOnlyList<PdoMappingEntry> Entries => _view ??= _entries.AsReadOnly();

    /// <summary>Length of the plain-data frame in bytes.</summary>
    public int TotalBytes
    {
        get
        {
            int total = 0;
            foreach (var entry in _entries) total += entry.ByteLength;
            return total;
        }
    }

    /// <summary>Appends an object. Dummy entries (0002h–0007h) are not accepted: safety data is
    /// never padding.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not a byte multiple of 8..64 bits.</exception>
    /// <exception cref="InvalidOperationException">A ninth entry, a dummy, or more than 8 bytes.</exception>
    public SrdoMapping Add(ushort index, byte subindex, byte bitLength) => Add(new PdoMappingEntry(index, subindex, bitLength));

    /// <inheritdoc cref="Add(ushort, byte, byte)"/>
    public SrdoMapping Add(PdoMappingEntry entry)
    {
        if (entry.IsDummy) throw new InvalidOperationException("An SRDO mapping carries no dummy entries.");
        if (_entries.Count >= MaxEntries) throw new InvalidOperationException($"An SRDO mapping holds at most {MaxEntries} entries.");
        if (TotalBytes + entry.ByteLength > 8) throw new InvalidOperationException("An SRDO carries at most 8 bytes of plain data (§8.1.3.1).");
        _entries.Add(entry);
        return this;
    }

    /// <summary>The entries as an array (a copy).</summary>
    public PdoMappingEntry[] ToArray() => _entries.ToArray();

    internal static SrdoMapping FromEntries(PdoMappingEntry[] entries)
    {
        var mapping = new SrdoMapping();
        foreach (var entry in entries) mapping.Add(entry);
        return mapping;
    }
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoCommunicationParameter.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>The SRDO communication parameter record, data type 26h (CiA DSP 304 V1.0 §8.4.1.1
/// Table 5): direction (sub1), refresh time for a producer or SCT for a consumer (sub2), SRVT
/// (sub3, consumer only), and the two COB-IDs (sub5, sub6). Sub4, the transmission type, is
/// the constant 254 and is not part of this record.</summary>
/// <param name="Direction">Sub-index 1.</param>
/// <param name="RefreshOrSafeguardCycleTime">Sub-index 2, whole milliseconds 1..65535.</param>
/// <param name="ValidationTime">Sub-index 3, whole milliseconds 1..255; ignored for a producer.</param>
/// <param name="CobId1">Sub-index 5, the odd CAN-ID of the plain-data frame (257, 259..383).</param>
/// <param name="CobId2">Sub-index 6, the even CAN-ID of the inverted frame (258, 260..384).</param>
public readonly record struct SrdoCommunicationParameter(
    SrdoDirection Direction,
    TimeSpan RefreshOrSafeguardCycleTime,
    TimeSpan ValidationTime,
    uint CobId1,
    uint CobId2);
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoInvalidReason.cs
namespace CanKit.Pro.CANopen.Safety;

/// <summary>Why an SRDO is not valid (CiA DSP 304 V1.0 §8.1.1, §8.1.3.1, §9.5).</summary>
public enum SrdoInvalidReason
{
    /// <summary>Consumer: no complete pair since entering Operational.</summary>
    NotReceived,
    /// <summary>The node is not in NMT state Operational; SRDOs exist only there (§8.3.2).</summary>
    NotOperational,
    /// <summary>13FEh is not A5h, or 13FFh does not match the record (§8.3.1 D, §9.5).</summary>
    ConfigurationInvalid,
    /// <summary>Consumer: no valid pair within the SCT (§8.1.1 Figure 2).</summary>
    SafeguardCycleExpired,
    /// <summary>Consumer: the second frame did not follow the first within the SRVT (§8.1.1 Figure 3).</summary>
    ValidationTimeExpired,
    /// <summary>Consumer: the inverted frame arrived without a preceding plain frame (§9.5).</summary>
    OutOfOrder,
    /// <summary>Consumer: the second frame is not the bitwise inverse of the first (§8.1, §9.5).</summary>
    Mismatch,
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoState.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>A snapshot of one SRDO's validity. For a consumer, valid means the last pair was
/// complete, correct and in time; for a producer, valid means the node is Operational with a
/// valid configuration and is transmitting the cycle.</summary>
public sealed class SrdoState
{
    internal SrdoState(int srdoNumber, SrdoDirection direction, bool isValid, SrdoInvalidReason? reason, DateTime? lastValidAt)
    {
        SrdoNumber = srdoNumber;
        Direction = direction;
        IsValid = isValid;
        Reason = reason;
        LastValidAt = lastValidAt;
    }

    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>Sub-index 1 of the record.</summary>
    public SrdoDirection Direction { get; }
    /// <summary>See the class summary.</summary>
    public bool IsValid { get; }
    /// <summary>Why not, when <see cref="IsValid"/> is false and the SRDO exists; null for an SRDO with direction <see cref="SrdoDirection.None"/>.</summary>
    public SrdoInvalidReason? Reason { get; }
    /// <summary>Consumer: when the last valid pair was accepted (UTC).</summary>
    public DateTime? LastValidAt { get; }
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SafetyEvents.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>A consumer SRDO received a complete, correct pair in time; the plain data was written
/// to the mapped objects (CiA DSP 304 V1.0 §8.1.3.1, "Indication").</summary>
public sealed class SrdoReceivedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public SrdoReceivedEventArgs(int srdoNumber, uint cobId, byte[] payload, DateTime timestamp)
    {
        SrdoNumber = srdoNumber;
        CobId = cobId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        Timestamp = timestamp;
    }
    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>COB-ID 1, the plain-data frame's id.</summary>
    public uint CobId { get; }
    /// <summary>The plain data (0..8 bytes).</summary>
    public byte[] Payload { get; }
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}

/// <summary>An SRDO became valid or invalid. Delivered on transitions only, never dropped
/// (a critical event like a heartbeat timeout).</summary>
public sealed class SrdoStateChangedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public SrdoStateChangedEventArgs(int srdoNumber, bool isValid, SrdoInvalidReason? reason, DateTime timestamp)
    {
        SrdoNumber = srdoNumber;
        IsValid = isValid;
        Reason = reason;
        Timestamp = timestamp;
    }
    /// <summary>1..64.</summary>
    public int SrdoNumber { get; }
    /// <summary>The new validity.</summary>
    public bool IsValid { get; }
    /// <summary>Why, when invalid.</summary>
    public SrdoInvalidReason? Reason { get; }
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}

/// <summary>A global failsafe command (COB-ID 001h, DLC 0) was received while 1300h is 1
/// (CiA DSP 304 V1.0 §8.2). On a bus that echoes, the node's own GFC arrives here too.</summary>
public sealed class GlobalFailsafeCommandReceivedEventArgs : EventArgs
{
    /// <summary>Constructs the event.</summary>
    public GlobalFailsafeCommandReceivedEventArgs(DateTime timestamp) => Timestamp = timestamp;
    /// <summary>UTC.</summary>
    public DateTime Timestamp { get; }
}
```

In `CanOpenCobId.cs`, after `HeartbeatBase` (line 86):

```csharp
    /// <summary>Global failsafe command, CiA DSP 304 V1.0 §8.2.3 / Table 3: COB-ID 001h, DLC 0.</summary>
    public const uint GlobalFailsafeCommand = 0x001;

    /// <summary>First CAN-ID of the SRDO range (CiA DSP 304 V1.0 Table 4; CiA 301 Table 40 restricts 101h–180h for this use).</summary>
    public const uint SrdoFirstCobId = 0x101;

    /// <summary>Last CAN-ID of the SRDO range.</summary>
    public const uint SrdoLastCobId = 0x180;

    /// <summary>COB-ID 1 of the first SRDO of a node in the pre-defined connection set: FFh + 2 × node-id (Table 4, object 1301h:05 default).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Node-ids above 64 have no pre-defined SRDO COB-ID (§8.3.3).</exception>
    public static uint SrdoDefaultCobId1(byte nodeId)
    {
        if (nodeId is < MinNodeId or > 64)
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "Only node-ids 1..64 have a pre-defined SRDO COB-ID (CiA DSP 304 §8.3.3).");
        return 0x0FFu + 2u * nodeId;
    }

    /// <summary>COB-ID 2 of the first SRDO: 100h + 2 × node-id (Table 4, object 1301h:06 default).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Node-ids above 64 have no pre-defined SRDO COB-ID (§8.3.3).</exception>
    public static uint SrdoDefaultCobId2(byte nodeId) => SrdoDefaultCobId1(nodeId) + 1;
```

In `CanOpenNodeOptions.cs`, after `Profile` (line 157):

```csharp
    /// <summary>
    /// How many SRDOs (CiA DSP 304 V1.0) this node implements, 0..64. With 0 — the default — the
    /// node creates none of the safety objects 1300h–13FFh and is a plain CiA 301 node
    /// (§9.4: "The implementation of CANopen Safety shall be allowed only in safety devices").
    /// A device description that declares SRDO records raises the count to the highest record
    /// it declares. The safety API is reached through <c>node.Safety()</c>.
    /// </summary>
    public int SrdoCount { get; init; }

    /// <summary>A write by the application to an object mapped in a transmit SRDO transmits the
    /// SRDO at once (CiA DSP 304 V1.0 §8.1, "event-driven … to ensure fast reaction"), like
    /// <see cref="EnableChangeOfStateTpdo"/>. The refresh cycle restarts from that transmission.</summary>
    public bool EnableChangeOfStateSrdo { get; init; } = true;
```

Add to `With(...)` two parameters `int? srdoCount = null, bool? enableChangeOfStateSrdo = null` (after `profile`) and the two assignments `SrdoCount = srdoCount ?? SrdoCount, EnableChangeOfStateSrdo = enableChangeOfStateSrdo ?? EnableChangeOfStateSrdo`; make `With` end with `options.Validate(); return options;` (assign the object to a local `options` first). Add to `Validate()`:

```csharp
        if (SrdoCount is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(SrdoCount), SrdoCount,
                "SrdoCount must be 0..64: CiA DSP 304 limits a network to 64 SRDOs (§3).");
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the same command. Expected: 5 tests PASS. Then run the whole CANopen suite once (`--filter "FullyQualifiedName~CANopen"`) to see that `With()` now validating broke nothing.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/Safety src/CanKit.Pro.CANopen/CanOpenCobId.cs src/CanKit.Pro.CANopen/CanOpenNodeOptions.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SafetyTypesTests.cs
git commit -m "feat(canopen): add the CiA 304 value types, SRDO COB-ID defaults and SrdoCount option"
```

---

### Task 2: `SrdoCrc`

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs`
- Modify: `src/CanKit.Pro.CANopen/Sdo/SdoBlockFrames.cs:243-256` (delegate `ComputeCrc16Xmodem` to `SrdoCrc.Crc16Xmodem`)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoCrcTests.cs`

**Interfaces:**
- Consumes: `SrdoCommunicationParameter`, `SrdoMapping` (Task 1).
- Produces: `public static class SrdoCrc { ushort Compute(in SrdoCommunicationParameter parameter, SrdoMapping mapping); ushort Crc16Xmodem(ReadOnlySpan<byte> data); internal static byte[] CanonicalBytes(in SrdoCommunicationParameter, SrdoMapping) }`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoCrcTests.cs
using System;
using System.Text;
using AwesomeAssertions;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>13FFh (CiA DSP 304 V1.0 §8.4.2.2): generator x^16+x^12+x^5+1 over the listed
/// field order. V1.0 names no initial value and no byte order; the record (spec decision) is
/// CRC-16/XMODEM, multi-byte fields MSB-first. The canonical sequence is pinned here as a
/// golden vector so that #289 can tell a deliberate change from an accident.</summary>
public class SrdoCrcTests
{
    private static readonly SrdoCommunicationParameter Parameter = new(
        SrdoDirection.Transmit, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(20), 0x101, 0x102);

    [Fact]
    public void Primitive_Is_Crc16_Xmodem()
    {
        SrdoCrc.Crc16Xmodem(Encoding.ASCII.GetBytes("123456789")).Should().Be(0x31C3);
        SrdoCrc.Crc16Xmodem(ReadOnlySpan<byte>.Empty).Should().Be(0x0000);
    }

    [Fact]
    public void Canonical_Sequence_Follows_The_Listed_Order()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        SrdoCrc.CanonicalBytes(Parameter, mapping).Should().Equal(
            0x01,                         // a) direction
            0x00, 0x19,                   // b) refresh time 25 ms, MSB first
            0x14,                         // c) SRVT 20 ms
            0x00, 0x00, 0x01, 0x01,       // d) COB-ID 1
            0x00, 0x00, 0x01, 0x02,       // e) COB-ID 2
            0x02,                         // f) mapping sub0: two entries (one object, plain + inverted)
            0x01, 0x20, 0x00, 0x01, 0x10, // g1/h1) sub-index 1, 0x20000110
            0x02, 0x20, 0x00, 0x01, 0x10);// g2/h2) sub-index 2, same object, inverted slot
    }

    [Fact]
    public void Compute_Is_The_Primitive_Over_The_Canonical_Sequence()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        SrdoCrc.Compute(Parameter, mapping).Should().Be(SrdoCrc.Crc16Xmodem(SrdoCrc.CanonicalBytes(Parameter, mapping)));
        SrdoCrc.Compute(Parameter, new SrdoMapping()).Should().Be(SrdoCrc.Crc16Xmodem(new byte[] { 0x01, 0x00, 0x19, 0x14, 0, 0, 1, 1, 0, 0, 1, 2, 0x00 }));
    }

    [Fact]
    public void Every_Field_Changes_The_Checksum()
    {
        var mapping = new SrdoMapping().Add(0x2000, 0x01, 16);
        ushort reference = SrdoCrc.Compute(Parameter, mapping);
        SrdoCrc.Compute(Parameter with { Direction = SrdoDirection.Receive }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { RefreshOrSafeguardCycleTime = TimeSpan.FromMilliseconds(26) }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { ValidationTime = TimeSpan.FromMilliseconds(21) }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { CobId1 = 0x103 }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter with { CobId2 = 0x104 }, mapping).Should().NotBe(reference);
        SrdoCrc.Compute(Parameter, new SrdoMapping().Add(0x2000, 0x02, 16)).Should().NotBe(reference);
    }
}
```

- [ ] **Step 2: Run to verify failure** — compile error, `SrdoCrc` missing.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs
using System;
using System.Collections.Generic;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The safety configuration checksum of object 13FFh (CiA DSP 304 V1.0 §8.4.2.2). The norm
/// gives the generator polynomial x^16 + x^12 + x^5 + 1 and the order of the fields; it names
/// no initial value and no byte order. This implementation uses CRC-16/XMODEM (initial value
/// 0000h, no reflection, no final XOR — the algorithm CiA 301 §7.2.4.3.16 prescribes for the
/// SDO block transfer) and feeds multi-byte fields MSB-first, the order in which D(x) lists
/// their bits (b15 … b0). Both choices are recorded in
/// docs/reviews/2026-10-09-canopen-safety-scope.md and are reconciled against EN 50325-5 in #289.
/// </summary>
public static class SrdoCrc
{
    /// <summary>The checksum the device compares at the transition to Operational and the tool
    /// writes to 13FFh:n (§8.3.1 step D, §9.2).</summary>
    public static ushort Compute(in SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        return Crc16Xmodem(CanonicalBytes(parameter, mapping));
    }

    /// <summary>CRC-16/XMODEM: poly 1021h, init 0000h, no reflection, no xor-out. Check value of
    /// "123456789" is 31C3h.</summary>
    public static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                else crc <<= 1;
            }
        }
        return crc;
    }

    /// <summary>§8.4.2.2 "The order for data which are checked by the CRC": a) direction (1),
    /// b) refresh time / SCT (2), c) SRVT (1), d) COB-ID 1 (4), e) COB-ID 2 (4), f) mapping
    /// sub-index 0 (1), then per mapping entry g) its sub-index (1) and h) its value (4). The
    /// record form holds each object twice, plain at the odd and inverted at the even sub-index,
    /// with the same mapping value.</summary>
    internal static byte[] CanonicalBytes(in SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        var bytes = new List<byte>(13 + 10 * mapping.Entries.Count);
        bytes.Add((byte)parameter.Direction);
        ushort cycle = (ushort)Math.Round(parameter.RefreshOrSafeguardCycleTime.TotalMilliseconds);
        bytes.Add((byte)(cycle >> 8));
        bytes.Add((byte)cycle);
        bytes.Add((byte)Math.Round(parameter.ValidationTime.TotalMilliseconds));
        AddU32(bytes, parameter.CobId1);
        AddU32(bytes, parameter.CobId2);
        bytes.Add((byte)(2 * mapping.Entries.Count));
        byte sub = 1;
        foreach (var entry in mapping.Entries)
        {
            uint value = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
            bytes.Add(sub++);
            AddU32(bytes, value);
            bytes.Add(sub++);
            AddU32(bytes, value);
        }
        return bytes.ToArray();
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
```

In `SdoBlockFrames.cs` replace the body of `ComputeCrc16Xmodem` with `=> Safety.SrdoCrc.Crc16Xmodem(data);` and keep its summary (one implementation, two citations).

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~SrdoCrcTests|FullyQualifiedName~CanOpenSdoCorrectnessTests"` — all PASS (the block-transfer CRC test still pins 31C3h).

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/Safety/SrdoCrc.cs src/CanKit.Pro.CANopen/Sdo/SdoBlockFrames.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoCrcTests.cs
git commit -m "feat(canopen): compute the 13FFh safety configuration checksum of CiA 304"
```

---

### Task 3: `SrdoFrames` and `SrdoRecords`

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/SrdoFrames.cs`, `src/CanKit.Pro.CANopen/Safety/SrdoRecords.cs`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoFramesTests.cs`, `SrdoRecordsTests.cs`

**Interfaces:**
- Consumes: `ObjectDictionary` (`AddU8/AddU16/AddU32`, `TryReadUnsigned`), `PdoMappingEntry`.
- Produces (both `internal static`):
  - `SrdoFrames.Invert(byte[] data) : byte[]`, `IsInversePair(byte[] first, byte[] second) : bool`, `IsCobId1(uint canId) : bool` (odd, 257..383), `IsCobId2(uint canId) : bool` (even, 258..384).
  - `SrdoRecords`: `const ushort GfcParameter = 0x1300, CommunicationBase = 0x1300, MappingBase = 0x1380, ConfigurationValid = 0x13FE, Checksum = 0x13FF; const byte ConfigurationValidValue = 0xA5; const int MaxSrdoCount = 64, MappingSubindices = 16; const byte TransmissionType = 254;` `ushort CommIndex(int n)`, `ushort MapIndex(int n)`, `bool IsCommunicationRecord(ushort)`, `bool IsMappingRecord(ushort)`, `bool IsSafetyObject(ushort)` (1300h, records, 13FEh, 13FFh), `bool IsStateGated(ushort)` (records, 13FEh, 13FFh — not 1300h), `bool IsChecksummed(ushort)` (records and 13FFh), `int? SrdoNumberOf(ushort index)`, `bool TryReadCommunication(ObjectDictionary od, int n, out SrdoCommunicationParameter p)`, `PdoMappingEntry[] ReadMapping(ObjectDictionary od, int n)` (the odd entries; an empty array when sub0 is 0 or a slot is malformed), `bool IsConfigurationValid(ObjectDictionary od, int n)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoFramesTests.cs
using AwesomeAssertions;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>CiA DSP 304 V1.0 §8.1: the second frame is the first "inverted bitwise"; Figure 7 /
/// Table 4: COB-ID 1 is odd in 257..383, COB-ID 2 even in 258..384.</summary>
public class SrdoFramesTests
{
    [Fact]
    public void Invert_Flips_Every_Bit_And_Keeps_The_Length()
    {
        SrdoFrames.Invert(new byte[] { 0x00, 0xFF, 0x5A }).Should().Equal(0xFF, 0x00, 0xA5);
        SrdoFrames.Invert(new byte[0]).Should().BeEmpty();
    }

    [Fact]
    public void A_Pair_Is_Equal_Length_And_Bitwise_Inverse()
    {
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED, 0xCB }).Should().BeTrue();
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED, 0xCA }).Should().BeFalse("one bit differs");
        SrdoFrames.IsInversePair(new byte[] { 0x12, 0x34 }, new byte[] { 0xED }).Should().BeFalse("lengths differ");
        SrdoFrames.IsInversePair(new byte[0], new byte[0]).Should().BeTrue("L = 0 is allowed (§8.1.3.1)");
    }

    [Theory]
    [InlineData(0x101u, true, false)]
    [InlineData(0x102u, false, true)]
    [InlineData(0x17Fu, true, false)]
    [InlineData(0x180u, false, true)]
    [InlineData(0x0FFu, false, false)]
    [InlineData(0x181u, false, false)]
    [InlineData(0x182u, false, false)]
    public void CobId_Rules_Of_Figure_7(uint canId, bool isFirst, bool isSecond)
    {
        SrdoFrames.IsCobId1(canId).Should().Be(isFirst);
        SrdoFrames.IsCobId2(canId).Should().Be(isSecond);
    }
}
```

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoRecordsTests.cs
using System;
using AwesomeAssertions;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Reading an SRDO's records out of a plain dictionary, without a node.</summary>
public class SrdoRecordsTests
{
    private static ObjectDictionary RecordFor(int n, byte direction, ushort cycle, byte srvt, uint cob1, uint cob2, params uint[] mappingSlots)
    {
        var od = new ObjectDictionary();
        var comm = SrdoRecords.CommIndex(n);
        var map = SrdoRecords.MapIndex(n);
        od.AddU8(comm, 0x00, 6); od.AddU8(comm, 0x01, direction); od.AddU16(comm, 0x02, cycle);
        od.AddU8(comm, 0x03, srvt); od.AddU8(comm, 0x04, 254); od.AddU32(comm, 0x05, cob1); od.AddU32(comm, 0x06, cob2);
        od.AddU8(map, 0x00, (byte)mappingSlots.Length);
        for (byte s = 1; s <= 16; s++) od.AddU32(map, s, s <= mappingSlots.Length ? mappingSlots[s - 1] : 0);
        od.AddU8(SrdoRecords.ConfigurationValid, 0, 0);
        od.AddU8(SrdoRecords.Checksum, 0, 1);
        od.AddU16(SrdoRecords.Checksum, (byte)n, 0);
        return od;
    }

    [Fact]
    public void Indices_And_Classification()
    {
        SrdoRecords.CommIndex(1).Should().Be((ushort)0x1301);
        SrdoRecords.CommIndex(64).Should().Be((ushort)0x1340);
        SrdoRecords.MapIndex(1).Should().Be((ushort)0x1381);
        SrdoRecords.MapIndex(64).Should().Be((ushort)0x13C0);
        SrdoRecords.SrdoNumberOf(0x1302).Should().Be(2);
        SrdoRecords.SrdoNumberOf(0x13C0).Should().Be(64);
        SrdoRecords.SrdoNumberOf(0x1300).Should().BeNull();
        SrdoRecords.SrdoNumberOf(0x13FE).Should().BeNull();
        SrdoRecords.IsSafetyObject(0x1300).Should().BeTrue();
        SrdoRecords.IsSafetyObject(0x1341).Should().BeFalse();
        SrdoRecords.IsSafetyObject(0x13FF).Should().BeTrue();
        SrdoRecords.IsStateGated(0x1300).Should().BeFalse("1300h is not a safety entry in the sense of §8.3.2.4 note 1");
        SrdoRecords.IsStateGated(0x13FE).Should().BeTrue();
        SrdoRecords.IsChecksummed(0x13FE).Should().BeFalse();
        SrdoRecords.IsChecksummed(0x13FF).Should().BeTrue();
        SrdoRecords.IsChecksummed(0x1381).Should().BeTrue();
    }

    [Fact]
    public void Reads_A_Record_Into_Typed_Values()
    {
        var od = RecordFor(1, 2, 50, 20, 0x101, 0x102, 0x20000110, 0x20000110);
        SrdoRecords.TryReadCommunication(od, 1, out var p).Should().BeTrue();
        p.Should().Be(new SrdoCommunicationParameter(SrdoDirection.Receive, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), 0x101, 0x102));
        var mapping = SrdoRecords.ReadMapping(od, 1);
        mapping.Should().HaveCount(1);
        mapping[0].Index.Should().Be((ushort)0x2000);
        mapping[0].Subindex.Should().Be((byte)0x01);
        mapping[0].BitLength.Should().Be((byte)16);
    }

    [Fact]
    public void Configuration_Is_Valid_Only_With_A5h_And_A_Matching_Checksum()
    {
        var od = RecordFor(1, 2, 50, 20, 0x101, 0x102, 0x20000110, 0x20000110);
        SrdoRecords.TryReadCommunication(od, 1, out var p);
        ushort crc = SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, 1)));
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("13FEh is 0");
        od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("13FFh is 0");
        od.WriteUnsigned(SrdoRecords.Checksum, 1, crc);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        od.WriteUnsigned(SrdoRecords.Checksum, 1, (uint)(crc ^ 1));
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeFalse("one bit of the checksum is wrong");
    }

    [Fact]
    public void A_Record_Without_A_Direction_Entry_Is_Absent()
    {
        SrdoRecords.TryReadCommunication(new ObjectDictionary(), 1, out _).Should().BeFalse();
        SrdoRecords.ReadMapping(new ObjectDictionary(), 1).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure** — compile errors.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoFrames.cs
namespace CanKit.Pro.CANopen.Safety;

/// <summary>The two CAN frames of an SRDO (CiA DSP 304 V1.0 §8.1, §8.1.3.1) and the COB-ID
/// rules of Figure 7 / Table 4.</summary>
internal static class SrdoFrames
{
    /// <summary>"the data on the 2nd transmission is inverted bitwise" (§8.1).</summary>
    public static byte[] Invert(byte[] data)
    {
        var inverted = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) inverted[i] = (byte)~data[i];
        return inverted;
    }

    /// <summary>§9.5: "compared bit by bit (modulo 2)". Equal length and every byte inverted.</summary>
    public static bool IsInversePair(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
        {
            if ((byte)~first[i] != second[i]) return false;
        }
        return true;
    }

    /// <summary>Sub-index 5 value range: 257, 259..383 — the odd ids of 101h..17Fh.</summary>
    public static bool IsCobId1(uint canId) => canId is >= 0x101 and <= 0x17F && (canId & 1) == 1;

    /// <summary>Sub-index 6 value range: 258, 260..384 — the even ids of 102h..180h.</summary>
    public static bool IsCobId2(uint canId) => canId is >= 0x102 and <= 0x180 && (canId & 1) == 0;
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoRecords.cs
using System;
using System.Collections.Generic;
using CanKit.Pro.CANopen.Pdo;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Where the safety objects live (CiA DSP 304 V1.0 §8.4.2.1 Table 6) and how to read
/// an SRDO's records out of the dictionary into typed values. Pure functions over an
/// <see cref="ObjectDictionary"/>, shared by the validator (writing thread) and the engine (actor).</summary>
internal static class SrdoRecords
{
    public const ushort GfcParameter = 0x1300;
    public const ushort CommunicationBase = 0x1300;   // record n at 1300h + n
    public const ushort MappingBase = 0x1380;         // record n at 1380h + n
    public const ushort ConfigurationValid = 0x13FE;
    public const ushort Checksum = 0x13FF;
    public const byte ConfigurationValidValue = 0xA5;
    public const int MaxSrdoCount = 64;
    /// <summary>8 objects, each plain (odd sub-index) and inverted (even sub-index).</summary>
    public const int MappingSubindices = 2 * SrdoMapping.MaxEntries;
    /// <summary>Sub-index 4 of every record: "defined as type 254" (§8.4.2.2).</summary>
    public const byte TransmissionType = 254;

    public static ushort CommIndex(int n) => (ushort)(CommunicationBase + n);
    public static ushort MapIndex(int n) => (ushort)(MappingBase + n);

    public static bool IsCommunicationRecord(ushort index) => index is > CommunicationBase and <= CommunicationBase + MaxSrdoCount;
    public static bool IsMappingRecord(ushort index) => index is > MappingBase and <= MappingBase + MaxSrdoCount;

    /// <summary>Table 6: 1300h, the records, 13FEh and 13FFh.</summary>
    public static bool IsSafetyObject(ushort index)
        => index == GfcParameter || index == ConfigurationValid || index == Checksum
           || IsCommunicationRecord(index) || IsMappingRecord(index);

    /// <summary>§8.3.2.4 note 1: "Writing to a safety entry in the OPERATIONAL state leads to an
    /// abort message" — the records, 13FEh and 13FFh. 1300h is not CRC-covered and not gated.</summary>
    public static bool IsStateGated(ushort index)
        => index == ConfigurationValid || index == Checksum || IsCommunicationRecord(index) || IsMappingRecord(index);

    /// <summary>§8.4.2.2 object 13FEh: "After a write access to the safety-relevant parameter
    /// the entry of object 13FEh is automatically 0" — the records and the checksums.</summary>
    public static bool IsChecksummed(ushort index)
        => index == Checksum || IsCommunicationRecord(index) || IsMappingRecord(index);

    public static int? SrdoNumberOf(ushort index)
    {
        if (IsCommunicationRecord(index)) return index - CommunicationBase;
        if (IsMappingRecord(index)) return index - MappingBase;
        return null;
    }

    public static bool TryReadCommunication(ObjectDictionary od, int n, out SrdoCommunicationParameter parameter)
    {
        var comm = CommIndex(n);
        parameter = default;
        if (!od.TryReadUnsigned(comm, 0x01, out var direction)) return false;
        od.TryReadUnsigned(comm, 0x02, out var cycle);
        od.TryReadUnsigned(comm, 0x03, out var srvt);
        od.TryReadUnsigned(comm, 0x05, out var cob1);
        od.TryReadUnsigned(comm, 0x06, out var cob2);
        parameter = new SrdoCommunicationParameter(
            direction <= 2 ? (SrdoDirection)direction : SrdoDirection.None,
            TimeSpan.FromMilliseconds(cycle), TimeSpan.FromMilliseconds(srvt),
            cob1 & CanOpenCobId.CanIdMask, cob2 & CanOpenCobId.CanIdMask);
        return true;
    }

    /// <summary>The objects of the mapping, read from the odd sub-indices. Empty when sub0 is 0
    /// or when any slot up to sub0 is empty or malformed (the validator refuses such a record;
    /// one can only get here through a re-declaration).</summary>
    public static PdoMappingEntry[] ReadMapping(ObjectDictionary od, int n)
    {
        var map = MapIndex(n);
        if (!od.TryReadUnsigned(map, 0x00, out var count) || count == 0 || count > MappingSubindices || (count & 1) != 0)
            return Array.Empty<PdoMappingEntry>();
        var entries = new List<PdoMappingEntry>((int)count / 2);
        for (byte s = 1; s <= count; s += 2)
        {
            if (!od.TryReadUnsigned(map, s, out var raw) || raw == 0) return Array.Empty<PdoMappingEntry>();
            try
            {
                entries.Add(new PdoMappingEntry((ushort)(raw >> 16), (byte)(raw >> 8), (byte)raw));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Array.Empty<PdoMappingEntry>();
            }
        }
        return entries.ToArray();
    }

    /// <summary>§9.5 last rule: "The CRC-Entry in the object dictionary shall be equal to the CRC
    /// calculation of the safety device and the configuration-valid flag shall be valid."</summary>
    public static bool IsConfigurationValid(ObjectDictionary od, int n)
    {
        if (!od.TryReadUnsigned(ConfigurationValid, 0x00, out var valid) || valid != ConfigurationValidValue) return false;
        if (!od.TryReadUnsigned(Checksum, (byte)n, out var stored)) return false;
        if (!TryReadCommunication(od, n, out var parameter)) return false;
        return stored == SrdoCrc.Compute(parameter, SrdoMapping.FromEntries(ReadMapping(od, n)));
    }
}
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~SrdoFramesTests|FullyQualifiedName~SrdoRecordsTests"` — PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/Safety/SrdoFrames.cs src/CanKit.Pro.CANopen/Safety/SrdoRecords.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoFramesTests.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoRecordsTests.cs
git commit -m "feat(canopen): SRDO frame pair rules and record readers"
```

---

### Task 4: The safety objects in the dictionary and their validator

**Files:**
- Create: `src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs` (this task: fields, populate, validator; later tasks add the facade)
- Modify: `src/CanKit.Pro.CANopen/CanOpenNode.cs:101` (`_state` becomes `volatile`), `:195-206` (constructor: compute `_srdoCount` before `PopulateCommunicationProfile()`); `src/CanKit.Pro.CANopen/CanOpenNode.CommunicationProfile.cs` — `Co` (line 44, add constants), `PopulateCommunicationProfile` (before `_od.DeclareGuard = …`, line 206), `IsManagedCommunicationObject` (line 216), `ValidateCommunicationWrite` (line 249, first line), `OnOdEntryWrittenForCommunicationProfile` (line 449)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyCommunicationProfileTests.cs`

**Interfaces:**
- Consumes: `SrdoRecords`, `SrdoFrames`, `SrdoCrc`, `CanOpenNodeOptions.SrdoCount`, `CanOpenCobId.SrdoDefaultCobId1/2`.
- Produces: node field `private readonly int _srdoCount;` (the effective count: `Math.Max(options.SrdoCount, DescribedSrdoCount(description))`; `DescribedSrdoCount` returns 0 in this task and is completed in Task 8); `private void PopulateSafetyObjects()`; `private OdWriteDecision ValidateSafetyWrite(ushort index, byte subindex, byte[] value)`; `private bool IsCanIdOfAnExistingSrdo(uint canId, int exceptSrdo)`; `private SdoAbortCode? ValidateSrdoMappingEntry(uint raw, SrdoDirection direction)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyCommunicationProfileTests.cs
using System;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The safety objects 1300h–13FFh as managed communication-profile objects (CiA DSP 304
/// V1.0 §8.4.2): their defaults, the value rules a write is held to on every path, the 13FEh
/// auto-reset, and that a node without SRDOs has none of them.</summary>
public class CanOpenSafetyCommunicationProfileTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Device = 0x11;

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static ICanOpenNode OpenDevice(ICanBus bus, int srdoCount = 2, byte nodeId = Device)
        => CanOpen.OpenNode(bus, nodeId, new CanOpenNodeOptions { SrdoCount = srdoCount, WritableCommunicationParameters = true });

    private static SdoAbortCode Rejected(Action write)
    {
        var ex = Assert.Throws<ArgumentException>(write);
        var hex = ex.Message.Substring(ex.Message.IndexOf("0x", StringComparison.Ordinal) + 2, 8);
        return (SdoAbortCode)Convert.ToUInt32(hex, 16);
    }

    [Fact]
    public void Without_Srdos_No_Safety_Object_Exists()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Device);
        node.ObjectDictionary.ContainsIndex(0x1300).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x1301).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x13FE).Should().BeFalse();
        node.ObjectDictionary.ContainsIndex(0x13FF).Should().BeFalse();
    }

    [Fact]
    public void Defaults_Of_Section_8_4_2_2()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus, srdoCount: 2);
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(0u);
        od.ReadUnsigned(0x1301, 0).Should().Be(6u);
        od.ReadUnsigned(0x1301, 1).Should().Be(0u, "not valid until configured");
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1301, 3).Should().Be(20u);
        od.ReadUnsigned(0x1301, 4).Should().Be(254u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x0FFu + 2 * Device);
        od.ReadUnsigned(0x1301, 6).Should().Be(0x100u + 2 * Device);
        od.ReadUnsigned(0x1302, 5).Should().Be(0u, "only the first SRDO has pre-defined COB-IDs");
        od.ReadUnsigned(0x1302, 6).Should().Be(0u);
        od.ReadUnsigned(0x1381, 0).Should().Be(0u);
        od.TryGet(0x1381, 16, out _).Should().BeTrue();
        od.TryGet(0x1381, 17, out _).Should().BeFalse("8 objects, plain and inverted");
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.ReadUnsigned(0x13FF, 0).Should().Be(2u);
        od.ReadUnsigned(0x13FF, 2).Should().Be(0u);
        od.ContainsIndex(0x1303).Should().BeFalse();
        od.ContainsIndex(0x1383).Should().BeFalse();
    }

    [Fact]
    public void A_Node_Above_64_Has_No_Default_CobIds()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus, srdoCount: 1, nodeId: 0x7F);
        node.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0u);
        node.ObjectDictionary.ReadUnsigned(0x1301, 6).Should().Be(0u);
    }

    [Theory]
    [InlineData(0x1301, 1, 3u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 2, 0u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 3, 0u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 4, 254u, SdoAbortCode.ValueRangeExceeded)]
    [InlineData(0x1301, 5, 0x102u, SdoAbortCode.ValueRangeExceeded)]   // even
    [InlineData(0x1301, 5, 0x0FFu, SdoAbortCode.ValueRangeExceeded)]   // below the range
    [InlineData(0x1301, 6, 0x101u, SdoAbortCode.ValueRangeExceeded)]   // odd
    [InlineData(0x1301, 6, 0x182u, SdoAbortCode.ValueRangeExceeded)]   // above the range
    [InlineData(0x1301, 6, 0x104u, SdoAbortCode.ValueRangeExceeded)]   // not COB-ID 1 + 1 (default 0x123)
    [InlineData(0x1301, 5, 0x8000_0121u, SdoAbortCode.ValueRangeExceeded)] // bits 31..11 reserved (Figure 7)
    public void Communication_Record_Value_Rules(ushort index, byte sub, uint value, SdoAbortCode expected)
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(index, sub, value)).Should().Be(expected);
    }

    [Fact]
    public void CobIds_Cannot_Change_While_The_Srdo_Exists()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1301, 1, 1);
        Rejected(() => od.WriteUnsigned(0x1301, 5, 0x103)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        Rejected(() => od.WriteUnsigned(0x1301, 6, 0x104)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        od.WriteUnsigned(0x1301, 1, 0);
        od.WriteUnsigned(0x1301, 5, 0x103);
        od.WriteUnsigned(0x1301, 6, 0x104);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x103u);
    }

    [Fact]
    public void Two_Srdos_Cannot_Share_A_CobId()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x1302, 5, 0x0FFu + 2 * Device);
        od.WriteUnsigned(0x1302, 6, 0x100u + 2 * Device);
        Rejected(() => od.WriteUnsigned(0x1302, 1, 2)).Should().Be(SdoAbortCode.ValueRangeExceeded,
            "SRDO 1 exists on these ids; one CAN-ID carries one communication object");
    }

    [Fact]
    public void A_Pdo_Cannot_Take_An_Srdo_CobId()
    {
        // CiA 301 Table 40 restricts 101h–180h; the PDO validator already refuses them. Pinned
        // here because it is the mirror image of Two_Srdos_Cannot_Share_A_CobId.
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1400, 1, 0x123)).Should().Be(SdoAbortCode.ValueRangeExceeded);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1005, 0, 0x124)).Should().Be(SdoAbortCode.ValueRangeExceeded);
    }

    [Fact]
    public void Mapping_Rules()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU16(0x2000, 0x00, 0);
        od.AddU8(0x2001, 0x00, 0);
        od.AddU32(0x2002, 0x00, 0, OdAccess.ReadWrite, pdoMappable: false);
        // Pairs: odd plain, even inverted, equal.
        od.WriteUnsigned(0x1381, 1, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 2, 0x2001_0008)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "sub 2 must repeat sub 1");
        od.WriteUnsigned(0x1381, 2, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 1)).Should().Be(SdoAbortCode.ValueRangeExceeded, "odd count");
        Rejected(() => od.WriteUnsigned(0x1381, 0, 4)).Should().Be(SdoAbortCode.ObjectDoesNotExist, "slots 3 and 4 are empty");
        od.WriteUnsigned(0x1381, 0, 2);
        // Not mappable, non-existent, wrong width, comm-profile area, dummy.
        od.WriteUnsigned(0x1381, 0, 0);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2002_0020)).Should().Be(SdoAbortCode.ObjectCannotBeMapped);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2FFF_0008)).Should().Be(SdoAbortCode.ObjectDoesNotExist);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x2001_000C)).Should().Be(SdoAbortCode.DataTypeLengthMismatch);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x1017_0010)).Should().Be(SdoAbortCode.ObjectCannotBeMapped);
        Rejected(() => od.WriteUnsigned(0x1381, 3, 0x0005_0008)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "no dummies in safety data");
        // Length: 8 bytes fit, a ninth does not.
        for (byte s = 1; s <= 16; s += 2) { od.WriteUnsigned(0x1381, s, 0x2001_0008); od.WriteUnsigned(0x1381, (byte)(s + 1), 0x2001_0008); }
        od.WriteUnsigned(0x1381, 0, 16);
        od.WriteUnsigned(0x1381, 0, 0);
        od.WriteUnsigned(0x1381, 1, 0x2000_0010); od.WriteUnsigned(0x1381, 2, 0x2000_0010);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 16)).Should().Be(SdoAbortCode.PdoMappingLengthExceeded, "2 + 7 bytes");
        // Not while the SRDO exists.
        od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        Rejected(() => od.WriteUnsigned(0x1381, 0, 0)).Should().Be(SdoAbortCode.UnsupportedAccess);
        Rejected(() => od.WriteUnsigned(0x1381, 1, 0x2001_0008)).Should().Be(SdoAbortCode.UnsupportedAccess);
    }

    [Fact]
    public void Direction_Checks_The_Mapped_Objects_Access()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.AddU8(0x2000, 0x00, 0, OdAccess.ReadOnly);
        od.WriteUnsigned(0x1381, 1, 0x2000_0008); od.WriteUnsigned(0x1381, 2, 0x2000_0008); od.WriteUnsigned(0x1381, 0, 2);
        od.WriteUnsigned(0x1301, 1, 1);
        od.WriteUnsigned(0x1301, 1, 0);
        Rejected(() => od.WriteUnsigned(0x1301, 1, 2)).Should().Be(SdoAbortCode.ObjectCannotBeMapped, "a consumer writes the object; it is read-only");
    }

    [Fact]
    public void Creating_An_Srdo_Needs_Both_CobIds()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        Rejected(() => node.ObjectDictionary.WriteUnsigned(0x1302, 1, 1)).Should().Be(SdoAbortCode.ValueRangeExceeded, "COB-IDs of SRDO 2 are 0 (disabled)");
    }

    [Fact]
    public void Checksum_Write_Clears_Configuration_Valid()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = OpenDevice(bus);
        var od = node.ObjectDictionary;
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        od.WriteUnsigned(0x13FF, 1, 0x1234);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "§8.4.2.2: automatically 0 after a write to a safety-relevant parameter");
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1301, 2, 30);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1381, 1, 0);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u);
        od.WriteUnsigned(0x13FE, 0, 0xA5);
        od.WriteUnsigned(0x1300, 0, 1);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "1300h is not CRC-covered");
        Rejected(() => od.WriteUnsigned(0x13FF, 0, 3)).Should().Be(SdoAbortCode.AttemptWriteReadOnly);
        Rejected(() => od.WriteUnsigned(0x1300, 0, 2)).Should().Be(SdoAbortCode.ValueRangeExceeded);
    }

    [Fact]
    public void Read_Only_On_The_Bus_Without_WritableCommunicationParameters()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-od");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Device, new CanOpenNodeOptions { SrdoCount = 1 });
        node.ObjectDictionary.TryGet(0x1301, 1, out var entry).Should().BeTrue();
        entry.Access.Should().Be(OdAccess.ReadOnly);
        node.ObjectDictionary.TryGet(0x13FE, 0, out entry);
        entry.Access.Should().Be(OdAccess.ReadOnly);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `Without_Srdos_No_Safety_Object_Exists` passes already (nothing exists); the rest fail with `KeyNotFoundException` on `0x1301`.

- [ ] **Step 3: Implement**

In `CanOpenNode.cs` line 101: `private volatile NmtState _state = NmtState.Initializing;` (the safety validator reads it on the writing thread).

In the constructor (line 195 ff.), before `PopulateCommunicationProfile();` insert:

```csharp
        _srdoCount = Math.Max(_options.SrdoCount, DescribedSrdoCount(description));
```

In `CanOpenNode.CommunicationProfile.cs` class `Co` add:

```csharp
        public const ushort GfcParameter = Safety.SrdoRecords.GfcParameter;
        public const ushort SrdoConfigurationValid = Safety.SrdoRecords.ConfigurationValid;
        public const ushort SrdoChecksum = Safety.SrdoRecords.Checksum;
```

In `PopulateCommunicationProfile`, before `_od.DeclareGuard = …`: `if (_srdoCount > 0) PopulateSafetyObjects();`

`IsManagedCommunicationObject`: add the arm `_ when Safety.SrdoRecords.IsSafetyObject(index) => true,` before `_ => false`.

`ValidateCommunicationWrite`: first statement `if (Safety.SrdoRecords.IsSafetyObject(index)) return ValidateSafetyWrite(index, subindex, value);`.

`OnOdEntryWrittenForCommunicationProfile`, after the TPDO CoS block:

```csharp
        // CiA DSP 304 §8.4.2.2, 13FEh: "After a write access to the safety-relevant parameter the
        // entry of object 13FEh is automatically 0". Still inside the write gate, so the next
        // write cannot see A5h beside a changed parameter. Unchecked: the reset is not a
        // configuration write and must not itself be refused in Operational (nothing reaches here
        // in Operational anyway, the validator refuses the write that would).
        if (Safety.SrdoRecords.IsChecksummed(index) && _od.TryGet(Co.SrdoConfigurationValid, 0, out _))
            _od.WriteRawUnchecked(Co.SrdoConfigurationValid, 0, new byte[] { 0 });
```

New file:

```csharp
// src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs
using System;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;

namespace CanKit.Pro.CANopen;

/// <summary>
/// CANopen Safety (CiA DSP 304 V1.0) on <see cref="CanOpenNode"/>: the objects 1300h–13FFh as
/// managed communication-profile objects, their validation, and — in later parts of this file —
/// the <see cref="ICanOpenSafety"/> facade and the wiring of <see cref="SrdoEngine"/>.
/// Opt-in: with <c>_srdoCount == 0</c> none of this exists in the dictionary.
/// </summary>
internal sealed partial class CanOpenNode
{
    private readonly int _srdoCount;

    /// <summary>The highest SRDO record a description declares; 0 without one. Completed when the
    /// loader learns the safety objects (device-description task).</summary>
    private static int DescribedSrdoCount(CanOpenDeviceDescription? description) => 0;

    /// <summary>§8.4.2.2 defaults. Bus access follows WritableCommunicationParameters, as the PDO
    /// records do (Table 6 footnote: "These may be read only"); sub0 and sub4 are const.</summary>
    private void PopulateSafetyObjects()
    {
        var acc = _options.WritableCommunicationParameters ? OdAccess.ReadWrite : OdAccess.ReadOnly;
        const OdAccess ro = OdAccess.ReadOnly;
        const bool nm = false;
        _od.AddU8(Co.GfcParameter, 0x00, 0, acc, nm);
        for (int n = 1; n <= _srdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            bool predefined = n == 1 && _nodeId <= 64;
            _od.AddU8(comm, 0x00, 6, ro, nm);
            _od.AddU8(comm, 0x01, (byte)SrdoDirection.None, acc, nm);
            _od.AddU16(comm, 0x02, 25, acc, nm);
            _od.AddU8(comm, 0x03, 20, acc, nm);
            _od.AddU8(comm, 0x04, SrdoRecords.TransmissionType, ro, nm);
            _od.AddU32(comm, 0x05, predefined ? CanOpenCobId.SrdoDefaultCobId1(_nodeId) : 0u, acc, nm);
            _od.AddU32(comm, 0x06, predefined ? CanOpenCobId.SrdoDefaultCobId2(_nodeId) : 0u, acc, nm);
            _od.AddU8(map, 0x00, 0, acc, nm);
            for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++) _od.AddU32(map, s, 0, acc, nm);
        }
        _od.AddU8(Co.SrdoConfigurationValid, 0x00, 0, acc, nm);
        _od.AddU8(Co.SrdoChecksum, 0x00, (byte)_srdoCount, ro, nm);
        for (byte n = 1; n <= _srdoCount; n++) _od.AddU16(Co.SrdoChecksum, n, 0, acc, nm);
    }

    // =========================================================================================
    // Validation (writing thread, before the store). Every rule cites DSP 304 V1.0.
    // =========================================================================================

    private OdWriteDecision ValidateSafetyWrite(ushort index, byte subindex, byte[] value)
    {
        // §8.3.2.4 note 1: "Writing to a safety entry in the OPERATIONAL state leads to an abort
        // message (abort code: 0800 0022h). Reading … is allowed."
        if (SrdoRecords.IsStateGated(index) && _state == NmtState.Operational)
            return OdWriteDecision.Reject(SdoAbortCode.DataCannotBeTransferredDeviceState);
        if (index == Co.GfcParameter)
            return value[0] <= 1 ? OdWriteDecision.Accept : OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 0 not valid, 1 valid
        if (index == Co.SrdoConfigurationValid)
            return OdWriteDecision.Accept; // any value; only A5h means valid
        if (index == Co.SrdoChecksum)
            return subindex == 0 ? OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly) : OdWriteDecision.Accept;
        if (SrdoRecords.IsCommunicationRecord(index))
            return ValidateSrdoCommunicationWrite(index, subindex, value);
        return ValidateSrdoMappingWrite(index, subindex, value);
    }

    private OdWriteDecision ValidateSrdoCommunicationWrite(ushort index, byte subindex, byte[] value)
    {
        int n = index - SrdoRecords.CommunicationBase;
        switch (subindex)
        {
            case 0x00:
                return OdWriteDecision.Reject(SdoAbortCode.AttemptWriteReadOnly);
            case 0x01:
            {
                byte direction = value[0];
                if (direction > 2) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 3..255 reserved
                if (direction == 0) return OdWriteDecision.Accept;
                // Creating: both COB-IDs must be set and free, and the mapped objects must be
                // accessible in this direction (a producer reads them, a consumer writes them).
                uint cob1 = _od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask;
                uint cob2 = _od.ReadUnsigned(index, 0x06) & CanOpenCobId.CanIdMask;
                if (!SrdoFrames.IsCobId1(cob1) || !SrdoFrames.IsCobId2(cob2) || cob2 != cob1 + 1)
                    return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                if (IsCanIdOfAnExistingSrdo(cob1, n) || IsCanIdOfAnExistingSrdo(cob2, n))
                    return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                foreach (var entry in SrdoRecords.ReadMapping(_od, n))
                {
                    uint raw = ((uint)entry.Index << 16) | ((uint)entry.Subindex << 8) | entry.BitLength;
                    if (ValidateSrdoMappingEntry(raw, (SrdoDirection)direction) is { } abort) return OdWriteDecision.Reject(abort);
                }
                return OdWriteDecision.Accept;
            }
            case 0x02:
                return (value[0] | (value[1] << 8)) == 0 ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept; // 1..65535
            case 0x03:
                return value[0] == 0 ? OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded) : OdWriteDecision.Accept; // 1..255
            case 0x04:
                // "On an attempt to change the value of the transmission type an abort message
                // (abort code: 0609 0030h) is generated."
                return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
            case 0x05:
            case 0x06:
            {
                uint word = ObjectDictionary.DecodeU32(value);
                if ((word & ~CanOpenCobId.CanIdMask) != 0) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // Figure 7: bits 31..11 reserved (= 0)
                uint current = _od.ReadUnsigned(index, subindex);
                // "It is not allowed to change the COB-ID 1 or COB-ID 2 while the SRDO exists."
                if (_od.ReadUnsigned(index, 0x01) != 0 && word != current) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                if (word == 0) return OdWriteDecision.Accept; // disabled
                bool inRange = subindex == 0x05 ? SrdoFrames.IsCobId1(word) : SrdoFrames.IsCobId2(word);
                if (!inRange) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                if (subindex == 0x06 && word != (_od.ReadUnsigned(index, 0x05) & CanOpenCobId.CanIdMask) + 1)
                    return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // "two following COB-IDs"
                if (IsCanIdOfAnExistingSrdo(word, n)) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded);
                return OdWriteDecision.Accept;
            }
            default:
                return OdWriteDecision.Reject(SdoAbortCode.SubIndexDoesNotExist);
        }
    }

    /// <summary>One CAN-ID carries one communication object (Codex on #133). PDO, SYNC and EMCY
    /// cannot sit on 101h–180h at all: CiA 301 Table 40 restricts the range and their validators
    /// refuse it, so only SRDO against SRDO is checked here.</summary>
    private bool IsCanIdOfAnExistingSrdo(uint canId, int exceptSrdo)
    {
        for (int other = 1; other <= _srdoCount; other++)
        {
            if (other == exceptSrdo) continue;
            var comm = SrdoRecords.CommIndex(other);
            if (!_od.TryReadUnsigned(comm, 0x01, out var direction) || direction == 0) continue;
            if ((_od.ReadUnsigned(comm, 0x05) & CanOpenCobId.CanIdMask) == canId) return true;
            if ((_od.ReadUnsigned(comm, 0x06) & CanOpenCobId.CanIdMask) == canId) return true;
        }
        return false;
    }

    private OdWriteDecision ValidateSrdoMappingWrite(ushort index, byte subindex, byte[] value)
    {
        int n = index - SrdoRecords.MappingBase;
        // "For changing the SRDO mapping first the SRDO shall be deleted." — same code as the
        // PDO re-mapping procedure uses for a live PDO.
        if (_od.TryReadUnsigned(SrdoRecords.CommIndex(n), 0x01, out var direction) && direction != 0)
            return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        if (subindex == 0x00)
        {
            byte count = value[0];
            if (count == 0) return OdWriteDecision.Accept; // deactivated
            if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) return OdWriteDecision.Reject(SdoAbortCode.ValueRangeExceeded); // 2, 4 … 16
            int total = 0;
            for (byte s = 1; s <= count; s++)
            {
                if (!_od.TryReadUnsigned(index, s, out var raw) || raw == 0) return OdWriteDecision.Reject(SdoAbortCode.ObjectDoesNotExist);
                if ((s & 1) == 0)
                {
                    if (raw != _od.ReadUnsigned(index, (byte)(s - 1))) return OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped); // inverted slot repeats the plain slot
                    continue;
                }
                if (ValidateSrdoMappingEntry(raw, SrdoDirection.None) is { } abort) return OdWriteDecision.Reject(abort);
                total += (int)(raw & 0xFF) / 8;
            }
            return total > 8 ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded) : OdWriteDecision.Accept;
        }
        if (_od.ReadUnsigned(index, 0x00) != 0) return OdWriteDecision.Reject(SdoAbortCode.UnsupportedAccess);
        uint entry = ObjectDictionary.DecodeU32(value);
        if (entry == 0) return OdWriteDecision.Accept;
        if (ValidateSrdoMappingEntry(entry, SrdoDirection.None) is { } reason) return OdWriteDecision.Reject(reason);
        if ((subindex & 1) == 0)
        {
            uint plain = _od.ReadUnsigned(index, (byte)(subindex - 1));
            return plain != 0 && plain != entry ? OdWriteDecision.Reject(SdoAbortCode.ObjectCannotBeMapped) : OdWriteDecision.Accept;
        }
        int precedingBytes = 0;
        for (byte s = 1; s < subindex; s += 2)
        {
            if (_od.TryReadUnsigned(index, s, out var earlier)) precedingBytes += (int)(earlier & 0xFF) / 8;
        }
        return precedingBytes + (int)(entry & 0xFF) / 8 > 8
            ? OdWriteDecision.Reject(SdoAbortCode.PdoMappingLengthExceeded)
            : OdWriteDecision.Accept;
    }

    /// <summary>Like <see cref="ValidateMappingEntry"/> for PDOs, minus dummies (safety data is
    /// never padding) and with the access check deferred to the direction write when
    /// <paramref name="direction"/> is None (the mapping is written while the SRDO is deleted).</summary>
    private SdoAbortCode? ValidateSrdoMappingEntry(uint raw, SrdoDirection direction)
    {
        var entryIndex = (ushort)((raw >> 16) & 0xFFFF);
        var entrySub = (byte)((raw >> 8) & 0xFF);
        var bitLength = (byte)(raw & 0xFF);
        if (bitLength == 0 || bitLength > 64 || bitLength % 8 != 0) return SdoAbortCode.DataTypeLengthMismatch;
        if (entryIndex is >= 0x1000 and <= 0x1FFF) return SdoAbortCode.ObjectCannotBeMapped;
        if (entrySub == 0 && PdoMappingEntry.DummyBitLength(entryIndex) != 0) return SdoAbortCode.ObjectCannotBeMapped;
        if (!_od.TryGet(entryIndex, entrySub, out var target)) return SdoAbortCode.ObjectDoesNotExist;
        if (!target.PdoMappable) return SdoAbortCode.ObjectCannotBeMapped;
        if (direction == SrdoDirection.Transmit && (target.Access & OdAccess.ReadOnly) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        if (direction == SrdoDirection.Receive && (target.Access & OdAccess.WriteOnly) == 0) return SdoAbortCode.ObjectCannotBeMapped;
        int fixedSize = OdEntryLayout.FixedSize(target.DataType);
        if (fixedSize > 0 && bitLength / 8 != fixedSize) return SdoAbortCode.ObjectCannotBeMapped;
        return null;
    }
}
```

Note on `ValueRangeExceeded` for sub5/sub6 "not while exists": the spec table maps this to `0609 0030h`, the same code SYNC/EMCY use.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenSafetyCommunicationProfileTests"` — all PASS; then the full CANopen filter to confirm nothing else moved (the volatile `_state` and the validator's first line touch every write).

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs src/CanKit.Pro.CANopen/CanOpenNode.cs src/CanKit.Pro.CANopen/CanOpenNode.CommunicationProfile.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyCommunicationProfileTests.cs
git commit -m "feat(canopen): create and validate the CiA 304 safety objects 1300h-13FFh"
```

---

### Task 5: `SrdoEngine` — core and producer

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/ISrdoEngineHost.cs`, `src/CanKit.Pro.CANopen/Safety/SrdoEngine.cs` (partial: core + producer)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineProducerTests.cs`

**Interfaces:**
- Consumes: `IProtocolActor` (`Post`, `PostAsync`, `Schedule`), `ITimeSource`, `IDeadlineScheduler.Arm`, `IDeadline.Rearm/Complete/Dispose/IsExpired/IsCancelled`, `ObjectDictionary`, `SrdoRecords`, `SrdoFrames`, `SrdoCrc`. The internal `ProtocolActor(mode, syncContext, timeSource, shutdownTimeout)` constructor and `ManualTimeSource` from the tests' infrastructure.
- Produces:

```csharp
internal interface ISrdoEngineHost
{
    void Send(uint cobId, byte[] payload);
    void EmitEmcy(ushort errorCode);
    void SrdoReceived(int srdoNumber, uint cobId, byte[] payload);
    void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason);
    void GlobalFailsafeCommandReceived();
    void ReportBackgroundException(Exception exception);
}

internal sealed partial class SrdoEngine : IDisposable
{
    public SrdoEngine(IProtocolActor actor, ITimeSource time, IDeadlineScheduler deadlines, ObjectDictionary od, byte nodeId, int srdoCount, ISrdoEngineHost host);
    public int SrdoCount { get; }
    public bool IsOperational { get; }
    public void Rebuild(int srdoNumber);            // actor
    public void EnterOperational();                 // actor
    public void LeaveOperational();                 // actor
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr);   // actor; Task 6
    public void Trigger(int srdoNumber);            // actor
    public bool TrySendGfc();                       // actor; Task 6
    public SrdoState GetState(int srdoNumber);      // any thread
    public void CollectChangeOfStateEntries(HashSet<uint> keys, Func<ushort, byte, uint> key); // writing thread, reads the OD only
    public void Dispose();                          // actor
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineProducerTests.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The producer half of <see cref="SrdoEngine"/> (CiA DSP 304 V1.0 §8.1, §8.1.3.1,
/// §9.5), driven without a bus: a real actor on a <see cref="ManualTimeSource"/>, a dictionary
/// holding the records, and a host that records what the engine asks it to send.</summary>
public class SrdoEngineProducerTests : IDisposable
{
    private const byte NodeId = 0x11;
    private readonly ManualTimeSource _clock = new();
    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od = new();
    private readonly RecordingHost _host = new();
    private SrdoEngine? _engine;

    public SrdoEngineProducerTests()
    {
        _actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _clock, null);
        _deadlines = new DeadlineScheduler(_actor);
    }

    public void Dispose()
    {
        _actor.PostAsync(() => _engine?.Dispose()).GetAwaiter().GetResult();
        _actor.Dispose();
    }

    internal sealed class RecordingHost : ISrdoEngineHost
    {
        public readonly List<(uint CobId, byte[] Payload)> Sent = new();
        public readonly List<ushort> Emcys = new();
        public readonly List<(int Srdo, uint CobId, byte[] Payload)> Received = new();
        public readonly List<(int Srdo, bool IsValid, SrdoInvalidReason? Reason)> States = new();
        public int Gfcs;
        public readonly List<Exception> Exceptions = new();
        public void Send(uint cobId, byte[] payload) => Sent.Add((cobId, payload));
        public void EmitEmcy(ushort errorCode) => Emcys.Add(errorCode);
        public void SrdoReceived(int srdoNumber, uint cobId, byte[] payload) => Received.Add((srdoNumber, cobId, payload));
        public void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason) => States.Add((srdoNumber, isValid, reason));
        public void GlobalFailsafeCommandReceived() => Gfcs++;
        public void ReportBackgroundException(Exception exception) => Exceptions.Add(exception);
    }

    /// <summary>The dictionary a node with SrdoCount = 2 would hold, plus two application objects.</summary>
    internal static void Populate(ObjectDictionary od, byte nodeId, int count)
    {
        od.AddU8(0x1001, 0, 0, OdAccess.ReadOnly, pdoMappable: false);
        od.AddU8(SrdoRecords.GfcParameter, 0, 0, OdAccess.ReadWrite, pdoMappable: false);
        for (int n = 1; n <= count; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            od.AddU8(comm, 0, 6, OdAccess.ReadOnly, false); od.AddU8(comm, 1, 0, OdAccess.ReadWrite, false);
            od.AddU16(comm, 2, 25, OdAccess.ReadWrite, false); od.AddU8(comm, 3, 20, OdAccess.ReadWrite, false);
            od.AddU8(comm, 4, 254, OdAccess.ReadOnly, false);
            od.AddU32(comm, 5, n == 1 ? CanOpenCobId.SrdoDefaultCobId1(nodeId) : 0u, OdAccess.ReadWrite, false);
            od.AddU32(comm, 6, n == 1 ? CanOpenCobId.SrdoDefaultCobId2(nodeId) : 0u, OdAccess.ReadWrite, false);
            od.AddU8(map, 0, 0, OdAccess.ReadWrite, false);
            for (byte s = 1; s <= 16; s++) od.AddU32(map, s, 0, OdAccess.ReadWrite, false);
        }
        od.AddU8(SrdoRecords.ConfigurationValid, 0, 0, OdAccess.ReadWrite, false);
        od.AddU8(SrdoRecords.Checksum, 0, (byte)count, OdAccess.ReadOnly, false);
        for (byte n = 1; n <= count; n++) od.AddU16(SrdoRecords.Checksum, n, 0, OdAccess.ReadWrite, false);
        od.AddU16(0x2000, 0, 0x1234);
        od.AddU8(0x2001, 0, 0x5A);
    }

    /// <summary>Writes record n as a producer/consumer of 0x2000:00 (16 bit) and 0x2001:00 (8 bit)
    /// and makes the configuration valid (13FFh:n = CRC, 13FEh = A5h).</summary>
    internal static void Configure(ObjectDictionary od, int n, SrdoDirection direction, ushort cycleMs, byte srvtMs, uint cob1, uint cob2)
    {
        var comm = SrdoRecords.CommIndex(n);
        var map = SrdoRecords.MapIndex(n);
        od.WriteUnsigned(comm, 1, 0);
        od.WriteUnsigned(map, 0, 0);
        od.WriteUnsigned(map, 1, 0x2000_0010); od.WriteUnsigned(map, 2, 0x2000_0010);
        od.WriteUnsigned(map, 3, 0x2001_0008); od.WriteUnsigned(map, 4, 0x2001_0008);
        od.WriteUnsigned(map, 0, 4);
        od.WriteUnsigned(comm, 2, cycleMs); od.WriteUnsigned(comm, 3, srvtMs);
        od.WriteUnsigned(comm, 5, cob1); od.WriteUnsigned(comm, 6, cob2);
        od.WriteUnsigned(comm, 1, (byte)direction);
        SrdoRecords.TryReadCommunication(od, n, out var p);
        od.WriteUnsigned(SrdoRecords.Checksum, (byte)n, SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, n))));
        od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
    }

    private SrdoEngine Start(int count = 2)
    {
        Populate(_od, NodeId, count);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, count, _host);
        return _engine;
    }

    private void OnActor(Action work) => _actor.PostAsync(work).GetAwaiter().GetResult();

    /// <summary>Two round-trips: the first may return while the drain that ran it is still
    /// firing timers; the second is drained only after those callbacks returned.</summary>
    private void Settle() { OnActor(() => { }); OnActor(() => { }); }

    private void Advance(TimeSpan by) { Settle(); _clock.Advance(by); Settle(); }

    [Fact]
    public void First_Cycle_Is_Delayed_Half_A_Millisecond_Per_NodeId()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Settle();
        _host.Sent.Should().BeEmpty("§9.5: the first cyclic transmit is delayed 0.5 ms × node-id");
        Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * NodeId / 2 - 1));
        _host.Sent.Should().BeEmpty();
        Advance(TimeSpan.FromTicks(1));
        _host.Sent.Should().HaveCount(2);
    }

    [Fact]
    public void A_Transmission_Is_A_Plain_Frame_And_Its_Inverse_On_Following_Ids()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent[0].CobId.Should().Be(0x123u);
        _host.Sent[0].Payload.Should().Equal(0x34, 0x12, 0x5A);
        _host.Sent[1].CobId.Should().Be(0x124u);
        _host.Sent[1].Payload.Should().Equal(0xCB, 0xED, 0xA5);
    }

    [Fact]
    public void Cycle_Repeats_Every_Refresh_Time()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent.Should().HaveCount(2);
        Advance(TimeSpan.FromMilliseconds(24));
        _host.Sent.Should().HaveCount(2);
        Advance(TimeSpan.FromMilliseconds(1));
        _host.Sent.Should().HaveCount(4);
        Advance(TimeSpan.FromMilliseconds(25));
        _host.Sent.Should().HaveCount(6);
    }

    [Fact]
    public void Trigger_Transmits_Now_And_Restarts_The_Cycle()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        Advance(TimeSpan.FromMilliseconds(10));
        _od.WriteUnsigned(0x2001, 0, 0x01);
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().HaveCount(4);
        _host.Sent[2].Payload.Should().Equal(0x34, 0x12, 0x01, "the payload is sampled at transmission");
        Advance(TimeSpan.FromMilliseconds(24));
        _host.Sent.Should().HaveCount(4, "the refresh cycle restarted with the triggered transmission");
        Advance(TimeSpan.FromMilliseconds(1));
        _host.Sent.Should().HaveCount(6);
    }

    [Fact]
    public void Nothing_Is_Sent_Outside_Operational_Or_With_An_Invalid_Configuration()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => engine.Rebuild(1));
        Advance(TimeSpan.FromMilliseconds(100));
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().BeEmpty("SRDOs exist only in Operational (§8.3.2.2)");
        engine.GetState(1).IsValid.Should().BeFalse();
        engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        _od.WriteUnsigned(SrdoRecords.Checksum, 1, 0xFFFF);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(100));
        OnActor(() => engine.Trigger(1));
        _host.Sent.Should().BeEmpty("§8.3.1 D: in case of mismatch the safety node shall not transmit SRDOs");
        engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.ConfigurationInvalid));
    }

    [Fact]
    public void Leaving_Operational_Stops_The_Cycle_And_Reports()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.States.Should().Contain((1, true, null));
        OnActor(() => engine.LeaveOperational());
        Advance(TimeSpan.FromMilliseconds(100));
        _host.Sent.Should().HaveCount(2);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.NotOperational));
        engine.GetState(1).Direction.Should().Be(SrdoDirection.Transmit);
    }

    [Fact]
    public void An_Empty_Mapping_Transmits_Empty_Frames()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        _od.WriteUnsigned(SrdoRecords.CommIndex(1), 1, 0);
        _od.WriteUnsigned(SrdoRecords.MapIndex(1), 0, 0);
        _od.WriteUnsigned(SrdoRecords.CommIndex(1), 1, 1);
        SrdoRecords.TryReadCommunication(_od, 1, out var p);
        _od.WriteUnsigned(SrdoRecords.Checksum, 1, SrdoCrc.Compute(p, new SrdoMapping()));
        _od.WriteUnsigned(SrdoRecords.ConfigurationValid, 0, 0xA5);
        OnActor(() => { engine.Rebuild(1); engine.EnterOperational(); });
        Advance(TimeSpan.FromMilliseconds(NodeId));
        _host.Sent.Should().HaveCount(2);
        _host.Sent[0].Payload.Should().BeEmpty("0 ≤ L ≤ 8 (§8.1.3.1)");
    }

    [Fact]
    public void ChangeOfState_Keys_Are_The_Transmit_Mappings()
    {
        var engine = Start();
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        Configure(_od, 2, SrdoDirection.Receive, 50, 20, 0x125, 0x126);
        var keys = new HashSet<uint>();
        engine.CollectChangeOfStateEntries(keys, (i, s) => ((uint)i << 8) | s);
        keys.Should().BeEquivalentTo(new uint[] { 0x2000_00, 0x2001_00 });
    }
}
```

- [ ] **Step 2: Run to verify failure** — compile errors (`SrdoEngine`, `ISrdoEngineHost`).

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/ISrdoEngineHost.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What <see cref="SrdoEngine"/> asks of the node: sending, an EMCY, and the three
/// events. All calls arrive on the actor loop. The node implements it; tests record it.</summary>
internal interface ISrdoEngineHost
{
    void Send(uint cobId, byte[] payload);
    void EmitEmcy(ushort errorCode);
    void SrdoReceived(int srdoNumber, uint cobId, byte[] payload);
    void SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason);
    void GlobalFailsafeCommandReceived();
    void ReportBackgroundException(Exception exception);
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoEngine.cs
using System;
using System.Collections.Generic;
using System.Threading;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.Reliability;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>
/// The SRDO runtime of a node (CiA DSP 304 V1.0 §8.1, §8.2): one producer or consumer per
/// record 1301h–1340h, derived from the records like the PDO engine derives its PDOs. Owned
/// and driven by <see cref="CanOpenNode"/> on its actor loop; every member except
/// <see cref="GetState"/> and <see cref="CollectChangeOfStateEntries"/> is actor-only.
/// Cycles run on <see cref="IProtocolActor.Schedule"/>, the SCT and SRVT on
/// <see cref="IDeadlineScheduler"/>, so a <c>ManualTimeSource</c> drives all of it in tests.
/// </summary>
internal sealed partial class SrdoEngine : IDisposable
{
    private readonly IProtocolActor _actor;
    private readonly ITimeSource _time;
    private readonly IDeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od;
    private readonly byte _nodeId;
    private readonly ISrdoEngineHost _host;
    private readonly SrdoRuntime?[] _runtimes;
    private readonly SrdoState[] _snapshots;
    private bool _operational;
    private bool _disposed;

    public SrdoEngine(IProtocolActor actor, ITimeSource time, IDeadlineScheduler deadlines, ObjectDictionary od,
        byte nodeId, int srdoCount, ISrdoEngineHost host)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
        _od = od ?? throw new ArgumentNullException(nameof(od));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (srdoCount is < 0 or > SrdoRecords.MaxSrdoCount) throw new ArgumentOutOfRangeException(nameof(srdoCount));
        _nodeId = nodeId;
        SrdoCount = srdoCount;
        _runtimes = new SrdoRuntime?[srdoCount + 1];
        _snapshots = new SrdoState[srdoCount + 1];
        for (int n = 1; n <= srdoCount; n++)
            _snapshots[n] = new SrdoState(n, SrdoDirection.None, false, null, null);
    }

    public int SrdoCount { get; }
    public bool IsOperational => _operational;

    /// <summary>Snapshot for any thread.</summary>
    public SrdoState GetState(int srdoNumber)
    {
        if (srdoNumber < 1 || srdoNumber > SrdoCount)
            throw new ArgumentOutOfRangeException(nameof(srdoNumber), srdoNumber, $"SRDO number must be 1..{SrdoCount}.");
        return Volatile.Read(ref _snapshots[srdoNumber]);
    }

    /// <summary>Reads record n and brings its runtime in line: configuration validity
    /// (§9.5 last rule), direction, times, ids and mapping. Re-arms when Operational.</summary>
    public void Rebuild(int srdoNumber)
    {
        if (_disposed) return;
        var rt = _runtimes[srdoNumber] ??= new SrdoRuntime(srdoNumber);
        Disarm(rt);
        if (SrdoRecords.TryReadCommunication(_od, srdoNumber, out var p))
        {
            rt.Direction = p.Direction;
            rt.CycleTime = p.RefreshOrSafeguardCycleTime;
            rt.ValidationTime = p.ValidationTime;
            rt.CobId1 = p.CobId1;
            rt.CobId2 = p.CobId2;
        }
        else
        {
            rt.Direction = SrdoDirection.None;
        }
        rt.Mapping = SrdoRecords.ReadMapping(_od, srdoNumber);
        rt.TotalBytes = 0;
        foreach (var e in rt.Mapping) rt.TotalBytes += e.ByteLength;
        rt.ConfigurationValid = rt.Direction != SrdoDirection.None && SrdoRecords.IsConfigurationValid(_od, srdoNumber);
        rt.ShortFrameReported = false;
        if (_operational) Arm(rt);
        else SetInvalid(rt, rt.Direction == SrdoDirection.None ? null : SrdoInvalidReason.NotOperational);
    }

    /// <summary>§8.3.2.2: "Safety communication is only supported in this state." Every SRDO is
    /// re-read — 13FEh/13FFh may have changed in Pre-Operational — and armed.</summary>
    public void EnterOperational()
    {
        if (_disposed) return;
        _operational = true;
        for (int n = 1; n <= SrdoCount; n++) Rebuild(n);
    }

    /// <summary>Cycles stop, deadlines go, every existing SRDO is NotOperational.</summary>
    public void LeaveOperational()
    {
        _operational = false;
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is not { } rt) continue;
            Disarm(rt);
            SetInvalid(rt, rt.Direction == SrdoDirection.None ? null : SrdoInvalidReason.NotOperational);
        }
    }

    /// <summary>§8.1: "SRDOs may also be transmitted event-driven". Only a valid producer
    /// transmits; the cycle restarts from this transmission.</summary>
    public void Trigger(int srdoNumber)
    {
        if (_disposed || !_operational) return;
        if (_runtimes[srdoNumber] is { Direction: SrdoDirection.Transmit, ConfigurationValid: true } rt) Transmit(rt);
    }

    /// <summary>The OD entries mapped in a transmit SRDO, for the node's change-of-state
    /// pre-filter. Reads the records, not the runtime: it runs on the writing thread.</summary>
    public void CollectChangeOfStateEntries(HashSet<uint> keys, Func<ushort, byte, uint> key)
    {
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (!SrdoRecords.TryReadCommunication(_od, n, out var p) || p.Direction != SrdoDirection.Transmit) continue;
            foreach (var e in SrdoRecords.ReadMapping(_od, n)) keys.Add(key(e.Index, e.Subindex));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operational = false;
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is { } rt) Disarm(rt);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Producer (§8.1.3.1 Figure 4, §9.5).
    // -----------------------------------------------------------------------------------------

    private void Arm(SrdoRuntime rt)
    {
        if (rt.Direction == SrdoDirection.None)
        {
            SetInvalid(rt, null);
            return;
        }
        if (!rt.ConfigurationValid)
        {
            // §8.3.1 step D: "In case of mismatch the safety node shall not transmit SRDOs; the
            // safety controller shall enter (stay) in safe state."
            SetInvalid(rt, SrdoInvalidReason.ConfigurationInvalid);
            return;
        }
        if (rt.Direction == SrdoDirection.Transmit)
        {
            SetValid(rt);
            // §9.5: "The first cyclic transmit of an SRDO shall be delayed for 0.5 ms * Node-ID."
            ScheduleCycle(rt, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * _nodeId / 2));
            return;
        }
        ArmConsumer(rt); // Task 6
    }

    private void Disarm(SrdoRuntime rt)
    {
        rt.Generation++;
        rt.CycleHandle?.Dispose();
        rt.CycleHandle = null;
        DisarmConsumer(rt); // Task 6
    }

    private void ScheduleCycle(SrdoRuntime rt, TimeSpan delay)
    {
        int generation = ++rt.Generation;
        rt.CycleHandle?.Dispose();
        rt.CycleHandle = _actor.Schedule(delay, () =>
        {
            if (_disposed || !_operational || generation != rt.Generation) return;
            if (!ReferenceEquals(_runtimes[rt.Number], rt) || !rt.ConfigurationValid) return;
            Transmit(rt);
        });
    }

    /// <summary>Both frames in one actor turn — "the redundant transmission is sent after the
    /// first transmission to the CAN controller with minimum delay" (§8.1) — then the refresh
    /// cycle restarts, so the refresh time is the maximum interval between transmissions.</summary>
    private void Transmit(SrdoRuntime rt)
    {
        var payload = BuildPayload(rt);
        _host.Send(rt.CobId1, payload);
        _host.Send(rt.CobId2, SrdoFrames.Invert(payload));
        ScheduleCycle(rt, rt.CycleTime);
    }

    private byte[] BuildPayload(SrdoRuntime rt)
    {
        var payload = new byte[rt.TotalBytes];
        int offset = 0;
        foreach (var entry in rt.Mapping)
        {
            if (_od.TryReadRaw(entry.Index, entry.Subindex, out var raw))
                Buffer.BlockCopy(raw, 0, payload, offset, Math.Min(raw.Length, entry.ByteLength));
            offset += entry.ByteLength;
        }
        return payload;
    }

    // -----------------------------------------------------------------------------------------
    // State (transitions only are reported; the snapshot is for any thread).
    // -----------------------------------------------------------------------------------------

    private void SetValid(SrdoRuntime rt)
    {
        bool changed = !rt.IsValid;
        rt.IsValid = true;
        rt.Reason = null;
        rt.LastValidAt = DateTime.UtcNow;
        Publish(rt);
        if (changed) _host.SrdoStateChanged(rt.Number, true, null);
    }

    private void SetInvalid(SrdoRuntime rt, SrdoInvalidReason? reason)
    {
        bool changed = rt.IsValid || rt.Reason != reason;
        rt.IsValid = false;
        rt.Reason = reason;
        Publish(rt);
        if (changed && reason is not null) _host.SrdoStateChanged(rt.Number, false, reason);
    }

    private void Publish(SrdoRuntime rt)
        => Volatile.Write(ref _snapshots[rt.Number], new SrdoState(rt.Number, rt.Direction, rt.IsValid, rt.Reason, rt.LastValidAt));

    private sealed class SrdoRuntime
    {
        public SrdoRuntime(int number) => Number = number;
        public int Number { get; }
        public SrdoDirection Direction { get; set; }
        public TimeSpan CycleTime { get; set; }
        public TimeSpan ValidationTime { get; set; }
        public uint CobId1 { get; set; }
        public uint CobId2 { get; set; }
        public PdoMappingEntry[] Mapping { get; set; } = Array.Empty<PdoMappingEntry>();
        public int TotalBytes { get; set; }
        public bool ConfigurationValid { get; set; }
        public bool IsValid { get; set; }
        public SrdoInvalidReason? Reason { get; set; }
        public DateTime? LastValidAt { get; set; }
        public int Generation;
        public IDisposable? CycleHandle { get; set; }
        // Consumer (Task 6).
        public byte[]? Pending { get; set; }
        public IDeadline? SctDeadline { get; set; }
        public IDeadline? SrvtDeadline { get; set; }
        public bool ShortFrameReported { get; set; }
    }
}
```

For Task 5 to compile, add a second file with the consumer stubs that Task 6 replaces:

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs  (Task 5 version)
namespace CanKit.Pro.CANopen.Safety;

internal sealed partial class SrdoEngine
{
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr) => false;
    public bool TrySendGfc() => false;
    private void ArmConsumer(SrdoRuntime rt) => SetInvalid(rt, SrdoInvalidReason.NotReceived);
    private void DisarmConsumer(SrdoRuntime rt) { }
}
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~SrdoEngineProducerTests"` — PASS. Mutation check (CLAUDE.md): comment out the `SrdoFrames.Invert` call in `Transmit` (send `payload` twice) and confirm `A_Transmission_Is_A_Plain_Frame_And_Its_Inverse_On_Following_Ids` fails; restore.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/Safety/ISrdoEngineHost.cs src/CanKit.Pro.CANopen/Safety/SrdoEngine.cs src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineProducerTests.cs
git commit -m "feat(canopen): SRDO engine with the producer cycle of CiA 304"
```

---

### Task 6: `SrdoEngine` — consumer and GFC

**Files:**
- Modify: `src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs` (replace the Task 5 stubs)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineConsumerTests.cs`

**Interfaces:**
- Consumes: Task 5's engine core, `SrdoRuntime`, `SetValid`/`SetInvalid`, `RecordingHost`/`Populate`/`Configure` helpers from `SrdoEngineProducerTests` (make them `internal static` there, as written).
- Produces: `bool TryHandleFrame(uint cobId, byte[] data, bool isRtr)` — true when the frame belongs to the safety layer (an SRDO id this node produces or consumes, or 001h); `bool TrySendGfc()` — false when 1300h ≠ 1 or not Operational.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineConsumerTests.cs
using System;
using AwesomeAssertions;
using CanKit.Pro.Actor;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Reliability;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.Safety.SrdoEngineProducerTests;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The consumer half (CiA DSP 304 V1.0 §8.1.1 Figures 2 and 3, §8.1.3.1, §9.5) and
/// the GFC (§8.2). SCT and SRVT are deadlines on the engine's clock; the tests move it.</summary>
public class SrdoEngineConsumerTests : IDisposable
{
    private const byte NodeId = 0x11;
    private readonly ManualTimeSource _clock = new();
    private readonly ProtocolActor _actor;
    private readonly DeadlineScheduler _deadlines;
    private readonly ObjectDictionary _od = new();
    private readonly RecordingHost _host = new();
    private SrdoEngine _engine = null!;

    public SrdoEngineConsumerTests()
    {
        _actor = new ProtocolActor(ActorExecutionMode.DedicatedThread, null, _clock, null);
        _deadlines = new DeadlineScheduler(_actor);
    }

    public void Dispose()
    {
        _actor.PostAsync(() => _engine.Dispose()).GetAwaiter().GetResult();
        _actor.Dispose();
    }

    private void OnActor(Action work) => _actor.PostAsync(work).GetAwaiter().GetResult();
    private void Settle() { OnActor(() => { }); OnActor(() => { }); }
    private void Advance(TimeSpan by) { Settle(); _clock.Advance(by); Settle(); }

    /// <summary>SRDO 1 consumes 0x2000:00 / 0x2001:00 on 123h/124h, SCT 50 ms, SRVT 20 ms, and is Operational.</summary>
    private void StartConsumer()
    {
        Populate(_od, NodeId, 2);
        Configure(_od, 1, SrdoDirection.Receive, 50, 20, 0x123, 0x124);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, 2, _host);
        OnActor(() => { _engine.Rebuild(1); _engine.Rebuild(2); _engine.EnterOperational(); });
        Settle();
    }

    private bool Frame(uint cobId, params byte[] data)
    {
        bool consumed = false;
        OnActor(() => consumed = _engine.TryHandleFrame(cobId, data, isRtr: false));
        return consumed;
    }

    private static readonly byte[] Plain = { 0x78, 0x56, 0x01 };
    private static readonly byte[] Inverse = { 0x87, 0xA9, 0xFE };

    [Fact]
    public void Starts_Invalid_And_Becomes_Valid_With_A_Complete_Pair()
    {
        StartConsumer();
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotReceived);
        Frame(0x123, Plain).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeFalse("one frame is not an SRDO");
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u);
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u);
        _host.States.Should().ContainInOrder((1, false, SrdoInvalidReason.NotReceived), (1, true, null));
        _host.Received.Should().ContainSingle().Which.Should().Be((1, 0x123u, Plain));
        _host.States.IndexOf((1, true, null)).Should().BeLessThan(_host.States.Count, "the state change precedes the payload event");
    }

    [Fact]
    public void Second_Frame_Without_First_Is_OutOfOrder()
    {
        StartConsumer();
        Frame(0x124, Inverse);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.OutOfOrder));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u, "nothing was written");
    }

    [Fact]
    public void A_Wrong_Inverse_Is_A_Mismatch()
    {
        StartConsumer();
        Frame(0x123, Plain);
        Frame(0x124, 0x87, 0xA9, 0xFF);
        _host.States.Should().Contain((1, false, SrdoInvalidReason.Mismatch));
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
        Frame(0x123, Plain);
        Frame(0x124, 0x87, 0xA9);
        _host.States.Where(s => s.Reason == SrdoInvalidReason.Mismatch).Should().HaveCount(1, "the reason did not change, so no second event");
    }

    [Fact]
    public void Srvt_Expiry_Invalidates_And_Drops_The_Pending_Frame()
    {
        StartConsumer();
        Frame(0x123, Plain);
        Advance(TimeSpan.FromMilliseconds(19));
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.ValidationTimeExpired);
        Advance(TimeSpan.FromMilliseconds(1));
        _host.States.Should().Contain((1, false, SrdoInvalidReason.ValidationTimeExpired));
        Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeFalse("the late second frame has no pending first frame");
        _host.States.Last().Reason.Should().Be(SrdoInvalidReason.OutOfOrder);
    }

    [Fact]
    public void Sct_Expiry_Invalidates_A_Valid_Srdo()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(49));
        _engine.GetState(1).IsValid.Should().BeTrue();
        Advance(TimeSpan.FromMilliseconds(1));
        _engine.GetState(1).IsValid.Should().BeFalse();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired);
    }

    [Fact]
    public void Valid_Pair_After_Expiry_Rearms_Sct()
    {
        StartConsumer();
        Advance(TimeSpan.FromMilliseconds(50));
        _host.States.Should().Contain((1, false, SrdoInvalidReason.SafeguardCycleExpired));
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue("the next valid pair re-validates");
        Advance(TimeSpan.FromMilliseconds(50));
        _host.States.Where(s => s.Reason == SrdoInvalidReason.SafeguardCycleExpired).Should().HaveCount(2, "the SCT was re-armed by the pair");
    }

    [Fact]
    public void Each_Pair_Restarts_The_Sct()
    {
        StartConsumer();
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(40));
        Frame(0x123, Plain); Frame(0x124, Inverse);
        Advance(TimeSpan.FromMilliseconds(40));
        _engine.GetState(1).IsValid.Should().BeTrue("80 ms since the first pair, 40 ms since the last");
    }

    [Fact]
    public void A_Second_First_Frame_Replaces_The_Pending_One()
    {
        StartConsumer();
        Frame(0x123, 0x00, 0x00, 0x00);
        Advance(TimeSpan.FromMilliseconds(15));
        Frame(0x123, Plain);
        Advance(TimeSpan.FromMilliseconds(15));
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.ValidationTimeExpired, "the SRVT restarted with the second first frame");
        Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x5678u, "the later first frame is the one paired");
    }

    [Fact]
    public void A_Short_Pair_Raises_Emcy_8210h_Once_And_Is_Not_Processed()
    {
        StartConsumer();
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().Equal((ushort)0x8210);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
        _engine.GetState(1).IsValid.Should().BeFalse("SCT decides; the pair was not processed");
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().HaveCount(1, "once per run of short frames");
        Frame(0x123, Plain); Frame(0x124, Inverse);
        _engine.GetState(1).IsValid.Should().BeTrue();
        Frame(0x123, 0x78, 0x56); Frame(0x124, 0x87, 0xA9);
        _host.Emcys.Should().HaveCount(2, "a frame that fits ends the run");
    }

    [Fact]
    public void A_Long_Pair_Uses_The_First_Mapped_Bytes()
    {
        StartConsumer();
        Frame(0x123, 0x78, 0x56, 0x01, 0xAA); Frame(0x124, 0x87, 0xA9, 0xFE, 0x55);
        _engine.GetState(1).IsValid.Should().BeTrue();
        _od.ReadUnsigned(0x2001, 0).Should().Be(0x01u);
    }

    [Fact]
    public void Frames_On_Own_Transmit_CobIds_Are_Ignored()
    {
        Populate(_od, NodeId, 2);
        Configure(_od, 1, SrdoDirection.Transmit, 25, 20, 0x123, 0x124);
        Configure(_od, 2, SrdoDirection.Receive, 50, 20, 0x125, 0x126);
        _engine = new SrdoEngine(_actor, _clock, _deadlines, _od, NodeId, 2, _host);
        OnActor(() => { _engine.Rebuild(1); _engine.Rebuild(2); _engine.EnterOperational(); });
        Frame(0x124, Inverse).Should().BeTrue("an echo of our own second frame is ours to swallow");
        _host.States.Should().NotContain(s => s.Reason == SrdoInvalidReason.OutOfOrder);
        Frame(0x127, Plain).Should().BeFalse("not an SRDO of this node");
    }

    [Fact]
    public void Rtr_And_Not_Operational_Are_Swallowed_Without_Effect()
    {
        StartConsumer();
        bool consumed = false;
        OnActor(() => consumed = _engine.TryHandleFrame(0x123, Array.Empty<byte>(), isRtr: true));
        consumed.Should().BeTrue("§8.1: RTR is not possible — nothing answers it");
        OnActor(() => _engine.LeaveOperational());
        Frame(0x123, Plain).Should().BeTrue();
        Frame(0x124, Inverse).Should().BeTrue();
        _engine.GetState(1).Reason.Should().Be(SrdoInvalidReason.NotOperational);
        _od.ReadUnsigned(0x2000, 0).Should().Be(0x1234u);
    }

    [Fact]
    public void Gfc_Is_Received_And_Sent_Only_While_1300h_Is_1_And_Operational()
    {
        StartConsumer();
        Frame(CanOpenCobId.GlobalFailsafeCommand).Should().BeTrue("001h is a safety id even when ignored");
        _host.Gfcs.Should().Be(0, "1300h = 0: GFC is not valid");
        bool sent = true;
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeFalse();
        _od.WriteUnsigned(SrdoRecords.GfcParameter, 0, 1);
        Frame(CanOpenCobId.GlobalFailsafeCommand);
        _host.Gfcs.Should().Be(1);
        Frame(CanOpenCobId.GlobalFailsafeCommand, 0x00);
        _host.Gfcs.Should().Be(1, "§8.2.3: L = 0");
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeTrue();
        _host.Sent.Should().ContainSingle().Which.Should().Be((CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>()));
        OnActor(() => _engine.LeaveOperational());
        OnActor(() => sent = _engine.TrySendGfc());
        sent.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure** — the stubs return false / never validate; most tests fail on `Should().BeTrue()`.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Consumer side (CiA DSP 304 V1.0 §8.1.1, §8.1.3.1, §9.5) and the global failsafe
/// command (§8.2).</summary>
internal sealed partial class SrdoEngine
{
    /// <summary>Routes a frame to the SRDO it belongs to. True when the id is one of this node's
    /// SRDO ids or 001h, whether or not anything happened: a remote frame (§8.1 "RTR is not
    /// possible"), an echo of our own producer frames (#95), a frame outside Operational.</summary>
    public bool TryHandleFrame(uint cobId, byte[] data, bool isRtr)
    {
        if (_disposed) return false;
        if (cobId == CanOpenCobId.GlobalFailsafeCommand)
        {
            // §8.2.3 Write GFC: L = 0. §8.4.2.2 1300h: "0: GFC is not valid".
            if (!isRtr && data.Length == 0 && _operational && IsGfcEnabled()) _host.GlobalFailsafeCommandReceived();
            return true;
        }
        for (int n = 1; n <= SrdoCount; n++)
        {
            if (_runtimes[n] is not { Direction: not SrdoDirection.None } rt) continue;
            if (cobId != rt.CobId1 && cobId != rt.CobId2) continue;
            if (isRtr || rt.Direction == SrdoDirection.Transmit || !_operational || !rt.ConfigurationValid) return true;
            if (cobId == rt.CobId1) OnFirstFrame(rt, data);
            else OnSecondFrame(rt, data);
            return true;
        }
        return false;
    }

    /// <summary>§8.2.2 push model, unconfirmed. False when 1300h ≠ 1 or not Operational.</summary>
    public bool TrySendGfc()
    {
        if (_disposed || !_operational || !IsGfcEnabled()) return false;
        _host.Send(CanOpenCobId.GlobalFailsafeCommand, Array.Empty<byte>());
        return true;
    }

    private bool IsGfcEnabled() => _od.TryReadUnsigned(SrdoRecords.GfcParameter, 0x00, out var v) && v == 1;

    private void ArmConsumer(SrdoRuntime rt)
    {
        SetInvalid(rt, SrdoInvalidReason.NotReceived);
        ArmSct(rt);
    }

    private void DisarmConsumer(SrdoRuntime rt)
    {
        rt.Pending = null;
        rt.SctDeadline?.Dispose();
        rt.SctDeadline = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
    }

    /// <summary>Figure 2: the safeguard cycle time "shall be survived by the safety controller".</summary>
    private void ArmSct(SrdoRuntime rt)
    {
        rt.SctDeadline?.Dispose();
        rt.SctDeadline = _deadlines.Arm(rt.CycleTime, () =>
        {
            if (_disposed || !ReferenceEquals(_runtimes[rt.Number], rt)) return;
            rt.SctDeadline = null;
            Invalidate(rt, SrdoInvalidReason.SafeguardCycleExpired);
        });
    }

    private void OnFirstFrame(SrdoRuntime rt, byte[] data)
    {
        // A second first frame replaces the pending one; Figure 3: the SRVT runs from the
        // first frame to its second.
        rt.Pending = data;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = _deadlines.Arm(rt.ValidationTime, () =>
        {
            if (_disposed || !ReferenceEquals(_runtimes[rt.Number], rt)) return;
            rt.SrvtDeadline = null;
            rt.Pending = null;
            Invalidate(rt, SrdoInvalidReason.ValidationTimeExpired);
        });
    }

    private void OnSecondFrame(SrdoRuntime rt, byte[] data)
    {
        if (rt.Pending is not { } first)
        {
            // §9.5: "received in chronological order (high priority identifier first)".
            Invalidate(rt, SrdoInvalidReason.OutOfOrder);
            return;
        }
        rt.Pending = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
        if (!SrdoFrames.IsInversePair(first, data))
        {
            Invalidate(rt, SrdoInvalidReason.Mismatch);
            return;
        }
        // §8.1.3.1: "If L is less than 'n' the data of the received SRDO is not processed and an
        // Emergency message with error code 8210h shall be produced" — once per run, like RPDOs.
        if (first.Length < rt.TotalBytes)
        {
            if (!rt.ShortFrameReported)
            {
                rt.ShortFrameReported = true;
                _host.EmitEmcy(0x8210);
            }
            return;
        }
        rt.ShortFrameReported = false;
        ArmSct(rt);
        Actuate(rt, first);
        SetValid(rt);
        _host.SrdoReceived(rt.Number, rt.CobId1, first);
    }

    private void Invalidate(SrdoRuntime rt, SrdoInvalidReason reason)
    {
        rt.Pending = null;
        rt.SrvtDeadline?.Dispose();
        rt.SrvtDeadline = null;
        SetInvalid(rt, reason);
    }

    /// <summary>"If L exceeds the number 'n' … only the first 'n' bytes are used" (§8.1.3.1).
    /// Writes under the OD lock, like the RPDO path; a rejected write is reported, not swallowed.</summary>
    private void Actuate(SrdoRuntime rt, byte[] payload)
    {
        int offset = 0;
        foreach (var entry in rt.Mapping)
        {
            var chunk = new byte[entry.ByteLength];
            Buffer.BlockCopy(payload, offset, chunk, 0, entry.ByteLength);
            try { _od.WriteRaw(entry.Index, entry.Subindex, chunk); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
            {
                _host.ReportBackgroundException(new InvalidOperationException(
                    $"SRDO{rt.Number}: the mapped object 0x{entry.Index:X4}:{entry.Subindex:X2} rejected {entry.ByteLength} byte(s): {ex.Message}", ex));
            }
            offset += entry.ByteLength;
        }
    }
}
```

Note: `_od.WriteRaw` runs the node's validator; the mapped objects are application objects (not `1000h`–`1FFFh`, enforced by `ValidateSrdoMappingEntry`), so no safety rule can refuse the write.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~SrdoEngine"` — all PASS. Mutation checks: (a) remove `ArmSct(rt)` from `OnSecondFrame` → `Valid_Pair_After_Expiry_Rearms_Sct` and `Each_Pair_Restarts_The_Sct` fail; (b) replace `IsInversePair` with a length-only comparison → `A_Wrong_Inverse_Is_A_Mismatch` fails. Restore both.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/Safety/SrdoEngine.Consumer.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/SrdoEngineConsumerTests.cs
git commit -m "feat(canopen): SRDO consumer with SCT, SRVT and pair checks; global failsafe command"
```

---

### Task 7: Wire the engine into the node; `ICanOpenSafety` and its extension

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs`, `src/CanKit.Pro.CANopen/Safety/CanOpenSafetyExtensions.cs`
- Modify: `src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs` (facade implementation, host implementation, raise helpers); `CanOpenNode.cs` — class declaration line 42 (no change; the partial adds interfaces), constructor after `_deadlines = …` (line 206) and the subscription filter (lines 236-244), `HandleIncoming` after the flying-master block (line 1262), `CleanUpOnActor` (line 788), `EventKey` (line 1204); `CanOpenNode.CommunicationProfile.cs` — `ApplyAllCommunicationObjects` (515), `ApplyCommunicationObject` (switch), `ApplyNmtTransition` (643-644), `PerformNmtReset` (685), `OnOdEntryWrittenForCommunicationProfile` (CoS pre-filter line); `CanOpenNode.Pdo.cs` — `OnOdEntryWrittenForCoS` (703), `EvaluateCoSOnActor` (733), `RebuildCosRelevantEntries` (773)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyNodeTests.cs`

**Interfaces:**
- Consumes: `SrdoEngine`, `ISrdoEngineHost`, `SrdoRecords`, `SrdoCrc`, value types.
- Produces: `public interface ICanOpenSafety` (device half; Task 9 and Task 11 add the master half to the same file), `public static class CanOpenSafetyExtensions { public static ICanOpenSafety Safety(this ICanOpenNode node) }`; node field `private readonly SrdoEngine _srdo;`, `private void RaiseSrdoReceived(int, uint, byte[])`, `private void RaiseSrdoStateChanged(int, bool, SrdoInvalidReason?)`, `private void RaiseGlobalFailsafeCommandReceived()`, `EventKey.SrdoState(int srdo, SrdoInvalidReason? reason)`, `EventKey.GlobalFailsafeCommand()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyNodeTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.RawCan;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Two nodes on the virtual bus: a producer and a consumer configured through
/// <see cref="ICanOpenSafety"/>, and the master configuring the device over SDO. Wire-level
/// expectations are awaited as positives; a negative is shown with an ordering witness
/// (the heartbeat the node emits on an NMT transition, or a later SRDO pair).</summary>
public class CanOpenSafetyNodeTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Producer = 0x11;
    private const byte Consumer = 0x12;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string NewSession() => VirtualAdapterFixture.NewSession("co-safety-node");

    private static ICanOpenNode OpenClocked(ICanBus bus, byte nodeId, ManualTimeSource clock, int srdoCount = 2)
        => new CanOpenNode(new CanBusService(bus), nodeId,
            new CanOpenNodeOptions { SrdoCount = srdoCount, WritableCommunicationParameters = true }, ownsService: true, clock);

    private static void Settle(ICanOpenNode node) { _ = node.State; _ = node.State; }
    private static void Advance(ManualTimeSource clock, ICanOpenNode node, TimeSpan by) { Settle(node); clock.Advance(by); Settle(node); }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(5);
        }
    }

    /// <summary>A raw channel that records data frames per COB-ID and sends NMT.</summary>
    private sealed class Wire : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly object _gate = new();
        private readonly Dictionary<uint, List<byte[]>> _frames = new();
        public Wire(string session, int channel)
        {
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame) return;
                lock (_gate)
                {
                    if (!_frames.TryGetValue((uint)f.ID, out var list)) _frames[(uint)f.ID] = list = new List<byte[]>();
                    list.Add(f.Data.ToArray());
                }
            };
        }
        public int Count(uint cobId) { lock (_gate) return _frames.TryGetValue(cobId, out var l) ? l.Count : 0; }
        public byte[][] Payloads(uint cobId) { lock (_gate) return _frames.TryGetValue(cobId, out var l) ? l.ToArray() : Array.Empty<byte[]>(); }
        public Task WaitForCountAsync(uint cobId, int count) => WaitUntilAsync(() => Count(cobId) >= count, $"expected {count} frame(s) on 0x{cobId:X3}, saw {Count(cobId)}");
        public void Transmit(uint cobId, byte[] data) => _bus.Transmit(CanFrame.Classic(unchecked((int)cobId), data, isExtendedFrame: false));
        public void SendNmt(NmtCommand command, byte nodeId) => Transmit(CanOpenCobId.NmtCommand, new[] { (byte)command, nodeId });
        public void Dispose() => _bus.Dispose();
    }

    private static void AddApplicationObjects(ObjectDictionary od)
    {
        od.AddU16(0x2000, 0x00, 0x1234);
        od.AddU8(0x2001, 0x00, 0x5A);
    }

    [Fact]
    public void Node_Without_Srdos_Is_Unchanged()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Producer);
        var safety = node.Safety();
        safety.SrdoCount.Should().Be(0);
        Assert.Throws<InvalidOperationException>(() => safety.ConfigureSrdoProducer(1, new SrdoMapping(), TimeSpan.FromMilliseconds(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => safety.GetSrdoState(1));
    }

    [Fact]
    public void Safety_Of_A_Foreign_Node_Is_Not_Supported()
    {
        var foreign = new ForeignNode();
        Assert.Throws<NotSupportedException>(() => foreign.Safety());
    }

    [Fact]
    public async Task Configure_Commit_And_Transmit()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(25u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x0FFu + 2 * Producer);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1381, 4).Should().Be(0x2001_0008u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0u, "not committed");
        safety.CommitSafetyConfiguration();
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.TryReadCommunication(od, 1, out var p);
        od.ReadUnsigned(0x13FF, 1).Should().Be(SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(od, 1))));
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        safety.GetSrdoState(1).IsValid.Should().BeTrue();
        Advance(clock, node, TimeSpan.FromMilliseconds(Producer));
        await wire.WaitForCountAsync(0x0FFu + 2 * Producer, 1);
        await wire.WaitForCountAsync(0x100u + 2 * Producer, 1);
        wire.Payloads(0x0FFu + 2 * Producer)[0].Should().Equal(0x34, 0x12, 0x5A);
        wire.Payloads(0x100u + 2 * Producer)[0].Should().Equal(0xCB, 0xED, 0xA5);
    }

    [Fact]
    public async Task Commit_Writes_Checksums_Then_Valid()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 2 });
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.ConfigureSrdoConsumer(2, new SrdoMapping().Add(0x2000, 0x00, 16), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), 0x125, 0x126);
        safety.CommitSafetyConfiguration();
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "A5h is written after the checksums, which each clear it");
        node.ObjectDictionary.ReadUnsigned(0x13FF, 2).Should().NotBe(0u);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Tampered_Checksum_Makes_The_Configuration_Invalid_At_Start()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        var clock = new ManualTimeSource();
        using var node = OpenClocked(bus, Producer, clock);
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        var changes = new List<SrdoStateChangedEventArgs>();
        safety.SrdoStateChanged += (_, e) => { lock (changes) changes.Add(e); };
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        node.ObjectDictionary.WriteUnsigned(0x13FF, 1, node.ObjectDictionary.ReadUnsigned(0x13FF, 1) ^ 1);
        node.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        node.StartHeartbeatProducer(TimeSpan.FromMilliseconds(100)); // the witness: the state-change heartbeat
        wire.SendNmt(NmtCommand.Start, Producer);
        await WaitUntilAsync(() => node.State == NmtState.Operational, "start");
        await wire.WaitForCountAsync(0x700u + Producer, 1);
        Advance(clock, node, TimeSpan.FromMilliseconds(100));
        wire.Count(0x0FFu + 2 * Producer).Should().Be(0, "§8.3.1 D: the safety node shall not transmit SRDOs");
        await WaitUntilAsync(() => { lock (changes) return changes.Any(c => c.Reason == SrdoInvalidReason.ConfigurationInvalid); }, "state change");
        safety.GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.ConfigurationInvalid);
    }

    [Fact]
    public void Configuration_Is_Refused_In_Operational()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        AddApplicationObjects(node.ObjectDictionary);
        var safety = node.Safety();
        safety.ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        safety.CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, Producer);
        WaitUntilAsync(() => node.State == NmtState.Operational, "start").GetAwaiter().GetResult();
        Assert.Throws<InvalidOperationException>(() => safety.ConfigureSrdoProducer(1, new SrdoMapping(), TimeSpan.FromMilliseconds(25)));
        Assert.Throws<InvalidOperationException>(() => safety.CommitSafetyConfiguration());
        Assert.Throws<InvalidOperationException>(() => safety.DeleteSrdo(1));
        var ex = Assert.Throws<ArgumentException>(() => node.ObjectDictionary.WriteUnsigned(0x1301, 2, 30));
        ex.Message.Should().Contain("0x08000022");
    }

    [Fact]
    public async Task Producer_And_Consumer_Exchange_An_Srdo()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var wire = new Wire(session, 3);
        var clockA = new ManualTimeSource();
        var clockB = new ManualTimeSource();
        using var producer = OpenClocked(busA, Producer, clockA);
        using var consumer = OpenClocked(busB, Consumer, clockB);
        AddApplicationObjects(producer.ObjectDictionary);
        consumer.ObjectDictionary.AddU16(0x3000, 0x00, 0);
        consumer.ObjectDictionary.AddU8(0x3001, 0x00, 0);
        producer.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(25));
        producer.Safety().CommitSafetyConfiguration();
        var received = new List<SrdoReceivedEventArgs>();
        consumer.Safety().SrdoReceived += (_, e) => { lock (received) received.Add(e); };
        consumer.Safety().ConfigureSrdoConsumer(1, new SrdoMapping().Add(0x3000, 0x00, 16).Add(0x3001, 0x00, 8),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(20), CanOpenCobId.SrdoDefaultCobId1(Producer), CanOpenCobId.SrdoDefaultCobId2(Producer));
        consumer.Safety().CommitSafetyConfiguration();
        wire.SendNmt(NmtCommand.Start, 0);
        await WaitUntilAsync(() => producer.State == NmtState.Operational && consumer.State == NmtState.Operational, "start");
        Advance(clockA, producer, TimeSpan.FromMilliseconds(Producer));
        await WaitUntilAsync(() => { lock (received) return received.Count >= 1; }, "SRDO received");
        consumer.Safety().GetSrdoState(1).IsValid.Should().BeTrue();
        consumer.ObjectDictionary.ReadUnsigned(0x3000, 0).Should().Be(0x1234u);
        consumer.ObjectDictionary.ReadUnsigned(0x3001, 0).Should().Be(0x5Au);
        received[0].CobId.Should().Be(CanOpenCobId.SrdoDefaultCobId1(Producer));
        // Change of state: the application writes a mapped object, the producer transmits at once.
        producer.ObjectDictionary.WriteUnsigned(0x2001, 0, 0x07);
        await WaitUntilAsync(() => { lock (received) return received.Count >= 2; }, "CoS SRDO");
        consumer.ObjectDictionary.ReadUnsigned(0x3001, 0).Should().Be(0x07u);
        // The consumer's SCT runs on its own clock: 50 ms without a pair invalidates it.
        Advance(clockB, consumer, TimeSpan.FromMilliseconds(50));
        consumer.Safety().GetSrdoState(1).IsValid.Should().BeFalse();
        consumer.Safety().GetSrdoState(1).Reason.Should().Be(SrdoInvalidReason.SafeguardCycleExpired);
    }

    [Fact]
    public async Task Gfc_Round_Trip()
    {
        var session = NewSession();
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var wire = new Wire(session, 3);
        using var sender = CanOpen.OpenNode(busA, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        using var receiver = CanOpen.OpenNode(busB, Consumer, new CanOpenNodeOptions { SrdoCount = 1 });
        int gfcs = 0;
        receiver.Safety().GlobalFailsafeCommandReceived += (_, _) => Interlocked.Increment(ref gfcs);
        receiver.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        wire.SendNmt(NmtCommand.Start, 0);
        await WaitUntilAsync(() => sender.State == NmtState.Operational && receiver.State == NmtState.Operational, "start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Safety().SendGlobalFailsafeCommandAsync());
        sender.ObjectDictionary.WriteUnsigned(0x1300, 0, 1);
        await sender.Safety().SendGlobalFailsafeCommandAsync();
        await wire.WaitForCountAsync(CanOpenCobId.GlobalFailsafeCommand, 1);
        wire.Payloads(CanOpenCobId.GlobalFailsafeCommand)[0].Should().BeEmpty();
        await WaitUntilAsync(() => Volatile.Read(ref gfcs) >= 1, "GFC received");
    }

    [Fact]
    public async Task Reset_Communication_Restores_The_Safety_Objects()
    {
        var session = NewSession();
        using var bus = Open(session, 1);
        using var wire = new Wire(session, 2);
        using var node = CanOpen.OpenNode(bus, Producer, new CanOpenNodeOptions { SrdoCount = 1 });
        AddApplicationObjects(node.ObjectDictionary);
        node.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2001, 0x00, 8), TimeSpan.FromMilliseconds(30));
        node.Safety().CommitSafetyConfiguration();
        node.StoreParameters();
        node.ObjectDictionary.WriteUnsigned(0x1301, 1, 0);
        node.ObjectDictionary.WriteUnsigned(0x1301, 2, 40);
        wire.SendNmt(NmtCommand.ResetCommunication, Producer);
        await WaitUntilAsync(() => node.ObjectDictionary.ReadUnsigned(0x1301, 2) == 30, "restored");
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(1u);
        node.ObjectDictionary.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
    }

    /// <summary>An ICanOpenNode this library did not create: every member throws.</summary>
    private sealed class ForeignNode : ICanOpenNode
    {
        // Implement every member of ICanOpenNode with `throw new NotImplementedException();`
        // (properties as `=> throw …`, events as `{ add { } remove { } }`). Nothing is called.
    }
}
```

(Write the `ForeignNode` members out in full; the interface has 11 events, 5 properties and 24 methods — see `ICanOpenNode.cs`. A stub is enough because only the extension's type test runs.)

- [ ] **Step 2: Run to verify failure** — compile error: `Safety()`, `ICanOpenSafety` missing.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs
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
    /// <c>13FEh</c> to 0; call <see cref="CommitSafetyConfiguration"/> afterwards.</summary>
    /// <exception cref="InvalidOperationException">The node has no SRDOs, or is Operational (0800 0022h).</exception>
    /// <exception cref="ArgumentOutOfRangeException">The number is not 1..<see cref="SrdoCount"/>, or a time is out of range.</exception>
    /// <exception cref="ArgumentException">A value the dictionary refused (the abort code is in the message), or no COB-ID for a node-id above 64.</exception>
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

    /// <summary>The current validity of an SRDO.</summary>
    SrdoState GetSrdoState(int srdoNumber);

    /// <summary>Sends the global failsafe command (§8.2: COB-ID 001h, DLC 0). Requires
    /// <c>1300h</c> = 1 and Operational.</summary>
    /// <exception cref="InvalidOperationException">1300h is 0 or the node is not Operational.</exception>
    Task SendGlobalFailsafeCommandAsync(CancellationToken cancellationToken = default);

    /// <summary>A consumer SRDO received a valid pair and wrote it to the mapped objects.</summary>
    event EventHandler<SrdoReceivedEventArgs>? SrdoReceived;

    /// <summary>An SRDO became valid or invalid (transitions only; never dropped).</summary>
    event EventHandler<SrdoStateChangedEventArgs>? SrdoStateChanged;

    /// <summary>A GFC arrived while <c>1300h</c> = 1 (including the node's own, on a bus that echoes).</summary>
    event EventHandler<GlobalFailsafeCommandReceivedEventArgs>? GlobalFailsafeCommandReceived;
}
```

```csharp
// src/CanKit.Pro.CANopen/Safety/CanOpenSafetyExtensions.cs
using System;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>Reaches the safety layer of a node without widening <see cref="ICanOpenNode"/>
/// (adding members to a published interface breaks every implementer; see the DisposeAsync
/// extension for the precedent).</summary>
public static class CanOpenSafetyExtensions
{
    /// <summary>The <see cref="ICanOpenSafety"/> of a node this library created.</summary>
    /// <exception cref="NotSupportedException">The node is not one of this library's.</exception>
    public static ICanOpenSafety Safety(this ICanOpenNode node)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        return node as ICanOpenSafety
            ?? throw new NotSupportedException("CANopen Safety is available on the nodes CanOpen.OpenNode creates.");
    }
}
```

Append to `CanOpenNode.Safety.cs` (the class declaration of this part becomes `internal sealed partial class CanOpenNode : ICanOpenSafety, ISrdoEngineHost`):

```csharp
    private readonly SrdoEngine _srdo;   // constructed in CanOpenNode's constructor, see below

    // ---- ICanOpenSafety ----------------------------------------------------------------------

    public event EventHandler<SrdoReceivedEventArgs>? SrdoReceived;
    public event EventHandler<SrdoStateChangedEventArgs>? SrdoStateChanged;
    public event EventHandler<GlobalFailsafeCommandReceivedEventArgs>? GlobalFailsafeCommandReceived;

    public int SrdoCount => _srdoCount;

    public void ConfigureSrdoProducer(int srdoNumber, SrdoMapping mapping, TimeSpan refreshTime, uint? cobId1 = null, uint? cobId2 = null)
        => ConfigureSrdo(srdoNumber, SrdoDirection.Transmit, mapping, refreshTime, null, cobId1, cobId2, nameof(refreshTime));

    public void ConfigureSrdoConsumer(int srdoNumber, SrdoMapping mapping, TimeSpan safeguardCycleTime, TimeSpan validationTime, uint? cobId1 = null, uint? cobId2 = null)
        => ConfigureSrdo(srdoNumber, SrdoDirection.Receive, mapping, safeguardCycleTime, validationTime, cobId1, cobId2, nameof(safeguardCycleTime));

    private void ConfigureSrdo(int srdoNumber, SrdoDirection direction, SrdoMapping mapping, TimeSpan cycle, TimeSpan? validation,
        uint? cobId1, uint? cobId2, string cycleParamName)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        if (mapping is null) throw new ArgumentNullException(nameof(mapping));
        ushort cycleMs = ToMilliseconds16(cycle, cycleParamName, allowZero: false);
        byte srvtMs = 0;
        if (validation is { } v)
        {
            long ms = (long)Math.Round(v.TotalMilliseconds);
            if (ms is < 1 or > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(validation), v, "SRVT must be 1 ms .. 255 ms: 1301h:03 is an UNSIGNED8 in ms (CiA DSP 304 §8.4.2.2).");
            srvtMs = (byte)ms;
        }
        uint id1 = cobId1 ?? (srdoNumber == 1 && _nodeId <= 64
            ? CanOpenCobId.SrdoDefaultCobId1(_nodeId)
            : throw new ArgumentException("No pre-defined COB-ID for this SRDO (only SRDO 1 of a node-id 1..64 has one, CiA DSP 304 §8.3.3); pass cobId1.", nameof(cobId1)));
        uint id2 = cobId2 ?? id1 + 1;
        RequireNotOperationalForSafetyWrite();
        var entries = mapping.ToArray();
        RunOnActorAndWait(() =>
        {
            var comm = SrdoRecords.CommIndex(srdoNumber);
            var map = SrdoRecords.MapIndex(srdoNumber);
            try
            {
                _od.Transaction(() =>
                {
                    _od.WriteUnsigned(comm, 0x01, 0);
                    _od.WriteUnsigned(map, 0x00, 0);
                    for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++)
                    {
                        int i = (s - 1) / 2;
                        _od.WriteUnsigned(map, s, i < entries.Length ? EncodeMappingEntry(entries[i]) : 0u);
                    }
                    _od.WriteUnsigned(map, 0x00, (uint)(2 * entries.Length));
                    _od.WriteUnsigned(comm, 0x02, cycleMs);
                    if (direction == SrdoDirection.Receive) _od.WriteUnsigned(comm, 0x03, srvtMs);
                    _od.WriteUnsigned(comm, 0x05, id1);
                    _od.WriteUnsigned(comm, 0x06, id2);
                    _od.WriteUnsigned(comm, 0x01, (byte)direction);
                });
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"SRDO{srdoNumber} configuration rejected: {ex.Message}", ex);
            }
        });
    }

    public void DeleteSrdo(int srdoNumber)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        RequireNotOperationalForSafetyWrite();
        RunOnActorAndWait(() => _od.WriteUnsigned(SrdoRecords.CommIndex(srdoNumber), 0x01, 0));
    }

    public void CommitSafetyConfiguration()
    {
        ThrowIfDisposed();
        if (_srdoCount == 0) throw NoSrdos();
        RequireNotOperationalForSafetyWrite();
        RunOnActorAndWait(() => _od.Transaction(() =>
        {
            // Every 13FFh:n write clears 13FEh, so the checksums go first and A5h last (§9.2).
            for (int n = 1; n <= _srdoCount; n++)
            {
                ushort crc = 0;
                if (SrdoRecords.TryReadCommunication(_od, n, out var p) && p.Direction != SrdoDirection.None)
                    crc = SrdoCrc.Compute(p, SrdoMapping.FromEntries(SrdoRecords.ReadMapping(_od, n)));
                _od.WriteUnsigned(Co.SrdoChecksum, (byte)n, crc);
            }
            _od.WriteUnsigned(Co.SrdoConfigurationValid, 0x00, SrdoRecords.ConfigurationValidValue);
        }));
    }

    public Task TriggerSrdoAsync(int srdoNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireSrdo(srdoNumber);
        return _actor.PostAsync(() => _srdo.Trigger(srdoNumber), cancellationToken);
    }

    public SrdoState GetSrdoState(int srdoNumber)
    {
        RequireSrdo(srdoNumber);
        return _srdo.GetState(srdoNumber);
    }

    public Task SendGlobalFailsafeCommandAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _actor.PostAsync(() =>
        {
            if (!_srdo.TrySendGfc())
                throw new InvalidOperationException("The global failsafe command needs 1300h = 1 and NMT state Operational (CiA DSP 304 §8.2).");
        }, cancellationToken);
    }

    private void RequireSrdo(int srdoNumber)
    {
        if (_srdoCount == 0) throw NoSrdos();
        if (srdoNumber < 1 || srdoNumber > _srdoCount)
            throw new ArgumentOutOfRangeException(nameof(srdoNumber), srdoNumber, $"SRDO number must be 1..{_srdoCount}.");
    }

    private static InvalidOperationException NoSrdos()
        => new("This node has no SRDOs: open it with CanOpenNodeOptions.SrdoCount > 0 or with a device description that declares SRDO records.");

    /// <summary>§8.3.2.4 note 1, surfaced as the exception the caller can act on; the validator
    /// refuses the write with 0800 0022h as well, should the state change in between.</summary>
    private void RequireNotOperationalForSafetyWrite()
    {
        if (_state == NmtState.Operational)
            throw new InvalidOperationException("Safety parameters cannot be written in NMT state Operational (abort 0800 0022h, CiA DSP 304 §8.3.2.4).");
    }

    // ---- ISrdoEngineHost (actor loop) ----------------------------------------------------------

    void ISrdoEngineHost.Send(uint cobId, byte[] payload) => _ = SendControlFrame(cobId, payload);

    void ISrdoEngineHost.EmitEmcy(ushort errorCode)
    {
        if (!_emcyValid) return;
        var errorRegister = (byte)_od.ReadUnsigned(Co.ErrorRegister, 0x00);
        _ = EmitEmcy(new EmcyMessage(_nodeId, errorCode, errorRegister));
    }

    void ISrdoEngineHost.SrdoReceived(int srdoNumber, uint cobId, byte[] payload) => RaiseSrdoReceived(srdoNumber, cobId, payload);
    void ISrdoEngineHost.SrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason) => RaiseSrdoStateChanged(srdoNumber, isValid, reason);
    void ISrdoEngineHost.GlobalFailsafeCommandReceived() => RaiseGlobalFailsafeCommandReceived();
    void ISrdoEngineHost.ReportBackgroundException(Exception exception) => RaiseBackgroundException(exception);

    private void RaiseSrdoReceived(int srdoNumber, uint cobId, byte[] payload)
    {
        var args = new SrdoReceivedEventArgs(srdoNumber, cobId, payload, DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(SrdoReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        });
    }

    private void RaiseSrdoStateChanged(int srdoNumber, bool isValid, SrdoInvalidReason? reason)
    {
        var args = new SrdoStateChangedEventArgs(srdoNumber, isValid, reason, DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(SrdoStateChanged, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.SrdoState(srdoNumber, isValid, reason), emcyProducer: -1);
    }

    private void RaiseGlobalFailsafeCommandReceived()
    {
        var args = new GlobalFailsafeCommandReceivedEventArgs(DateTime.UtcNow);
        EnqueueEvent(() =>
        {
            try { DeliverToSubscribers(GlobalFailsafeCommandReceived, args); }
            catch (Exception ex) { RaiseBackgroundException(ex); }
        }, critical: true, EventKey.GlobalFailsafeCommand(), emcyProducer: -1);
    }
```

Add `using CanKit.Pro.CANopen.Emcy; using CanKit.Pro.CANopen.Nmt; using System.Threading; using System.Threading.Tasks;` to that file. `EncodeMappingEntry` already exists in `CanOpenNode.Pdo.cs` (private static) and is reachable from the partial.

`CanOpenNode.cs`:
- after `_deadlines = new DeadlineScheduler(_actor);` (line 206): `_srdo = new SrdoEngine(_actor, _actor.TimeSource, _deadlines, _od, _nodeId, _srdoCount, this);` — but `_srdoCount` is assigned before `PopulateCommunicationProfile()`; move the `_srdoCount = …` line above this one (both before `PopulateCommunicationProfile()`).
- subscription filter: `|| id == CanOpenCobId.GlobalFailsafeCommand` after `id == CanOpenCobId.NmtCommand`.
- `HandleIncoming`, after the flying-master `return;` block:

```csharp
            // CiA DSP 304: SRDO pairs on this node's configured ids, GFC on 001h. Before SYNC
            // and the RPDO table: an SRDO id can be nothing else on this node (the validators
            // keep 101h–180h and 001h out of every other object).
            if (_srdo.TryHandleFrame(cobId, data, isRtr)) return;
```

- `CleanUpOnActor`: `_srdo.Dispose();` after `DisposePdoRuntime();`.
- `EventKey`: add

```csharp
        public static EventKey SrdoState(int srdo, bool isValid, Safety.SrdoInvalidReason? reason)
            => new(4, (byte)srdo, isValid ? 0xFFUL : (ulong)(reason.HasValue ? (int)reason.Value : 0xFE));
        public static EventKey GlobalFailsafeCommand() => new(5, 0, 0);
```

`CanOpenNode.CommunicationProfile.cs`:
- `ApplyAllCommunicationObjects`: after the PDO loop, `for (int n = 1; n <= _srdoCount; n++) _srdo.Rebuild(n);`
- `ApplyCommunicationObject`: before the `switch`, `if (Safety.SrdoRecords.SrdoNumberOf(index) is { } srdo) { _srdo.Rebuild(srdo); return; }`
- `ApplyNmtTransition` after line 644: `if (previous == NmtState.Operational && target != NmtState.Operational) _srdo.LeaveOperational(); if (target == NmtState.Operational && previous != NmtState.Operational) _srdo.EnterOperational();`
- `PerformNmtReset` line 685: `if (_state == NmtState.Operational) { OnLeaveOperational(); _srdo.LeaveOperational(); }`
- `OnOdEntryWrittenForCommunicationProfile`: extend the CoS condition: `if (index is (>= Co.TpdoComm …) || Safety.SrdoRecords.IsCommunicationRecord(index) || Safety.SrdoRecords.IsMappingRecord(index)) RebuildCosRelevantEntries();`

`CanOpenNode.Pdo.cs`:
- `OnOdEntryWrittenForCoS` first line becomes `if (!_options.EnableChangeOfStateTpdo && !_options.EnableChangeOfStateSrdo) return;`
- `RebuildCosRelevantEntries`: after the TPDO block, `if (_options.EnableChangeOfStateSrdo) _srdo.CollectChangeOfStateEntries(set, CosKey);`
- `EvaluateCoSOnActor`: after the TPDO loop:

```csharp
        if (_options.EnableChangeOfStateSrdo)
        {
            for (int n = 1; n <= _srdoCount; n++)
            {
                if (_srdo.GetState(n).Direction != Safety.SrdoDirection.Transmit) continue;
                bool hit = false;
                foreach (var e in Safety.SrdoRecords.ReadMapping(_od, n))
                {
                    if (dirty.Contains(CosKey(e.Index, e.Subindex))) { hit = true; break; }
                }
                if (hit) _srdo.Trigger(n);
            }
        }
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenSafetyNodeTests"` then the whole CANopen filter — PASS. Mutation: swap the order in `CommitSafetyConfiguration` (A5h first) → `Commit_Writes_Checksums_Then_Valid` fails; restore.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyNodeTests.cs
git commit -m "feat(canopen): ICanOpenSafety — configure, commit, trigger and observe SRDOs and the GFC on a node"
```

---

### Task 8: Device descriptions — loading the safety objects, `PeerSafetyConfiguration`

**Files:**
- Create: `src/CanKit.Pro.CANopen/Safety/PeerSafety.cs` (`PeerSafetyConfiguration`, `PeerSafetyMismatch`, `PeerSafetyResult`, `ForeignSrdoObserveResult`), `tests/CanKit.Pro.Tests/TestCases/CANopen/Fixtures/safety.dcf`
- Modify: `src/CanKit.Pro.CANopen/CanOpenNode.DeviceDescription.cs` — `ApplyDescribedObject` (line 168, new branches before the "everything else is data" tail), `PdoRecordRank` (line 115, rank the SRDO records like PDO records), `ApplyDeviceDescription` (line 45, remove undeclared SRDO records like PDOs); `src/CanKit.Pro.CANopen/CanOpenNode.Safety.cs` — `DescribedSrdoCount`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyDeviceDescriptionTests.cs`

**Interfaces:**
- Consumes: `DescribedEntry`, `EntriesOf`, `ApplyManagedVariable`, `ApplyManagedValue`, `ParseUnsigned(DescribedEntry, out string?)`, `MapAccess`, `_od.Declare/Remove/TryWriteRaw`, `DeviceDescriptionFinding`/`DeviceDescriptionOutcome`; `CanOpenDeviceDescription.Objects.Objects` (`CanOpenObject` with `SubObjects`, `ParameterValue`, `DefaultValue`), `CanOpenValueConverter.Parse`.
- Produces:

```csharp
public sealed class PeerSafetyConfiguration
{
    public bool GlobalFailsafeCommandEnabled { get; init; }
    public IReadOnlyDictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)> Srdos { get; }
    public PeerSafetyConfiguration Add(int srdoNumber, SrdoCommunicationParameter parameter, SrdoMapping mapping);
    public static PeerSafetyConfiguration FromDeviceDescription(CanOpenDeviceDescription description, byte nodeId);
    public bool DeclaresAnySrdo => Srdos.Count > 0;
}
public sealed class PeerSafetyMismatch { ushort Index; byte Subindex; byte[] Expected; byte[] Actual; }
public sealed class PeerSafetyResult { bool Succeeded; IReadOnlyList<PeerSafetyMismatch> Mismatches; }
public sealed class ForeignSrdoObserveResult { uint CobId1; ForeignPdoObservation? Observation; string? Reason; }
```
  and `private static int DescribedSrdoCount(CanOpenDeviceDescription? description)` returning the highest n in 1..64 for which `1300h + n` or `1380h + n` is declared.

- [ ] **Step 1: Write the fixture and the failing tests**

`safety.dcf` — a device commissioned as node 5 with SRDO 1 as producer of `2000h` (16 bit) and `2001h` (8 bit) on the pre-defined ids, SRDO 2 declared but direction 0, and 13FEh/13FFh. Start from a copy of `device.dcf`, keep its `[FileInfo]`, `[DeviceComissioning]` (`NodeID=5`), `[DeviceInfo]`, `[MandatoryObjects]`, `[1000]`, `[1001]`, `[1018]…` blocks, and set:

```ini
[OptionalObjects]
SupportedObjects=7
1=0x1300
2=0x1301
3=0x1302
4=0x1381
5=0x1382
6=0x13FE
7=0x13FF

[1300]
ParameterName=Global failsafe command parameter
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=0
ParameterValue=1
PDOMapping=0

[1301]
ParameterName=SRDO communication parameter 1
SubNumber=7
ObjectType=0x9

[1301sub0]
ParameterName=Number of entries
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=6
PDOMapping=0

[1301sub1]
ParameterName=Information direction
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=0
ParameterValue=1
PDOMapping=0

[1301sub2]
ParameterName=Refresh-time/SCT
ObjectType=0x7
DataType=0x0006
AccessType=rw
DefaultValue=25
ParameterValue=30
PDOMapping=0

[1301sub3]
ParameterName=SRVT
ObjectType=0x7
DataType=0x0005
AccessType=rw
DefaultValue=20
PDOMapping=0

[1301sub4]
ParameterName=Transmission type
ObjectType=0x7
DataType=0x0005
AccessType=ro
DefaultValue=254
PDOMapping=0

[1301sub5]
ParameterName=COB-ID 1
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=$NODEID+0x0FF+$NODEID
ParameterValue=0x109
PDOMapping=0

[1301sub6]
ParameterName=COB-ID 2
ObjectType=0x7
DataType=0x0007
AccessType=rw
DefaultValue=$NODEID+0x100+$NODEID
ParameterValue=0x10A
PDOMapping=0
```

(`1302` the same with no `ParameterValue` on sub1 and `0` on sub5/sub6; `1381` as an ARRAY record `SubNumber=5` with sub0 `DataType=0x0005 ParameterValue=4`, sub1..sub4 `DataType=0x0007` with `ParameterValue=0x20000010, 0x20000010, 0x20000008, 0x20000008`; `1382` with sub0 `ParameterValue=0`; `13FE` VAR `DataType=0x0005 ParameterValue=0xA5`; `13FF` ARRAY `SubNumber=3`, sub0 `DefaultValue=2`, sub1 `DataType=0x0006 ParameterValue=0x0000` — the test overwrites sub1 with the computed CRC, see below —, sub2 `ParameterValue=0`.) Also declare the application objects `[2000]` (`DataType=0x0006`, `AccessType=ro`, `PDOMapping=1`, `DefaultValue=0x1234`) and `[2001]` (`DataType=0x0005`, `AccessType=ro`, `PDOMapping=1`, `DefaultValue=0x5A`) under `[ManufacturerObjects]`.

Because the correct CRC depends on the implementation's byte order, the fixture carries `13FF sub1 = 0x0000` and the test computes the right value with `SrdoCrc`; a second test variant patches the text in memory.

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyDeviceDescriptionTests.cs
using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>The safety objects in an EDS/DCF (CiA 306) are loaded like the PDO records: the
/// records the file declares exist, their values are taken through the validated path, and
/// every deviation is a finding (FR-CO-025..028 applied to CiA 304).</summary>
public class CanOpenSafetyDeviceDescriptionTests : IClassFixture<VirtualAdapterFixture>
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", name);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static string SafetyDcfText() => File.ReadAllText(Fixture("safety.dcf"));

    /// <summary>The fixture with 13FFh:01 set to the checksum this implementation computes.</summary>
    private static CanOpenDeviceDescription SafetyDcf()
    {
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8);
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        var text = SafetyDcfText().Replace("ParameterValue=0x0000", $"ParameterValue=0x{crc:X4}");
        return CanOpenDeviceDescription.ParseDcf(text);
    }

    [Fact]
    public void Srdo_Count_Follows_The_Highest_Declared_Record()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, SafetyDcf());
        node.NodeId.Should().Be(5);
        node.Safety().SrdoCount.Should().Be(2);
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue();
        node.ObjectDictionary.ContainsIndex(0x1303).Should().BeFalse();
    }

    [Fact]
    public void Described_Values_Reach_The_Records_And_The_Configuration_Is_Valid()
    {
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, SafetyDcf());
        var od = node.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(1u);
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(30u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x109u);
        od.ReadUnsigned(0x1301, 6).Should().Be(0x10Au);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1381, 3).Should().Be(0x2000_0008u);
        od.ReadUnsigned(0x1302, 1).Should().Be(0u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u, "the file is the power-on state; 13FEh is applied last");
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
        node.DeviceDescription!.Findings.Where(f => f.Index is >= 0x1300 and <= 0x13FF).Should().BeEmpty(
            string.Join("\n", node.DeviceDescription.Findings));
        node.Safety().GetSrdoState(1).Direction.Should().Be(SrdoDirection.Transmit);
    }

    [Fact]
    public void A_Rejected_Value_Is_A_Finding_With_Its_Abort_Code()
    {
        var text = SafetyDcfText().Replace("ParameterValue=0x109", "ParameterValue=0x108");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text));
        var finding = node.DeviceDescription!.Findings.Single(f => f.Index == 0x1301 && f.Subindex == 5);
        finding.Outcome.Should().Be(DeviceDescriptionOutcome.Corrected);
        finding.AbortCode.Should().Be(Sdo.SdoAbortCode.ValueRangeExceeded);
        node.ObjectDictionary.ReadUnsigned(0x1301, 5).Should().Be(0x109u, "the default for node 5 is kept");
        node.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(1u, "sub1 is written last and the kept ids are consistent");
    }

    [Fact]
    public void Undeclared_Srdo_Records_Do_Not_Exist_And_SrdoCount_Option_Adds_None()
    {
        var text = SafetyDcfText().Replace("3=0x1302\n", "").Replace("5=0x1382\n", "").Replace("SupportedObjects=7", "SupportedObjects=5");
        var session = VirtualAdapterFixture.NewSession("co-safety-dcf");
        using var bus = Open(session, 1);
        using var node = CanOpen.OpenNode(bus, CanOpenDeviceDescription.ParseDcf(text), new CanOpenNodeOptions { SrdoCount = 4 });
        node.Safety().SrdoCount.Should().Be(4, "the option is a floor");
        node.ObjectDictionary.ContainsIndex(0x1302).Should().BeTrue("the option created it");
        node.ObjectDictionary.ReadUnsigned(0x13FF, 0).Should().Be(4u);
    }

    [Fact]
    public void Peer_Configuration_From_A_Dcf()
    {
        var configuration = PeerSafetyConfiguration.FromDeviceDescription(SafetyDcf(), 5);
        configuration.GlobalFailsafeCommandEnabled.Should().BeTrue();
        configuration.Srdos.Should().ContainKey(1);
        configuration.Srdos.Should().NotContainKey(2, "direction 0 is deleted");
        var (parameter, mapping) = configuration.Srdos[1];
        parameter.Should().Be(new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A));
        mapping.Entries.Select(e => (e.Index, e.Subindex, e.BitLength)).Should().Equal(((ushort)0x2000, (byte)0, (byte)16), ((ushort)0x2001, (byte)0, (byte)8));
        configuration.DeclaresAnySrdo.Should().BeTrue();
        PeerSafetyConfiguration.FromDeviceDescription(CanOpenDeviceDescription.Load(Fixture("device.dcf")), 5).DeclaresAnySrdo.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure** — `SrdoCount` is 0 for the DCF node (loader ignores the records), `PeerSafetyConfiguration` missing.

- [ ] **Step 3: Implement**

`CanOpenNode.Safety.cs`, replace `DescribedSrdoCount`:

```csharp
    private static int DescribedSrdoCount(CanOpenDeviceDescription? description)
    {
        if (description is null) return 0;
        int highest = 0;
        foreach (var index in description.Objects.Objects.Keys)
        {
            if (SrdoRecords.SrdoNumberOf(index) is { } n) highest = Math.Max(highest, n);
        }
        return highest;
    }
```

`CanOpenNode.DeviceDescription.cs`:
- `PdoRecordRank`: add `>= 0x1301 and <= 0x1340 => 1, >= 0x1381 and <= 0x13C0 => 2, 0x13FF => 3, 0x13FE => 4,` (communication records before mappings as with PDOs — the mapping is written while the SRDO is deleted, the record's sub1 is applied in a deferred step like `pendingPdoCreates`; checksums after everything; 13FEh last because every checksummed write clears it).
- `ApplyDeviceDescription`: beside the PDO removal loop, `for (int n = 1; n <= _srdoCount; n++) { if (!objects.ContainsKey(SrdoRecords.CommIndex(n))) RemoveObject(SrdoRecords.CommIndex(n)); if (!objects.ContainsKey(SrdoRecords.MapIndex(n))) RemoveObject(SrdoRecords.MapIndex(n)); }` — but only when `_options.SrdoCount < n` (the option is a floor: records it asked for stay). Add a `List<(int Srdo, byte Direction, string Raw)> pendingSrdoCreates` next to `pendingPdoCreates`, pass it into `ApplyDescribedObject`, and after the PDO creates loop:

```csharp
        foreach (var (srdo, direction, raw) in pendingSrdoCreates)
        {
            if (!_od.TryWriteRaw(SrdoRecords.CommIndex(srdo), 0x01, new[] { direction }, out var abort))
                findings.Add(new DeviceDescriptionFinding(SrdoRecords.CommIndex(srdo), 0x01, DeviceDescriptionOutcome.Corrected,
                    "the direction was rejected; the SRDO stays deleted", raw, abort));
        }
```

- `ApplyDescribedObject`: before the "everything else is data" tail:

```csharp
        if (index == Co.GfcParameter || index == Co.SrdoConfigurationValid)
            return ApplyManagedVariable(index, entries, findings);
        if (index == Co.SrdoChecksum)
            return ApplySafetyChecksumArray(entries, findings);
        if (SrdoRecords.IsCommunicationRecord(index))
            return ApplySrdoCommunicationRecord(index, entries, findings, pendingSrdoCreates);
        if (SrdoRecords.IsMappingRecord(index))
            return ApplySrdoMappingRecord(index, entries, findings);
```

and the three methods, modelled on `ApplyPdoCommunicationRecord` / `ApplyPdoMappingRecord`:

```csharp
    private int ApplySrdoCommunicationRecord(ushort index, List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings,
        List<(int, byte, string)> pendingSrdoCreates)
    {
        int n = index - SrdoRecords.CommunicationBase;
        if (n > _srdoCount) return 0; // cannot happen: _srdoCount covers every declared record
        int loaded = 0;
        var described = new Dictionary<byte, DescribedEntry>();
        foreach (var entry in entries) described[entry.Subindex] = entry;
        foreach (byte sub in new byte[] { 1, 2, 3, 5, 6 })
        {
            if (!described.TryGetValue(sub, out var entry))
            {
                findings.Add(new DeviceDescriptionFinding(index, sub, DeviceDescriptionOutcome.SuppliedDefault,
                    "CiA DSP 304 §8.4.2.2 makes this sub-index mandatory; the node keeps its default"));
                continue;
            }
            if (!_od.TryGet(index, sub, out var current)) continue;
            _od.Declare(index, sub, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
        }
        foreach (var entry in entries.Where(e => e.Subindex is > 6 or 4 && e.Subindex != 0))
            findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                entry.Subindex == 4 ? "the transmission type is the constant 254 (CiA DSP 304 §8.4.2.2)" : "no such sub-index in an SRDO communication parameter record", entry.Value));
        // Values with the SRDO deleted: times first, then the ids (COB-ID 1 before COB-ID 2 — sub6 is validated against sub5).
        _od.WriteUnsigned(index, 0x01, 0);
        foreach (byte sub in new byte[] { 2, 3, 5, 6 })
        {
            if (!described.TryGetValue(sub, out var entry) || !_od.TryGet(index, sub, out var current)) continue;
            ApplyManagedValue(index, sub, current.DataType, entry, findings);
        }
        if (described.TryGetValue(1, out var direction) && !string.IsNullOrEmpty(direction.Value))
        {
            var value = ParseUnsigned(direction, out var raw);
            if (value is null)
                findings.Add(new DeviceDescriptionFinding(index, 0x01, DeviceDescriptionOutcome.Corrected, "the direction could not be read as UNSIGNED8; the SRDO stays deleted", raw));
            else if (value.Value != 0)
                pendingSrdoCreates.Add((n, (byte)value.Value, raw ?? ""));
        }
        return loaded;
    }

    private int ApplySrdoMappingRecord(ushort index, List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int n = index - SrdoRecords.MappingBase;
        int loaded = 0;
        var byIndex = entries.ToDictionary(e => e.Subindex);
        foreach (var entry in entries)
        {
            if (entry.Subindex > SrdoRecords.MappingSubindices)
            {
                findings.Add(new DeviceDescriptionFinding(index, entry.Subindex, DeviceDescriptionOutcome.Omitted,
                    $"a byte-aligned SRDO mapping holds at most {SrdoMapping.MaxEntries} objects (16 sub-indices); this sub-index is not created", entry.Value));
                continue;
            }
            if (!_od.TryGet(index, entry.Subindex, out var current)) continue;
            _od.Declare(index, entry.Subindex, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
        }
        _od.WriteUnsigned(SrdoRecords.CommIndex(n), 0x01, 0); // the mapping is written with the SRDO deleted
        uint count = 0;
        bool failed = false;
        if (byIndex.TryGetValue(0, out var sub0) && !string.IsNullOrEmpty(sub0.Value))
        {
            if (ParseUnsigned(sub0, out _) is { } parsed) count = parsed;
            else { findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected, "the mapping count could not be read as UNSIGNED8; the mapping stays disabled", sub0.Value)); failed = true; }
        }
        _od.WriteUnsigned(index, 0x00, 0);
        for (byte s = 1; s <= Math.Min(count, (uint)SrdoRecords.MappingSubindices); s++)
        {
            if (!byIndex.TryGetValue(s, out var entry) || string.IsNullOrEmpty(entry.Value)) continue;
            var value = ParseUnsigned(entry, out var raw);
            if (value is null) { findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected, "the mapping entry could not be read as UNSIGNED32; the slot stays empty and the mapping stays disabled", raw)); failed = true; continue; }
            if (!_od.TryWriteRaw(index, s, ObjectDictionary.EncodeU32(value.Value), out var abort))
            { findings.Add(new DeviceDescriptionFinding(index, s, DeviceDescriptionOutcome.Corrected, "the mapping entry was rejected; the slot stays empty and the mapping stays disabled", raw, abort)); failed = true; }
        }
        if (!failed && count > 0 && !_od.TryWriteRaw(index, 0x00, new[] { (byte)count }, out var countAbort))
            findings.Add(new DeviceDescriptionFinding(index, 0, DeviceDescriptionOutcome.Corrected, "the mapping count was rejected; the mapping stays disabled", sub0.Value, countAbort));
        return loaded;
    }

    private int ApplySafetyChecksumArray(List<DescribedEntry> entries, List<DeviceDescriptionFinding> findings)
    {
        int loaded = 0;
        foreach (var entry in entries)
        {
            if (entry.Subindex == 0 || entry.Subindex > _srdoCount)
            {
                if (entry.Subindex != 0)
                    findings.Add(new DeviceDescriptionFinding(Co.SrdoChecksum, entry.Subindex, DeviceDescriptionOutcome.Omitted, $"the node holds {_srdoCount} SRDO checksum(s)", entry.Value));
                continue;
            }
            if (!_od.TryGet(Co.SrdoChecksum, entry.Subindex, out var current)) continue;
            _od.Declare(Co.SrdoChecksum, entry.Subindex, current.DataType, MapAccess(entry.Access), current.GetRawValue(), pdoMappable: false);
            loaded++;
            ApplyManagedValue(Co.SrdoChecksum, entry.Subindex, current.DataType, entry, findings);
        }
        return loaded;
    }
```

The ordering of `PdoRecordRank` guarantees 13FEh is applied after every checksum; `ApplyManagedVariable` writes it through the validated path, which accepts A5h.

```csharp
// src/CanKit.Pro.CANopen/Safety/PeerSafety.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CanKit.Pro.CANopen.Pdo;
using EdsDcfNet.Models;

namespace CanKit.Pro.CANopen.Safety;

/// <summary>What a safety node's configuration should be: 1300h and, per SRDO number, the
/// communication parameter and the mapping. Built by hand or from a DCF's parameter values
/// (CiA 306). A record whose direction is 0 is not part of it — it is deleted on the peer.</summary>
public sealed class PeerSafetyConfiguration
{
    private readonly Dictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)> _srdos = new();
    private ReadOnlyDictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)>? _view;

    /// <summary>1300h: 1 when true.</summary>
    public bool GlobalFailsafeCommandEnabled { get; init; }

    /// <summary>The SRDOs to exist on the peer, by number.</summary>
    public IReadOnlyDictionary<int, (SrdoCommunicationParameter Parameter, SrdoMapping Mapping)> Srdos => _view ??= new(_srdos);

    /// <summary>True when at least one SRDO is configured — what makes a peer a safety slave for the boot-up.</summary>
    public bool DeclaresAnySrdo => _srdos.Count > 0;

    /// <summary>Adds or replaces SRDO <paramref name="srdoNumber"/> (1..64). The direction must not be None.</summary>
    public PeerSafetyConfiguration Add(int srdoNumber, SrdoCommunicationParameter parameter, SrdoMapping mapping)
    {
        if (srdoNumber is < 1 or > SrdoRecords.MaxSrdoCount) throw new ArgumentOutOfRangeException(nameof(srdoNumber));
        if (parameter.Direction == SrdoDirection.None) throw new ArgumentException("An SRDO in a configuration has a direction; leave it out to delete it.", nameof(parameter));
        _srdos[srdoNumber] = (parameter, mapping ?? throw new ArgumentNullException(nameof(mapping)));
        return this;
    }

    /// <summary>Reads 1300h, 1301h–1340h and 1381h–13C0h from the file's ParameterValue (DefaultValue
    /// when absent, $NODEID resolved with <paramref name="nodeId"/>). Records the file does not
    /// declare, or declares with direction 0, are absent. Malformed values make the record absent too.</summary>
    public static PeerSafetyConfiguration FromDeviceDescription(CanOpenDeviceDescription description, byte nodeId)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));
        var objects = description.Objects.Objects;
        var configuration = new PeerSafetyConfiguration
        {
            GlobalFailsafeCommandEnabled = objects.TryGetValue(SrdoRecords.GfcParameter, out var gfc) && Value(gfc, 0, nodeId) == 1,
        };
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
        {
            if (!objects.TryGetValue(SrdoRecords.CommIndex(n), out var comm)) continue;
            uint direction = Value(comm, 1, nodeId) ?? 0;
            if (direction is 0 or > 2) continue;
            uint? cycle = Value(comm, 2, nodeId), srvt = Value(comm, 3, nodeId), cob1 = Value(comm, 5, nodeId), cob2 = Value(comm, 6, nodeId);
            if (cycle is null || cob1 is null || cob2 is null) continue;
            var mapping = new SrdoMapping();
            if (objects.TryGetValue(SrdoRecords.MapIndex(n), out var map))
            {
                uint count = Value(map, 0, nodeId) ?? 0;
                bool ok = (count & 1) == 0 && count <= SrdoRecords.MappingSubindices;
                for (byte s = 1; ok && s <= count; s += 2)
                {
                    uint? raw = Value(map, s, nodeId);
                    if (raw is null or 0) { ok = false; break; }
                    try { mapping.Add(new PdoMappingEntry((ushort)(raw.Value >> 16), (byte)(raw.Value >> 8), (byte)raw.Value)); }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException) { ok = false; }
                }
                if (!ok) continue;
            }
            configuration.Add(n, new SrdoCommunicationParameter((SrdoDirection)direction,
                TimeSpan.FromMilliseconds(cycle.Value), TimeSpan.FromMilliseconds(srvt ?? 0),
                cob1.Value & CanOpenCobId.CanIdMask, cob2.Value & CanOpenCobId.CanIdMask), mapping);
        }
        return configuration;
    }

    private static uint? Value(CanOpenObject obj, byte subindex, byte nodeId)
    {
        string? text;
        if (obj.SubObjects.Count == 0)
        {
            if (subindex != 0) return null;
            text = string.IsNullOrEmpty(obj.ParameterValue) ? obj.DefaultValue : obj.ParameterValue;
        }
        else if (obj.SubObjects.TryGetValue(subindex, out var sub))
        {
            text = string.IsNullOrEmpty(sub.ParameterValue) ? sub.DefaultValue : sub.ParameterValue;
        }
        else return null;
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return Convert.ToUInt32(CanOpenValueConverter.Parse(text!, CanOpenDataType.Unsigned32, nodeId), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }
}

/// <summary>One (index, sub-index) whose value on the peer is not what was expected.</summary>
public sealed class PeerSafetyMismatch
{
    internal PeerSafetyMismatch(ushort index, byte subindex, byte[] expected, byte[] actual)
    {
        Index = index; Subindex = subindex; Expected = expected; Actual = actual;
    }
    /// <summary>Object index.</summary>
    public ushort Index { get; }
    /// <summary>Sub-index.</summary>
    public byte Subindex { get; }
    /// <summary>The bytes written (configuration) or computed (verification).</summary>
    public byte[] Expected { get; }
    /// <summary>The bytes read back.</summary>
    public byte[] Actual { get; }
    /// <inheritdoc />
    public override string ToString() => $"0x{Index:X4}:{Subindex:X2} expected {BitConverter.ToString(Expected)} read {BitConverter.ToString(Actual)}";
}

/// <summary>Outcome of <see cref="ICanOpenSafety.ConfigurePeerSafetyAsync"/> (succeeded =
/// acknowledged with 13FEh = A5h) and <see cref="ICanOpenSafety.VerifyPeerSafetyConfigurationAsync"/>
/// (succeeded = verified).</summary>
public sealed class PeerSafetyResult
{
    internal PeerSafetyResult(bool succeeded, IReadOnlyList<PeerSafetyMismatch> mismatches)
    {
        Succeeded = succeeded; Mismatches = mismatches;
    }
    /// <summary>No mismatch, and for a configuration the acknowledgement was written and read back.</summary>
    public bool Succeeded { get; }
    /// <summary>Every pair that differed; empty when <see cref="Succeeded"/>.</summary>
    public IReadOnlyList<PeerSafetyMismatch> Mismatches { get; }
}

/// <summary>Outcome of <see cref="ICanOpenSafety.ObserveForeignSrdoAsync"/>.</summary>
public sealed class ForeignSrdoObserveResult
{
    internal ForeignSrdoObserveResult(uint cobId1, ForeignPdoObservation? observation, string? reason)
    {
        CobId1 = cobId1; Observation = observation; Reason = reason;
    }
    /// <summary>The id of the plain-data frame the caller passed.</summary>
    public uint CobId1 { get; }
    /// <summary>The decoding, with <see cref="ForeignPdoObservation.Kind"/> = <see cref="ForeignPdoKind.Srdo"/>; null when no record matched.</summary>
    public ForeignPdoObservation? Observation { get; }
    /// <summary>Why nothing was decoded, when <see cref="Observation"/> is null or not decoded.</summary>
    public string? Reason { get; }
}
```

(`ForeignPdoKind.Srdo` is added in Task 11; until then reference it as the enum member you add now: in `ForeignPdo.cs` append `/// <summary>An SRDO (CiA DSP 304), decoded by ObserveForeignSrdoAsync; PdoNumber is the SRDO number.</summary> Srdo = 2,`.)

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenSafetyDeviceDescriptionTests|FullyQualifiedName~CanOpenDeviceDescription"` — PASS (the existing description tests prove `device.eds`/`device.dcf`/`quirky.eds`, which declare no safety object, are untouched).

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen tests/CanKit.Pro.Tests/TestCases/CANopen/Fixtures/safety.dcf tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyDeviceDescriptionTests.cs
git commit -m "feat(canopen): load the CiA 304 safety objects from an EDS/DCF and build a peer safety configuration from one"
```

---

### Task 9: Master/tool — §9.2 peer configuration and verification

**Files:**
- Create: `src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs`
- Modify: `src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs` (two members)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenPeerSafetyTests.cs`

**Interfaces:**
- Consumes: `SdoUploadAsync(byte, ushort, byte, CancellationToken)`, `SdoDownloadAsync(byte, ushort, byte, ReadOnlyMemory<byte>, CancellationToken)` (both go through `EnsurePeerSdoAccess`), `PeerSafetyConfiguration`, `SrdoCrc`, `ObjectDictionary.EncodeU32`.
- Produces on `ICanOpenSafety`:

```csharp
    /// <summary>§9.2 Figure 9: downloads 1300h, every record (deleted first, created last), the
    /// checksums 13FFh:n, reads everything back and compares byte for byte, and only then writes
    /// 13FEh = A5h and reads it back. Every transfer passes the peer-SDO gate. One operation per
    /// peer at a time. An SDO abort (for example 0800 0022h — the peer is Operational), a
    /// timeout or a gate refusal propagates as from SdoDownloadAsync; the peer is then left
    /// with 13FEh = 0, because every parameter write clears it.</summary>
    Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>§8.3.1 step D: uploads 13FEh (must be A5h), 13FFh:n (must equal the checksum of
    /// the expected record) and the records themselves, and compares. Nothing is written.</summary>
    Task<PeerSafetyResult> VerifyPeerSafetyConfigurationAsync(byte peerNodeId, PeerSafetyConfiguration expected, CancellationToken cancellationToken = default);
```
  and in the node `private IEnumerable<(ushort Index, byte Subindex, byte[] Value)> SafetyWrites(PeerSafetyConfiguration configuration, int peerSrdoCount)` — the ordered list of (index, sub, bytes) that §9.2 writes and the readback compares — shared by both methods.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenPeerSafetyTests.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.CANopen.Sdo;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>A master configures a device's safety parameters over SDO (CiA DSP 304 V1.0 §9.2,
/// Figure 9) and verifies them (§8.3.1 step D). The device is a real node; the mismatch path
/// uses a fake expedited SDO server on a raw channel that answers one readback wrongly.</summary>
public class CanOpenPeerSafetyTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    /// <summary>The file that lists every safety object of node 5 (the peer gate reads it).</summary>
    private static CanOpenDeviceDescription PeerFile()
        => CanOpenDeviceDescription.ParseDcf(System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")));

    private static PeerSafetyConfiguration Configuration() => new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
        .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
            new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));

    private static ICanOpenNode OpenDevice(ICanBus bus)
    {
        var device = CanOpen.OpenNode(bus, Device, new CanOpenNodeOptions { SrdoCount = 2, WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU16(0x2000, 0x00, 0x1234);
        device.ObjectDictionary.AddU8(0x2001, 0x00, 0x5A);
        return device;
    }

    [Fact]
    public async Task Configure_Downloads_Reads_Back_And_Acknowledges()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeTrue(string.Join("\n", result.Mismatches));
        var od = device.ObjectDictionary;
        od.ReadUnsigned(0x1300, 0).Should().Be(1u);
        od.ReadUnsigned(0x1301, 1).Should().Be(1u);
        od.ReadUnsigned(0x1301, 2).Should().Be(30u);
        od.ReadUnsigned(0x1301, 5).Should().Be(0x109u);
        od.ReadUnsigned(0x1381, 0).Should().Be(4u);
        od.ReadUnsigned(0x1302, 1).Should().Be(0u, "an SRDO the configuration does not name is deleted");
        od.ReadUnsigned(0x13FF, 2).Should().Be(0u);
        od.ReadUnsigned(0x13FE, 0).Should().Be(0xA5u);
        SrdoRecords.IsConfigurationValid(od, 1).Should().BeTrue();
    }

    [Fact]
    public async Task Configure_Is_Refused_Without_A_Bound_Description()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        await Assert.ThrowsAsync<PeerSdoAccessException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        device.ObjectDictionary.ReadUnsigned(0x1301, 1).Should().Be(0u, "nothing was sent");
    }

    [Fact]
    public async Task Configure_Aborts_When_The_Peer_Is_Operational()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        await master.SendNmtCommandAsync(NmtCommand.Start, Device);
        var deadline = DateTime.UtcNow + ShortTimeout;
        while (device.State != NmtState.Operational && DateTime.UtcNow < deadline) await Task.Delay(5);
        var ex = await Assert.ThrowsAsync<SdoAbortException>(() => master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout));
        ex.AbortCode.Should().Be(SdoAbortCode.DataCannotBeTransferredDeviceState);
    }

    [Fact]
    public async Task Verify_Succeeds_After_Configure_And_Fails_On_A_Tampered_Checksum()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        (await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout)).Succeeded.Should().BeTrue();
        var verified = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        verified.Succeeded.Should().BeTrue(string.Join("\n", verified.Mismatches));
        device.ObjectDictionary.WriteUnsigned(0x13FF, 1, device.ObjectDictionary.ReadUnsigned(0x13FF, 1) ^ 0x0100);
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0xA5);
        var failed = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        failed.Succeeded.Should().BeFalse();
        failed.Mismatches.Should().ContainSingle(m => m.Index == 0x13FF && m.Subindex == 1);
        device.ObjectDictionary.WriteUnsigned(0x13FE, 0, 0);
        var notValid = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        notValid.Mismatches.Should().Contain(m => m.Index == 0x13FE);
    }

    [Fact]
    public async Task Verify_Reports_A_Record_That_Differs_From_The_Expectation()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDevice(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        var other = new PeerSafetyConfiguration { GlobalFailsafeCommandEnabled = true }
            .Add(1, new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20), 0x109, 0x10A),
                new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8));
        var result = await master.Safety().VerifyPeerSafetyConfigurationAsync(Device, other).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Mismatches.Should().Contain(m => m.Index == 0x1301 && m.Subindex == 2);
        result.Mismatches.Should().Contain(m => m.Index == 0x13FF && m.Subindex == 1);
    }

    [Fact]
    public async Task Configure_Does_Not_Acknowledge_When_The_Readback_Differs()
    {
        var session = VirtualAdapterFixture.NewSession("co-peer-safety");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master);
        using var fake = new FakeExpeditedServer(session, 2, Device, lieAt: (0x1301, 0x02));
        master.BindPeerDeviceDescription(Device, PeerFile());
        var result = await master.Safety().ConfigurePeerSafetyAsync(Device, Configuration()).WithTimeoutAsync(ShortTimeout);
        result.Succeeded.Should().BeFalse();
        result.Mismatches.Should().ContainSingle(m => m.Index == 0x1301 && m.Subindex == 2);
        fake.Written.Should().NotContainKey((0x13FE, 0), "A5h is written only after a clean readback");
    }

    /// <summary>An SDO server for expedited transfers only: stores downloads, answers uploads
    /// with what was stored (13FFh:00 reads 2), and answers one upload with the stored value
    /// plus one so the readback differs.</summary>
    private sealed class FakeExpeditedServer : IDisposable
    {
        private readonly ICanBus _bus;
        private readonly byte _nodeId;
        private readonly (ushort, byte) _lieAt;
        public readonly Dictionary<(ushort, byte), byte[]> Written = new();
        public FakeExpeditedServer(string session, int channel, byte nodeId, (ushort, byte) lieAt)
        {
            _nodeId = nodeId;
            _lieAt = lieAt;
            Written[(0x13FF, 0)] = new byte[] { 2 };
            _bus = Open(session, channel);
            _bus.FrameObserved += (_, e) =>
            {
                var f = e.CanFrame;
                if (f.IsExtendedFrame || f.IsRemoteFrame || f.ID != 0x600 + _nodeId || f.Data.Length < 4) return;
                var d = f.Data.ToArray();
                ushort index = (ushort)(d[1] | (d[2] << 8));
                byte sub = d[3];
                byte[] reply;
                if ((d[0] & 0xE0) == 0x20)
                {
                    int n = (d[0] & 0x01) != 0 ? 4 - ((d[0] >> 2) & 0x03) : 4;
                    lock (Written) Written[(index, sub)] = d.Skip(4).Take(n).ToArray();
                    reply = new byte[] { 0x60, d[1], d[2], d[3], 0, 0, 0, 0 };
                }
                else if (d[0] == 0x40)
                {
                    byte[] value;
                    lock (Written) value = Written.TryGetValue((index, sub), out var v) ? (byte[])v.Clone() : new byte[] { 0 };
                    if ((index, sub) == _lieAt) value[0]++;
                    reply = new byte[8];
                    reply[0] = (byte)(0x43 | ((4 - value.Length) << 2));
                    reply[1] = d[1]; reply[2] = d[2]; reply[3] = d[3];
                    Array.Copy(value, 0, reply, 4, value.Length);
                }
                else return;
                _bus.Transmit(CanFrame.Classic(0x580 + _nodeId, reply, isExtendedFrame: false));
            };
        }
        public void Dispose() => _bus.Dispose();
    }
}
```

`WithTimeoutAsync` is the tests' existing extension (used by `CanOpenPdoEngineTests`).

- [ ] **Step 2: Run to verify failure** — `ConfigurePeerSafetyAsync` missing.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

/// <summary>The tool side of CiA DSP 304 V1.0: configuring a peer's safety parameters with
/// readback and acknowledgement (§9.2, Figure 9) and verifying them (§8.3.1 step D). Every
/// transfer is a client SDO through the peer gate (FR-CO-029).</summary>
internal sealed partial class CanOpenNode
{
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _peerSafetyGate = new();

    public async Task<PeerSafetyResult> ConfigurePeerSafetyAsync(byte peerNodeId, PeerSafetyConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        var gate = _peerSafetyGate.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
            var writes = SafetyWrites(configuration, peerCount).ToList();
            // "write all safety-relevant parameter incl. checksums"
            foreach (var (index, sub, value) in writes)
                await SdoDownloadAsync(peerNodeId, index, sub, value, cancellationToken).ConfigureAwait(false);
            // "read all safety-relevant parameter incl. checksums back" — "compared"
            var mismatches = await CompareAsync(peerNodeId, writes, cancellationToken).ConfigureAwait(false);
            if (mismatches.Count > 0) return new PeerSafetyResult(false, mismatches);
            // "configuration acknowledged"
            var valid = new[] { SrdoRecords.ConfigurationValidValue };
            await SdoDownloadAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, valid, cancellationToken).ConfigureAwait(false);
            var back = await SdoUploadAsync(peerNodeId, SrdoRecords.ConfigurationValid, 0x00, cancellationToken).ConfigureAwait(false);
            if (!back.AsSpan().SequenceEqual(valid))
                return new PeerSafetyResult(false, new[] { new PeerSafetyMismatch(SrdoRecords.ConfigurationValid, 0x00, valid, back) });
            return new PeerSafetyResult(true, Array.Empty<PeerSafetyMismatch>());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PeerSafetyResult> VerifyPeerSafetyConfigurationAsync(byte peerNodeId, PeerSafetyConfiguration expected,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        var gate = _peerSafetyGate.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int peerCount = await ReadPeerSrdoCountAsync(peerNodeId, cancellationToken).ConfigureAwait(false);
            var expectedValues = SafetyWrites(expected, peerCount)
                .Where(w => w.Index != SrdoRecords.GfcParameter) // 1300h is not part of step D's list (§8.3.1 D)
                .Append((SrdoRecords.ConfigurationValid, (byte)0x00, new[] { SrdoRecords.ConfigurationValidValue }))
                .ToList();
            var mismatches = await CompareAsync(peerNodeId, expectedValues, cancellationToken).ConfigureAwait(false);
            return new PeerSafetyResult(mismatches.Count == 0, mismatches);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> ReadPeerSrdoCountAsync(byte peerNodeId, CancellationToken cancellationToken)
    {
        var count = await SdoUploadAsync(peerNodeId, SrdoRecords.Checksum, 0x00, cancellationToken).ConfigureAwait(false);
        return count.Length >= 1 ? Math.Min(count[0], SrdoRecords.MaxSrdoCount) : 0;
    }

    /// <summary>The writes of §9.2 in order: per SRDO the deletion, the mapping (disabled, slots,
    /// count), the times, the ids, the creation; SRDOs the configuration does not name are
    /// deleted; then 1300h; then every checksum. The same list is what the readback compares.</summary>
    private static IEnumerable<(ushort Index, byte Subindex, byte[] Value)> SafetyWrites(PeerSafetyConfiguration configuration, int peerSrdoCount)
    {
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            var map = SrdoRecords.MapIndex(n);
            if (!configuration.Srdos.TryGetValue(n, out var srdo))
            {
                yield return (comm, 0x01, new byte[] { 0 });
                continue;
            }
            var (p, mapping) = srdo;
            var entries = mapping.ToArray();
            yield return (comm, 0x01, new byte[] { 0 });
            yield return (map, 0x00, new byte[] { 0 });
            for (byte s = 1; s <= SrdoRecords.MappingSubindices; s++)
            {
                int i = (s - 1) / 2;
                yield return (map, s, ObjectDictionary.EncodeU32(i < entries.Length ? EncodeMappingEntry(entries[i]) : 0u));
            }
            yield return (map, 0x00, new[] { (byte)(2 * entries.Length) });
            ushort cycle = (ushort)Math.Round(p.RefreshOrSafeguardCycleTime.TotalMilliseconds);
            yield return (comm, 0x02, new[] { (byte)cycle, (byte)(cycle >> 8) });
            if (p.Direction == SrdoDirection.Receive)
                yield return (comm, 0x03, new[] { (byte)Math.Round(p.ValidationTime.TotalMilliseconds) });
            yield return (comm, 0x05, ObjectDictionary.EncodeU32(p.CobId1));
            yield return (comm, 0x06, ObjectDictionary.EncodeU32(p.CobId2));
            yield return (comm, 0x01, new[] { (byte)p.Direction });
        }
        yield return (SrdoRecords.GfcParameter, 0x00, new[] { configuration.GlobalFailsafeCommandEnabled ? (byte)1 : (byte)0 });
        for (int n = 1; n <= peerSrdoCount; n++)
        {
            ushort crc = configuration.Srdos.TryGetValue(n, out var srdo) ? SrdoCrc.Compute(srdo.Parameter, srdo.Mapping) : (ushort)0;
            yield return (SrdoRecords.Checksum, (byte)n, new[] { (byte)crc, (byte)(crc >> 8) });
        }
    }

    /// <summary>Uploads each pair once (the last value written to a pair is the expectation) and
    /// lists every difference.</summary>
    private async Task<List<PeerSafetyMismatch>> CompareAsync(byte peerNodeId,
        IReadOnlyList<(ushort Index, byte Subindex, byte[] Value)> expected, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(ushort, byte), byte[]>();
        var order = new List<(ushort, byte)>();
        foreach (var (index, sub, value) in expected)
        {
            if (!final.ContainsKey((index, sub))) order.Add((index, sub));
            final[(index, sub)] = value;
        }
        var mismatches = new List<PeerSafetyMismatch>();
        foreach (var (index, sub) in order)
        {
            var actual = await SdoUploadAsync(peerNodeId, index, sub, cancellationToken).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(final[(index, sub)]))
                mismatches.Add(new PeerSafetyMismatch(index, sub, final[(index, sub)], actual));
        }
        return mismatches;
    }
}
```

Note for `CompareAsync`: a pair written twice (sub1 = 0 then sub1 = direction; map sub0 = 0 then 2k) is compared against its last value. An SDO upload of a sub-index the peer declares `wo` would abort; the safety objects are `rw`, so none does.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenPeerSafetyTests"` — PASS. Mutation: in `ConfigurePeerSafetyAsync` move the `13FEh = A5h` download before `CompareAsync` → `Configure_Does_Not_Acknowledge_When_The_Readback_Differs` fails; restore.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/CanOpenNode.PeerSafety.cs src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenPeerSafetyTests.cs
git commit -m "feat(canopen): configure and verify a peer's CiA 304 safety parameters over SDO"
```

---

### Task 10: Boot-up step D — verify a safety slave before NMT Start

**Files:**
- Modify: `src/CanKit.Pro.CANopen/CanOpenNode.BootUp.cs` (fields after `_bootDeadline`, `ConsiderStart`, `TryFinishBoot`, `OnBootTimeout` → extract `ApplyBootErrorReaction`, `CancelBootUp`), `src/CanKit.Pro.CANopen/Nmt/FlyingMaster.cs` (`FlyingMasterSignal`)
- Create: `tests/CanKit.Pro.Tests/TestCases/CANopen/FlyingMasterRig.cs` (extracted helpers), `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyBootUpTests.cs`
- Modify: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenFlyingMasterTests.cs` (use the extracted helpers; no behaviour change)

**Interfaces:**
- Consumes: `VerifyPeerSafetyConfigurationAsync`, `PeerSafetyConfiguration.FromDeviceDescription`, `_peerDescriptions`, `RaiseFlyingMaster`, `SendNmt`, `IsMandatory`, `IsAssignedSlave`, `ReadStartup`, the `Nmt*Bit` constants, `_slaveStarted`, `_bootHalted`, `_bootBroadcastSent`.
- Produces: `FlyingMasterSignal.SlaveSafetyConfigurationInvalid`; node members `private readonly bool[] _slaveVerifying`, `private readonly bool[] _slaveVerified`, `private CancellationTokenSource? _slaveVerificationCts`, `private PeerSafetyConfiguration? SafetyExpectationOf(byte nodeId)`, `private void BeginSlaveVerification(byte nodeId, PeerSafetyConfiguration expected)`, `private void OnSlaveVerified(byte nodeId, bool verified, Exception? failure)`, `private void ApplyBootErrorReaction(byte failedSlave)`, `private bool AnySlaveVerifying()`.

- [ ] **Step 1: Extract the rig, then write the failing tests**

Move, unchanged except for `internal` visibility and `using static` friendliness, from `CanOpenFlyingMasterTests.cs` into `tests/CanKit.Pro.Tests/TestCases/CANopen/FlyingMasterRig.cs` as `internal static class FlyingMasterRig`: the constants `Startup`, `Timing`, `MasterBits`, `SuppressSelfStart`, `SuppressSlaveStart`, `Assigned`, `BootSlave`, `MandatorySlave`, `LeftId`, `WitnessForLeft`, `Heartbeat`; the methods `NewSession`, `Open`, `OpenClockedNode`, `Tighten`, `IsNmt`, `TransmitHeartbeat`, `TransmitNmt`, `OpenMaster`, `UntilAsync`, `AdvanceAsync`, `QuiesceAsync`; the nested classes `MasterRig`, `FrameLog`, `ActorWitness`. Replace their definitions in `CanOpenFlyingMasterTests` with `using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;` and run `--filter "FullyQualifiedName~CanOpenFlyingMaster"` to confirm the suite is unchanged before touching the product. Commit that refactor alone: `git commit -m "test(canopen): share the flying-master rig between test classes"`.

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyBootUpTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Nmt;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;
using static CanKit.Pro.Tests.TestCases.CANopen.FlyingMasterRig;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>CiA DSP 304 V1.0 §8.3.1 step D in the boot-up manager (CiA 302-2): an assigned slave
/// whose bound DCF declares SRDOs is verified over SDO before the master sends NMT Start. The
/// slave is a real device node on the rig's peer bus, so it answers the SDO uploads.</summary>
public class CanOpenSafetyBootUpTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Slave = 0x05; // safety.dcf is commissioned for node 5

    private static CanOpenDeviceDescription SlaveDcf(bool validChecksum)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf"));
        var parameter = new SrdoCommunicationParameter(SrdoDirection.Transmit, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(20), 0x109, 0x10A);
        var mapping = new SrdoMapping().Add(0x2000, 0x00, 16).Add(0x2001, 0x00, 8);
        ushort crc = SrdoCrc.Compute(parameter, mapping);
        return CanOpenDeviceDescription.ParseDcf(text.Replace("ParameterValue=0x0000", $"ParameterValue=0x{(validChecksum ? crc : (ushort)(crc ^ 1)):X4}"));
    }

    /// <summary>The slave: a device opened from the DCF (its 13FFh is what the file says), on
    /// the rig's peer bus, in Pre-Operational.</summary>
    private static ICanOpenNode OpenSlave(ICanBus peerBus, bool validChecksum)
        => CanOpen.OpenNode(peerBus, SlaveDcf(validChecksum), new CanOpenNodeOptions { WritableCommunicationParameters = true });

    private static List<(FlyingMasterSignal Signal, byte? Other)> Record(CanOpenNode master)
    {
        var signals = new List<(FlyingMasterSignal, byte?)>();
        master.FlyingMasterChanged += (_, e) => { lock (signals) signals.Add((e.Signal, e.OtherNodeId)); };
        return signals;
    }

    [Fact]
    public async Task A_Verified_Safety_Slave_Is_Started()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, SuppressSelfStart);
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => rig.Node.FlyingMasterRole == FlyingMasterRole.Active, 800, "the master is active");
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the slave was verified and started");
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.Start, Slave));
        signals.Should().NotContain(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid);
        rig.Log.Snapshot().Should().Contain(f => f.Id == 0x600u + Slave, "step D read the slave's safety objects over SDO");
    }

    [Fact]
    public async Task An_Unverified_Optional_Slave_Is_Skipped_And_Signalled()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => { lock (signals) return signals.Any(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid); }, 2000, "the verification failed");
        signals.Should().Contain((FlyingMasterSignal.SlaveSafetyConfigurationInvalid, (byte?)Slave));
        await QuiesceAsync(rig.Witness, null);
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave), "§8.3.1 D: no start without a verified configuration");
        rig.Node.State.Should().Be(NmtState.Operational, "an optional slave does not hold the master");
        slave.State.Should().Be(NmtState.PreOperational);
    }

    [Fact]
    public async Task An_Unverified_Mandatory_Slave_Halts_The_Boot()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: false);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        Tighten(rig.Node);
        var signals = Record(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => { lock (signals) return signals.Any(s => s.Signal == FlyingMasterSignal.SlaveSafetyConfigurationInvalid); }, 2000, "the verification failed");
        await QuiesceAsync(rig.Witness, null);
        rig.Node.State.Should().NotBe(NmtState.Operational, "a mandatory slave that fails step D halts the boot like a boot timeout");
        rig.Log.Snapshot().Should().NotContain(f => IsNmt(f, NmtCommand.Start, Slave));
        rig.Log.Snapshot().Should().Contain(f => IsNmt(f, NmtCommand.ResetNode, Slave), "1F80h bits 4 and 6 clear: Reset Node to the failing slave");
    }

    [Fact]
    public async Task A_Simultaneous_Start_Waits_For_The_Verification()
    {
        using var rig = OpenMaster();
        using var slave = OpenSlave(rig.Peer, validChecksum: true);
        rig.Node.BindPeerDeviceDescription(Slave, SlaveDcf(validChecksum: true));
        var od = rig.Node.ObjectDictionary;
        od.WriteUnsigned(Startup, 0x00, 0x02); // bit 1: one NMT Start to all
        od.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave | MandatorySlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "the broadcast start went out after the verification");
        var log = rig.Log.Snapshot();
        int sdo = log.FindIndex(f => f.Id == 0x600u + Slave);
        int start = log.FindIndex(f => IsNmt(f, NmtCommand.Start, 0));
        sdo.Should().BeGreaterThanOrEqualTo(0);
        start.Should().BeGreaterThan(sdo, "the broadcast waits for every running verification");
    }

    [Fact]
    public async Task A_Slave_Without_Srdo_Records_Is_Booted_As_Before()
    {
        using var rig = OpenMaster();
        var plain = CanOpenDeviceDescription.Load(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "device.dcf"));
        using var slave = CanOpen.OpenNode(rig.Peer, plain);
        rig.Node.BindPeerDeviceDescription(Slave, plain);
        rig.Node.ObjectDictionary.WriteUnsigned(0x1F81, Slave, Assigned | BootSlave);
        Tighten(rig.Node);
        rig.Node.StartFlyingMaster(0, Heartbeat);
        await UntilAsync(rig.Clock, rig.Witness, null, () => slave.State == NmtState.Operational, 2000, "started");
        rig.Log.Snapshot().Should().NotContain(f => f.Id == 0x600u + Slave, "no SDO: the file declares no SRDO");
    }
}
```

- [ ] **Step 2: Run to verify failure** — `SlaveSafetyConfigurationInvalid` missing; after adding the enum member alone, `An_Unverified_Optional_Slave_Is_Skipped_And_Signalled` fails (the slave is started).

- [ ] **Step 3: Implement**

`Nmt/FlyingMaster.cs`, append to `FlyingMasterSignal`:

```csharp
    /// <summary>CiA DSP 304 V1.0 §8.3.1 step D: an assigned slave whose bound DCF declares SRDOs
    /// did not verify — 13FEh is not A5h, a checksum or a record differs from the file, or the
    /// uploads failed. <see cref="CanKit.Pro.CANopen.FlyingMasterChangedEventArgs.OtherNodeId"/>
    /// is that slave. It is not started; a mandatory one, or any one under a simultaneous start,
    /// halts the boot like <see cref="SlaveBootTimeout"/>.</summary>
    SlaveSafetyConfigurationInvalid,
```

`CanOpenNode.BootUp.cs`:

Fields (after `_bootDeadline`):

```csharp
    // CiA DSP 304 §8.3.1 step D. A slave whose bound DCF declares SRDOs is verified before it is
    // started; the verification is SDO traffic and runs off the actor, its result is posted back.
    private readonly bool[] _slaveVerifying = new bool[CanOpenCobId.MaxNodeId + 1];
    private readonly bool[] _slaveVerified = new bool[CanOpenCobId.MaxNodeId + 1];
    private CancellationTokenSource? _slaveVerificationCts;
```

`ConsiderStart` — after the `if (state is not (0x00 or RequestStopped or RequestPreOperational)) return;` line and before the simultaneous check:

```csharp
        // Step D before step E: a safety slave is started only once its configuration verified.
        if (!_slaveVerified[nodeId] && SafetyExpectationOf(nodeId) is { } expected)
        {
            if (!_slaveVerifying[nodeId]) BeginSlaveVerification(nodeId, expected);
            return;
        }
```

`TryFinishBoot` — the simultaneous broadcast condition gains `&& !AnySlaveVerifying()`; the self-start condition too (`(startup & NmtSuppressSelfStartBit) == 0 && !_bootSelfStarted && !AnySlaveVerifying()`), so the master does not enter Operational while a mandatory verification is still running.

Extract from `OnBootTimeout` everything after `_bootHalted = true;` into:

```csharp
    /// <summary>The 1F80h bit 6 / bit 4 reaction, else Reset Node to the failing slave; then the
    /// signal. Used by the boot timeout (SlaveBootTimeout for each unseen mandatory slave) and by
    /// a failed safety verification (SlaveSafetyConfigurationInvalid).</summary>
    private void ApplyBootErrorReaction(IEnumerable<byte> failedSlaves, FlyingMasterSignal signal)
    {
        _bootHalted = true;
        uint startup = ReadStartup();
        bool stopAll = (startup & NmtStopAllOnErrorBit) != 0;
        bool resetAll = !stopAll && (startup & NmtResetAllOnErrorBit) != 0;
        if (stopAll || resetAll)
        {
            var command = stopAll ? NmtCommand.Stop : NmtCommand.ResetNode;
            for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++)
            {
                if (IsAssignedSlave(id)) SendNmt(command, id);
            }
        }
        foreach (var id in failedSlaves)
        {
            if (!stopAll && !resetAll) SendNmt(NmtCommand.ResetNode, id);
            RaiseFlyingMaster(signal, id, null);
        }
    }
```

and have `OnBootTimeout` call `ApplyBootErrorReaction(unseenMandatory, FlyingMasterSignal.SlaveBootTimeout)` with the list it computed before (`id` where `IsMandatory(id) && !_slaveSeen[id]`).

New methods:

```csharp
    /// <summary>The expectation of a safety slave: what its bound DCF says, when that file
    /// declares at least one SRDO (spec decision 4). An EDS has no parameter values and never
    /// makes a safety slave.</summary>
    private PeerSafetyConfiguration? SafetyExpectationOf(byte nodeId)
    {
        if (!_peerDescriptions.TryGetValue(nodeId, out var description) || !description.IsConfigurationFile) return null;
        var expected = PeerSafetyConfiguration.FromDeviceDescription(description, nodeId);
        return expected.DeclaresAnySrdo ? expected : null;
    }

    private bool AnySlaveVerifying()
    {
        for (byte id = 1; id <= CanOpenCobId.MaxNodeId; id++) if (_slaveVerifying[id]) return true;
        return false;
    }

    private void BeginSlaveVerification(byte nodeId, PeerSafetyConfiguration expected)
    {
        _slaveVerifying[nodeId] = true;
        var cts = _slaveVerificationCts ??= new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            bool verified = false;
            Exception? failure = null;
            try
            {
                verified = (await VerifyPeerSafetyConfigurationAsync(nodeId, expected, token).ConfigureAwait(false)).Succeeded;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { failure = ex; }
            try { _actor.Post(() => OnSlaveVerified(nodeId, verified, failure)); }
            catch (ObjectDisposedException) { }
        });
    }

    private void OnSlaveVerified(byte nodeId, bool verified, Exception? failure)
    {
        if (!_slaveVerifying[nodeId]) return; // cancelled by a reset, a role change or dispose
        _slaveVerifying[nodeId] = false;
        if (_disposed != 0 || _flyingMasterRole != FlyingMasterRole.Active || _bootHalted) return;
        if (failure is not null) RaiseBackgroundException(failure);
        if (verified)
        {
            _slaveVerified[nodeId] = true;
            byte state = (byte)_od.ReadUnsigned(Co.RequestNmt, nodeId);
            ConsiderStart(nodeId, state);
            TryFinishBoot();
            return;
        }
        uint startup = ReadStartup();
        bool simultaneous = (startup & NmtStartAllNodesBit) != 0 && (startup & NmtSuppressSelfStartBit) == 0;
        if (IsMandatory(nodeId) || simultaneous)
        {
            // A broadcast cannot leave one slave out, and a mandatory slave gates the network:
            // the boot halts, with the 1F80h error reaction, as on a boot timeout.
            ApplyBootErrorReaction(new[] { nodeId }, FlyingMasterSignal.SlaveSafetyConfigurationInvalid);
            return;
        }
        _slaveStarted[nodeId] = true; // skipped: never started by this boot
        RaiseFlyingMaster(FlyingMasterSignal.SlaveSafetyConfigurationInvalid, nodeId, null);
        TryFinishBoot();
    }
```

`CancelBootUp`: add `Array.Clear(_slaveVerifying, 0, _slaveVerifying.Length); Array.Clear(_slaveVerified, 0, _slaveVerified.Length); _slaveVerificationCts?.Cancel(); _slaveVerificationCts?.Dispose(); _slaveVerificationCts = null;` — it is already called from `CleanUpOnActor`, from the reset path and when the role leaves Active (verify with `grep -n CancelBootUp`), which is the cancellation the spec asks for.

`ReadPeerSrdoCountAsync` in Task 9 uploads `13FFh:00`; a peer DCF that declares 13FFh passes the gate. `safety.dcf` does.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenSafetyBootUpTests|FullyQualifiedName~CanOpenFlyingMaster"` — PASS. Mutation: make `OnSlaveVerified` ignore `verified == false` (always start) → the optional-slave and mandatory-slave tests fail; restore.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/CanOpenNode.BootUp.cs src/CanKit.Pro.CANopen/Nmt/FlyingMaster.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenSafetyBootUpTests.cs
git commit -m "feat(canopen): verify a safety slave's configuration before NMT Start (CiA 304 step D)"
```

---

### Task 11: `ObserveForeignSrdoAsync`

**Files:**
- Create: `src/CanKit.Pro.CANopen/CanOpenNode.ForeignSrdo.cs`
- Modify: `src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs` (one member), `src/CanKit.Pro.CANopen/ForeignPdo.cs` (`ForeignPdoKind.Srdo = 2` if not yet added in Task 8)
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenForeignSrdoTests.cs`

**Interfaces:**
- Consumes: `TryReadLiveCobIdAsync(byte, ushort, CancellationToken) : Task<LiveCobId>` (private in `CanOpenNode.ForeignPdo.cs`; reachable from the partial), `SdoUploadAsync`, `IsLiveReadUnavailable`, `TryDescribedObject`, `TryDescribedValue`, `ParseDescribedUnsigned`, `IForeignPdoSink`, `ForeignPdoSignal`, `ForeignPdoObservation` (internal ctor), `ForeignSrdoObserveResult`, `_foreignPdoObserve` semaphore map.
- Produces on `ICanOpenSafety`:

```csharp
    /// <summary>Splits an SRDO pair of another node into <paramref name="sink"/>, like
    /// <see cref="ICanOpenNode.ObserveForeignPdoAsync"/>: the record whose live 1301h–1340h:05
    /// equals <paramref name="cobId1"/> (the file is the fallback when the upload fails), the
    /// pair checked for equal length and bitwise inversion, the mapping read live from
    /// 1381h–13C0h (odd sub-indices) or from the file. Signals carry
    /// <see cref="ForeignPdoKind.Srdo"/> and the SRDO number. SRVT and SCT are the caller's to
    /// judge: it holds the timestamps. Nothing is written to this node's dictionary.</summary>
    Task<ForeignSrdoObserveResult> ObserveForeignSrdoAsync(byte peerNodeId, uint cobId1, ReadOnlyMemory<byte> frame1,
        ReadOnlyMemory<byte> frame2, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken = default);
```

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenForeignSrdoTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using CanKit.Abstractions.API.Can;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Core;
using CanKit.Pro.CANopen;
using CanKit.Pro.CANopen.Safety;
using CanKit.Pro.Tests.Infrastructure;
using Xunit;

namespace CanKit.Pro.Tests.TestCases.CANopen.Safety;

/// <summary>Decoding a peer's SRDO pair from its live records (FR-CO-030's rules applied to
/// CiA DSP 304): live before file, the pair checked, the plain data split into the sink.</summary>
public class CanOpenForeignSrdoTests : IClassFixture<VirtualAdapterFixture>
{
    private const byte Master = 0x01;
    private const byte Device = 0x05;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);

    private static ICanBus Open(string session, int channel) => CanBus.Open(
        $"virtual://{session}/{channel}",
        cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(VirtualAdapterFixture.Bitrate));

    private static CanOpenDeviceDescription PeerFile()
        => CanOpenDeviceDescription.ParseDcf(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestCases", "CANopen", "Fixtures", "safety.dcf")));

    private sealed class ListSink : IForeignPdoSink
    {
        public readonly List<ForeignPdoSignal> Signals = new();
        public void Write(ForeignPdoSignal signal) => Signals.Add(signal);
    }

    /// <summary>The device from the file, but with its live SRDO 1 moved to 0x111/0x112 and a
    /// different mapping than the file — live must win.</summary>
    private static ICanOpenNode OpenDeviceDivergingFromFile(ICanBus bus)
    {
        var device = CanOpen.OpenNode(bus, PeerFile(), new CanOpenNodeOptions { WritableCommunicationParameters = true });
        device.ObjectDictionary.AddU32(0x2002, 0x00, 0, OdAccess.ReadOnly);
        device.Safety().ConfigureSrdoProducer(1, new SrdoMapping().Add(0x2002, 0x00, 32), TimeSpan.FromMilliseconds(25), 0x111, 0x112);
        device.Safety().CommitSafetyConfiguration();
        return device;
    }

    [Fact]
    public async Task Decodes_With_The_Live_Record_Ahead_Of_The_File()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDeviceDivergingFromFile(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xED }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        result.Observation.Should().NotBeNull(result.Reason);
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Kind.Should().Be(ForeignPdoKind.Srdo);
        result.Observation.PdoNumber.Should().Be(1);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.LiveMapping);
        sink.Signals.Should().ContainSingle();
        sink.Signals[0].Index.Should().Be((ushort)0x2002);
        sink.Signals[0].Value.Should().Equal(0x78, 0x56, 0x34, 0x12);
        sink.Signals[0].Kind.Should().Be(ForeignPdoKind.Srdo);
        master.ObjectDictionary.ContainsIndex(0x2002).Should().BeFalse("nothing is written here");
    }

    [Fact]
    public async Task Falls_Back_To_The_File_When_The_Peer_Does_Not_Answer()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var master = CanOpen.OpenNode(busA, Master, new CanOpenNodeOptions { SdoTimeout = TimeSpan.FromMilliseconds(100) });
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var result = await master.Safety().ObserveForeignSrdoAsync(Device, 0x109,
            new byte[] { 0x34, 0x12, 0x5A }, new byte[] { 0xCB, 0xED, 0xA5 }, PeerFile(), sink).WithTimeoutAsync(TimeSpan.FromSeconds(30));
        result.Observation!.Decoded.Should().BeTrue(result.Observation.Reason);
        result.Observation.Origin.Should().Be(ForeignPdoMappingOrigin.DeviceDescription);
        sink.Signals.Should().HaveCount(2);
        sink.Signals[0].Index.Should().Be((ushort)0x2000);
        sink.Signals[0].Value.Should().Equal(0x34, 0x12);
        sink.Signals[1].Index.Should().Be((ushort)0x2001);
        sink.Signals[1].Value.Should().Equal(0x5A);
    }

    [Fact]
    public async Task A_Bad_Pair_Or_An_Unknown_Id_Is_Not_Decoded()
    {
        var session = VirtualAdapterFixture.NewSession("co-foreign-srdo");
        using var busA = Open(session, 1);
        using var busB = Open(session, 2);
        using var master = CanOpen.OpenNode(busA, Master);
        using var device = OpenDeviceDivergingFromFile(busB);
        master.BindPeerDeviceDescription(Device, PeerFile());
        var sink = new ListSink();
        var mismatch = await master.Safety().ObserveForeignSrdoAsync(Device, 0x111,
            new byte[] { 0x78, 0x56, 0x34, 0x12 }, new byte[] { 0x87, 0xA9, 0xCB, 0xEC }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        mismatch.Observation!.Decoded.Should().BeFalse();
        mismatch.Observation.Reason.Should().Contain("inverse");
        var unknown = await master.Safety().ObserveForeignSrdoAsync(Device, 0x141,
            new byte[] { 1 }, new byte[] { 0xFE }, PeerFile(), sink).WithTimeoutAsync(ShortTimeout);
        unknown.Observation.Should().BeNull();
        unknown.Reason.Should().Contain("no SRDO");
        sink.Signals.Should().BeEmpty();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => master.Safety().ObserveForeignSrdoAsync(Device, 0x112, new byte[0], new byte[0], PeerFile(), sink));
    }
}
```

- [ ] **Step 2: Run to verify failure** — member missing.

- [ ] **Step 3: Implement**

```csharp
// src/CanKit.Pro.CANopen/CanOpenNode.ForeignSrdo.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanKit.Pro.CANopen.Pdo;
using CanKit.Pro.CANopen.Safety;

namespace CanKit.Pro.CANopen;

/// <summary>Splitting a peer's SRDO pair (CiA DSP 304 V1.0 §8.1) with the rules of
/// <see cref="ObserveForeignPdoAsync"/>: live record before file, nothing written here.</summary>
internal sealed partial class CanOpenNode
{
    public async Task<ForeignSrdoObserveResult> ObserveForeignSrdoAsync(byte peerNodeId, uint cobId1, ReadOnlyMemory<byte> frame1,
        ReadOnlyMemory<byte> frame2, CanOpenDeviceDescription peerDescription, IForeignPdoSink sink,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (peerDescription is null) throw new ArgumentNullException(nameof(peerDescription));
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        CanOpenCobId.ValidateNodeId(peerNodeId);
        if (!SrdoFrames.IsCobId1(cobId1))
            throw new ArgumentOutOfRangeException(nameof(cobId1), cobId1, "COB-ID 1 of an SRDO is odd, 257..383 (CiA DSP 304 Figure 7).");
        if (frame1.Length > 8 || frame2.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(frame1), "A classic CAN frame carries at most 8 bytes.");
        var first = frame1.ToArray();
        var second = frame2.ToArray();
        var gate = _foreignPdoObserve.GetOrAdd(peerNodeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int? number = await MatchSrdoAsync(peerDescription, peerNodeId, cobId1, cancellationToken).ConfigureAwait(false);
            if (number is not { } n)
                return new ForeignSrdoObserveResult(cobId1, null, "no SRDO communication record of the peer uses this COB-ID 1");
            if (!SrdoFrames.IsInversePair(first, second))
                return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, decoded: false, origin: null, signalsWritten: 0,
                    "the second frame is not the bitwise inverse of the first with the same length (§8.1)"), null);
            PdoMappingEntry[] mapping;
            ForeignPdoMappingOrigin origin;
            var live = await TryReadLiveSrdoMappingAsync(peerNodeId, n, cancellationToken).ConfigureAwait(false);
            if (live is { } liveEntries) { mapping = liveEntries; origin = ForeignPdoMappingOrigin.LiveMapping; }
            else if (TryDescribedSrdoMapping(peerDescription, n, peerNodeId, out mapping, out var why)) origin = ForeignPdoMappingOrigin.DeviceDescription;
            else return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, null, 0,
                "the live mapping record could not be read, and the description's mapping could not be used: " + why), null);
            int total = 0;
            foreach (var e in mapping) total += e.ByteLength;
            if (first.Length < total)
                return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, false, origin, 0,
                    $"the frames have {first.Length} byte(s) and the mapping needs {total}"), null);
            int offset = 0, written = 0;
            foreach (var entry in mapping)
            {
                var chunk = new byte[entry.ByteLength];
                Buffer.BlockCopy(first, offset, chunk, 0, entry.ByteLength);
                sink.Write(new ForeignPdoSignal(peerNodeId, ForeignPdoKind.Srdo, n, cobId1, entry.Index, entry.Subindex, chunk, origin));
                written++;
                offset += entry.ByteLength;
            }
            return new ForeignSrdoObserveResult(cobId1, new ForeignPdoObservation(ForeignPdoKind.Srdo, n, true, origin, written, null), null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The record n with live 1300h+n:05 == cobId1; the file's value when that upload fails.</summary>
    private async Task<int?> MatchSrdoAsync(CanOpenDeviceDescription description, byte peerNodeId, uint cobId1, CancellationToken cancellationToken)
    {
        int count = 0;
        for (int n = 1; n <= SrdoRecords.MaxSrdoCount; n++)
            if (description.Objects.Objects.ContainsKey(SrdoRecords.CommIndex(n))) count = n;
        for (int n = 1; n <= count; n++)
        {
            var comm = SrdoRecords.CommIndex(n);
            uint? liveId = null;
            try
            {
                var raw = await SdoUploadAsync(peerNodeId, comm, 0x05, cancellationToken).ConfigureAwait(false);
                if (raw.Length >= 4) liveId = ObjectDictionary.DecodeU32(raw) & CanOpenCobId.CanIdMask;
            }
            catch (Exception ex) when (IsLiveReadUnavailable(ex)) { }
            if (liveId is { } id)
            {
                if (id == cobId1) return n;
                continue;
            }
            if (TryDescribedObject(description, comm, out var record) && TryDescribedValue(record, 0x05, out var text)
                && ParseDescribedUnsigned(text, peerNodeId) is { } word && (word & CanOpenCobId.CanIdMask) == cobId1)
                return n;
        }
        return null;
    }

    /// <summary>Sub0 and the odd entries of 1380h+n; null when unavailable or malformed (an even
    /// count ≤ 16, each odd slot non-zero and byte-aligned, ≤ 8 bytes). A live count of 0 is a mapping.</summary>
    private async Task<PdoMappingEntry[]?> TryReadLiveSrdoMappingAsync(byte peerNodeId, int n, CancellationToken cancellationToken)
    {
        var map = SrdoRecords.MapIndex(n);
        byte[] countBytes;
        try { countBytes = await SdoUploadAsync(peerNodeId, map, 0x00, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (IsLiveReadUnavailable(ex)) { return null; }
        if (countBytes.Length < 1) return null;
        int count = countBytes[0];
        if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) return null;
        var entries = new List<PdoMappingEntry>(count / 2);
        int total = 0;
        for (byte s = 1; s <= count; s += 2)
        {
            byte[] raw;
            try { raw = await SdoUploadAsync(peerNodeId, map, s, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsLiveReadUnavailable(ex)) { return null; }
            if (raw.Length < 4) return null;
            uint word = ObjectDictionary.DecodeU32(raw);
            if (word == 0) return null;
            PdoMappingEntry entry;
            try { entry = new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word); }
            catch (ArgumentOutOfRangeException) { return null; }
            total += entry.ByteLength;
            if (total > 8) return null;
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    private static bool TryDescribedSrdoMapping(CanOpenDeviceDescription description, int n, byte peerNodeId,
        out PdoMappingEntry[] entries, out string reason)
    {
        entries = Array.Empty<PdoMappingEntry>();
        if (!TryDescribedObject(description, SrdoRecords.MapIndex(n), out var record)) { reason = "the file declares no mapping record"; return false; }
        if (!TryDescribedValue(record, 0x00, out var countText) || ParseDescribedUnsigned(countText, peerNodeId) is not { } count)
        { reason = "the file has no readable mapping count"; return false; }
        if ((count & 1) != 0 || count > SrdoRecords.MappingSubindices) { reason = "the file's mapping count is not an even number ≤ 16"; return false; }
        var list = new List<PdoMappingEntry>();
        int total = 0;
        for (byte s = 1; s <= count; s += 2)
        {
            if (!TryDescribedValue(record, s, out var text) || ParseDescribedUnsigned(text, peerNodeId) is not { } word || word == 0)
            { reason = $"the file's mapping entry {s} is missing or unreadable"; return false; }
            try { list.Add(new PdoMappingEntry((ushort)(word >> 16), (byte)(word >> 8), (byte)word)); }
            catch (ArgumentOutOfRangeException) { reason = $"the file's mapping entry {s} is not byte-aligned"; return false; }
            total += list[^1].ByteLength;
            if (total > 8) { reason = "the file's mapping exceeds 8 bytes"; return false; }
        }
        entries = list.ToArray();
        reason = "";
        return true;
    }
}
```

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~CanOpenForeignSrdoTests|FullyQualifiedName~CanOpenForeignPdoObserveTests"` — PASS.

- [ ] **Step 5: Commit**

```bash
git add src/CanKit.Pro.CANopen/CanOpenNode.ForeignSrdo.cs src/CanKit.Pro.CANopen/Safety/ICanOpenSafety.cs src/CanKit.Pro.CANopen/ForeignPdo.cs tests/CanKit.Pro.Tests/TestCases/CANopen/Safety/CanOpenForeignSrdoTests.cs
git commit -m "feat(canopen): decode a peer's SRDO pair from its live records or the file"
```

---

### Task 12: API approval, documentation, the gate and the pull request

**Files:**
- Modify: `tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.approved.txt` (replaced by the generated file), `src/CanKit.Pro.CANopen/README.md`, `src/CanKit.Pro.CANopen/CanKit.Pro.CANopen.csproj` (`Description`, `PackageTags`), `docs/requirements/SRS-CanKit.Pro.md` (§4.3.2 Ist-Zustand, rows FR-CO-035..046, §8 traceability row), `docs/architecture/arc42-CanKit.Pro.md` (§5.1 L4 row, ADR-12 before `# 10.`), `docs/packages/index.md` (CANopen row, Standard column), `README.md` (Roadmap, line 141), `docs/reviews/2026-10-09-canopen-safety-scope.md` (one "Nachgetragen" paragraph: the FR-CO rows now exist)

**Interfaces:** none new. The SRS ids are fixed here so the README's coverage table and the tests' summaries can cite them:

| Id | Requirement (German, in the SRS style "Das System MUSS …") | Source | Verification |
|---|---|---|---|
| FR-CO-035 | Safety-Objekte 1300h, 1301h–(1300h+n), 1381h–(1380h+n), 13FEh, 13FFh als verwaltete Objekte mit den Defaults von §8.4.2.2, nur bei `SrdoCount` > 0 oder beschriebenen Records; Reset, 1010h/1011h wie die Kommunikationsobjekte | DSP 304 §8.4.2, Table 6; Posten 1 | `CanOpenSafetyCommunicationProfileTests`, `CanOpenSafetyNodeTests.Reset_Communication_Restores_The_Safety_Objects` |
| FR-CO-036 | Validierung jedes Writes nach der Tabelle der Akte (Wertebereiche, 0800 0022h in OPERATIONAL, Lösch-zuerst, Paare, ≤ 8 Byte, Kollisionen), 13FEh-Autoreset | §8.3.2.4, §8.4.2.2; Posten 2 | `CanOpenSafetyCommunicationProfileTests` |
| FR-CO-037 | CRC 13FFh:n nach §8.4.2.2 als CRC-16/XMODEM, MSB-first (Entscheidung, #289); `SrdoCrc` öffentlich | §8.4.2.2; Posten 3 | `SrdoCrcTests` |
| FR-CO-038 | Beim Übergang nach OPERATIONAL je SRDO: 13FEh = A5h und CRC-Gleichheit, sonst `ConfigurationInvalid`, kein Senden | §9.5, §8.3.1 D; Posten 4 | `SrdoEngineProducerTests`, `CanOpenSafetyNodeTests.Tampered_Checksum_…` |
| FR-CO-039 | Producer: Zyklus = Refresh-Time, erster Zyklus nach 0,5 ms × Node-ID, Paar Klartext/invertiert in einem Durchlauf, `TriggerSrdoAsync` und Change-of-State senden sofort und starten den Zyklus neu | §8.1, §8.1.3.1, §9.5; Posten 5 | `SrdoEngineProducerTests`, `CanOpenSafetyNodeTests.Producer_And_Consumer_Exchange_An_Srdo` |
| FR-CO-040 | Consumer: Reihenfolge, Inversion, SRVT, SCT, Länge (EMCY 8210h einmal je Lauf), OD-Schreiben, `SrdoReceived`/`SrdoStateChanged` (Übergänge, kritisch), Re-Validierung | §8.1.1, §8.1.3.1, §9.5; Posten 6 | `SrdoEngineConsumerTests` |
| FR-CO-041 | GFC: Senden und Melden nur mit 1300h = 1 in OPERATIONAL, DLC 0 | §8.2; Posten 7 | `SrdoEngineConsumerTests.Gfc_…`, `CanOpenSafetyNodeTests.Gfc_Round_Trip` |
| FR-CO-042 | `ICanOpenSafety` über `CanOpenSafetyExtensions.Safety`, ohne Erweiterung von `ICanOpenNode`; `Configure*`, `DeleteSrdo`, `CommitSafetyConfiguration`, `GetSrdoState`, Options | Entscheidung 7; Posten 8 | `CanOpenSafetyNodeTests`, `PublicApiSurfaceTests` |
| FR-CO-043 | EDS/DCF: Safety-Objekte wie die PDO-Records geladen, Findings, `SrdoCount` als Untergrenze | CiA 306; Posten 9 | `CanOpenSafetyDeviceDescriptionTests` |
| FR-CO-044 | `ConfigurePeerSafetyAsync` nach §9.2 (schreiben, zurücklesen, vergleichen, A5h nur ohne Abweichung) und `VerifyPeerSafetyConfigurationAsync` nach §8.3.1 D, beide durch das Peer-SDO-Tor | §9.2, §8.3.1 D; Posten 10, 11 | `CanOpenPeerSafetyTests` |
| FR-CO-045 | Schritt D im Boot-up: DCF-gebundener Safety-Slave wird vor NMT Start verifiziert; Fehlschlag → `SlaveSafetyConfigurationInvalid`, kein Start; Pflicht-Slave oder Simultanstart → Halt mit 1F80h-Reaktion | §8.3.1 D, Entscheidung 4; Posten 12 | `CanOpenSafetyBootUpTests` |
| FR-CO-046 | `ObserveForeignSrdoAsync`: live vor Datei, Paarprüfung, Zerlegung in `IForeignPdoSink` mit `ForeignPdoKind.Srdo`, keine Zeitbewertung | Entscheidung 3; Posten 13 | `CanOpenForeignSrdoTests` |

- [ ] **Step 1: Regenerate the API approval**

Run `dotnet test tests/CanKit.Pro.Tests/CanKit.Pro.Tests.csproj -c Release --framework net10.0 --filter "FullyQualifiedName~PublicApiSurfaceTests"`. Expected: FAIL for `CanKit.Pro.CANopen` with a `.received.txt` next to the approval. Read the diff (`git diff --no-index tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.approved.txt tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.received.txt`): only additions — the `Safety` namespace, `CanOpenCobId` members, `CanOpenNodeOptions` members, `FlyingMasterSignal.SlaveSafetyConfigurationInvalid`, `ForeignPdoKind.Srdo`. Any removal or signature change is a defect to fix in the code, not in the approval. Then `cp` the received file over the approved one and re-run: PASS.

- [ ] **Step 2: Package README** (`src/CanKit.Pro.CANopen/README.md`)

- Line 12 ("CANopen (CiA 301) node implementation …"): append one sentence: "CANopen Safety (CiA DSP 304 V1.0) is available opt-in through `node.Safety()` — see [CANopen Safety](#canopen-safety-cia-304)."
- Coverage table (after the FR-CO-034 row, line 74): one row per FR-CO-035..046 with the short English form of the table above and a link to the new section.
- "Not built, and why" (line 75 ff.): add bullets —
  - "**Diverse redundancy and the safe state (CiA DSP 304 §9.5, §8.3.2)** — this library is not developed to IEC 61508 / DIN V VDE 0801 and claims no safety integrity level. It builds the inverted frame from the plain one and compares bit by bit on reception; building the pair 'by two different ways' and entering the safe state are the device's."
  - "**SRDO details V1.0 leaves open** — the CRC's initial value and byte order (CRC-16/XMODEM, MSB-first here) and the `ro`/`rw` conflict of 13FFh (`rw` here, §9.2 needs it). Reconciled against EN 50325-5:2010 in #289."
  - "**Bit-granular SRDO mapping, more than 8 objects per SRDO, 29-bit SRDO COB-IDs** — as for PDOs."
  - "**Timing in `ObserveForeignSrdoAsync`** — the caller holds the timestamps and judges SRVT and SCT."
- New section `## CANopen Safety (CiA 304)` before `## Flying master` (line 480), with sub-sections: *Opening a safety node* (`SrdoCount`, or a DCF), *The objects* (a table of 1300h, 1301h–1340h sub-indices with defaults and the rule each write is held to, 1381h–13C0h, 13FEh, 13FFh), *Configuring and committing* (`ConfigureSrdoProducer`/`Consumer`, delete-first order, `CommitSafetyConfiguration`, why A5h comes last, the Operational gate 0800 0022h), *Producer*, *Consumer* (the state machine and reasons, EMCY 8210h, re-validation, events), *GFC* (1300h, echo note), *Configuring a peer* (§9.2 flow, result, what a mismatch means, 13FEh = 0 on an interrupted run), *Verifying and the boot-up* (step D, DCF-bound slaves, the halt rule, the signal), *Observing a peer SRDO*, *What this is not* (the disclaimer, one paragraph). Every claim in it is one the tests of Tasks 4–11 prove; cite the section of DSP 304 as the existing sections cite CiA 301.
- Layout block (line 649 ff.): add `CanOpenNode.Safety.cs`, `CanOpenNode.PeerSafety.cs`, `CanOpenNode.ForeignSrdo.cs` and `Safety/ // CiA 304: SrdoEngine, SrdoCrc, SrdoFrames, SrdoRecords, ICanOpenSafety, value types`.

- [ ] **Step 3: csproj, package index, root README**

- `CanKit.Pro.CANopen.csproj` `Description`: append " CANopen Safety (CiA DSP 304 V1.0): SRDO producer and consumer with SCT/SRVT, GFC, the objects 1300h–13FFh with the 13FFh checksum, peer configuration with readback (§9.2) and step-D verification in the boot-up — opt-in via `node.Safety()`." `PackageTags`: add `;CANopen Safety;CiA 304;SRDO`.
- `docs/packages/index.md` CANopen row: "What it gives you" gains "; CANopen Safety: SRDO/GFC, opt-in" and the Standard column becomes `CiA 301, CiA 302-2, CiA 304 (DSP V1.0), CiA 306`.
- Root `README.md` line 141: `- XCP, DeviceNet.` (CANopen Safety is delivered).

- [ ] **Step 4: SRS and arc42**

- SRS §4.3.2 Ist-Zustand paragraph (line 298): append "CANopen Safety nach CiA DSP 304 V1.0 ist am 09.10.2026 entschieden (`docs/reviews/2026-10-09-canopen-safety-scope.md`) und steht als FR-CO-035..046; die Posten-Nummern in der Spalte Quelle verweisen auf die Tabelle „Die Posten" dort. Der Abgleich gegen EN 50325-5:2010 ist #289." Then the twelve rows after FR-CO-034 (line 330), in the table's five columns, German, with the verification column naming the test classes as in the table above.
- SRS §8 (line 414): `FR-CO-001..046`, and extend the cell: "… CiA 304 als `Safety/SrdoEngine` auf demselben Aktor, `CanOpenNode.Safety.cs` / `CanOpenNode.PeerSafety.cs` / `CanOpenNode.BootUp.cs`".
- arc42 §5.1 L4 row (line 338): mention "CANopen Safety (CiA 304) im CANopen-Paket, `SrdoEngine`". Before `# 10. Qualitätsanforderungen` (line 1230) add:

```markdown
### ADR-12 (umgesetzt): CANopen Safety (CiA 304) im CANopen-Paket, als eigene Engine-Klasse
- **Kontext:** CiA DSP 304 ergänzt CiA 301 um SRDO, GFC und die Objekte 1300h–13FFh. Alles, was
  eine Umsetzung braucht — Aktor, Deadline-Scheduler, Bus-Subscription, Validator-Kette,
  NMT-Übergänge, EDS-Pfad — ist intern in `CanOpenNode`; `ICanOpenNode` darf seit 1.3.0 nicht
  erweitert werden (`c636a17`).
- **Entscheidung:** im Paket `CanKit.Pro.CANopen` (kein eigenes Paket: eine öffentliche
  Erweiterungs-SPI wäre ein zweites Designprojekt und friert eine Plugin-API ein, die sonst
  niemand nutzt; Präzedenz CiA 302-2 und CiA 306). Laufzeit als `Safety/SrdoEngine` mit
  injiziertem `IProtocolActor`, `IDeadlineScheduler`, `ObjectDictionary` und Host-Callbacks,
  nach dem Muster von `HeartbeatProducer`/`HeartbeatConsumer`; die Objekte sind verwaltete
  Kommunikationsobjekte nach dem PDO-Muster. Öffentliche API als `ICanOpenSafety` über
  `CanOpenSafetyExtensions.Safety(this ICanOpenNode)`. Opt-in über `SrdoCount`.
- **Konsequenzen:** + kein Major-Release, ein Paket, eine Zeitquelle für alle Fristen, Engine
  ohne Bus testbar; − das CANopen-Paket wächst; − Normbasis ist DSP 304 V1.0, der Abgleich gegen
  EN 50325-5 steht aus (#289). Entscheidungsakte: `docs/reviews/2026-10-09-canopen-safety-scope.md`.
- **Status:** Umgesetzt als FR-CO-035..046.
```

- Scope record: add at the top, after the intro paragraphs, "Nachgetragen (<date>): umgesetzt als FR-CO-035..046 (SRS §4.3.2), ADR-12; PR #<n>."

- [ ] **Step 5: The gate, then push and open the pull request**

```bash
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build  CanKit.Pro.sln -c Release -p:CI=true
dotnet test   CanKit.Pro.sln -c Release --no-build --framework net10.0
dotnet format CanKit.Pro.sln --verify-no-changes
out=$(mktemp -d) && dotnet pack CanKit.Pro.sln -c Release -o "$out" && python3 eng/verify-packages.py "$out"
```

All four green (fix formatting with `dotnet format CanKit.Pro.sln` and re-run, never by hand-tuning the verifier). Check XML well-formedness of the edited csproj (`python3 -c "import xml.dom.minidom,sys;xml.dom.minidom.parse(sys.argv[1])" src/CanKit.Pro.CANopen/CanKit.Pro.CANopen.csproj`). Commit the docs: `git commit -m "docs(canopen): document CANopen Safety (CiA 304): README, SRS FR-CO-035..046, ADR-12, package index"`.

```bash
git push -u origin feat/canopen-safety
gh pr create --title "feat(canopen): CANopen Safety (CiA DSP 304 V1.0) — SRDO, GFC, peer configuration and boot-up step D" --body-file <(cat <<'EOF'
## Summary
<one paragraph: what, opt-in, the facade, the norm basis and #289>

## Scope record
`docs/reviews/2026-10-09-canopen-safety-scope.md`; plan `docs/superpowers/plans/2026-10-09-canopen-safety.md`.

## Checklist (from .github/PULL_REQUEST_TEMPLATE.md — tick only what was run)
<the template's boxes>

## Mutations run (CLAUDE.md § Before claiming something is true)
- Task 5: inversion removed from Transmit → `A_Transmission_Is_A_Plain_Frame_And_Its_Inverse_On_Following_Ids` red
- Task 6: `ArmSct` removed from `OnSecondFrame` → `Valid_Pair_After_Expiry_Rearms_Sct`, `Each_Pair_Restarts_The_Sct` red; pair check reduced to length → `A_Wrong_Inverse_Is_A_Mismatch` red
- Task 7: A5h written before the checksums → `Commit_Writes_Checksums_Then_Valid` red
- Task 9: acknowledgement before the readback → `Configure_Does_Not_Acknowledge_When_The_Readback_Differs` red
- Task 10: verification result ignored → optional- and mandatory-slave tests red

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)
```

Fill the template's checklist from `.github/PULL_REQUEST_TEMPLATE.md` honestly. Then drive every review thread, bot finding and coverage row to closed before saying "FERTIG — mergebar" (CLAUDE.md § Pull requests).

---

## Self-review (run after writing)

**Spec coverage.** Spec section → task: OD and validator → 4; CRC → 2; engine producer/consumer/GFC → 5, 6; wiring, facade, events, CoS, options → 1, 7; EDS/DCF and `PeerSafetyConfiguration` → 8; §9.2 and verification → 9; boot-up step D → 10; foreign SRDO → 11; docs, SRS, arc42, index, approval, issue (#289 exists) → 12. "Not built" items are documentation (12). Every item of the spec's Posten table 1–15 maps: 1→4, 2→4, 3→2, 4→5+7, 5→5, 6→6, 7→6, 8→7, 9→8, 10→9, 11→9, 12→10, 13→11, 14→12, 15 done (#289).

**Placeholder scan.** No TBD/TODO. The `ForeignNode` stub in Task 7 is a mechanical implementation of an existing interface with throwing bodies, stated as such. The README section in Task 12 lists its sub-sections and their sources rather than full prose; the prose is written from the tests of Tasks 4–11.

**Type consistency.** `SrdoRecords.CommIndex/MapIndex/IsSafetyObject/IsStateGated/IsChecksummed/SrdoNumberOf/TryReadCommunication/ReadMapping/IsConfigurationValid` used in 3, 4, 5, 6, 7, 8, 9, 11 with the same names; `Co.GfcParameter/SrdoConfigurationValid/SrdoChecksum` defined in 4, used in 7, 8; `ISrdoEngineHost` members defined in 5, implemented in 7; `SrdoEngine.TryHandleFrame/Trigger/TrySendGfc/GetState/Rebuild/EnterOperational/LeaveOperational/CollectChangeOfStateEntries/Dispose` defined in 5/6, called in 7; `PeerSafetyResult.Succeeded`, `PeerSafetyMismatch`, `PeerSafetyConfiguration.Srdos/DeclaresAnySrdo/FromDeviceDescription/Add` defined in 8, used in 9, 10; `ForeignSrdoObserveResult` defined in 8, used in 11; `EncodeMappingEntry` (existing, `CanOpenNode.Pdo.cs`) used in 7 and 9; `FlyingMasterSignal.SlaveSafetyConfigurationInvalid` defined in 10, used in 10 and 12; `ForeignPdoKind.Srdo` defined in 8, used in 11.

**Review Focus.** Each of the five lines names its test and task; all five tests are in the plan.

## Execution notes

- Tasks 1–3 are independent of the node and can be run in any order; 4 needs 1–3; 5 needs 1–3; 6 needs 5; 7 needs 4–6; 8 needs 4 and 7; 9 needs 7 and 8; 10 needs 9; 11 needs 7 and 8; 12 needs everything.
- The whole CANopen test filter (`--filter "FullyQualifiedName~CANopen"`) runs at the end of Tasks 4, 7, 8, 9 and 10 — the tasks that touch shared node code — so a regression in the CiA 301 behaviour is caught in the task that caused it (CLAUDE.md § Stay inside the task).
- A test that asserts against a wall clock is a plan violation; `ManualTimeSource` and ordering witnesses only.

