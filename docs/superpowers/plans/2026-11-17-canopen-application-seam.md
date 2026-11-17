# CANopen Application Seam Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `CanOpenNode` an internal seam through which a device profile (CiA 401, CiA 408, later others) attaches behaviour to the node at open, and give the tool side an internal profile descriptor and catalogue — step 3 of the device-profile sequence.

**Architecture:** A profile implements `ICanOpenApplication` and is handed to the node in `CanOpenNodeOptions`. The constructor attaches it after the device description is applied and before the start-up job is queued. The node owns one host object per application (`ICanOpenApplicationHost`) in a new partial class `CanOpenNode.Application.cs`; existing node code gets one-line calls into it at the points named below. Everything is `internal`; nothing reaches an API approval file.

**Tech Stack:** C# (netstandard2.0 + net10.0), xUnit, AwesomeAssertions, the virtual CAN adapter and `ManualTimeSource` of `tests/CanKit.Pro.Tests`.

**Spec:** `docs/reviews/2026-11-17-canopen-device-profiles-scope.md` (§ „Die Anwendungsnaht", § „Gerätetyp und logische Geräte", § „Reihenfolge" step 3) and ADR-14 in `docs/architecture/arc42-CanKit.Pro.md`.

## Global Constraints

- `ICanOpenNode` is not widened. No public type, member or behaviour changes: `tests/CanKit.Pro.Tests/ApiApprovals/*.approved.txt` must stay byte-identical. If an approval test produces a `*.received.txt`, something became public by accident — fix the code, not the baseline.
- Every new type is `internal` and lives in namespace `CanKit.Pro.CANopen.Application`, folder `src/CanKit.Pro.CANopen/Application/`.
- A node opened without applications behaves exactly as before; the new code paths return at once when `_applications.Length == 0`.
- No commit of type `feat` or `fix`: nothing a user can reach changes, so the branch must not publish a release. Use `refactor(canopen):`, `test(canopen):`, `docs:`. Every commit is a Conventional Commit and ends with the attribution trailer of the model that wrote it.
- Callbacks into an application run on the actor loop, except `ValidateWrite` (writing thread, under the dictionary's write gate) and `Attach` (constructing thread). An exception thrown by an actor-loop callback goes to `RaiseBackgroundException` and does not stop the node or the next application.
- Nothing is asserted against a wall clock. Time is driven with `ManualTimeSource` (`tests/CanKit.Pro.Tests/Infrastructure/ManualTimeSource.cs`); "has the actor run it" is settled with `node.PostToActorAsync(() => { })` awaited, not with a delay.
- Every test this plan says catches something is checked by a mutation before the task is reported done: break the named source line, watch the test fail, restore. Report each mutation and its result.
- Gate before each commit: `dotnet build CanKit.Pro.sln -c Release -p:CI=true` (0 warnings, 0 errors), the new test class on `net10.0`, `dotnet format CanKit.Pro.sln --verify-no-changes`.
- No SRS row, no README section: the scope record puts both in the publication step.

## Review Focus

1. An application callback that throws: the exception surfaces through `BackgroundExceptionOccurred`, the second application still gets its callback, the node still answers SDO. (Task 2)
2. A writer thread hammering a claimed entry while the actor is held: at most one evaluation is queued, and after release the application sees the entry once. (Task 2)
3. An application that writes one of its own entries from inside `OnEntriesWritten`: no second notification, no loop. (Task 2)
4. `CanOpenNodeOptions.With(...)` / `WithLss` / the other copy helpers on options that carry applications: the copy still carries them. (Task 1)
5. `Attach` throwing during open: the open fails with that exception and the node's actor thread has ended — same guarantee the constructor gives for a description that cannot be applied (#357 C-10; find its test in `tests/CanKit.Pro.Tests` and follow its method). (Task 1)

---

## File Structure

| File | Responsibility |
|---|---|
| `src/CanKit.Pro.CANopen/Application/CanOpenObjectAddress.cs` | `(Index, Subindex)` value |
| `src/CanKit.Pro.CANopen/Application/CanOpenIndexRange.cs` | inclusive index range, overlap test, range of a logical device |
| `src/CanKit.Pro.CANopen/Application/ICanOpenApplication.cs` | what a profile gives the node; `CanOpenApplicationRequirements` |
| `src/CanKit.Pro.CANopen/Application/ICanOpenApplicationHost.cs` | what the node gives a profile |
| `src/CanKit.Pro.CANopen/Application/ICanOpenProfile.cs` | tool-side descriptor; `ICanOpenProfilePeer` |
| `src/CanKit.Pro.CANopen/Application/CanOpenProfileCatalog.cs` | descriptors by profile number → peer access |
| `src/CanKit.Pro.CANopen/CanOpenNode.Application.cs` | attach, claims, host implementation, dispatch of every callback |
| `src/CanKit.Pro.CANopen/CanOpenNodeOptions.cs` | internal `Applications`, internal `WithApplication`, carried by `Copy` |
| `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenApplicationSeamTests.cs` (+ partials if it grows past ~600 lines) | all seam tests; nested `RecordingApplication` |
| `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenProfileCatalogTests.cs` | catalogue tests |

Patterns to read before writing: `Safety/ISrdoEngineHost.cs` (host interface, doc style), `CanOpenNode.Pdo.cs` `OnOdEntryWrittenForCoS` / `EvaluateCoSOnActor` (bounded coalescing), `CanOpenNode.ErrorRegister.cs` (register composition), `tests/.../CANopen/CanOpenControllerEmcyTests.cs` (`ControllerEmcyRig`: node on a virtual bus with `ManualTimeSource`, `Settle`, `Wire`), `tests/.../CANopen/CanOpenLogicalDevicesTests.cs` (two nodes over the virtual adapter).

---

### Task 1: Seam types, options, attach at open, claims, restore

**Files:**
- Create: the four files `CanOpenObjectAddress.cs`, `CanOpenIndexRange.cs`, `ICanOpenApplication.cs`, `ICanOpenApplicationHost.cs`; `CanOpenNode.Application.cs`
- Modify: `CanOpenNodeOptions.cs` (property, `WithApplication`, `Copy`), `CanOpenNode.cs` (constructor, inside the existing `try` after `ApplyDeviceDescription`), `CanOpenNode.CommunicationProfile.cs` (`IsRestorableObject`)
- Test: `CanOpenApplicationSeamTests.cs`

**Interfaces — Produces** (complete seam; later tasks implement the host members that this task leaves throwing `NotSupportedException`):

```csharp
namespace CanKit.Pro.CANopen.Application;

internal readonly record struct CanOpenObjectAddress(ushort Index, byte Subindex);

internal readonly record struct CanOpenIndexRange
{
    public CanOpenIndexRange(ushort first, ushort last);        // first > last → ArgumentException
    public ushort First { get; }
    public ushort Last { get; }
    public bool Contains(ushort index);
    public bool Overlaps(CanOpenIndexRange other);
    /// 6000h + n·800h .. +7FFh, through CanOpenLogicalDevices.ObjectAreaStart.
    public static CanOpenIndexRange ForLogicalDevice(int logicalDevice);
}

internal sealed class CanOpenApplicationRequirements
{
    public static CanOpenApplicationRequirements None { get; }
    /// Highest PDO number the application configures; 0 for none.
    public int PdoCount { get; init; }
    /// The application wants OnCommunicationFailureChanged; needs CanOpenNodeOptions.EnableErrorBehavior.
    public bool CommunicationFailureMonitoring { get; init; }
}

internal interface ICanOpenApplication
{
    string Name { get; }
    IReadOnlyList<CanOpenIndexRange> ClaimedRanges { get; }
    CanOpenApplicationRequirements Requirements { get; }
    void Attach(ICanOpenApplicationHost host);
    OdWriteDecision ValidateWrite(ushort index, byte subindex, byte[] value);
    void OnEntriesWritten(IReadOnlyList<CanOpenObjectAddress> entries);
    void OnNmtStateChanged(NmtState previous, NmtState current);
    void OnReset(NmtCommand command);
    void OnCommunicationFailureChanged(bool active);
}

internal interface ICanOpenApplicationHost
{
    ObjectDictionary ObjectDictionary { get; }
    NmtState State { get; }                       // actor loop or Attach
    TimeSpan Now { get; }                         // monotonic, the node's time source
    bool CommunicationFailureActive { get; }      // actor loop
    IDeadline Arm(TimeSpan delay, Action onExpired);
    void SetDeviceType(int logicalDevice, CanOpenDeviceType deviceType);          // Attach only
    void RequestTransmission(IReadOnlyList<CanOpenObjectAddress> entries);        // actor loop
    void RaiseError(ushort errorCode, byte errorRegisterBits, ReadOnlySpan<byte> manufacturerSpecific = default); // actor loop
    void ClearError(ushort errorCode);                                            // actor loop
    void RequestNmtState(NmtState target);                                        // actor loop
}
```

`CanOpenNodeOptions`: `internal IReadOnlyList<ICanOpenApplication> Applications { get; init; } = Array.Empty<ICanOpenApplication>();` and `internal CanOpenNodeOptions WithApplication(ICanOpenApplication application)` (null → `ArgumentNullException`; returns a copy with the application appended). `Copy(...)` carries `Applications` over.

`CanOpenNode.Application.cs`: `private ApplicationSlot[] _applications` (application + its host), `private bool IsClaimed(ushort index)`, `private ApplicationSlot? ClaimantOf(ushort index)`, `private void AttachApplications()`.

**Rules this task decides:**

- A claimed range lies within `2000h`–`FFFFh`. A range touching anything below `2000h` fails the open: `ArgumentException`, `ParamName == "options"`, message names the application and the range.
- Two ranges that overlap — of two applications or of one — fail the open the same way, message names both applications.
- `Requirements.PdoCount > _pdoCount`, or `CommunicationFailureMonitoring` without `EnableErrorBehavior`, fails the open the same way, message names the option to set and the value needed.
- The same application instance listed twice fails the open the same way.
- Order in the constructor: `PopulateCommunicationProfile` → `ApplyDeviceDescription` → **validate claims and requirements → `Attach` each application in list order** → retake `_factoryDefaults = SnapshotRestorableValues(); _powerOnValues = _factoryDefaults;` → monitors. All inside the existing `try`, so a throw disposes the actor.
- `IsRestorableObject(index)` becomes true for every claimed index as well. Consequence to test: an object the application declared in `Attach` returns to its attach-time value on NMT Reset Node, is left alone by Reset Communication, and is covered by `1010h` save / `1011h` load.
- `ObjectDictionary`, `State`, `Now`, `Arm` work from this task on (`Now` from `_actor.TimeSource`, `Arm` → `_deadlines.Arm`). The other host members throw `NotSupportedException` until their task.

- [ ] **Step 1: Write `RecordingApplication` and the failing tests**

`RecordingApplication : ICanOpenApplication` — constructor `(string name, params CanOpenIndexRange[] claims)`; public settable `Requirements`, `Action<ICanOpenApplicationHost>? OnAttach`, `Func<ushort, byte, byte[], OdWriteDecision>? Validate`, one `Action<…>?` per callback; a thread-safe `List<string> Log` every callback appends to (`"attach"`, `"written 6000:01,6000:02"`, `"nmt PreOperational>Operational"`, `"reset ResetNode"`, `"failure True"`); `Host` captured in `Attach`.

Tests (names are the claims):

```csharp
An_Application_Is_Attached_Before_The_Node_Boots()
// OnAttach declares 0x6000:01 U8 = 0x5A; a second node uploads 6000h:01 right after seeing the boot-up → 0x5A.
// Also: app.Log[0] == "attach" and Host.State == NmtState.Initializing inside Attach.
A_Description_Is_Applied_Before_Attach()
// node opened from a description declaring 0x6000; inside OnAttach host.ObjectDictionary.ContainsIndex(0x6000) is true.
A_Claim_Below_2000h_Fails_The_Open()                  // range 0x1F00..0x2000 → ArgumentException, ParamName "options"
Overlapping_Claims_Fail_The_Open()                    // 6000..67FF and 6700..6FFF, two applications; message contains both names
The_Same_Application_Twice_Fails_The_Open()
A_Pdo_Requirement_Above_PdoCount_Fails_The_Open()     // Requirements.PdoCount = 5, options.PdoCount = 4; message contains "PdoCount" and "5"
Failure_Monitoring_Without_ErrorBehavior_Fails_The_Open()   // message contains "EnableErrorBehavior"
An_Attach_That_Throws_Fails_The_Open_And_Ends_The_Actor()   // Review Focus 5
Options_Copies_Keep_Their_Applications()              // Review Focus 4: WithApplication(a).With(pdoCount: 8).WithLss(null) … Applications == [a]
Reset_Node_Restores_What_The_Application_Declared()   // declare 6000:01 = 1 in Attach, write 9, NMT Reset Node → 1
Reset_Communication_Leaves_Application_Objects_Alone()// same, Reset Communication → 9
A_Stored_Application_Value_Survives_Reset_Node()      // write 9, node.StoreParameters(), write 3, Reset Node → 9
Deadlines_Run_On_The_Nodes_Clock()                    // host.Arm(50 ms) in Attach; not fired before clock.Advance(50 ms); fired after; Now advanced by 50 ms
```

- [ ] **Step 2: Run them, see them fail to compile / fail**

`dotnet test tests/CanKit.Pro.Tests -c Release --framework net10.0 --filter "FullyQualifiedName~CanOpenApplicationSeamTests"`

- [ ] **Step 3: Implement** the types, the options plumbing, `AttachApplications`, the `IsRestorableObject` change.

- [ ] **Step 4: Run the tests — pass. Run the whole `CANopen` test folder — no regression.**

`--filter "FullyQualifiedName~TestCases.CANopen"`

- [ ] **Step 5: Mutations** — (a) attach after the start-up `_actor.Post` instead of before → `An_Application_Is_Attached_Before_The_Node_Boots` fails or (if it races green) say so and strengthen it with `Host.State` inside `Attach`; (b) drop the snapshot retake → `Reset_Node_Restores…` fails; (c) drop `Applications` from `Copy` → `Options_Copies…` fails; (d) remove the claimed-index term from `IsRestorableObject` → `A_Stored_Application_Value…` fails.

- [ ] **Step 6: Commit** — `refactor(canopen): attach applications to a node at open (internal seam)`

---

### Task 2: Write validation, written-entry notification, who decides a transmission

**Files:**
- Modify: `CanOpenNode.Application.cs`, `CanOpenNode.CommunicationProfile.cs` (`ValidateCommunicationWrite`), `CanOpenNode.Pdo.cs` (`OnOdEntryWrittenForCoS`, `EvaluateCoSOnActor`), `CanOpenNode.cs` (subscribe the new handler next to `OnOdEntryWrittenForCoS`)
- Test: `CanOpenApplicationSeamTests.cs`

**Interfaces — Consumes:** Task 1. **Produces:** `ICanOpenApplicationHost.RequestTransmission` working; `private void OnOdEntryWrittenForApplications(ushort index, byte subindex)`; `private void TriggerChangeOfState(HashSet<uint> dirty)` (the body of `EvaluateCoSOnActor` after it has taken the dirty set, reused by `RequestTransmission`).

**Rules this task decides:**

- `ValidateCommunicationWrite`: before the final `return OdWriteDecision.Accept;` — `if (ClaimantOf(index) is { } slot) return slot.Application.ValidateWrite(index, subindex, value);`. Managed objects keep precedence because they return earlier.
- Notification: every stored write to a claimed entry — from the application thread, an SDO download, an RPDO — reaches the claimant as `OnEntriesWritten` on the actor loop. Mechanism as `OnOdEntryWrittenForCoS`: a dirty set under a gate, one posted evaluation at most, re-armed by writes that land during the evaluation. Entries are delivered sorted by index, then sub-index, each once.
- Not notified: a write the claimant itself makes on the actor loop from inside one of its own callbacks (field `_applicationInCallback`, actor-confined, set around every callback dispatch); a write made while the node restores values in a reset (`_restoringValues`); writes during `Attach` (the handler is subscribed after the attach phase).
- **Who claims decides.** `OnOdEntryWrittenForCoS` returns at once for a claimed index: a write to a claimed entry never triggers a TPDO or SRDO by itself. The claimant triggers with `host.RequestTransmission(entries)`: every entry must be claimed by the caller (`ArgumentException` otherwise), the call must be on the actor loop (`InvalidOperationException` otherwise), and it has the effect an application write has today for an unclaimed entry — event-driven TPDOs that map an entry transmit subject to inhibit time, synchronous-acyclic ones latch for the next SYNC, transmit SRDOs trigger; nothing happens outside Operational. It is not subject to `EnableChangeOfStateTpdo` / `EnableChangeOfStateSrdo`: those switch the automatism off, and this is not the automatism.
- All actor-loop callbacks go through one private dispatcher that sets `_applicationInCallback`, catches every exception and passes it to `RaiseBackgroundException`.

- [ ] **Step 1: Failing tests**

```csharp
A_Write_To_A_Claimed_Entry_Asks_Its_Claimant()            // Validate → Reject(ValueRangeExceeded); SDO download from a 2nd node aborts 0x06090030; value unchanged
An_Unclaimed_Entry_Is_Not_The_Applications_Business()     // entry 0x2100 outside the claim: Validate never called, write stored
A_Handled_Write_Is_Not_Stored()                           // Validate → Handled; SDO download succeeds, value unchanged, no "written" in Log
An_Sdo_Download_Is_Reported_On_The_Actor()                // Log gets "written 6200:01"; callback observed IsOnCurrentActor (assert via host.State readable without deadlock + thread id != test thread)
An_Rpdo_Is_Reported_On_The_Actor()                        // RPDO 1 mapped to 6200:01; frame from Wire → "written 6200:01"
Many_Writes_Are_Reported_Once()                           // Review Focus 2: hold the actor (PostToActorAsync with a gate), 1000 WriteUnsigned on 6000:01 from the test thread, release → exactly one "written 6000:01"
The_Applications_Own_Write_In_A_Callback_Is_Not_Reported()// Review Focus 3: OnEntriesWritten writes 6000:02; Log holds one "written 6000:01", no "6000:02"
A_Throwing_Callback_Is_A_Background_Exception()           // Review Focus 1: two apps; first throws in OnEntriesWritten; BackgroundExceptionOccurred raised; second app's later write is still reported; SDO upload still answered
A_Reset_Does_Not_Report_The_Restored_Entries()            // write 6000:01, settle, clear Log, Reset Node → no "written"
A_Claimed_Entry_Does_Not_Transmit_By_Itself()             // TPDO 1 event-driven maps 6000:01, node Operational; WriteUnsigned(6000:01) → no frame on Wire after settle
The_Claimant_Requests_The_Transmission()                  // same; OnEntriesWritten calls host.RequestTransmission(entries) → one TPDO frame with the new value
A_Requested_Transmission_Ignores_The_Automatism_Switch()  // EnableChangeOfStateTpdo = false → still one frame
A_Request_For_A_Foreign_Entry_Is_Refused()                // RequestTransmission(0x2100:00) → ArgumentException (surfaces as background exception; assert on it)
An_Unclaimed_Entry_Still_Transmits_By_Itself()            // regression guard: TPDO maps 0x2100:00, an application is attached, write → one frame
```

- [ ] **Step 2: Run — fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the class and the `CANopen` folder — pass.**
- [ ] **Step 5: Mutations** — (a) remove the claimant branch in `ValidateCommunicationWrite` → first test fails; (b) post per write instead of coalescing → `Many_Writes…` fails; (c) remove the `_applicationInCallback` check → `…Own_Write…` fails; (d) remove the claimed-index return in `OnOdEntryWrittenForCoS` → `A_Claimed_Entry_Does_Not_Transmit_By_Itself` fails; (e) remove the `_restoringValues` check → `A_Reset_Does_Not_Report…` fails; (f) drop the try/catch in the dispatcher → `A_Throwing_Callback…` fails.
- [ ] **Step 6: Commit** — `refactor(canopen): let an attached application validate, observe and transmit its entries`

---

### Task 3: NMT state, reset, requesting a state

**Files:**
- Modify: `CanOpenNode.Application.cs`, `CanOpenNode.CommunicationProfile.cs` (`ApplyNmtTransition`, `PerformNmtReset`, `EnterPreOperationalFromReset`), `CanOpenNode.cs` (start-up job, `_state = NmtState.PreOperational`)
- Test: `CanOpenApplicationSeamTests.cs`

**Interfaces — Produces:** `host.RequestNmtState`; `private void NotifyNmtStateChanged(NmtState previous)` (no-op when `previous == _state`).

**Rules this task decides:**

- `OnNmtStateChanged(previous, current)` is raised for every change of the node's NMT state, at four places: end of the start-up job (`Initializing → PreOperational`), end of `ApplyNmtTransition` (after the pending-EMCY handling, before the state-change heartbeat is ordered; not inside `_od.Transaction`), in `PerformNmtReset` right after `_state = NmtState.Initializing` (`previous → Initializing`, only when it differs), and in `EnterPreOperationalFromReset`.
- `OnReset(command)` is raised in `PerformNmtReset` after the values are restored and immediately before `RaiseApplicationReset`: the profile resets its state machine first, the application's own handler then sees a profile in power-on state. `command` is `NmtCommand.ResetNode` or `NmtCommand.ResetCommunication`.
- Sequence a profile sees on Reset Node from Operational: `nmt Operational>Initializing`, `reset ResetNode`, `nmt Initializing>PreOperational`.
- `host.RequestNmtState(target)`: actor loop only; `target` is `PreOperational` or `Stopped` (`ArgumentOutOfRangeException` otherwise — a profile does not start its own node); a no-op when the node is already there or is `Initializing`; otherwise `ApplyNmtTransition(target)`, which notifies every application including the caller.

- [ ] **Step 1: Failing tests**

```csharp
The_Start_Up_Is_Reported()                    // Log contains "nmt Initializing>PreOperational" after settle
A_Start_Command_Is_Reported()                 // NMT Start from Wire → "nmt PreOperational>Operational"
A_Reset_Node_Is_Reported_In_Order()           // from Operational: exactly the three lines above, in that order, and 6000:01 already restored when "reset ResetNode" is logged
A_Reset_Reaches_The_Profile_Before_The_Application()  // ApplicationReset handler appends to the same Log → after "reset ResetNode"
A_Profile_Can_Take_Its_Node_Out_Of_Operational()      // OnEntriesWritten → host.RequestNmtState(PreOperational); heartbeat producer on → Wire sees state 0x7F; Log has "nmt Operational>PreOperational"
A_Profile_Cannot_Start_Its_Node()             // RequestNmtState(Operational) → ArgumentOutOfRangeException as background exception; state unchanged
A_Request_For_The_Current_State_Does_Nothing()// no second "nmt" line, no extra heartbeat
```

- [ ] **Step 2–4: fail, implement, pass (class + `CANopen` folder).**
- [ ] **Step 5: Mutations** — (a) move `OnReset` after `RaiseApplicationReset` → `…Before_The_Application` fails; (b) drop the notify in `PerformNmtReset` → `A_Reset_Node_Is_Reported_In_Order` fails; (c) drop the notify in the start-up job → `The_Start_Up_Is_Reported` fails; (d) allow `Operational` in `RequestNmtState` → `A_Profile_Cannot_Start_Its_Node` fails.
- [ ] **Step 6: Commit** — `refactor(canopen): report NMT state and reset to an attached application`

---

### Task 4: Errors of a profile, communication failure

**Files:**
- Modify: `CanOpenNode.Application.cs`, `CanOpenNode.ErrorRegister.cs`, `CanOpenNode.cs` (`EmitEmcy`, `RaiseHeartbeatTimeout`), `CanOpenNode.ControllerEmcy.cs`, `CanOpenNode.NodeGuarding.cs`
- Test: `CanOpenApplicationSeamTests.cs`

**Interfaces — Produces:** `host.RaiseError`, `host.ClearError`, `host.CommunicationFailureActive`; `private byte NodeErrorBits` (replaces the two reads of `CommunicationErrorBits` in `EmitEmcy` and `EmitPendingEmcy`); `EmitNodeEmcy(ushort errorCode, byte[]? manufacturerSpecific = null)`; `private void PublishCommunicationFailure()`.

**Rules this task decides:**

- A profile error is the node's, not the application's part of `1001h`: it is kept beside `_communicationErrors` in `Dictionary<(ApplicationSlot, ushort code), byte bits>`. `NodeErrorBits` = `CommunicationErrorBits` OR every active profile error's bits OR bit 0 when any is active. The application's part (`SendEmcyAsync`, a write to `1001h`) is untouched and still ORed in by `UpdateErrorRegister`.
- `RaiseError(code, bits, manufacturerSpecific)`: actor loop only; `code == 0` → `ArgumentOutOfRangeException`; `manufacturerSpecific` longer than `EmcyMessage.ManufacturerFieldLength` → `ArgumentException`. Already active for this application and code → nothing (once per error event, CiA 301 §7.2.7.1). Otherwise recorded and one EMCY ordered through `EmitNodeEmcy`, so it is held in Stopped and writes the register without a frame while `1014h` bit 31 is set — unlike `RaiseCommunicationError`, the error is recorded even then, because the register must show it.
- `ClearError(code)`: not active → nothing. Otherwise removed and one EMCY `0000h` ordered, carrying whatever the register still holds (§7.2.7.1: an error reset "may be transmitted together with the remaining errors").
- The existing rule for communication errors is unchanged in what it sends; only the register it sends with now includes profile bits.
- `EndCommunicationErrorsForReset` also clears the profile errors (no EMCY). A profile whose cause persists raises again from `OnReset`.
- Communication failure is exactly the condition of `ResolveErrorBehaviorTarget`: `_controllerObservedBusOff || _heartbeatEmcyTimeouts.Count > 0 || _lifeGuardingOccurred`. `PublishCommunicationFailure()` compares it with the last published value and, on a change, calls `OnCommunicationFailureChanged(active)` on every application whose requirements ask for it. It is called after every mutation of the three operands — today: `CanOpenNode.cs` `RaiseHeartbeatTimeout`; `CanOpenNode.ErrorRegister.cs` `NoteHeartbeatForMonitoring`, `ReconcileHeartbeatEmcyWatches`, `EndCommunicationErrorsForReset`; `CanOpenNode.ControllerEmcy.cs` (four assignments of `_controllerObservedBusOff`); `CanOpenNode.NodeGuarding.cs` (three assignments of `_lifeGuardingOccurred`). Re-grep the three names before editing; the list is as of `b3bd26aa`.
- Where a site also calls `ApplyCommunicationErrorBehavior()`, publish first: the profile sets its outputs to the fault value before the node leaves Operational (`OnCommunicationFailureChanged(true)` then `OnNmtStateChanged`).
- Before relying on it, confirm in `InitializeControllerErrorMonitor` that bus-off is observed whenever `EnableErrorBehavior` is set. If it is observed only with `EmitControllerErrorEmcy`, do not change that here — report it; the requirement check of Task 1 then has to name that option too, which is a controller decision.

- [ ] **Step 1: Failing tests** (EMCY frames read from `Wire` on `80h + node-id`; register read back with `ObjectDictionary.ReadUnsigned(0x1001, 0)` after the EMCY chain settled — see how `CanOpenControllerEmcyTests` waits for it)

```csharp
A_Profile_Error_Is_One_Emcy_With_Its_Bits()        // RaiseError(0x5000, 0x04, {1,2}) → frame 00 50 05 01 02 00 00 00; 1001h == 0x05
The_Same_Error_Twice_Is_One_Emcy()
Clearing_The_Last_Error_Sends_The_Reset()          // → frame 00 00 00 …; 1001h == 0
Clearing_One_Of_Two_Keeps_The_Others_Bits()        // RaiseError(0x5000, 0x04), RaiseError(0x3200, 0x20) → after ClearError(0x5000): frame 00 00 21 …; 1001h == 0x21
The_Applications_Own_Bits_Stay()                   // SendEmcyAsync(0x1000, 0x80) first; RaiseError(0x5000,0x04) → 0x85; ClearError → frame 00 00 80, 1001h == 0x80
An_Error_In_Stopped_Is_Held()                      // node Stopped; RaiseError → no frame; NMT Enter Pre-Operational → the frame
An_Error_With_Emcy_Switched_Off_Still_Shows_In_1001h()   // 1014h bit 31 set → no frame, 1001h == 0x05
A_Reset_Ends_Profile_Errors()                      // RaiseError, Reset Node → 1001h == 0, no 0000h frame, RaiseError(0x5000) afterwards sends again
A_Heartbeat_Timeout_Is_A_Communication_Failure()   // EnableErrorBehavior, consumer for node 0x22 at 100 ms, one heartbeat from Wire, clock.Advance(150 ms) → "failure True"; next heartbeat → "failure False"
The_Failure_Is_Reported_Before_The_Node_Leaves_Operational()  // 1029h:01 = 0, node Operational → Log order: "failure True", "nmt Operational>PreOperational"
An_Application_That_Did_Not_Ask_Hears_No_Failure() // Requirements.None → no "failure" line
A_Reset_Ends_The_Failure()                         // failure active, Reset Node → "failure False" before "reset ResetNode"
```

- [ ] **Step 2–4: fail, implement, pass (class + `CANopen` folder — the EMCY and error-register tests there are the regression net for this task).**
- [ ] **Step 5: Mutations** — (a) leave `nodeBits = CommunicationErrorBits` in `EmitEmcy` → first test fails on the register byte; (b) make `RaiseError` not record while `!_emcyValid` → `…Switched_Off…` fails; (c) skip clearing in `EndCommunicationErrorsForReset` → `A_Reset_Ends_Profile_Errors` fails; (d) publish after `ApplyCommunicationErrorBehavior` → `…Before_The_Node_Leaves_Operational` fails; (e) drop the publish in `NoteHeartbeatForMonitoring` → the `failure False` half fails.
- [ ] **Step 6: Commit** — `refactor(canopen): carry a profile's errors in 1001h and report communication failure to it`

---

### Task 5: Device type of a node with profiles

**Files:**
- Modify: `CanOpenNode.Application.cs`
- Test: `CanOpenApplicationSeamTests.cs`

**Interfaces — Consumes:** `CanOpenDeviceType`, `CanOpenLogicalDevices.{MaxCount, DeviceTypeIndex, Validate}` (public, merged with #544). **Produces:** `host.SetDeviceType`; `private void ComposeDeviceTypes()` called at the end of the attach phase, before the snapshot retake.

**Rules this task decides** (CiA 301 v4.2.0 §7.5.2.1):

- `SetDeviceType(logicalDevice, deviceType)`: only during `Attach` (`InvalidOperationException` afterwards); `logicalDevice` 0..7 (`ArgumentOutOfRangeException`, `ParamName "logicalDevice"`); `DeviceTypeIndex(logicalDevice)` must lie in a range the caller claims (`ArgumentException`); a logical device set twice — by anyone — fails the open (`ArgumentException`, `ParamName "options"`, names both applications).
- No application set one: nothing is touched; `1000h` keeps its placeholder.
- The set logical devices must be 0..n-1 without a gap, else the open fails (`ArgumentException`, `"options"`, names the missing number): a reader stops at the first missing object, so a gap would hide every device behind it.
- Exactly logical device 0: `1000h` = its word.
- More than one: `1000h` = profile number of device 0 in bits 0..15 and `FFFFh` in bits 16..31; for every device i, `DeviceTypeIndex(i)` sub-index 0 is declared UNSIGNED32, read-only (`const` access if `OdAccess` has it — check `OdEntry.cs`), value = its full word. Device 0's additional information then lives in `67FFh` only, as the clause has it.
- With a device description, the file wins: an object the description declared (`_describedObjects`) is not written or re-declared. Instead the profile number in what the file holds must equal the one set — `1000h` bits 0..15 against device 0, and each described `67FFh + i·800h` against device i — else the open fails (`ArgumentException`, `"options"`, names the object, the file's profile number and the application's).

- [ ] **Step 1: Failing tests**

```csharp
One_Profile_Sets_1000h()                       // SetDeviceType(0, 0x000F0191) → 1000h == 0x000F0191; 67FFh does not exist
Two_Profiles_Announce_Further_Devices()        // 0: 0x000F0191, 1: 0x00010198 → 1000h == 0xFFFF0191, 67FFh == 0x000F0191, 6FFFh == 0x00010198
The_Chain_Reads_Back_Over_The_Bus()            // same node; a 2nd node with a description declaring 67FFh and 6FFFh bound for the peer:
                                               // CanOpenLogicalDevices.ReadAsync → two devices with those words (the end value is whatever #544 defines for that file; assert the devices)
Without_A_Profile_1000h_Is_Left_Alone()        // application sets no type → 1000h equals that of a node opened without applications
A_Gap_In_The_Logical_Devices_Fails_The_Open()  // 0 and 2 → message contains "1"
The_Same_Logical_Device_Twice_Fails_The_Open()
A_Device_Type_Outside_The_Claim_Is_Refused()   // claims 6000..67FF, SetDeviceType(1, …) → ArgumentException out of Attach → open fails
Setting_The_Type_After_Attach_Is_Refused()     // from OnEntriesWritten → InvalidOperationException as background exception
The_Device_Type_Survives_Reset_Node()          // 67FFh still there with its value after Reset Node
A_Description_Keeps_Its_Own_1000h()            // file 1000h = 0x00030191, SetDeviceType(0, 0x000F0191) → 1000h stays 0x00030191
A_Description_Of_Another_Profile_Fails_The_Open()  // file 1000h = 0x00000192 → message contains "0192"/"402" and "401"
```

- [ ] **Step 2–4: fail, implement, pass (class + `CANopen` folder).**
- [ ] **Step 5: Mutations** — (a) write device 0's full word into `1000h` in the multi case → `Two_Profiles…` fails; (b) skip declaring `67FFh` for device 0 → same test and the bus test fail; (c) drop the gap check → its test fails; (d) overwrite a described `1000h` → `A_Description_Keeps…` fails; (e) compose after the snapshot retake → `The_Device_Type_Survives_Reset_Node` — if this mutation stays green (a declared read-only object is not rewritten by a reset), say so and replace the test's claim with one that fails: Reset Node after `1011h` load restores `1000h` to the composed word, not the placeholder.
- [ ] **Step 6: Commit** — `refactor(canopen): compose 1000h and the 67FFh chain from attached profiles`

---

### Task 6: Profile descriptor and catalogue (tool side)

**Files:**
- Create: `Application/ICanOpenProfile.cs`, `Application/CanOpenProfileCatalog.cs`
- Test: `tests/CanKit.Pro.Tests/TestCases/CANopen/CanOpenProfileCatalogTests.cs`

**Interfaces — Produces:**

```csharp
internal interface ICanOpenProfilePeer
{
    ICanOpenProfile Profile { get; }
    byte PeerNodeId { get; }
    CanOpenLogicalDevice LogicalDevice { get; }
}

internal interface ICanOpenProfile
{
    ushort ProfileNumber { get; }      // 401, 408 …; never 0
    string Name { get; }               // "CiA 401"
    ICanOpenProfilePeer CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice);
}

internal sealed class CanOpenProfileCatalog
{
    public CanOpenProfileCatalog(IEnumerable<ICanOpenProfile> profiles);
    public CanOpenProfileCatalog(params ICanOpenProfile[] profiles);
    public IReadOnlyCollection<ICanOpenProfile> Profiles { get; }
    public bool TryGetProfile(ushort profileNumber, [NotNullWhen(true)] out ICanOpenProfile? profile);
    /// The peer access for one logical device; null when its profile number is 0 or not in the catalogue.
    public ICanOpenProfilePeer? CreatePeer(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDevice logicalDevice);
    /// One element per device of the result, in its order; null where the profile is unknown.
    public IReadOnlyList<ICanOpenProfilePeer?> CreatePeers(ICanOpenNode node, byte peerNodeId, CanOpenLogicalDeviceReadResult result);
}
```

**Rules:** null list, null element, a descriptor with `ProfileNumber == 0`, or two descriptors with the same number → `ArgumentException` from the constructor (message names the number and both `Name`s). `node` null → `ArgumentNullException`; `peerNodeId` checked with `CanOpenCobId.ValidateNodeId`. No global registry, no assembly scan: the catalogue holds what it was given. An unknown profile is not an error. The catalogue performs no bus access; a peer made here reads through `ICanOpenNode.SdoUploadAsync` and so stays behind the peer-SDO gate (the catalogue must not offer a way around it — no method here takes or binds a device description). `[NotNullWhen]` needs the polyfill the project already uses for netstandard2.0, if any; otherwise drop the attribute and return the profile through a nullable out.

- [ ] **Step 1: Failing tests** — a `FakeProfile(ushort number)` whose `CreatePeer` returns a `FakePeer` recording its arguments.

```csharp
A_Known_Profile_Yields_Its_Peer()              // device type 0x000F0191, catalogue { 401 } → FakePeer with that node, peer id, logical device
An_Unknown_Profile_Yields_Null()               // device type profile 402
A_Device_Without_A_Profile_Yields_Null()       // device type 0x00000000, catalogue { 401 }
A_Read_Result_Maps_Device_By_Device()          // result with devices [401, 402, 401] (build it through ReadCoreAsync with a scripted upload, as CanOpenLogicalDevicesTests does) → [peer, null, peer], logical device indexes 0 and 2
Two_Descriptors_For_One_Profile_Are_Refused()
A_Descriptor_For_Profile_0_Is_Refused()
An_Invalid_Peer_Node_Id_Is_Refused()           // 0 and 128
```

- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Mutations** — (a) key the lookup by the full device-type word → `A_Known_Profile…` fails; (b) drop the duplicate check → its test fails; (c) skip unknown devices instead of yielding null in `CreatePeers` → `A_Read_Result_Maps…` fails on the list length.
- [ ] **Step 6: Commit** — `refactor(canopen): add the internal profile descriptor and catalogue`

---

### Task 7: Record where the seam stands

**Files:**
- Modify: `docs/architecture/arc42-CanKit.Pro.md` and `docs/architecture/arc42-CanKit.Pro-en.md` (ADR-14, **Status** line only)

- [ ] **Step 1:** Replace the status sentence „Nichts davon ist gebaut." (and its English counterpart) with a statement of fact, no count and no file list: the device type and logical devices are public (FR-CO-063); the seam, the profile descriptor and the catalogue exist as `internal` types in namespace `CanKit.Pro.CANopen.Application`; no profile package exists; the ADR stays „entschieden, nicht umgesetzt" until the seam is published. Leave the heading as it is.
- [ ] **Step 2:** Full gate — build `-p:CI=true`, `dotnet test CanKit.Pro.sln -c Release --no-build --framework net10.0`, `dotnet format --verify-no-changes`, pack into a fresh directory + `python eng/verify-packages.py <dir>`, `python eng/verify-requirements-traceability.py`. Confirm `git status` shows no `*.received.txt` and no change under `tests/CanKit.Pro.Tests/ApiApprovals/`.
- [ ] **Step 3: Commit** — `docs(arc42): record that the application seam exists internally (ADR-14)`

---

## Decisions this plan makes that the scope record left to the builder

For the reviewer of the pull request; each is internal and can change until the seam is published.

1. Claims are index ranges at or above `2000h`; everything in a claim is restorable and belongs to `1010h`/`1011h`, without a separate registration call.
2. Who claims an entry decides whether writing it transmits; the node's change-of-state automatism never applies to claimed entries.
3. `OnEntriesWritten` reports that entries changed, coalesced, including bus-originated writes; not the claimant's own writes from inside a callback, not a reset's restore.
4. A profile's errors are part of the node's share of `1001h`, keyed by application and error code; raising is idempotent, clearing always sends `0000h` with the remaining register; a reset ends them.
5. Communication failure means the three sources of `1029h` (bus-off, heartbeat timeout, life guarding) and needs `EnableErrorBehavior`.
6. A profile may request Pre-operational or Stopped, never Operational.
7. Device types are composed by the node from what the profiles set; logical devices must be gapless from 0; a device description's own `1000h`/`67FFh` is kept and only checked for the profile number.
8. Default PDOs get no host call yet: a profile writes the PDO records through the dictionary in `Attach`, and the start-up job applies them. Whether that deserves a helper is for the first profile to show.
9. No `feat` commit, although the scope record's sequence labels this step `feat(canopen)`: nothing public changes, so no release should be published for it.
