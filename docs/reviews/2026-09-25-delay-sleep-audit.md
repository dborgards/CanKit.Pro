# Delay and sleep audit (#114)

**Date:** 2026-09-25 · **Tree:** `main` @ `c359bf0` · **Scope:** `tests/CanKit.Pro.Tests/TestCases`
**Closes:** [#114](https://github.com/dborgards/CanKit.Pro/issues/114)

This is a count and a classification. It does not convert a test, widen a tolerance, or change product code. The comments already on #114 were measured against `f966cf5` (113 real call sites). The suite has grown since then; the numbers below are a fresh pass over `c359bf0`, not a correction of that ledger.

## How the count was taken

Every `Task.Delay` and `Thread.Sleep` under `TestCases` was listed with file and line, then split into a comment and a call. A match is a comment when it sits after `//` on that line or inside a `/* */` span. The call-site buckets below add up to the call count.

`WithTimeout`, `WithTimeoutAsync` and `AsTaskWithTimeout` were counted separately. They are not sleeps. A hop-count poll (`for` / `while` plus `Task.Delay`, leaving when an observable becomes true) is category 1: each hop can overshoot, so the wait grows when the runner is slow. Turning one of those into a fixed wall-clock deadline makes it more fragile, which is what an earlier pass on this issue measured and then reverted. A hop loop whose healthy path burns every hop, because both early exits are failures, is category 2: a fixed settle wearing a poll's clothing.

Judgement, not a loaded run, assigns a site to a bucket. The counts and the arithmetic on named intervals (a period, `BamPacketSpacing`, T2, P2) are the part that is measured. Nothing here was reproduced by slowing a runner.

## Counts

| | |
|---|---|
| Grep hits (`Task.Delay` / `Thread.Sleep`) | **201** |
| Of which comments | 6 |
| Real call sites | **195** |
| Category 1 — waits for an effect | 56 |
| Category 2 — assumes work completed in a window | **115** (99 tests) |
| Category 3 — the sleep itself is the budget | **1** |
| Helper, test double, or the body of a timeout helper | 23 |
| `.WithTimeout` / `.WithTimeoutAsync` / `.AsTaskWithTimeout` call sites | **375** |
| Of those, still a tight whole-test budget | **1** (not a sleep; see below) |

195 = 56 + 115 + 1 + 23. The issue's 117 was the grep on an older tree, comments included.

The six comments, so they are not recounted as calls later: `CanOpenBlockAndGuardingTests.cs:211`, `CanOpenNodeIntegrationTests.cs:201`, `CanOpenSdoCorrectnessTests.cs:69`, `DeadlineTests.cs:86`, `IsoTpChannelIntegrationTests.cs:529`, `J1939NodeTests.cs:1779`.

Per file, call sites only:

| File | Calls | 1 | 2 | 3 | Helper |
|---|---:|---:|---:|---:|---:|
| `J1939/J1939NodeTests.cs` | 36 | 23 | 11 | | 2 |
| `Uds/UdsFunctionalClientTests.cs` | 23 | | 23 | | |
| `CANopen/CanOpenNodeIntegrationTests.cs` | 19 | 3 | 14 | | 2 |
| `Uds/UdsClientTests.cs` | 18 | 3 | 15 | | |
| `IsoTp/IsoTpChannelIntegrationTests.cs` | 18 | 4 | 12 | | 2 |
| `J1939TpTests.cs` | 12 | | 8 | | 4 |
| `ProtocolActorTests.cs` | 10 | 8 | 2 | | |
| `CANopen/CanOpenCommunicationProfileTests.cs` | 9 | | 8 | | 1 |
| `DeadlineTests.cs` | 8 | 3 | 5 | | |
| `Uds/UdsExpiredDeadlineTests.cs` | 6 | 1 | 1 | | 4 |
| `CANopen/CanOpenDynamicMappingTests.cs` | 5 | | 5 | | |
| `IsoTp/IsoTpFunctionalClientTests.cs` | 5 | 1 | 3 | 1 | |
| `BusStateMonitorTests.cs` | 4 | 2 | 2 | | |
| `ProtocolActorTimerTests.cs` | 4 | 2 | | | 2 |
| `Uds/SimulatedUdsEcu.cs` | 4 | | | | 4 |
| `RawCanSubscriptionTests.cs` | 3 | | 3 | | |
| `CANopen/CanOpenPdoEngineTests.cs` | 2 | 2 | | | |
| `CANopen/CanOpenSdoCorrectnessTests.cs` | 2 | | | | 2 |
| `IsoTp/IsoTpBusOffTests.cs` | 2 | 1 | 1 | | |
| `CANopen/CanOpenDeviceDescriptionTests.cs` | 1 | 1 | | | |
| `IsoTp/IsoTpCanFdTests.cs` | 1 | | 1 | | |
| `IsoTp/IsoTpStminTimingTests.cs` | 1 | 1 | | | |
| `TxConfirmTests.cs` | 1 | 1 | | | |
| `Uds/UdsTransferTests.cs` | 1 | | 1 | | |

Category 1 is left as a count. Those sites poll until something is observable, or they are a `WhenAny` that the test requires the effect to win, with a hang-guard of seconds around work that is itself milliseconds. A slow runner makes them slower. They are not the conversion list. The 23 helpers are the same: `SimulatedUdsEcu` pacing an ECU, stub-channel transmission delays, `SendConfirmed` doubles that let N_As expire, the `Task.Delay` inside `WithTimeout` itself, and two gaps that are the subject of the test rather than a wait for it (`CanOpenSdoCorrectnessTests` idle gaps against `SdoServerTimeout`; `ProtocolActorTimerTests` proving the wall clock is not the actor's clock, and measuring that a sub-millisecond remainder really sleeps).

## What "could take a VirtualClock" means on this tree

The issue's seam note is half stale. Read the constructors, not the issue text.

| Component | Today | What a conversion can do |
|---|---|---|
| `IsoTpChannel` | Optional `IProtocolActor`. Tests already pass `VirtualClock.NewActor()`. | Ready. No product change. |
| `J1939NodeImpl` | Optional `ProtocolActor` on the internal constructor. Several claim tests already pass `clock.NewActor()`. `J1939Node.Open` does not expose it; tests construct `J1939NodeImpl`. | Ready for timers the **node** arms (claim backoff, single-frame periodic send). The TP channel the node opens for itself is still the row below. |
| `BusStateMonitor` | Constructor takes `IProtocolActor`. | Ready. No product change. |
| `DeadlineScheduler` / `ProtocolActor` | The test constructs the actor. | Ready. |
| `CanOpenNode` | Still does `new ProtocolActor(...)` itself. The internal constructor takes `ITimeSource` and hands it to that actor. `CanOpenPdoEngineTests.OpenClocked` and the life-guarding tests already pass a `ManualTimeSource`. | Clock is injectable. The actor is not. Pass `ManualTimeSource`; do not wait for an actor parameter that `VirtualClock.NewActor()` would need. |
| `J1939TpChannel` | `_actor = new ProtocolActor()` with no time source (`J1939TpChannel.cs` constructor). | **Needs the seam first.** Nothing in this layer can move to a virtual clock until then. |
| `UdsClientImpl`, `UdsFunctionalClient` | `Stopwatch.GetTimestamp()` for P2, P2*, and collection windows. | **Needs a time seam, not an actor.** |
| ISO-TP functional collection | `CancellationTokenSource.CancelAfter` plus a `Stopwatch` deadline in `IsoTpFunctionalClient.CollectFromSubscriptionAsync`. Not the channel actor. | A channel `VirtualClock` does not move this window. |
| Raw CAN subscription pump, object-dictionary write gate | No actor timer. | A signal (`ManualResetEvent`, "pump drained"), not a clock. |

Two lessons from #113 apply to every row marked ready, and they are why this list exists:

- **Bracket the interval from both sides.** Advancing past a deadline, or sleeping past it, shows that some wait happened. It does not show that the wait was the configured one. Halving the interval leaves a one-sided test green.
- **A frame on the wire is not proof the timer is armed.** Flow control goes out before `ArmNCr`. STmin is armed from a thread-pool confirmation. `WaitUntilTimerArmedAsync` / `ProtocolActor.NextTimerDelayAsync` exist so the clock moves only after the arm. Move it in the gap and the interval is armed from the new reading and never elapses.

## Category 2 — 99 tests, 115 sites

These are the defects. "Direction" is which way a slow runner pushes the result: **red** means a correct implementation fails, **green** means a broken one still passes, **both** means the sleep is only establishing an order. Green is the column a red CI run will never find.

### Bus state monitor — actor already injected

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `StateChanged_Is_Not_Raised_While_The_State_Is_Unchanged` | 57 | 200 ms contained enough 20 ms polls to have seen a spurious edge | green |
| `Dispose_Stops_Further_StateChanged_Events_And_Is_Idempotent` | 120 | a disposed monitor would have raised within 150 ms | green |

### Actor and deadlines — the test owns the actor

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `Complete_Before_Expiry_Prevents_OnExpired_And_Is_Idempotent` | 57 | the original due point has passed within 300 ms | green |
| `Cancel_Before_Expiry_Prevents_OnExpired` | 73 | same, for a 200 ms deadline | green |
| `Disposing_The_Owning_Actor_While_Pending_Never_Fires_The_Deadline_And_Escapes_No_Exception` | 183 | same | green |
| `Rearm_Before_Original_Expiry_Extends_The_Deadline` | 92, 98 | a 50 ms sleep is still inside the 600 ms arm, and 850 ms is past the original due point but not the re-armed 2 s | both — this is the bracket, done on the wall clock. The comment at line 86 already records that a 50 ms `Task.Delay` can overshoot by hundreds of milliseconds |
| `Disposing_The_Schedule_Handle_Before_Due_Prevents_The_Callback_From_Firing` | 139 | a cancelled 100 ms timer would have fired within 300 ms | green |
| `Dispose_Called_Reentrantly_From_A_Posted_Callback_Does_Not_Deadlock` | 195 | teardown has finished within 200 ms | red |

### ISO-TP channel — actor injectable

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `Active_MultiFrame_Send_Faults_With_BusOff_Instead_Of_Hanging` | 66 | the FF confirmation is pending within 100 ms, so BusOff hits that send | red |
| `Channel_With_UseCanFd_Emits_Only_CanFd_Frames_For_Sf_Ff_Cf_And_Fc` | 156 | tail frames have reached the sniffer within 100 ms | green |
| `MultiFrame_Send_Aborts_When_Peer_Exceeds_WftMax` | 349 | each Wait FC is processed within 20 ms before the next is sent | red |
| `Settle_Takes_A_Buffered_Single_Frame_Through_To_The_Inbox` | 436 | 50 ms was long enough for a non-starved reader to have delivered | green |
| `A_Discard_Given_A_Stamp_Keeps_What_Arrived_After_It` | 459 | 5 ms moves `Stopwatch` past the earlier stamp | red |
| `DiscardPendingPdus_Aborts_InFlight_Reassembly` | 642 | the trailing CF was processed within 50 ms | red |
| `Send_Cancelled_Before_Actor_Delivery_Emits_No_Frame_And_Channel_Remains_Usable` | 1156 | a frame the bug would emit has had 100 ms to appear | green |
| `Cancelled_Send_Holds_Gate_Until_InFlight_Bus_Tx_Completes` | 1306 | same shape, while the confirmation is held | green |
| `MultiFrame_Send_Accepts_FlowControl_Arriving_During_Last_Cf_Confirm` | 1391 | the deferred FC is installed within 30 ms of the transmit | red. This is the arm-before-advance lesson: the frame is on the wire before the actor has taken it |
| `MultiFrame_Send_Counts_Wait_FlowControls_Deferred_During_Ff_Confirm` | 1445 | each Wait FC is processed within 20 ms | red |
| `Rx_Classic_Rejects_CanFd_Escape_FirstFrame_Without_Allocating` | 1541 | an illegal FC would have been sent within 100 ms | green |
| `A_Stale_StMin_Timer_Does_Not_Send_A_ConsecutiveFrame_Of_The_Next_Transfer` | 1688 | after `clock.AdvanceAsync(stMin)`, 100 ms of wall time is enough to see a frame the timer released | green. The clock half is already virtual; the sleep is the wire not proving the arm |
| `Receiver_With_LocalBlockSize_Emits_FlowControl_After_Each_Full_Block` | 1760 | all three FCs have been counted within 50 ms of receive completing | red |
| `Handoff_Instant_Is_Taken_Inside_The_Service_Lock_After_Another_Senders_Call` | 1980 | sender B has reached the lock within 50 ms. The comment says a shorter wait only weakens the check | green |
| `Functional_Collect_Discards_Frames_That_Arrived_Before_Send` | 291 | the stale frame has been routed within 50 ms | red |
| `Functional_Collect_Does_Not_Accept_Frames_After_Window_Expiry` | 450 | the late frame has been considered within 60 ms. Line 442 of the same test is category 3 | green |
| `Functional_Send_Refuses_A_Window_Beyond_A_Timers_Reach_Before_Transmitting` | 642 | a buggy transmit would have been seen within 50 ms | green |

Hop polls left in category 1 in this layer, so they are not "fixed" into deadlines: `IsoTpChannelIntegrationTests.cs` 942, 1318, 1496, 2510; `IsoTpFunctionalClientTests.cs` 513; `IsoTpStminTimingTests.cs` 168.

### J1939 node — actor injectable for node timers

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `A_Delayed_Cannot_Claim_Is_Dropped_Once_A_New_Claim_Has_Started` | 474 | 300 ms is past the ~153 ms backoff, so a second Cannot Claim would have been seen | green. One side only |
| `A_Request_During_The_Backoff_Starts_The_Round_With_A_Single_Announcement` | 582 | same | green |
| `A_Lost_Claim_Faults_Only_Once_Its_Cannot_Claim_Is_On_The_Bus` | 615 | a backoff that survived `Dispose` would have fired within 300 ms | green |
| `A_Request_During_The_Cannot_Claim_Backoff_Is_Answered_By_That_One_Frame` | 667 | 500 ms is past the backoff. Line 670 does bracket the lower side (≥ 100 ms) on the wall clock | green for the "no second copy" half |
| `ClaimAddressAsync_CancelDuringArbitration_TearsDownPendingClaim` | 1805 | 700 ms is past the arbitration window, so a surviving timer would have fired | green |
| `ReClaim_RejectsSendUntilNewClaimSucceeds` | 1936, 1971 | the single-frame send has been observed within 50 ms | red. The hop at 1945 exits when `ClaimState` leaves `Claimed` and stays category 1 |
| `RebindTransport_DoesNotDeliverBamMoreThanOncePerRebind` | 2114 | 150 ms is long enough that a duplicate datagram would have surfaced. The test says so: there is no event for "no duplicate" | green |
| `Send_InFlightAcrossReclaim_FailsWithNoAddressException` | 2193 | twenty hops of 10 ms. Both early exits (`sendTask` completed, or state already left `Claimed`) are failures, so a healthy run always burns the hops and then starts the reclaim. That is not a poll for "the TP session has started" | both. The session lives on `J1939TpChannel`, which has no clock seam |
| `StartPeriodicSend_SingleFrame_FiresAtConfiguredPeriod` | 2318 | two periods after `Dispose` is enough quiet time that further frames would have arrived. The collection budget above this sleep is category 3 | both |
| `StartPeriodicSend_SingleFrame_StopsAfterAddressLoss` | 3160 | `period + period + 50 ms` of quiet after the loss | both |

Siblings of the backoff tests in this file already run on `VirtualClock` and call `WaitUntilTimerArmedAsync`. Lines 474, 582, 615 and 667, plus the arbitration sleep at line 1805, are the same timers still measured on the wall clock.

### J1939-TP — needs a seam before any of these move

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `Bam_Sender_On_An_Unflagged_Echo_Bus_Does_Not_Receive_Its_Own_Broadcast` | 111 | within 500 ms the sender has not reassembled its own BAM. `BamPacketSpacing` here is 5 ms | green |
| `Bam_AnnounceTxRejected_FailsSendAndDoesNotEmitDt` | 542 | 80 ms was long enough for a wrongly scheduled DT (`BamPacketSpacing` is 5 ms) | green |
| `SendCm_CanceledBeforeStart_DoesNotTransmit` | 576 | 100 ms was long enough to see a frame | green |
| `Cm_Sender_EomSizeMismatch_FailsSend` | 1010 | 20 ms is enough that `OnCmDtConfirmed` has armed the EndOfMsg wait. Wire confirmation, then a sleep | red |
| `Cm_Receiver_Survives_A_First_Dt_That_Arrives_300ms_After_Cts` | 1848 | `Task.Delay(300)` stays under T2 (1250 ms). The delay is a lower bound and load only widens it, until it crosses T2 and a correct receiver aborts. Margin on that side is 950 ms. `ReceiveAsync` is also inside `ShortTimeout` (5 s) | red past T2 |
| `A_Pdu1_Pgn_With_A_Low_Byte_Is_Refused_Before_Anything_Goes_Out` | 2002 | 50 ms was long enough to see a transmit | green |
| `A_Retransmit_Request_Mid_Block_Takes_Effect_After_The_Outstanding_Packet` | 2122 | "the CTS is on the actor before the confirmation is released" — a 50 ms sleep in place of an arm barrier | red |
| `A_Cts_For_An_Unsent_Packet_Of_The_Block_Is_A_Sequence_Error_Not_A_Retransmit` | 2172 | the abort frame is on the wire within 50 ms of the send faulting | red |

`DriveAsync` (488, 493) spaces a scripted peer. That delay is the double, not an assertion, and stays in the helper bucket.

### CANopen — time source injectable, actor still constructed inside the node

The repeated shape is NMT Start, `Task.Delay(50)` or `Task.Delay(100)`, then a PDO or a state assertion. Nine sites are that shape: dynamic-mapping lines 119, 165, 362 and 411, and integration lines 613, 724, 1205, 1274 and 1305.

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `Sdo_DynamicTpdoMapping_ReconfiguresPayloadViaSdo` | 119 | both nodes are Operational within 50 ms of Start | red |
| `Sdo_DynamicRpdoMapping_ReconfiguresUnpackViaSdo` | 165 | same | red |
| `Tpdo_ChangeOfState_Emits_On_ApplicationOdWrite` | 362 | same | red |
| `Tpdo_ChangeOfState_DoesNotEcho_On_RpdoUnpack` | 411, 413 | Start has applied within 50 ms, and 300 ms is long enough for a wrong echo | red, then green |
| `Sdo_ServerSupersede_EmitsWireAbort_ForPriorTransfer` | 377 | the segmented session is installed within 50 ms | red |
| `Sdo_ClientResponseWithShortDlc_IsAcceptedAndCompletes` | 422 | the upload init is on the wire within 30 ms | red |
| `Sdo_ClientSegmentedUploadResponse_OverMaxTransferBytes_AbortsOutOfMemory` | 540 | same | red |
| `Tpdo_Emission_UnderConcurrentOdWrites_NeverTears` | 613, 666 | Start has applied; 200 ms drains the RPDO pump before the counts are sampled | red |
| `Nmt_Broadcast_TransitionsAllNodes` | 724 | both slaves are Operational within 100 ms | red |
| `Nmt_ResetNode_EmitsBootup` | 785 | the initial boot-up has already happened within 50 ms, so it is not the one under test | both |
| `Nmt_ResetCommunication_EmitsBootup_And_Settles_In_PreOperational` | 810, 821 | initial boot-up consumed; Pre-operational within 50 ms of the reset boot-up | both, then red |
| `Sdo_Segmented_Download_Wrong_Toggle_Aborts` | 841 | the init-ack has happened within 100 ms | red |
| `Heartbeat_Consumer_FiresTimeoutWhenPeerGoesSilent` | 903 | 100 ms is past the initial boot-up so the consumer arms cleanly | both |
| `Tpdo_EventDriven_Emits_MappedOdValues` | 1205 | Start has applied within 50 ms | red |
| `Tpdo_DummyMapping_KeepsSubsequentSlotOffsets` | 1274 | same | red |
| `Tpdo_SyncTriggered_FiresEverySync` | 1305 | same | red |
| `Overlapping_Emcys_Reach_The_Bus_In_The_Order_The_Register_Was_Written` | 932 | 200 ms was long enough for a second EMCY that did not wait to have been transmitted | green |
| `A_Guarding_Reply_To_A_Poll_Queued_Behind_A_Reset_Stays_Behind_The_Bootup` | 1058 | 100 ms was long enough to see a reply that did not wait | green |
| `A_Producer_Tick_Due_During_ApplicationReset_Stays_Behind_The_Bootup_And_Restarts_The_Cycle` | 1115, 1124 | after `clock.Advance` and `SettleAsync`, another 100 ms of wall time changes nothing on the wire | green. The clock is already manual; the sleeps are leftover |

These three are the object-dictionary write gate, not an actor timer. A clock will not order them. They want the other thread to have reached the gate, which is a signal:

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `A_Redeclaration_Waits_For_The_Write_In_Flight_On_The_Entry` | 436 | the re-declaration has reached the gate within 200 ms | green |
| `A_Typed_Write_Resolves_Its_Type_Under_The_Write_Gate` | 496 | the typed write has reached the gate within 200 ms | green |
| `A_Direct_Write_Cannot_Land_Inside_A_ConfigureTpdo_Transaction` | 639 | the hammers are at the gate within 100 ms | green |
| `A_Save_During_An_Nmt_Reset_Stores_All_Restored_Values_Not_A_Mix` | 1732 | 300 ms is long enough for a save that is not held back to finish | green |

`ApplicationReset_Runs_On_The_Loop_Before_The_Bootup` sleeps 300 ms inside the reset handler. That sleep is the slow restore under test, and a longer one only strengthens it. It stays in the helper bucket.

### Raw CAN — a signal, not a clock

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `Callback_Subscribe_Dispose_Stops_Delivery` | 226 | frames sent after dispose would have been delivered within 200 ms | green |
| `Callback_Subscribe_Dispose_From_Inside_Handler_Does_Not_Deliver_Buffered_Frames` | 291 | same, for frames buffered during the callback | green |
| `Callback_Subscribe_OnError_Takes_Precedence_Over_The_Service_Fault_Event` | 398 | a second, wrong report would have arrived within 100 ms | green |

### UDS — time seam, not an actor

`UdsClientImpl` and `UdsFunctionalClient` read `Stopwatch` directly. The sleeps below are almost all "this frame lands on one side of P2, P2*, or the functional window." The ECU-side `Task.Delay` in `SimulatedUdsEcu` is the double; the assumption lives in the test that chooses the interval.

| Test | Lines | Assumes | Direction |
|---|---|---|---|
| `A_Late_Negative_Response_To_A_Suppressed_Send_Is_Not_The_Next_Requests` | 502 | `Thread.Sleep(100)` stays inside `P2ClientMax` of 300 ms | red if the sleep crosses P2 |
| `Suppressed_Send_Windows_Are_Kept_Per_Service` | 532 | same | red |
| `A_Queued_Pending_Answer_Still_Extends_A_Window_That_Has_Run_Out` | 624 | spinning `Task.Delay(10)` until the captured window end has passed | the spin itself is safe; the earlier poll must still have observed the 0x78 inside P2 |
| `A_Queued_Pending_Answer_From_After_The_Windows_End_Does_Not_Revive_It` | 664 | 700 ms covers both the 200 ms window and the ECU's 500 ms delay | red if the ECU delay overshoots the 700 ms |
| `A_Pending_Answer_Still_On_Its_Way_Through_The_Channel_Extends_The_Window` | 698 | 150 ms is past a 100 ms window | overshoot makes "past" surer |
| `A_Stale_Pending_Answer_Queued_Before_A_Suppressed_Send_Does_Not_Extend_Its_Window` | 851 | 150 ms covers the ECU's 100 ms delay | red |
| `A_Cancelled_Wait_Keeps_The_Rest_Of_The_Window` | 874 | `Thread.Sleep(200)` stays inside P2 of 400 ms | red |
| `A_Pending_Answer_For_Another_Service_Heard_During_A_Wait_Extends_That_Services_Window` | 914 | `Thread.Sleep(250)` so this response loses the race to a negative scheduled at 550 ms. P2 on the test is 400 ms | both |
| `A_Pending_Answer_Consumed_As_Another_Requests_Stray_Still_Extends_Its_Window` | 949, 954 | 400 ms and 200 ms place two responses on specific sides of a 600 ms P2. The non-suppressed `0x11` handler sleeps 400 ms inside that P2, so the margin before a correct client times out is 200 ms | both. **Measured red** on `macos-latest` for this pull request (run 36102259889, job 107967157821): `UdsTimeoutException`, P2 after 600 ms waiting for service `0x11`, at the `SendRawAsync` on line 970. Ubuntu and Windows on the same commit passed. The diff is this document only, so the failure is not attributable to it. Not fixed here: #114 is a count, and widening P2 would be the tolerance this audit exists to refuse |
| `SecurityAccess_Holds_Lock_Across_Seed_And_Key` | 1206 | 120 ms covers several 30 ms keep-alive periods while the lock is held | red if fewer periods elapse than the assertion needs; overshoot adds periods |
| `TimedOut_Request_Does_Not_Poison_Next_Same_Service_Transaction` | 1240 | `Thread.Sleep(250)` is after P2 of 80 ms. Overshoot keeps it after | green for the "after" direction |
| `Dispose_During_InFlight_Request_Does_Not_Race_RequestLock` | 1576 | the request holds the lock within 50 ms | red |
| `Dispose_Cancels_Suppress_TesterPresent_Blocked_On_RequestLock` | 1606, 1610 | in the receive within 50 ms, then parked on the lock within 30 ms | red |
| `N_Dispose_Leaves_The_Lock_To_A_Holder_That_Outlasts_The_Wait` | 374 | the stubbed request holds the lock within 50 ms | red |
| `DownloadAsync_Holds_Exclusive_Lock_Against_Concurrent_TesterPresent` | 402 | `Thread.Sleep(2)` per block gives a concurrent TesterPresent a chance to interleave. The sleep is the width of the race window | green if 2 ms is not wide enough for the racer |

`UdsFunctionalClientTests` is 23 category-2 sites and no category-1 poll. `Window` in that file is 300 ms. A `Task.Delay(20)` before a positive response has to land inside that window (red if the delay crosses it; the margin is large). A `Task.Delay` of 100–250 ms before a negative response is placing the frame after a shorter window or during the next one.

| Test | Lines | What the delays are doing |
|---|---|---|
| `A_Call_Queued_Behind_Another_Does_Not_Send_After_Dispose` | 167 | 50 ms: the first call is inside its 300 ms window and the second is queued |
| `A_Late_Negative_Answer_To_A_Suppressed_Send_Does_Not_Land_In_The_Next_Window` | 244 | 100 ms, then the negative |
| `A_Late_Negative_Answer_To_A_Previous_Request_Does_Not_Land_In_The_Next_Window` | 276 | 150 ms, then the negative |
| `A_Cancelled_Collection_Still_Leaves_Its_Window_For_The_Next_Call` | 308 | 150 ms, then the negative |
| `A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_Before_The_Send` | 449, 458 | 200 ms while the driver is held (the first window is 50 ms); then 20 ms before the positive, inside `Window` |
| `A_Window_Is_Anchored_At_The_Drivers_Acceptance_Not_At_The_Confirmation` | 489, 497 | 2 s with the confirmation held, so a 50 ms window is long over; then 20 ms before the positive |
| `A_Pending_Answer_Collected_After_P2_Does_Not_Revive_The_Window` | 561 | 250 ms, against a 100 ms response window |
| `A_Listener_Starting_After_The_Window_Still_Hears_What_It_Buffered` | 601 | 250 ms, then the negative |
| `A_Cancelled_Collection_Does_Not_Lose_The_Pending_Answer_The_Listener_Heard` | 640 | 250 ms, then the negative |
| `A_Cancelled_Wait_Leaves_The_Listener_To_Hear_The_Pending_Answer` | 677, 678 | 50 ms then the pending; 200 ms then the negative |
| `A_Pending_Answer_In_The_Gap_After_A_Suppressed_Send_Is_Observed` | 718, 719, 730 | 100 ms, 400 ms, and 200 ms of "nobody collecting" |
| `A_Pending_Answer_From_Before_The_Handoff_Is_Not_This_Requests` | 803, 811 | 50 ms of setup; 20 ms before the positive |
| `An_Invalid_Collection_Window_Transmits_Nothing` | 864 | 50 ms to have seen a transmit |
| `A_Listener_Is_Restarted_When_The_Acceptance_Outlasted_The_Window` | 924, 928, 934 | 1100 ms past the window; 150 ms then a negative; 20 ms then a positive |
| `A_Collection_That_Outlasts_The_Window_Does_Not_Leave_A_Zombie_Listener` | 957 | 150 ms, then the negative |

## Category 3 — budgets that are not (only) a sleep

375 call sites of `.WithTimeout(`, `.WithTimeoutAsync(` and `.AsTaskWithTimeout(` wrap a single operation in 5 s or 10 s (`ShortTimeout` in almost every file). That is a hang-guard. It fails a correct implementation only when the work inside is long because the runner is slow. Most of these operations are one claim, one single frame, or one short exchange. They are not listed. Listing them would repeat the mistake of treating every bounded wait as a defect.

Two budgets are the shape the issue asked for. One further family is the product timer that races a sleep; the sleep grep sees only half of it.

### Still tight

**`J1939NodeTests.StartPeriodicSend_SingleFrame_FiresAtConfiguredPeriod`.** Not a `WithTimeout`. The loop at line 2311 is a category-1 poll (it leaves when 22 samples exist). The budget around it is wall-clock:

- period = 120 ms, samples required = 22, so 21 gaps
- nominal = 21 × 120 ms = 2.52 s
- `collectBudget` = 120 ms × 22 × 4 = **10.56 s**
- if a loaded runner stretches each period to 3× (360 ms), the same 21 gaps take 7.56 s, and the margin is 10.56 / 7.56 ≈ **1.4×**

The comment in the test says a loaded runner coalescing to 2× or 3× needs "several times" the nominal run, and that four times nominal is the allowance. That is the whole-test budget. The schedule is the node actor's, so this test can move to the virtual clock the neighbouring claim tests already use. Do that before lengthening `collectBudget`. The `Task.Delay(period + period)` after dispose (line 2318) is the category-2 half of the same test.

**`IsoTpFunctionalClientTests.Functional_Collect_Does_Not_Accept_Frames_After_Window_Expiry` line 442.** `CollectResponsesAsync(200 ms)` then `Task.Delay(400 ms)`, then a late frame. The window is `CancelAfter` plus a `Stopwatch` deadline in `CollectFromSubscriptionAsync`, both before that method's first await, so the call arms the window before it returns the task. The 200 ms of slack is one full window against arm-to-inject latency, not against the channel actor. A `VirtualClock` handed to `IsoTpChannel` does not move it. The follow-up `Task.Delay(60)` (line 450) is category 2. The comment in the test records that a 40 ms window and an 80 ms wait already failed on macOS; the margin was widened, not removed.

### Margin already opened — not conversion candidates

These were the flakes #114's discussion found. They are recorded so the next change does not rediscover them or "fix" them by widening a timeout.

| Test | What was tight | What the tree does now |
|---|---|---|
| `J1939TpTests.Parallel_Bam_And_TwoCm_Sessions_Do_Not_Interfere` | 200-byte BAM, default `BamPacketSpacing` 50 ms → 28 gaps → 1400 ms of pacing inside `ShortTimeout` of 5 s | `bamPacketSpacing: 5 ms` → 140 ms of pacing, about 35× inside 5 s. Comment on the test says so |
| `J1939TpTests` maximum-payload BAM | `BamPacketSpacing` of 1 ms × 254 gaps. A 1 ms `Schedule` parks the loop; under load that was tens of seconds inside a 10 s budget | spacing is `TimeSpan.Zero` |
| `UdsClientTests.ResponsePending_Loop_Aborts_When_Exceeding_MaxResponsePendingCount` | ECU `delayBetween` of 20 ms against `P2StarClientMax` of 1 s. Each 0x78 restarts P2*, so a stretched delay makes the client raise `UdsTimeoutException` instead of the protocol abort | `delayBetween: TimeSpan.Zero`. The subject is the count |

Every other successful BAM in `J1939TpTests` passes `bamPacketSpacing` of 5 ms or zero. The default 50 ms is not on a paced round-trip in that file.

### The product-timer family

A per-test `P2ClientMax`, `P2StarClientMax`, T1–T4, or `SdoServerTimeout` is a budget a slow runner can exhaust, and it does not appear in either grep above. This pass did not re-measure the ISO-TP N_As/N_Bs/N_Cr defaults (an earlier pass on the issue walked those down to 20 ms without a failure). It did re-read the sleeps that sit next to a named product timer:

- `Cm_Receiver_Survives_A_First_Dt_That_Arrives_300ms_After_Cts` — 300 ms lower bound, T2 = 1250 ms, 950 ms of room before a correct receiver aborts. Category 2, and the right eventual form is a virtual clock that brackets 300 ms &lt; gap &lt; T2. That needs the J1939-TP seam.
- `ResponsePending_Restarts_P2Star_And_Returns_Final_Response` — `delayBetween` 60 ms against `P2StarClientMax` of 1 s, and the 60 ms is what pushes the transfer past `P2ClientMax` of 120 ms. Zeroing it would drop the property. Margin to P2* is about 940 ms per gap. Not the race #117 removed.
- The UDS rows in the category-2 table whose sleep must stay inside P2 are the same family from the other side.

## Conversion order

By risk, and only after a failing run of the unmodified test under load. The hop-count recommendation on this issue was reasoned from the shape and then measured false. Do not repeat that.

1. **J1939 node backoff and arbitration sleeps** (`J1939NodeTests` lines 474, 582, 615, 667, 1805). The seam is in use beside them. They are green: CI will not trip them. Bracket the configured backoff from both sides, and arm the timer before advancing. No product change.
2. **`StartPeriodicSend_SingleFrame_FiresAtConfiguredPeriod`**, both halves (the 10.56 s budget and the two-period quiet window). Same seam. This is the budget a 3× stretch can still reach. `StartPeriodicSend_SingleFrame_StopsAfterAddressLoss` (line 3160) is the same schedule.
3. **ISO-TP category 2 on the injected actor**, negative windows first (1156, 1306, 1541, 1688, 1980), then the "FC is surely processed" sleeps (349, 1391, 1445). Line 1688 already advances a virtual clock and then sleeps for the wire. Line 1391 is the arm-before-advance lesson in a comment.
4. **`BusStateMonitor` negatives and the deadline / `ProtocolActor` negatives**, including the Rearm bracket. The test already holds the actor. Small, and entirely green.
5. **CANopen**, passing the `ITimeSource` the node already accepts. The NMT-Start-then-sleep pattern is nine sites in two files; the other node-timing sleeps in `CanOpenNodeIntegrationTests` are the same seam. Do the four object-dictionary gate sleeps separately, with a signal, not a clock. The producer-tick test already has a `ManualTimeSource`; its two wall sleeps are what is left.
6. **J1939-TP, after a seam** that lets `J1939TpChannel` take a time source or an actor the way `IsoTpChannel` and `J1939NodeImpl` do. Eight sites. Do not get there by shrinking `BamPacketSpacing` again; that knob has already been used for the two tests that were actually tight. `Send_InFlightAcrossReclaim_FailsWithNoAddressException` waits on a TP session the node opened internally, so it waits on this seam too, or on an observable for "the session has started" — the twenty hops are not that observable.
7. **UDS and the functional collection window last.** They need a time seam in the client, not an actor. Overlaps the P2 work already in the tree. Do not widen `P2StarClientMax`. The functional-client file is 23 sites of the same "place a frame relative to a window" shape; one seam covers the file.
8. **Raw CAN subscription**, three green windows, as a "the pump has drained" signal. Not a virtual clock.

Leave category 1, the helper bucket, and the 375 hang-guards alone. Leave the three tests whose margin #115, #116 and #117 already opened.
