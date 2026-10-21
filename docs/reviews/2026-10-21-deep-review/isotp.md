# CanKit.Pro.IsoTp — deep review, 2026-10-21

Base: `main` @ 36c97ca (after PR #336). Previous review: `docs/reviews/2026-09-30-deep-review.md` (main @ 8cec2ee).
Standard citations (ISO 15765-2) below are from memory unless marked otherwise; the code's own section numbers are quoted as the code quotes them.

## 1. Scope read

Source (all read in full):

| File | Lines |
|---|---|
| `src/CanKit.Pro.IsoTp/IsoTpChannel.cs` | 1927 |
| `src/CanKit.Pro.IsoTp/IsoTpFrameCodec.cs` | 677 |
| `src/CanKit.Pro.IsoTp/IsoTpFunctionalClient.cs` | 418 |
| `src/CanKit.Pro.IsoTp/IsoTpFunctionalListener.cs` (incl. `FunctionalWindow`) | 179 |
| `src/CanKit.Pro.IsoTp/IIsoTpChannel.cs` | 207 |
| `src/CanKit.Pro.IsoTp/IsoTp.cs` | 157 |
| `src/CanKit.Pro.IsoTp/IsoTpChannelOptions.cs` | 140 |
| `src/CanKit.Pro.IsoTp/IsoTpEndpoint.cs` | 137 |
| `src/CanKit.Pro.IsoTp/IsoTpException.cs` | 98 |
| `Pci.cs`, `PciType.cs`, `FlowStatus.cs`, `IsoTpAddressingMode.cs`, `IsoTpReceivedPdu.cs`, `IsoTpReceptionInProgress.cs`, `IsoTpTransmitStamps.cs`, `IsoTpDatagramReceivedEventArgs.cs`, `IsoTpFunctionalOptions.cs`, `IsoTpFunctionalResponse.cs`, `IsoTpFunctionalCollection.cs` | 99 + 20 + 23 + 33 + 65 + 45 + 54 + 24 + 53 + 42 + 22 |
| `src/CanKit.Pro.IsoTp/README.md` | 221 |

Tests (all read in full):

| File | Lines |
|---|---|
| `tests/CanKit.Pro.Tests/TestCases/IsoTp/IsoTpChannelIntegrationTests.cs` | 3534 |
| `IsoTpFrameCodecTests.cs` | 868 |
| `IsoTpFunctionalClientTests.cs` | 760 |
| `IsoTpFrameCodecPropertyTests.cs` | 222 |
| `IsoTpCanFdTests.cs` | 214 |
| `IsoTpStminTimingTests.cs` | 198 |
| `IsoTpBusOffTests.cs` | 96 |

Read for the claims that depend on them: `src/CanKit.Pro.Actor/ProtocolActor.cs` (Post/Schedule/Dispose/FinalDrain, lines 290-620, 690-720, 855-870, 950-977), `src/CanKit.Pro.Reliability/DeadlineScheduler.cs` (full), `src/CanKit.Pro.RawCan/CanBusService.cs` (Dispose, `SendConfirmedAsync`, both confirm paths, `TryMatchEcho`, `OnFaultOccurred`), `Subscription.cs`, `ISubscription.cs`, `TxConfirmation.cs`, `tests/CanKit.Pro.Tests/Infrastructure/StarvedReaderBusService.cs`.

History since 8cec2ee touching the package (from `git log 8cec2ee..HEAD -- src/CanKit.Pro.IsoTp tests/.../IsoTp`): PR #261 (I6/I7/I8, canceled-token read, loss-under-pump-lock), #264 (README STmin paragraph), #268 (reader generic-catch filter), #269 (test race #270), #273 (FD DLC rounding, Q7/#250), #275 (#262, inbox-closed flag), and the shared tick conversion (`551261a`).

## 2. Status of previous findings

| ID | Status today | Evidence |
|---|---|---|
| **Q7** (FD without padding → invalid DLC, echo never matches) | **Fixed in the codec**, PR #273 (`7d66d46`, `84c647f`), issue #250 closed. Channel-level gap remains. | `IsoTpFrameCodec.cs:128-131` `FrameLengthOnTheWire`: `padding \|\| (isCanFd && payloadLen > 8)` → `NextValidFrameLength`. Pinned by `BuildSingleFrame_CanFd_Without_Padding_Lands_On_A_Valid_Length`, `BuildConsecutiveFrame_CanFd_Without_Padding_Lands_On_A_Valid_Length`, `CanFd_Frames_Of_Eight_Bytes_Or_Fewer_Stay_Exact_Without_Padding` (`IsoTpFrameCodecTests.cs:60-109`). Options doc corrected (`IsoTpChannelOptions.cs:28-36`, `IsoTpFunctionalOptions.cs:33-39`). **Still no channel-level test with `UseCanFd = true, UsePadding = false`** — `grep UsePadding = false tests/…/IsoTp` hits nothing; `IsoTpCanFdTests.FastOptions` hard-codes `UsePadding = true`. The Vector/PCAN echo-match half of the finding is unverifiable without hardware (README §"Not validated" says so). |
| **I6** (ReceiveAsync hangs when the shared service is disposed) | **Fixed**, PR #261 (`89ce021`, `4c3b569`, `2eeef26`, `e43b4bb`, `4ae8e1f`), hardened by #268 (`c660fd2`: excluded exception types) | `IsoTpChannel.cs:685-774` (`RunReaderAsync` → `PublishSubscriptionLoss` under `_pumpGate` → `EndInboxAfterSubscriptionLoss` sets `_inboxLost`/signal). Tests: `A_Service_Disposed_Under_The_Channel_Faults_Every_Waiting_Receive` (:858), `A_PDU_Received_Before_The_Service_Goes_Is_Still_Delivered_First` (:890), `A_Discard_After_The_Service_Goes_Keeps_The_Pdus_From_After_Its_Stamp` (:914), `ReceiveAll_Yields_The_Buffered_Pdu_And_Then_Throws_The_Loss` (:947), `The_Loss_Does_Not_Overtake_A_Frame_A_Pumping_Caller_Is_About_To_Post` (:1149), `A_Failing_Subscription_Ends_The_Inbox_With_Its_Failure` (:1178), `An_Excluded_Reader_Failure_Still_Wakes_Waiting_And_Later_Receives` (:1214), `A_Reception_Under_Way_Is_Withdrawn_When_The_Service_Goes` (:1258), `The_Loss_Is_Still_Reported_When_The_Injected_Actor_Is_Already_Gone` (:1279). One residual path, see I-2. |
| **I7** (`BeginSendOnLoop` disposed guard) | **Fixed**, PR #261 | `IsoTpChannel.cs:903-910`. Test `A_Dispose_Between_The_Check_And_The_Post_Fails_The_Send_Before_It_Reaches_The_Bus` (:1301) with the `DisposeOnFirstPostActor` double (:1321). Mutation M5 below pins it. |
| **I8** (negative `NBs`/`NCr` not rejected at Open) | **Fixed**, PR #261 | `IsoTpChannel.cs:193-198` `RequirePositive` for `NAs`, `NBs`, `NCr` (zero rejected too). Test `A_Protocol_Timer_That_Cannot_Arm_Is_Rejected_At_Open` (:1367, six cases). Not validated: `MaxReceivePduLength <= 0` (every FF → `FC(OVFLW)`), `WftMax < 0` (behaves as 0); both harmless misuse. |
| **#262** (Dispose during `DiscardPendingPdus` drops the retained PDUs) | **Fixed**, PR #275 (`595ed90`, `51046d4`, `ec0ecf7`, `3252406`) | The inbox writer is never completed; `_inboxClosed` is set under `_inboxGate` (`IsoTpChannel.cs:87-98`, `:603-604`), producers check it under the same gate (`:1689-1694`, `:1752-1756`), the discard's write-back ignores it (`:497-498`). Tests `A_Dispose_During_A_Discard_Does_Not_Drop_The_Pdu_The_Discard_Retains` (:1013), `A_Pending_Receive_Ends_On_Dispose_Although_The_Actor_Is_Busy` (:1047), `A_Producer_That_Writes_After_The_Disposal_Does_Not_Queue_Anything` (:1077), `A_Receive_During_A_Discard_After_The_Loss_Still_Gets_The_Retained_Pdu_First` (:976). |
| **#270** (test race on the failure report) | **Fixed**, commit `70db3e8` (landed through PR #269's main merge), issue closed | `IsoTpChannelIntegrationTests.cs:1197-1201`: the report is awaited via `firstReport` instead of read at once. |
| **#246** (README STmin paragraph claims a ±1 ms wall-clock measurement) | **Fixed**, PR #264 (`0983869`), issue closed | `README.md:102-115` now says the figure "is a target, not a measured result" and names the clock-driven test. **The SRS row still carries the old claim**: `docs/requirements/SRS-CanKit.Pro.md:383` NFR-003 verification = "gemessene Inter-Frame-Zeit von Consecutive Frames vs. konfiguriertes STmin" — no such measurement exists (out of this package's files, noted for the docs owner). |
| §3 ISO-TP: **N_Ar not modelled** | **Open**, no issue found (searched `gh issue list --search "isotp OR iso-tp"`) | `IsoTpChannel.cs:1716-1741` `SendUnsequencedFrame`: a failed/timed-out FC confirmation only raises `BackgroundExceptionOccurred`; `_rx` and N_Cr stay armed. Minor (hostile bus / driver fault only). |
| §3 ISO-TP: **N_Cr starts at FC handoff, not at FC confirmation** | **Open** | `IsoTpChannel.cs:1466` `SendUnsequencedFrame(fc)` then `:1476` `ArmNCr()`; the block FC at `:1540` likewise, N_Cr rearmed at `:1546`. Effective N_Cr is shorter by the FC's own N_Ar. Minor. |
| §3 ISO-TP: **reserved FlowStatus silently dropped instead of N_INVALID_FS** | **Open** | `IsoTpFrameCodec.cs:603-604` returns false → `IsoTpChannel.cs:1339-1340` drops. A sender waiting in `WaitFc*` then fails on N_Bs rather than at once. Buggy/hostile peer only. Minor. |
| §3 ISO-TP: **escape FF with FF_DL > int.MaxValue ignored instead of FC(OVFLW)** | **Open** | `IsoTpFrameCodec.cs:569-570` (`longLen > int.MaxValue → false`); `TryParsePci_FirstFrame_CanFd_Escape_Accepts_Only_Lengths_Above_The_Short_Form(0x8000_0000u, false)` cements it. Hostile peer only. Minor. |
| §3 ISO-TP: **`EncodeStMin` rounds down in the sub-ms band, doc says "nearest"** | **Open** | `IsoTpFrameCodec.cs:655-660` (`micro / 100`): 190 µs → `0xF1` = 100 µs; 1 µs..99 µs → `0xF1` (rounds *up*); ms band rounds to nearest (`:649`): 1 499 µs → 1 ms. For a *minimum* separation the only safe direction is up. Doc at `:633-634` still says "nearest". `StMin_Encode_Is_Monotonic_And_Lands_On_A_Defined_Value` pins monotonicity only. Minor + Docs. |
| §3 ISO-TP: **no TX_DL, no BRS** | **Open** (design, documented nowhere as deliberate) | `BuildFirstFrame` always fills 64 bytes on FD (`IsoTpFrameCodec.cs:342`); `CanFrame.Fd(...)` is built without BRS (`IsoTpChannel.cs:1030-1032`, `:1718-1720`, `IsoTpFunctionalClient.cs:291-293`). Minor; a peer with RX_DL < 64 cannot be addressed. |
| §3 ISO-TP: **Mixed / NormalFixed addressing untested at channel level** | **Open** | `grep "Mixed(\|NormalFixed("` in the channel test files: 0 hits (only `IsoTpFrameCodecTests.cs:681` and the property tests). Extended addressing with distinct SA/TA is tested with **Single Frames only** (`ExtendedAddressing_With_Distinct_Source_And_Target_Round_Trips` :1846, `Reciprocal_Extended_Channels_On_One_Identifier_Still_Exchange` :2570), so the FC's address-extension byte on the multi-frame path is unpinned — see mutation M4. |
| §3 ISO-TP: **mixed ISO section numbers** | **Open** | `IsoTpChannelOptions.cs:10, :63, :74, :80` (§6.5), `:89` (§6.3), `:102` (§9.6.5.2) vs `IsoTpChannel.cs:837` (§5.2.4.4), `:1381` (§6.5.2 "abort"), `:1397` (§9.6.3.1), `:1424` (§6.5.5), `:1483` (§6.5.2 "drop" — the same section cited for the opposite behaviour), `:1498` (§9.8), `:1555` (§6.5.5.2). Docs. |
| §3 UDS-side item (channel disposed during a request → `InvalidOperationException`, not `IsoTpException`/ODE) | **Open, now deliberate on the ISO-TP side** | `IsoTpChannel.cs:335` throws `InvalidOperationException("Channel is disposed…")`; `Dispose_Unblocks_Pending_ReceiveAsync` (:822-851) pins `BeOfType<InvalidOperationException>()` on purpose. `SendAsync` after dispose throws `ObjectDisposedException` (`:261`, `:908`). The UDS client classifies both as "refused before transmission" (`UdsClientImpl.cs:547-548`), so the asymmetry is absorbed there; `IIsoTpChannel.ReceiveAsync` (:75-83) still documents neither outcome. Docs. |
| §4.1 "`IsoTpStminTimingTests.cs:113-115` cements STmin before the first CF as a standard property" | **Open as a question** | `IsoTpChannel.cs:1596-1597`: CTS → `ScheduleNextCf` → STmin wait before the first CF of every block. Linux `can-isotp` does the same (from memory); the standard's text was not checked. See §8 Q3. |

Previous mutation results (isotp-M1..M5) all still hold by inspection: M1 (`:1588-1593` BS/STmin first-FC-only, test `Flow_Parameters_Come_From_The_First_Flow_Control_Only`), M2 (`:1503-1507`, `MultiFrame_Receive_Ignores_A_Shortened_ConsecutiveFrame_That_Is_Not_The_Last`), M3 (`:1002` + `:1251`, `A_Stale_StMin_Timer_Does_Not_Send_A_ConsecutiveFrame_Of_The_Next_Transfer`), M4 (`:1602`, `MultiFrame_Send_Aborts_When_Peer_Exceeds_WftMax`), M5 (now red — see mutation M3 below).

## 3. New findings

### I-1 · Disposing `_sendGate` after its 2-second wait strands a second `SendAsync` queued on the gate — Important

**Where.** `IsoTpChannel.cs:669-676`:

```csharp
try { _sendGate.Wait(TimeSpan.FromSeconds(2)); } catch (ObjectDisposedException) { }
try { _sendGate.Dispose(); } catch (ObjectDisposedException) { }
```

and `:266` `await _sendGate.WaitAsync(cancellationToken)`.

**Mechanism.** `SemaphoreSlim.Dispose` does not complete pending `WaitAsync` tasks — it drops the async waiter list (`m_asyncHead = m_asyncTail = null`, from memory of the BCL source; the documented contract is only "Dispose while in use is undefined"). A caller parked in `WaitAsync` with `CancellationToken.None` — which `IIsoTpChannel.SendAsync` allows and whose doc says "callers may invoke SendAsync concurrently from arbitrary threads" (`IIsoTpChannel.cs:11-13`) — is therefore never released once the gate is disposed while another send holds it. The holder's later `Release()` throws ODE and is swallowed (`:320-321`), so nothing ever wakes the queued caller: its `SendAsync` task never completes, with no ODE and no cancellation.

The 2-second timeout is reached whenever `FailInFlightSend` (`:609-633`) has not run by then, which has three reachable causes:

1. **Reentrant Dispose from an actor-side handler.** `BackgroundExceptionOccurred` is raised *on the actor* for every `AbortRx` (`:1702`) and every deadline/actor failure; the README explicitly supports calling `DiscardPendingPdus` from such a handler (`README.md:99-100`), so calling `Dispose` there is the next thing a user does. Then `_actor.Dispose()` returns at once (`ProtocolActor.cs:494-503`, `IsOnCurrentActor`), `FailInFlightSend` is still queued behind the running handler, the gate is held by the in-flight send, `_sendGate.Wait(2 s)` **blocks the actor loop for 2 s** and then disposes the gate. After the handler returns, `FinalDrain` runs `FailInFlightSend`, the in-flight send fails with ODE and its `Release` throws into the catch; the queued sender hangs forever.
2. **Owned actor whose join timed out** (`ProtocolActor.cs:506-520`, 5 s): same sequence, `FinalDrain` runs later.
3. **Injected actor that is busy** (test seam only; production always passes `null`, `IsoTpChannel.cs:168-169`).

**Reachability.** API misuse-adjacent, not peer-triggerable: needs two concurrent `SendAsync` calls on one channel plus a `Dispose` while the actor cannot run the cleanup within 2 s. The UDS client serialises requests behind its own lock, so it is not affected; a direct `IIsoTpChannel` user with a sender pool is.

**Confidence.** Medium-high. The channel-side sequence is read from the code; the `SemaphoreSlim.Dispose` behaviour is from memory of the BCL and is the one thing worth a five-line repro before fixing.

**Test.** None. `Dispose_During_InFlight_Send_Does_Not_Race_SendGate_Release` (:1889) covers one in-flight send with an owned actor whose `FinalDrain` runs `FailInFlightSend` *before* the gate wait, so the timeout branch is never entered. Proposed test: injected `ProtocolActor` held busy (as in `A_Pending_Receive_Ends_On_Dispose_Although_The_Actor_Is_Busy` :1047), `send1 = SendAsync(...)`, `send2 = SendAsync(...)` (both with `CancellationToken.None`), `Task.Run(channel.Dispose)`, release the actor, then assert both `send1` and `send2` fault with `ObjectDisposedException` within `ShortTimeout` — today `send2.WaitAsync(ShortTimeout)` throws `TimeoutException`.

**Fix.** Stop disposing the gate (a `SemaphoreSlim` that never had `AvailableWaitHandle` touched owns no OS handle; `ThrowIfDisposed` after the acquire at `:269` already refuses post-dispose callers), and drop the 2-second wait with it — which also removes the actor-loop stall in cause 1. If a disposal must be visible to waiters, link `WaitAsync` to a channel-lifetime `CancellationTokenSource` cancelled at the top of `Dispose` and translate that cancellation into ODE.

### I-2 · A foreign subscription that ends with `OperationCanceledException` is taken for the channel's own cancellation, and the loss is not published — Minor

**Where.** `IsoTpChannel.cs:698-701`:

```csharp
catch (OperationCanceledException) { return; } // expected on Dispose
```

**Mechanism.** The catch is unfiltered. `CanBusService`'s subscription only throws OCE for the channel's own `_readerCts` token (`Subscription.cs:193-194` → `ChannelReader.WaitToReadAsync(ct)`), so with the shipped service this is fine. An `ICanBusService` from outside this repository — `IsoTp.Open(ICanBusService, …)` is public and `Unstamped_Events_From_A_Foreign_Bus_Service_Fall_Back_To_Now` (:126) shows the shape — that signals its own shutdown by cancelling the wait leaves the reader without publishing `_inboxLost`: exactly the I6 hang, re-entered through a different exception type. `PumpSubscription`'s `IngestFrame` cannot throw OCE, so the filter is cheap and exact.

**Reachability.** Foreign `ICanBusService` only. **Confidence.** High on mechanism, low on anyone hitting it.

**Test.** None; `A_Failing_Subscription_Ends_The_Inbox_With_Its_Failure` (:1178) uses `InvalidOperationException`. Adding `ReaderFault = new OperationCanceledException()` to `StarvedReaderBusService` reproduces it (the waiting receive never wakes).

**Fix.** `catch (OperationCanceledException) when (_readerCts.IsCancellationRequested)`; any other OCE falls through to the generic branch and is published as the loss.

### I-3 · `TryReceiveWithArrival` can report "empty" while a discard has the retained PDUs out of the inbox — Minor

**Where.** `IsoTpChannel.cs:379-389` reads `_pduInbox.Reader.TryRead` without consulting `_discarding` (`:79`, `:482-503`); `TakeNextAsync` (`:540-545`) does.

**Mechanism.** `ClearReceptionsBefore` takes every item out and writes the kept ones back on the actor. A concurrent `TryReceiveWithArrival` from another thread in that gap returns `false` although a PDU that arrived after the discard's stamp is about to be put back. The documented meaning of `false` ("the inbox is empty or the channel is disposed", `IIsoTpChannel.cs:99-101`) is then wrong for that instant.

**Reachability.** Only with `DiscardPendingPdus(long)` and `TryReceiveWithArrival` running concurrently on two threads. The UDS client calls them sequentially (`UdsClientImpl.cs` `DiscardStalePdus`), so it is not affected. **Confidence.** High on mechanism.

**Test.** None (`A_Receive_During_A_Discard_After_The_Loss_Still_Gets_The_Retained_Pdu_First` :976 covers the async path via `DiscardGapObserver`; the same hook can drive `TryReceiveWithArrival`).

**Fix.** Either spin on `_discarding` (as `TakeNextAsync` does) or document that `TryReceiveWithArrival` is not meant to overlap a discard.

### I-4 · A caller-side pump after the injected actor was disposed leaves a First Frame published for ever — Minor

**Where.** `IsoTpChannel.cs:855-866`: `PublishReception(announce)` precedes `_actor.Post(...)`; `PumpSubscription` (`:800-807`) catches the post's `ObjectDisposedException` and returns, leaving the record in `_receptionsInProgress`. The disposed guard at `:795` only looks at the *channel's* `_disposed`.

**Mechanism.** With the owned actor this is unreachable (the actor dies with the channel, and `Dispose` withdraws everything at `:655-659`). With an injected actor its owner disposed while the channel lives — the #205 scenario the code otherwise handles — the next `GetReceptionsInProgress()`/`SettleAsync()`/`DiscardPendingPdus()` that pumps a First Frame announces it and nothing ever withdraws it until `channel.Dispose()`. A UDS P2 check sitting on that record would wait N_Cr-bounded re-checks for a transfer nobody will complete.

**Reachability.** Misuse of the test seam (production passes `actor: null`). **Confidence.** High. **Test.** `A_First_Frame_Arriving_After_Dispose_Is_Not_Announced` (:3467) disposes both the actor *and* the channel, so it does not reach this. **Fix.** In `IngestFrame`, post first and publish only if the post succeeded, or withdraw in the `catch (ObjectDisposedException)` at `:804`.

### I-5 · `IsoTpFunctionalClient.CollectResponsesAsync` has no window range check, unlike `SendAndCollectAsync` — Minor (API consistency)

**Where.** `IsoTpFunctionalClient.cs:238-245` vs `:163-165`; `FunctionalWindow` `:146-149` (`_linked.CancelAfter(window)`).

**Mechanism.** `SendAndCollectAsync` refuses `window < 0 || window > MaxWindow` with an `ArgumentOutOfRangeException(nameof(window))` *before* subscribing or sending. `CollectResponsesAsync` subscribes, computes `TickMath.DueAt(...)` and only then hits `CancellationTokenSource.CancelAfter`, which throws `ArgumentOutOfRangeException` for a delay below −1 ms or above `uint.MaxValue − 1` ms with the BCL's parameter name, not `window`; the listener (`IsoTpFunctionalListener.cs:73-87`) treats `window <= 0` as "read the buffer" instead. Three behaviours for the same argument on three entry points.

**Reachability.** Caller-side argument error only. **Confidence.** High. **Test.** `Functional_Send_Refuses_A_Window_Beyond_A_Timers_Reach_Before_Transmitting` (:656) covers `SendAndCollectAsync` only. **Fix.** Hoist the check into `CollectFromSubscriptionAsync` (or `CollectResponsesAsync`) with `nameof(window)`.

### I-6 · Dispose from an actor-side `BackgroundExceptionOccurred` handler blocks the actor loop for up to 2 s while a send is in flight — Minor (same root as I-1)

Covered under I-1 cause 1; listed separately because it stands even without a second sender: `_readerTask.Wait(2 s)` (`:646`) returns at once (the reader was cancelled), but `_sendGate.Wait(2 s)` (`:673`) cannot succeed because `FailInFlightSend` is queued behind the very callback that is calling `Dispose`. During those 2 s no frame is handled and no timer fires on that actor. Fix as in I-1.

### Confirmed-sound areas (no finding)

Read with the brief's list in mind and found consistent:

- **Cancellation while FC pending / CF chain in flight / STmin armed**: `CancelInFlightSend` (`:1295-1321`) completes the TCS first, posts the actor-side teardown (deadline completed, STmin timer disposed, `_tx = null`); `BeginSendOnLoop` re-checks after publishing `_tx` (`:927-932`); `SendNextConsecutiveFrame` checks identity (`:1002`); a late `OnSendConfirmed` is ignored by identity (`:1141-1142`) and still drains `_busTxInFlight` in `finally` (`:1073-1076`); the gate is held until idle, bounded by N_As on both confirm paths (`:305-311`, `CanBusService.cs:391-429`).
- **`TxConfirmFailureReason.Timeout` mid-chain**: `FailTx(IsoTpTimeoutException(NAs))` (`:1170-1172`), identity-guarded; the peer then runs into its own N_Cr; the next `SendAsync` can start at once (the confirm *did* complete), and a conforming peer treats the new FF as a supersede (`:1424-1431`).
- **WFT counting**: incremented per Wait, failed on `> WftMax` (`:1601-1606`), reset on CTS (`:1595`), deferred Waits during FF/last-CF confirm kept as a list and replayed in order (`:1561-1574`, `:1624-1651`); N_Bs re-armed on every Wait (`:1609`). The two untested halves are mutations M1/M2.
- **Deferred FC application**: a CTS in the batch moves state to `SendingCf` and every later FC in the same batch is dropped rather than re-deferred (`:1576-1577`) — correct for a conforming peer; `DeferredFcs.Clear()` on a mid-block confirm (`:1236`) keeps a stray FC from being applied at the next block boundary.
- **Inbox completion paths**: the loss (`_inboxLost`) and the end (`_inboxClosed`) live beside the inbox; `TakeNextAsync` (`:527-575`) reads what is buffered first, waits out a discard gap, double-reads before believing the end, and distinguishes the two ends. A caller token cancelled before the read does not consume a buffered item (`:534`).
- **Posts after Dispose**: every `_actor.Post`/`PostAsync`/`Schedule` outside the actor is wrapped (`:283-289`, `:404-411`, `:451-458`, `:631-644`, `:741-757`, `:800-807`, `:1065-1083`, `:1114-1134`, `:1304-1320`); `HandleReceivedFrame` (`:1332-1337`) and `BeginSendOnLoop` (`:906-910`) guard the channel flag; producers check `_inboxClosed` under the gate; `EmitPdu` does not raise `DatagramReceived` after the flag (`:1759-1760`). The one leak found is I-4.
- **Padding / FD DLC after #273**: `FrameLengthOnTheWire` is used by SF, CF and FC builders; FF always fills the frame; `TryBeginRx` holds every CF to the FF's CAN_DL and the last to `<= RX_DL` with enough bytes (`:1503-1507`), so an unpadded last CF on FD (length ≤ 8, exact) still conforms.
- **Functional fan-in**: subscribe-then-drain-then-send (`IsoTpFunctionalClient.cs:183-186`), handoff cutoff on arrival stamps (`:189-191`), dispose-then-drain on window expiry (`:354-360`), listener carry-over by stamp (`IsoTpFunctionalListener.cs:112-120`), clocked window disposed without the actor (`:156-178`). The service-disposed case ends a collection with what it has and throws on the next (`:96-98`, `:68-70`).

## 4. Test quality and gaps

**Strong.** The seams (`VirtualClock` + injected actor, `StarvedReaderBusService`, `DiscardGapObserver`, `DisposeOnFirstPostActor`, the delaying/holding confirm services) make most of the state machine assertable without the wall clock; the #171 rewrites replaced the sleeps with actor round-trips and on-wire signals and say so in place.

**Gaps (ordered by value):**

1. **No channel-level `UseCanFd && !UsePadding` round trip** (Q7 residual): a Virtual-bus FD exchange with an SF of 9 user bytes and a multi-frame PDU whose last CF is < 8 bytes would pin both the codec rounding and `TryBeginRx`'s last-CF rule in one place.
2. **No multi-frame transfer under Extended addressing with distinct SA/TA** — the FC's address-extension byte (`IsoTpFrameCodec.cs:433-434`) is unpinned (M4). Mixed and NormalFixed: zero channel-level tests.
3. **Wait → CTS resume is untested**: no test sends `FC(WAIT)` then `FC(CTS)` and watches the transfer complete with the WFT counter reset (M1) or N_Bs re-armed after a single Wait (M2).
4. **I-1 scenario**: two concurrent `SendAsync` + Dispose with the actor unable to run `FailInFlightSend` within 2 s.
5. **Reserved FlowStatus / FS mid-transfer**: nothing exercises a sender meeting `FS = 3..15`.
6. **`FC(OVFLW)` deferred during FF confirm** (`ApplyDeferredFlowControls` with an Overflow entry) is untested; the Wait-deferred path is (`:2110`).
7. **`DatagramReceived` after `Dispose`**: `A_Frame_Handled_After_Dispose_Raises_No_DatagramReceived` (:3491) is a 500 ms negative window (false pass possible, acknowledged in the test).

**Tests that cement a behaviour the code picks, not the standard**: `TryParsePci_FirstFrame_CanFd_Escape_Accepts_Only_Lengths_Above_The_Short_Form(0x8000_0000u, false)` (:784) cements "ignore" for FF_DL > int.MaxValue where OVFLW is the arguable answer; `Sender_Advances_One_Consecutive_Frame_Per_Stmin_On_A_Clock_The_Test_Drives` (`IsoTpStminTimingTests.cs:113-116`) cements the STmin wait before the first CF of a block (§8 Q3).

**Timing-gated tests (per the #92 rule — "name the quantity the host can perturb"):**

| Test | Site | Perturbable quantity / margin | Risk |
|---|---|---|---|
| `The_Loss_Does_Not_Overtake_A_Frame_A_Pumping_Caller_Is_About_To_Post` | `IsoTpChannelIntegrationTests.cs:1163-1168`: `Task.Run(pump)`, `Task.Delay(100)`, `WakeReader()`, `Task.Delay(200)` | **Thread-pool start latency of the pump vs 100 ms.** If the pump has not reached `TryRead` within 100 ms, the subscription ends first, the loss is posted first, the pump then posts the frame behind it and the receive throws the loss → **false failure**. The test's own comment describes macOS CI granting a starved pool a thread "about once a second". The 200 ms half is a negative window (false pass only). | High — the one genuinely #92-class test in this area |
| `MultiFrame_Receive_Faults_When_Superseded_By_FirstFrame_Then_Overflow` | `:1609-1612`, 50 × `Task.Delay(20)` then `overflowFc.Should().Be(1)` | Hub delivery + actor scheduling of the OVFLW reply vs 1 s | Low |
| `Rx_FirstFrame_Above_Classic_Max_Sends_Overflow_And_Does_Not_Allocate` | `:2215-2217`, same 1 s poll | same | Low |
| `Cancelled_Send_Holds_Gate_Until_InFlight_Bus_Tx_Completes` | `:2004-2007`, 50 × 20 ms then `framesToPeer.Should().Be(2)` | hub delivery vs 1 s; the 200 ms `WaitAsync` at `:1969` asserts non-completion (safe direction) | Low |
| `Functional_Client_SendAsync_Puts_Frame_On_Bus` | `IsoTpFunctionalClientTests.cs:537-540`, 50 × 10 ms then `Be(1)` | hub delivery vs 500 ms | Low–medium |
| `ArrivalStamp_Is_Taken_On_Arrival_Not_On_Collection` | `:102-116`, `Task.Delay(300)` then `age > 250 ms` | lower bound on elapsed time; a slow host only widens it | None |
| `IsoTpBusOffTests` | `:81-94`, `sw.Elapsed < 5 s` with 10 s timers | 5 s margin | None |
| Negative windows (false pass only, all self-declared): `:1001`, `:1034` (`Thread.Sleep(300)` on the actor), `:1828`, `:1993`, `:2429` (`Task.Delay(100)`), `:3518` (500 ms), `Functional…Before_Transmitting` `:667` (50 ms) | — | cannot fail falsely | — |

## 5. Mutations proposed

| # | Edit (one line) | Expectation |
|---|---|---|
| **M1** | `IsoTpChannel.cs:1595` delete `tx.WaitFramesReceived = 0;` | **expected green = gap** (WFT counter reset on CTS is untested; no test sends Wait then CTS) |
| **M2** | `IsoTpChannel.cs:1609` delete `ArmNBs();` (the `FlowStatus.Wait` case) | **expected green = gap** (a single non-deferred Wait then silence would hang the send without N_Bs; `MultiFrame_Send_Aborts_When_Peer_Exceeds_WftMax` sends enough Waits to abort, and the deferred-Wait test re-arms via the tail of `ApplyDeferredFlowControlsOrArmNBs`) |
| **M3** | `IsoTpFrameCodec.cs:129-131` replace the body of `FrameLengthOnTheWire` with `padding ? NextValidFrameLength(payloadLen, isCanFd) : payloadLen` | **red**: `BuildSingleFrame_CanFd_Without_Padding_Lands_On_A_Valid_Length(7)` and `BuildConsecutiveFrame_CanFd_Without_Padding_Lands_On_A_Valid_Length(11)` (pins #273; was green = gap in the previous review) |
| **M4** | `IsoTpFrameCodec.cs:434` `destination[0] = endpoint.AddressExtension;` → `destination[0] = endpoint.RxAddressExtension;` (in `BuildFlowControl` only) | **expected green = gap** (no multi-frame Extended-addressing test with distinct SA/TA; codec FC tests and the FC property test never assert byte 0; SF/CF builders are untouched) |
| **M5** | `IsoTpChannel.cs:906-910` delete the `_disposed` guard in `BeginSendOnLoop` | **red**: `A_Dispose_Between_The_Check_And_The_Post_Fails_The_Send_Before_It_Reaches_The_Bus` (pins I7) |
| **M6** | `IsoTpChannel.cs:746-749` remove the `lock (_pumpGate)` around the loss post (keep the `Post`) | **red**: `The_Loss_Does_Not_Overtake_A_Frame_A_Pumping_Caller_Is_About_To_Post` — on a quiet host; this is the test whose 100 ms margin §4 flags, so a green here is as likely to be the host as the mutation |

## 6. Doc drift (README / XML docs vs behaviour)

1. `IIsoTpChannel.cs:99-101` (`TryReceiveWithArrival`): "returns false … when the inbox is empty or the channel is disposed" — since #275 a PDU buffered before `Dispose` is still returned afterwards (`IsoTpChannel.cs:381-385` has no disposed check; `A_Dispose_During_A_Discard_Does_Not_Drop_The_Pdu_The_Discard_Retains` relies on the async twin of this). The disposed clause is wrong.
2. `IIsoTpChannel.cs:75-83` (`ReceiveAsync`) and `:86-96`: neither the disposed outcome (`InvalidOperationException`, `IsoTpChannel.cs:335`) nor the service-loss outcome (`ObjectDisposedException(nameof(ICanBusService))`, `:695-696`) is documented; `UdsException.cs:12-13` still says transport failures "are already typed as IsoTpException subclasses". README `:56-60` describes the inbox but not these ends either.
3. `IIsoTpChannel.cs:41-46` (`SendAsync`): "faults with an IsoTpException on …" — it also faults with `ObjectDisposedException` (`:908`, `:613`), `OperationCanceledException`, and the raw bus-layer exception unrewritten (`Send_Faults_With_The_Bus_Layer_Exception_And_Channel_Remains_Usable` pins `InvalidOperationException("bus-layer boom")`).
4. `IsoTpFrameCodec.cs:633-634` (`EncodeStMin`): "clamped to the nearest representable value" — sub-ms band truncates (`:658`), see §2.
5. Section numbers: `IsoTpChannel.cs:1381-1382` cites §6.5.2 for "an unexpected N_PCI type shall abort reception" and `:1483` cites §6.5.2 for "stray CF … drop"; `IsoTpChannelOptions.cs:10` says timeouts "follow ISO 15765-2 §6.5 naming" while `:102` cites §9.6.5.2 — two editions' numbering side by side (previous §3 item, unchanged).
6. `IsoTpChannelOptions.cs:93-97` (`ReceiveBufferCapacity`) and README `:56-57`: "the oldest PDU is dropped" — true, and nothing reports it (no `BackgroundExceptionOccurred`, no counter); fault items are dropped the same way. Worth one sentence so a consumer-less channel's silent loss is a documented property.
7. `IsoTpFunctionalClient.cs:40-45` (class remark): "They are not mutually concurrent — only one call at a time is meaningful because each creates a fresh subscription window" — each call *is* independent (own subscription), so concurrent calls are merely redundant, not unsafe; the sentence reads as a thread-safety caveat it is not.
8. `docs/requirements/SRS-CanKit.Pro.md:383` (NFR-003): verification column still claims a measured inter-frame time (the #246 drift, left behind when README was corrected in #264). Outside this package's files.
9. `IsoTpChannel.cs:1483`'s comment "drop per ISO 15765-2 §6.5.2" and `:1555` "§6.5.5.2": the FC-with-no-TX drop at `:1554-1555` is also where a reserved FS is silently lost one layer below (`IsoTpFrameCodec.cs:603-604`); neither comment says that the codec, not the channel, decides it.

## 7. What is done well

- The inbox's end-state design after #275 (flag + signal beside an always-open writer, producers and disposal under one gate, discard write-back exempt) closes a whole class of dispose/discard races at once, and `TakeNextAsync`'s double-read-before-believing-the-end is the right shape.
- Deferred FC frames as a list, replayed in arrival order with `deferIfBusy: false`, with N_Bs armed only when nothing was deferred — a small state machine that handles the FF-confirm and last-CF-confirm windows without special cases.
- Two independent defences for the stale STmin timer (handle disposed on every completion path *and* identity check in the callback), both still mutation-pinned.
- Arrival stamps taken at the demux, First Frames announced before the actor sees them, and `SettleAsync` as the caller-side pump: a coherent answer to "what does a deadline mean when everything is scheduled".
- The test seams are documented as seams (`IsoTpChannel.cs:172-180`), and the #171 rewrites explain in place what each former sleep stood for and what replaced it.

## 8. Open questions for the maintainer

1. **I-1**: dispose the gate at all? Removing `_sendGate.Dispose()` and its 2-second wait is the smallest fix and also removes the actor stall (I-6); is there a reason the gate must be disposed (a code-analysis rule?) that would make the linked-CTS variant preferable?
2. **Reserved FlowStatus**: keep the silent drop (codec returns `false`, sender waits out N_Bs) or abort the send at once with a typed exception (N_INVALID_FS)? Needs the standard's text; the code's comment at `IsoTpFrameCodec.cs:481` documents the drop as the parser's contract.
3. **STmin before the first CF of a block** (`IsoTpChannel.cs:1596-1597`, cemented by `IsoTpStminTimingTests.cs:113-116`): Linux `can-isotp` waits the gap after FC too (from memory); if the standard says the first CF may follow the FC immediately, the current behaviour is conservative but costs one STmin per block. Decide and write it down either way.
4. **N_Ar / failed FC confirmation**: should a failed or timed-out FC send abort the reception (`AbortRx` with a typed error, so a waiting `ReceiveAsync` learns the transfer is dead) instead of only a background report? Today the receiver waits N_Cr for CFs the peer will never send.
5. **FF_DL > int.MaxValue**: ignore (now) or answer `FC(OVFLW)` as any other over-size FF? Same question for the codec's deliberately strict escape-form rule (`IsoTpFrameCodec.cs:571-577`), which was written without checking the standard's text.
6. **Channel-level FD-without-padding and Extended multi-frame tests** (gaps 1–2 in §4): worth two tests in `IsoTpCanFdTests` / `IsoTpChannelIntegrationTests`, or leave the codec unit tests as the pin?
7. **Foreign `ICanBusService` contract** (I-2): is "the subscription ends by returning false, never by throwing OCE" a documented requirement on implementors? If so the fix is a doc line; if not, the filter.
