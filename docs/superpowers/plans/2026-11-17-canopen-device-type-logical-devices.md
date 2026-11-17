# CANopen Device Type and Logical Devices Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a tool a typed reading of a peer's device type (`1000h`) and of the logical devices behind it (`67FFh + x·800h`), as public API of `CanKit.Pro.CANopen`.

**Architecture:** Three small public types beside `CanOpenDiscovery`, none of them touching `CanOpenNode`: a value type that splits the 32-bit word, a static helper that owns the logical-device arithmetic, and a static reader that walks the chain through `ICanOpenNode.SdoUploadAsync` — and therefore through the peer-SDO gate. The reader has an internal core that takes the upload as a delegate, the way `CanOpenDiscovery.ScanCoreAsync` does, so the chain logic is tested without a bus.

**Tech Stack:** C# 14, `netstandard2.0;net10.0`, xunit + AwesomeAssertions, `CanKit.Adapter.Virtual` loopback.

**Spec:** `docs/reviews/2026-11-17-canopen-device-profiles-scope.md` — step 2 of its § „Reihenfolge", designed in § „Gerätetyp und logische Geräte". Norm text: CiA 301 v4.2.0 §7.5.2.1 (device type), §7.4.1 Table 41 (object areas), §7.2.2.1 (PDO numbers), in the maintainer's collection, not in the repository.

## Global Constraints

- Branch `feat/canopen-logical-devices` from `main`. One pull request; every commit a Conventional Commit (`CLAUDE.md` § Pull requests). The `feat` commits publish a minor release.
- `ICanOpenNode` is not widened. No change to `CanOpenNode*.cs`.
- `ICanOpenProfile` and the profile catalogue are **not** part of this plan; the spec keeps them internal until step 6.
- Every SDO read goes through `ICanOpenNode.SdoUploadAsync`. No path around the peer-SDO gate: without a bound peer description only `1000h:00` is readable (spec, Entscheidungsfrage 2).
- A logical device is numbered `0..7` (spec: „Nummer 0..7"). CiA 301 counts „1st..8th"; index 0 is the 1st.
- Values copied from CiA 301: object area `6000h + index·800h`; PDO number offset `index·64`; device-type object `67FFh + index·800h`; „further logical devices" is additional information `FFFFh`; „no standardized profile" is profile number `0000h`.
- Public members carry XML comments. The API approval is taken from the generated `*.received.txt`, never hand-written.
- A claim that a test catches something needs the mutation run (`CLAUDE.md` § Before claiming something is true). Each task names its mutation.
- Gate before pushing (`CLAUDE.md` § Verify locally before pushing): build with `-p:CI=true`, test `net10.0`, `dotnet format --verify-no-changes`, pack + `eng/verify-packages.py`, and `python3 eng/verify-requirements-traceability.py`.

## Review Focus

Each line is pinned by a test in Task 3.

1. `1000h` announces further devices (`FFFFh`) but the peer has no `67FFh` → one device is still reported, taken from `1000h`, and the read counts as complete.
2. `FFFFh` and no peer description bound → the result says a description is required; nothing is asked beyond `1000h`.
3. A `67FFh + x·800h` that is not exactly four bytes → the chain stops as failed; devices read before it are kept.
4. A timeout or a peer abort other than „object does not exist" in the middle of the chain → failed, with the exception attached, earlier devices kept.
5. Cancellation → `OperationCanceledException` reaches the caller; it is never folded into a result.

## File Structure

| File | Responsibility |
|---|---|
| `src/CanKit.Pro.CANopen/CanOpenDeviceType.cs` (create) | The 32-bit word and its two halves. |
| `src/CanKit.Pro.CANopen/CanOpenLogicalDevices.cs` (create) | Arithmetic per logical device; `CanOpenLogicalDevice`; the reader and its result. |
| `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenDeviceTypeTests.cs` (create) | Task 1. |
| `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenLogicalDevicesTests.cs` (create) | Tasks 2–4. |
| `tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.approved.txt` (modify) | Task 4. |
| `src/CanKit.Pro.CANopen/README.md`, `docs/requirements/SRS-CanKit.Pro.md`, `-en.md` (modify) | Task 4. |

Run one test class with:
`dotnet test tests/CanKit.Pro.Tests -c Release --framework net10.0 --filter "FullyQualifiedName~<ClassName>"`
(if the runner rejects `--filter`, `CONTRIBUTING.md` § "Before you push" has the current form).

---

### Task 1: `CanOpenDeviceType`

**Files:**
- Create: `src/CanKit.Pro.CANopen/CanOpenDeviceType.cs`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenDeviceTypeTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace CanKit.Pro.CANopen;

public readonly record struct CanOpenDeviceType(uint Value)
{
    public const ushort FurtherLogicalDevices = 0xFFFF;
    public ushort ProfileNumber { get; }            // bits 0..15
    public ushort AdditionalInformation { get; }    // bits 16..31
    public bool FollowsStandardizedProfile { get; } // ProfileNumber != 0
    public bool HasFurtherLogicalDevices { get; }   // AdditionalInformation == FFFFh
    public static bool TryParse(ReadOnlySpan<byte> littleEndian, out CanOpenDeviceType deviceType);
    public override string ToString();              // "0x000F0191 (profile 401)" / "0x00000000 (no profile)"
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class CanOpenDeviceTypeTests
{
    [Theory]
    [InlineData(0x000F0191u, 401, 0x000F, true, false)]   // CiA 401, four function bits
    [InlineData(0xFFFF0198u, 408, 0xFFFF, true, true)]    // CiA 408 first, more follow
    [InlineData(0x00000000u, 0, 0x0000, false, false)]
    [InlineData(0xFFFF0000u, 0, 0xFFFF, false, true)]     // §7.5.2.1: no profile, further device
    public void The_Word_Splits_Into_Profile_And_Additional_Information(
        uint value, int profile, int additional, bool standardized, bool further)
    {
        var type = new CanOpenDeviceType(value);
        type.ProfileNumber.Should().Be((ushort)profile);
        type.AdditionalInformation.Should().Be((ushort)additional);
        type.FollowsStandardizedProfile.Should().Be(standardized);
        type.HasFurtherLogicalDevices.Should().Be(further);
    }

    [Fact]
    public void Four_Little_Endian_Bytes_Parse_And_Any_Other_Length_Does_Not()
    {
        CanOpenDeviceType.TryParse(new byte[] { 0x91, 0x01, 0x0F, 0x00 }, out var type).Should().BeTrue();
        type.Value.Should().Be(0x000F0191u);
        CanOpenDeviceType.TryParse(new byte[3], out _).Should().BeFalse();
        CanOpenDeviceType.TryParse(new byte[5], out _).Should().BeFalse();
    }

    [Fact]
    public void ToString_Names_The_Profile_In_Decimal()
    {
        new CanOpenDeviceType(0x000F0191u).ToString().Should().Be("0x000F0191 (profile 401)");
        new CanOpenDeviceType(0u).ToString().Should().Be("0x00000000 (no profile)");
    }
}
```

- [ ] **Step 2: Run** `CanOpenDeviceTypeTests` — expected: does not compile (`CanOpenDeviceType` not found).
- [ ] **Step 3: Implement** the struct as in Interfaces. `ToString` formats with `CultureInfo.InvariantCulture`. `Utils/IsExternalInit.cs` already makes the record struct compile on `netstandard2.0`.
- [ ] **Step 4: Run** `CanOpenDeviceTypeTests` — expected: PASS.
- [ ] **Step 5: Mutation.** Swap the two halves (`ProfileNumber` from the upper 16 bits); the theory must fail. Revert.
- [ ] **Step 6: Commit** — `feat(canopen): split the device type 1000h into profile and additional information`

---

### Task 2: Logical-device arithmetic

**Files:**
- Create: `src/CanKit.Pro.CANopen/CanOpenLogicalDevices.cs`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenLogicalDevicesTests.cs`

**Interfaces:**
- Consumes: `CanOpenDeviceType` (Task 1).
- Produces:

```csharp
public sealed class CanOpenLogicalDevice
{
    public CanOpenLogicalDevice(int index, CanOpenDeviceType deviceType); // index outside 0..7 → ArgumentOutOfRangeException
    public int Index { get; }
    public CanOpenDeviceType DeviceType { get; }
    public ushort ObjectAreaStart { get; }   // CanOpenLogicalDevices.ObjectAreaStart(Index)
    public int PdoNumberOffset { get; }      // CanOpenLogicalDevices.PdoNumberOffset(Index)
}

public static class CanOpenLogicalDevices
{
    public const int MaxCount = 8;
    public static ushort ObjectAreaStart(int index);   // 6000h + index·800h
    public static ushort DeviceTypeIndex(int index);   // 67FFh + index·800h
    public static int PdoNumberOffset(int index);      // index·64
    public static ushort ToAbsoluteIndex(int index, ushort profileIndex); // profileIndex in 6000h..67FFh, else ArgumentOutOfRangeException
}
```

All four statics throw `ArgumentOutOfRangeException` for `index` outside `0..7`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(0, 0x6000, 0x67FF, 0)]
[InlineData(1, 0x6800, 0x6FFF, 64)]
[InlineData(7, 0x9800, 0x9FFF, 448)]
public void Each_Logical_Device_Has_Its_Object_Area_Device_Type_Object_And_Pdo_Offset(
    int index, int areaStart, int deviceTypeIndex, int pdoOffset)
{
    CanOpenLogicalDevices.ObjectAreaStart(index).Should().Be((ushort)areaStart);
    CanOpenLogicalDevices.DeviceTypeIndex(index).Should().Be((ushort)deviceTypeIndex);
    CanOpenLogicalDevices.PdoNumberOffset(index).Should().Be(pdoOffset);
}

[Fact]
public void A_Profile_Index_Moves_By_800h_Per_Logical_Device()
{
    CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6200).Should().Be(0x6200);
    CanOpenLogicalDevices.ToAbsoluteIndex(2, 0x6200).Should().Be(0x7200);
    FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x5FFF)).Should().Throw<ArgumentOutOfRangeException>();
    FluentActions.Invoking(() => CanOpenLogicalDevices.ToAbsoluteIndex(0, 0x6800)).Should().Throw<ArgumentOutOfRangeException>();
}

[Theory]
[InlineData(-1)]
[InlineData(8)]
public void There_Are_Eight_Logical_Devices_And_No_More(int index)
{
    FluentActions.Invoking(() => CanOpenLogicalDevices.ObjectAreaStart(index)).Should().Throw<ArgumentOutOfRangeException>();
    FluentActions.Invoking(() => new CanOpenLogicalDevice(index, default)).Should().Throw<ArgumentOutOfRangeException>();
}
```

- [ ] **Step 2: Run** `CanOpenLogicalDevicesTests` — expected: does not compile.
- [ ] **Step 3: Implement** both types as in Interfaces.
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Mutation.** Change the PDO offset factor from 64 to 32; the first theory must fail. Revert.
- [ ] **Step 6: Commit** — `feat(canopen): object area and PDO offset of a logical device`

---

### Task 3: Reading the chain

**Files:**
- Modify: `src/CanKit.Pro.CANopen/CanOpenLogicalDevices.cs`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenLogicalDevicesTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; `ICanOpenNode.SdoUploadAsync(byte, ushort, byte, CancellationToken)`; `SdoAbortException` (`Origin`, `AbortCode`), `SdoAbortCode.ObjectDoesNotExist`, `PeerSdoAccessException.PeerDescriptionLoaded`.
- Produces:

```csharp
public enum CanOpenLogicalDeviceReadEnd
{
    Complete = 0,                 // every logical device the peer has is in the list
    PeerDescriptionRequired = 1,  // 1000h says more follow; the gate refused 67FFh because no description is bound
    Failed = 2,                   // a read inside the chain failed; Failure says how
}

public sealed class CanOpenLogicalDeviceReadResult
{
    public CanOpenDeviceType DeviceType { get; }                 // 1000h as read
    public IReadOnlyList<CanOpenLogicalDevice> Devices { get; }  // never empty, ordered by Index
    public CanOpenLogicalDeviceReadEnd End { get; }
    public Exception? Failure { get; }                           // non-null only for Failed
}

// in CanOpenLogicalDevices:
public static Task<CanOpenLogicalDeviceReadResult> ReadAsync(
    ICanOpenNode client, byte peerNodeId, CancellationToken cancellationToken = default);

internal static Task<CanOpenLogicalDeviceReadResult> ReadCoreAsync(
    Func<ushort, CancellationToken, Task<byte[]>> uploadSubindexZero, CancellationToken cancellationToken);
```

The rule `ReadCoreAsync` implements — this is the decision, write the body from it:

1. Upload `1000h`. Any exception propagates. A value that is not four bytes throws `InvalidDataException`.
2. `HasFurtherLogicalDevices` false → one device, index 0, type = `1000h`; `Complete`.
3. Otherwise, for `index` 0..7, upload `DeviceTypeIndex(index)`:
   - four bytes → device `index` with that type; continue.
   - `SdoAbortException` from the peer with `ObjectDoesNotExist` → stop, `Complete`.
   - `PeerSdoAccessException` with `PeerDescriptionLoaded == true` → stop, `Complete` (the bound file is the list of what exists).
   - `PeerSdoAccessException` with `PeerDescriptionLoaded == false` → stop, `PeerDescriptionRequired`.
   - any other `SdoAbortException`, or a value of another length (`InvalidDataException`) → stop, `Failed`, `Failure` set.
   - `OperationCanceledException` and everything else → propagate.
4. If the loop stopped before device 0 was read, device 0 is reported with the type from `1000h`.

`ReadAsync` validates `client` (null → `ArgumentNullException`) and `peerNodeId` (`CanOpenCobId.ValidateNodeId`) and calls the core with `client.SdoUploadAsync(peerNodeId, index, 0x00, token)`.

- [ ] **Step 1: Write the failing tests.** A helper `Script(params (ushort Index, object Answer)[] answers)` returns the upload delegate: a `uint` answer yields its four little-endian bytes, a `byte[]` is returned as is, an `Exception` is thrown, an index not in the script throws the peer abort `ObjectDoesNotExist`; it records the indices asked. Build the `SdoAbortException` instances the way `CanOpenDiscoveryTests.Any_Answer_Is_Presence_…` does.

```csharp
[Fact] // FR-CO-063
public async Task A_Device_Without_FFFFh_Is_One_Logical_Device_And_Nothing_Else_Is_Asked()
{
    var script = Script((0x1000, 0x000F0191u));
    var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);
    result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
    result.Devices.Should().ContainSingle().Which.DeviceType.Value.Should().Be(0x000F0191u);
    script.Asked.Should().Equal(0x1000);
}

[Fact] // FR-CO-063
public async Task The_Chain_Is_Walked_Until_An_Object_Does_Not_Exist()
{
    var script = Script((0x1000, 0xFFFF0191u), (0x67FF, 0x000F0191u), (0x6FFF, 0x00010198u));
    var result = await CanOpenLogicalDevices.ReadCoreAsync(script.Upload, default);
    result.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
    result.DeviceType.Value.Should().Be(0xFFFF0191u);
    result.Devices.Select(d => (d.Index, d.DeviceType.Value))
        .Should().Equal((0, 0x000F0191u), (1, 0x00010198u));
    script.Asked.Should().Equal(0x1000, 0x67FF, 0x6FFF, 0x77FF);
}

[Fact]
public async Task Eight_Devices_End_The_Chain_Without_A_Ninth_Request()
// all of 67FFh..9FFFh scripted → 8 devices, Complete, Asked has 9 entries, last is 0x9FFF

[Fact] // Review Focus 1
public async Task FFFFh_Without_67FFh_Still_Reports_The_First_Device_From_1000h()
// Script((0x1000, 0xFFFF0191u)) → Complete, single device, index 0, Value 0xFFFF0191

[Fact] // Review Focus 2
public async Task Without_A_Peer_Description_The_Result_Says_One_Is_Required()
// 0x67FF → new PeerSdoAccessException(1, 0x67FF, 0, peerDescriptionLoaded: false, "x")
// → PeerDescriptionRequired, single device from 1000h, Failure null, Asked == {0x1000, 0x67FF}

[Fact]
public async Task A_Bound_Description_That_Omits_The_Object_Ends_The_Chain_As_Complete()
// 0x67FF answers, 0x6FFF → PeerSdoAccessException(peerDescriptionLoaded: true) → Complete, one device from 67FFh

[Fact] // Review Focus 3
public async Task A_Device_Type_Of_The_Wrong_Length_Fails_The_Chain_And_Keeps_What_Was_Read()
// 0x67FF ok, 0x6FFF → new byte[2] → Failed, Failure is InvalidDataException, Devices has index 0 only

[Fact] // Review Focus 4
public async Task A_Timeout_Or_Another_Abort_Inside_The_Chain_Is_A_Failure_With_Its_Cause()
// 0x67FF ok, 0x6FFF → local SdoProtocolTimedOut abort → Failed, Failure is that instance

[Fact] // Review Focus 5
public async Task Cancellation_And_A_Failing_1000h_Reach_The_Caller()
// 0x67FF → OperationCanceledException → awaiting throws OperationCanceledException
// 0x1000 → peer abort → awaiting throws SdoAbortException
// 0x1000 → new byte[2] → awaiting throws InvalidDataException

[Fact]
public async Task ReadAsync_Rejects_A_Null_Client_And_A_Node_Id_Outside_1_To_127()
```

Write each commented test out with the assertions its comment names.

- [ ] **Step 2: Run** — expected: does not compile.
- [ ] **Step 3: Implement** `CanOpenLogicalDeviceReadEnd`, `CanOpenLogicalDeviceReadResult` (internal constructor), `ReadCoreAsync` and `ReadAsync` from the rule above. `ConfigureAwait(false)` on every await, as elsewhere in the package.
- [ ] **Step 4: Run** — expected: PASS.
- [ ] **Step 5: Mutations**, one at a time, each must turn the named test red, then revert:
  - treat `PeerDescriptionLoaded == false` as `Complete` → `Without_A_Peer_Description_…`
  - drop rule 4 → `FFFFh_Without_67FFh_…`
  - loop to 9 → `Eight_Devices_…`
- [ ] **Step 6: Commit** — `feat(canopen): read the logical devices of a peer from 1000h and 67FFh`

---

### Task 4: On the bus, approval, documents

**Files:**
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenLogicalDevicesTests.cs`
- Modify: `tests/CanKit.Pro.Tests/ApiApprovals/CanKit.Pro.CANopen.approved.txt`, `src/CanKit.Pro.CANopen/README.md`, `docs/requirements/SRS-CanKit.Pro.md`, `docs/requirements/SRS-CanKit.Pro-en.md`

**Interfaces:**
- Consumes: `CanOpenLogicalDevices.ReadAsync` (Task 3); `PeerSdoLaboratory.Variables(params ushort[])`; the two-node loopback setup of `CanOpenDiscoveryTests.The_Scan_Finds_Answering_Nodes_And_Honours_A_Peer_File_Without_1000h`.

- [ ] **Step 1: Write the integration test.** Make the class `IClassFixture<VirtualAdapterFixture>` like `CanOpenDiscoveryTests`. Server node 0x10 declares `1000h` = `FFFF0191h`, `67FFh` = `000F0191h`, `6FFFh` = `00010198h` in its object dictionary; client node 0x7F.

```csharp
[Fact] // FR-CO-063
public async Task Over_The_Bus_The_Gate_Decides_How_Far_The_Chain_Is_Read()
{
    // no description bound
    var unbound = await CanOpenLogicalDevices.ReadAsync(client, 0x10);
    unbound.End.Should().Be(CanOpenLogicalDeviceReadEnd.PeerDescriptionRequired);
    unbound.Devices.Should().ContainSingle();

    // description lists 1000h, 67FFh, 6FFFh
    client.BindPeerDeviceDescription(0x10, PeerSdoLaboratory.Variables(0x1000, 0x67FF, 0x6FFF));
    var bound = await CanOpenLogicalDevices.ReadAsync(client, 0x10);
    bound.End.Should().Be(CanOpenLogicalDeviceReadEnd.Complete);
    bound.Devices.Select(d => d.DeviceType.ProfileNumber).Should().Equal((ushort)401, (ushort)408);
}
```

- [ ] **Step 2: Run** `CanOpenLogicalDevicesTests` — expected: PASS (the code exists). If the unbound half fails because the gate's exception is not the one Task 3 assumed, fix Task 3's rule in code and its test, not this test.
- [ ] **Step 3: Mutation.** In `ReadAsync`, pass sub-index `0x01` instead of `0x00`; this test must fail. Revert.
- [ ] **Step 4: API approval.** Run `PublicApiSurfaceTests`; it fails and writes `CanKit.Pro.CANopen.received.txt`. Check the diff contains only `CanOpenDeviceType`, `CanOpenLogicalDevice`, `CanOpenLogicalDevices`, `CanOpenLogicalDeviceReadEnd`, `CanOpenLogicalDeviceReadResult`; replace the approval with it; run again — PASS.
- [ ] **Step 5: README.** In `src/CanKit.Pro.CANopen/README.md` add `### Device type and logical devices` at the end of `## Discovery`: what `1000h` holds, the `FFFFh` / `67FFh + x·800h` rule with its CiA 301 clause, the three `End` values and when each occurs, a six-line sample calling `ReadAsync`. Add the FR-CO-063 row to the table under `## Coverage`. State no count that the code or the SRS already states.
- [ ] **Step 6: SRS.** Add `FR-CO-063` after `FR-CO-062` in both SRS files, in the column layout of `FR-CO-034`: the system MUST split `1000h` into profile number and additional information and MUST read a peer's logical devices from `1000h` and `67FFh + x·800h` through the peer-SDO gate, reporting whether the list is complete, needs a peer description, or failed (CiA 301 §7.5.2.1). German original, English translation.
- [ ] **Step 7: Gate.** Run the five commands of Global Constraints. Expected: all pass; traceability prints `Must untraced (NEW): 0`.
- [ ] **Step 8: Commit** — `test(canopen): read logical devices over the bus` for the test and approval, `docs(canopen): FR-CO-063, device type and logical devices` for README and SRS.
