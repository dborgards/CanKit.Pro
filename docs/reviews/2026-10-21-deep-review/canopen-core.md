# CanKit.Pro.CANopen — node core and SDO (deep review 2026-10-21)

Repository `main` @ 36c97ca (after PR #336). Read-only review; nothing was built or run. All
line numbers are current `main`. CiA 301 clauses are quoted from memory and say so; no copy of
the standard was consulted.

Finding ID prefix: **C**. New findings are C-1 … C-14 (section 3); the previous review's C9–C16,
Q9 and the §3 "CANopen" minor list are re-checked in section 2.

## 1. Scope read

Source (`src/CanKit.Pro.CANopen/`), read completely:

| File | Lines |
| --- | ---: |
| CanOpenNode.cs | 3 074 |
| CanOpenNode.SdoBlock.cs | 1 324 |
| CanOpenNode.CommunicationProfile.cs | 1 087 |
| CanOpenNode.BootUp.cs | 564 |
| CanOpenNode.NodeGuarding.cs | 393 |
| CanOpenNode.PeerSdo.cs | 275 |
| CanOpenNode.PdoTiming.cs (SYNC send / counter, for C14) | 190 |
| ICanOpenNode.cs | 535 |
| ObjectDictionary.cs / OdEntry.cs | 563 / 212 |
| CanOpenNodeOptions.cs / CanOpenEvents.cs | 370 / 280 |
| Sdo/SdoFrames.cs, SdoBlockFrames.cs, SdoAbortCode.cs, SdoAbortException.cs, SdoAbortOrigin.cs, SdoTransferMode.cs | 201 / 244 / 101 / 90 / 24 / 51 |
| Heartbeat/HeartbeatConsumer.cs, HeartbeatProducer.cs | 111 / 107 |
| Nmt/NmtState.cs, Emcy/EmcyMessage.cs | 52 / 88 |
| CanOpen.cs, CanOpenCobId.cs, CanOpenNodeExtensions.cs, CanOpenNodeProfile.cs, CanOpenTransportException.cs, PeerSdoAccessException.cs, Utils/IsExternalInit.cs | 111 / 219 / 34 / 21 / 24 / 78 / 9 |
| README.md (sections: validated, coverage, not built, OD, error control, NMT, block transfer, quick start, layout, changes, install) | of 1 692 |

Read for the core's calls into them: `CanOpenNode.Pdo.cs:560-660` (`HandleSync`, `HandleRpdo`),
`CanOpenNode.Lss.cs:40-345` (`GateTransmit`, `TryReserveTransmit`, held transmits),
`src/CanKit.Pro.Actor/ProtocolActor.cs:478-640` (Dispose, FinalDrain),
`src/CanKit.Pro.Reliability/IDeadline.cs` and `DeadlineScheduler.cs:113-210` (Rearm contract).

Tests (`tests/CanKit.Pro.Tests/TestCases/CANopen/`), read completely: CanOpenNodeIntegrationTests
(1 470), CanOpenSdoCorrectnessTests (1 765), CanOpenSdoClientSendFailureTests (800),
CanOpenSdoTruncatedSegmentTests (203), CanOpenBlockAndGuardingTests (677),
CanOpenCommunicationProfileTests (1 818), CanOpenLifeGuardingAndNmtStateTests (911),
CanOpenCriticalEventQueueTests (1 101), CanOpenQueuedEmcyTests (77), CanOpenPeerSdoGateTests (255),
PeerSdoLaboratory (193), HeartbeatConsumerTests (232), HeartbeatProducerTests (157),
ObjectDictionaryTests (136), CanOpenDisposeTests (720), CanOpenCodecTests (304), CanOpenFixtures (20).

## 2. Status of previous findings (review of 2026-09-30, main @ 8cec2ee)

| ID | Status | Evidence |
| --- | --- | --- |
| **C9** block-download client batch continues after abort/cancel | **Fixed** (PR #259, `c0bdff5`, on main) | `CanOpenNode.SdoBlock.cs:600` passes `shouldStop: () => session.Tcs.Task.IsCompleted` into `SendOrderedControlFrames`, checked before every frame (`CanOpenNode.cs:2830`). Test `A_Block_Download_That_Ends_Mid_SubBlock_Sends_No_More_Segments` (both rows: server abort, caller cancel), `A_Rejected_Block_Segment_Stops_The_Sub_Block`. Residual: the *server-side* mirror was never fixed — see **C-1**. |
| **C10** classic upload accepts size mismatch | **Fixed** (#253, PR #265 `8af66cc`, `124130a`, `8ff03eb`) | `CanOpenNode.cs:2481-2485` aborts `0607 0012h` on overrun (judged before the cap, 2486); `:2516-2520` reports a shortfall on `BackgroundExceptionOccurred` and returns the shorter data (maintainer decision in PR #265). Tests `Sdo_Client_Upload_Aborts_When_The_Server_Sends_More_Than_It_Announced`, `…_Over_An_Announced_Cap_Sized_Length_Is_Length_Too_High_Not_Out_Of_Memory`, `…_Announces_Zero_And_Sends_Data`, `…_Returns_Short_Data_And_Reports_The_Shortfall`, `…_With_The_Announced_Length_Reports_Nothing`. Mutation canopenB-M1 now has a detector. |
| **C11** linear buffer growth | **Fixed** (PR #263 `61a5a82`, `cb1a42b`) | `GrowCapacity` (`CanOpenNode.cs:2535-2540`) used at `:2493`, `SdoBlock.cs:505`, `:830`. Tests `GrowCapacity_Doubles_Clamps_To_The_Ceiling_And_Never_Undershoots`, `Sdo_Client_Sizeless_Upload_Grows_Geometrically`, `Sdo_BlockDownload_Server_Sizeless_Grows_Geometrically`, `Sdo_BlockUpload_Client_Sizeless_Grows_Geometrically` (net5+ only; allocation bound 40 MB vs ≈307 MB linear). |
| **C12** block-download request timer over the whole sub-block | **Fixed** (PR #267 `83f7ef5`) | `SdoBlock.cs:592-605`: `onFrameConfirmed` posts `RearmBlockClient` per confirmed segment; a timeout that fired meanwhile is not revived (`IsLiveBlockSend` + `TimedOut`, `650957c`). Options doc updated (`CanOpenNodeOptions.cs:19-27`). Tests `A_SubBlock_Longer_Than_The_Sdo_Timeout_Does_Not_Time_Out_Before_Its_Ack`, `A_Segment_Confirmed_After_The_Timeout_Does_Not_Revive_The_Block_Download` (manual clock). |
| **C13** EMCY 8210h per short RPDO (PDO slice) | Fixed in PR #263 (`rp.ShortFrameReported`, `CanOpenNode.Pdo.cs:625-636`) — noted only; PDO reviewer owns it. |
| **C14** `SendSyncAsync` bypasses NMT state / bit 30 | **Closed by decision** (#254, PR #265): documented as a raw send | `ICanOpenNode.cs:273-276`, README NMT table `:648`. Code unchanged (`CanOpenNode.cs:459-466`, `PdoTiming.cs:98` gates only `periodic`). No test, per PR #265 ("documentation only"). Consistent docs; nothing further. |
| **C15** self-application of own NMT broadcast depends on echo | **Closed by documentation** (PR #288) | `ICanOpenNode.cs:174-183`, README quick start `:1524-1529`. Behaviour unchanged (`HandleNmtCommand` is the only application path, `CanOpenNode.cs:1562-1606`). The review's proposed local application was not taken; that is a decision, not drift. |
| **C16** reserved heartbeat bytes reported as Initializing | **Fixed** (#255, #266; PRs #265, #267) | `TryDecodeHeartbeatState` (`CanOpenNode.cs:1677-1687`), `HandleHeartbeat:1704` also drops bit 7; guarding path `NodeGuarding.cs:214`. `HeartbeatReceivedEventArgs.State` doc updated. Tests `Heartbeat_With_A_Reserved_State_Byte_Is_Not_Reported` (0x01, 0x03, 0x80, 0x84), `Guarding_Reply_With_A_Reserved_State_Byte_Is_Not_Reported`. |
| **Q9** `Dispose` blocks on work needing its own actor | **Fixed** (#251, PR #269 + follow-ups `9792880`…`b443730`) | `DisposeAsync` (`CanOpenNode.cs:778-809`), `IAsyncDisposable` on the node, extension `CanOpenNodeExtensions.DisposeAsync`; cleanup runs inline when on the actor (`:886`), the thread markers (`Callbacks`, `:831-872`) stop a subscriber from waiting for itself. The actor's own `FinalDrain` runs the posted cleanup (`ProtocolActor.cs:612-631`). 20 tests in CanOpenDisposeTests. `ICanOpenNode` remarks `:39-50` describe it. |
| **#311 / PR #335** stale block-send-outcome test | Fixed in test: `CanOpenSdoClientSendFailureTests.cs:505-552, 557-620` use a 10-minute SDO timeout and cancel at the end. |
| **#321 / PR #322** SDO tap causal order | Out of this slice (`CanOpenForeignPdoObserveTests.cs`); PR merged `8462826`. |
| §3 constructor: actor thread leaks when `ApplyDeviceDescription` throws; reader before init post | **Open, unchanged.** `_actor` is created at `CanOpenNode.cs:223`; `PopulateCommunicationProfile` (`:241`) and `ApplyDeviceDescription` (`:244`) run before the `try` at `:255` that disposes the actor on failure. The dedicated thread is a background thread (`ProtocolActor.cs:280`), so it is a leak until process exit, not a hang. Reader starts at `:306`, init post at `:311` — the window is one thread-pool hop; a Start arriving in it would run `ApplyNmtTransition(Operational)` and then be overwritten by `_state = PreOperational` at `:318` without `OnLeaveOperational`. Window-only; see C-10. |
| §3 non-alternating guarding toggle silently dropped | **Open, deliberate per code comment** (`NodeGuarding.cs:216-222`). See C-7. |
| §3 life guarding tied to `RespondToNodeGuardingRtr` | **Open** (`NodeGuarding.cs:244` returns before `:259`). See C-8. |
| §3 EMCY with DLC < 8 silently dropped | **Open** (`CanOpenNode.cs:1665`). See C-11. |
| §3 block upload of an empty domain ends only in client timeout | **Open** (`SdoBlock.cs:1093-1096`). See C-3. |
| §3 block control frames outside their phase fall into the classic server | **Open** (`SdoBlock.cs:730` → `CanOpenNode.cs:1788`). See C-9. |
| §3 expedited e = 1, s = 0 on a 1..3-byte object rejected | **Open** (`SdoFrames.cs:160`, `CanOpenNode.cs:1976-1984`). See C-4. |
| §3 README layout / dependencies / "not built" | **Open** (see section 6). |
| Mutation canopenB-M5 (client-side toggle check) | **Still a gap.** `grep ToggleBitNotAlternated tests/…/CANopen` hits only `Sdo_Segmented_Download_Wrong_Toggle_Aborts` (server side, `CanOpenNodeIntegrationTests.cs:911`). No test sends a wrong toggle to the client (`CanOpenNode.cs:2440`, `:2467`). See C-13 and mutation M2. |

## 3. New findings (by severity)

### C-1 · Block-upload server keeps streaming a sub-block after its session ended — Important

`CanOpenNode.SdoBlock.cs:1114`: `SendNextBlockUploadSubBlock` hands the whole sub-block (up to
127 frames) to `SendOrderedControlFrames(frames)` with **no `shouldStop`**, unlike the download
client (`:600`, the C9 fix). Every path that ends the server session while the batch is still
going leaves the batch running: a client abort (`:659-665`), the server idle timeout
(`OnSdoBlockServerTimeout:1132`), a supersede by a new initiate (`AbortSupersededBlockServerSession:1162`,
`AbortSupersededServerSession`), NMT Stop or reset (`CommunicationProfile.cs:947-961`), and
disposal (`CanOpenNode.cs:924-925`).

Mechanism and consequence. A conforming client that cancels a block upload mid-sub-block (its
own timeout, an application cancel → `CancelSdoClient` sends an abort) and immediately starts
its next transfer with the same server gets the old sub-block's remaining segments interleaved
with the new transfer's frames on `0x580 + id`:
* next transfer is a block upload: the old segments (seqno 2..127, byte 0 = seqno) arrive while
  the new session expects seqno 1 → `SubBlockDamaged` (`:522`), the stale `c = 1` segment makes
  the client confirm and move to `AwaitEnd` (`:527-544`), after which the server's real segments
  are ignored → both sides time out. A second `SendOrderedControlFrames` loop for the new
  session runs concurrently with the old one, so the interleaving is real, not theoretical.
* next transfer is classic: the client is in its initiate phase and checks bytes 1..3 as the
  multiplexer (`CanOpenNode.cs:2368-2369`); a segment whose data bytes 1..3 happen to equal the
  requested index/sub-index and whose seqno reads as `0x43/0x47/0x4B/0x4F` completes the upload
  with payload bytes (`:2394-2402`). Low probability, silent wrong value.

Reachability: a conforming peer triggers the interleaving (abort + restart within the batch's
duration: ≈17 ms at 1 Mbit/s, ≈130 ms at 125 kbit/s for 127 segments). Confidence: high on the
mechanism (both paths read); medium on how often a real master restarts that fast.

Test: none. `A_Block_Download_That_Ends_Mid_SubBlock_Sends_No_More_Segments` covers the client
only; `Sdo_BlockUpload_Server_*` tests never end the session mid-batch.

Fix: give `SdoBlockServerSession` a `volatile bool Ended` set in every teardown path (abort,
timeout, supersede, `AbortServerSessions`, dispose) and pass
`shouldStop: () => Volatile.Read(ref session.Ended)` at `:1114` — the exact shape of the C9 fix.
Test: raw-frame client aborts after segment 1 of a 20-segment sub-block and counts what else
appears on `0x580 + id`.

### C-2 · Classic server abort/stray frames clear the *other* protocol's state only one way — Minor

`SendSdoServerAbort` (`CanOpenNode.cs:2120-2126`) always nulls `_sdoServer`. It is reached for
*any* unrecognised command specifier (`:1788`), including block-protocol control frames that the
block handler declined (`SdoBlock.cs:730`: no block session, or wrong phase) — e.g. a late
`0xA2`/`0xA3`/`0xA1` from the previous block transfer. Such a frame therefore aborts an open
**classic** segmented session with a bogus multiplexer taken from bytes 1..3 of a frame that
carries none (an `0xA2` ack's bytes are ackseq/blksize). Conversely the abort-from-peer branch
(`:1740-1745`) ends only `_sdoServer`; a block session in `AwaitStart`/`AwaitSubBlockAck`/
`AwaitEndResponse` is ended by the block handler first (`SdoBlock.cs:659`), so that direction is
fine.

Reachability: only a buggy or second client (the default SDO channel has one client). Confidence:
high (code). Test: none. Fix: in `HandleSdoServerRequest` answer an unknown specifier with
`0504 0001h` *without* touching `_sdoServer` (abort the open session only when the frame could
belong to it), or at least use index 0/sub 0 for a frame that carries no multiplexer.

### C-3 · Block upload of an empty object never sends a segment; the transfer ends in both timeouts — Minor (open §3 item)

`SdoBlock.cs:1093-1096`: with `Buffer.Length == 0` the loop breaks before building a frame,
`frames` is empty, the phase becomes `AwaitSubBlockAck` and nothing is sent; the client waits
for seqno 1 and times out (`SdoTimeout`, 1 s), the server after `SdoServerTimeout` (5 s). The
classic path handles the same value correctly (`CanOpenNode.cs:1850-1868`, one `n = 7, c = 1`
segment). The block encoding can express it too: one segment `c = 1` with seven unused bytes and
`n = 7` in the end frame — the client side already handles `n = 7` (`:395-403`, offset 7 → 0).

Reachability: any client that asks for `SdoTransferMode.Block` on an empty DOMAIN/string
(including this library's own client). Confidence: high. Test: none
(`Sdo_UploadOfEmptyOdValue_ReturnsEmptyArray_ViaSegmented` is classic only). Fix: in
`SendNextBlockUploadSubBlock`, when `Buffer.Length == 0 && Offset == 0` emit one
`BuildSegment(1, isLastSegment: true, empty)` and set `LastSegmentUnusedBytes = 7`.

### C-4 · Expedited download with e = 1, s = 0 is refused for 1..3-byte objects — Minor (open §3 item)

`SdoFrames.ReadExpeditedPayload:160` returns four bytes when `s = 0`; `HandleServerDownloadInit`
`:1977-1984` then aborts `0607 0012h` for any fixed-width object narrower than four bytes. CiA 301
§7.2.4.3.3 (from memory) defines `e = 1, s = 0` as "expedited, data set size not indicated"; the
widely used reference implementation takes the object's own length in that case. A master that
sends `0x22` (legal, uncommon) cannot write any UNSIGNED8/16 object of this node.

Reachability: a conforming peer (rare encoding). Confidence: high on the code, medium on how
common `0x22` is in the field. Test: none (codec test
`SdoExpeditedPayload_Without_A_Size_Bit_Keeps_All_Four_Bytes` pins the decoder, not the server).
Fix: in `HandleServerDownloadInit`, when `(cs & 0x01) == 0` and `required is 1..4`, take the
first `required` bytes.

### C-5 · Block-upload client treats "size indicated, size 0" as unbounded — Minor

`SdoBlock.cs:361-369` and `:405`: `ReadUploadTotalSize` returns 0 for both `s = 0` and
`s = 1, size = 0`, and the end check is `DeclaredTotalSize > 0 && …`, so a server that announces
zero and then streams data is accepted. The classic client was deliberately changed the other way
in `124130a` ("an announced upload length of zero is a length", `CanOpenNode.cs:2481`). Same
norm, two answers. Reachability: buggy peer. Confidence: high. Test: none for the block side
(`Sdo_Client_Upload_Aborts_When_The_Server_Announces_Zero_And_Sends_Data` is classic). Fix: keep
`SizeIndicated` on `SdoBlockClientSession` and check `SizeIndicated && Offset != Declared`.

### C-6 · Cancel-abort can overtake the last in-flight segment of a cancelled block download — Minor (residual of C9)

`CancelSdoClient` (`CanOpenNode.cs:2231-2237`) sends the abort through its own `Task.Run`
(`SendControlFrame`), while the batch task checks `shouldStop` only *between* frames
(`:2830`). A segment already past the check and the abort are two independent sends; if the abort
reaches the bus first, one segment follows it and the server (which has dropped the session,
`SdoBlock.cs:659-665`) reads that segment as a command specifier — C9's original symptom,
narrowed to one frame. Reachability: a caller cancels mid-sub-block (API use, not misuse).
Confidence: medium (depends on the adapter's send ordering; the Virtual adapter is in-order per
sender so the existing test cannot see it). Fix: chain the cancel abort behind the batch task
(`SendOrderedControlFrames` returns the task) or send the abort from the batch loop when
`shouldStop` turns true.

### C-7 · A guarding reply whose toggle did not alternate is dropped without an indication — Minor (open §3 item; norm from memory)

`NodeGuarding.cs:221-222`. CiA 301 §7.2.8.2.2.1 (from memory): the NMT master indicates a node
guarding event when the response is missing *or the toggle bit does not alternate*. Here the
repeat is silently discarded and the error surfaces only when the life time runs out
(`guardTime × lifeTimeFactor` later). The code comment calls the drop deliberate; the README
(`:625`) says "without a reply whose toggle bit alternated", so docs and code agree with each
other and (if memory is right) disagree with the norm's timing of the indication. Reachability:
a peer that repeats a frame (hostile/buggy) or a bus that delivers a producer's state-change
heartbeat and its guarding reply with the same toggle. Confidence: medium (norm from memory).
Test: none sends a repeated toggle (mutation M6 shows that). Fix: raise `NodeGuardingTimeout`
(or a dedicated event) at once on a non-alternating reply, keep the life-time deadline as is.

### C-8 · `RespondToNodeGuardingRtr = false` also disables producer-side life guarding — Minor/Docs (open §3 item)

`NodeGuarding.cs:244` returns before `OnGuardingPollReceived()` at `:259`, so with the option off
`100Ch/100Dh` life guarding never starts although the OD says it is on. The option's XML doc
(`CanOpenNodeOptions.cs:131-138`) and the README "Precedence" paragraph (`:630-631`,
"additionally gates the reply") speak of the reply only; the README "Life guarding" paragraph
(`:627-629`) and `ICanOpenNode.LifeGuardingEvent` (`:157-165`) promise "guarding starts with the
first RTR received". Reachability: API configuration. Confidence: high. Test: none
(`NodeGuarding_Timeout_FiresWhenSlaveStopsResponding` uses the option but asserts the consumer
side only). Fix: move the option check below `OnGuardingPollReceived()`, or document the
coupling in both places.

### C-9 · Duplicate "block upload start" (0xA3) restarts the server's sub-block in any phase — Minor

`SdoBlock.cs:712-716` matches `0xA3` whenever `_sdoBlockServer is { InDownload: false }`, phase
not checked. In `AwaitSubBlockAck` a duplicate start calls `SendNextBlockUploadSubBlock` again:
with `ResumeSeqno` past the last sent segment the loop emits nothing (harmless), but after a
*partial* ack it re-anchors nothing and re-sends from the current offset with the resumed
numbering — a second batch interleaved with the first (see C-1). Reachability: buggy peer.
Confidence: high. Fix: add `Phase: SdoBlockServerPhase.AwaitStart` to the pattern at `:712`.

### C-10 · Constructor start-up order (open §3 item) — Minor

See section 2. Fix: wrap `PopulateCommunicationProfile`/`ApplyDeviceDescription` into the
`try` that disposes the actor (`CanOpenNode.cs:255-304`), and post the init work (`:311`)
*before* starting the reader (`:306`) — both posts go to the same mailbox, so the order is then
guaranteed. Confidence: high (window), low (probability).

### C-11 · EMCY frames with DLC < 8 are dropped without a trace — Minor (open §3 item)

`CanOpenNode.cs:1665`. CiA 301 §7.2.7.3.1 (from memory) fixes the EMCY DLC at 8, so a short
frame is malformed; dropping it is defensible, but it is the one frame class the node neither
pads (as it does for SDO, `:1722-1736`) nor reports. A device that sends `[code, reg]` with DLC 3
(seen on older firmware) is invisible to the master. Reachability: non-conforming peer.
Confidence: high. Fix: decode what is there (zero-pad to 8, as SDO does) or report once per
producer on `BackgroundExceptionOccurred`; either way say so in the README "Error control"
section. Test: none.

### C-12 · SDO *client* transfers are not gated by the NMT state — Docs

Table 37 says "SDO: no" in Stopped for the node, not for the server role only; `SdoUploadAsync`
/`SdoDownloadAsync` run in Stopped (`CanOpenNode.cs:626-736`, no state check) and the README NMT
table (`:647`) describes the server only ("requests ignored"). Same decision pattern as C14
(`SendSyncAsync`): either gate or document. A master is rarely Stopped, so this is a docs item.
Confidence: high.

### C-13 · Client-side toggle checks are untested (canopenB-M5 still green) — Test gap

`CanOpenNode.cs:2440-2443` (download segment ack) and `:2467-2470` (upload segment). No test
drives a wrong toggle at the client; both would survive `if (false)`. See mutation M2.

### C-14 · `EmcyMessage.ProducerNodeId` is derived from `COB-ID − 080h` even when 1014h was moved — Docs

`CanOpenNode.cs:1666`, event doc `CanOpenEvents.cs:13-14`, `EmcyMessage.cs:46-49`. A peer whose
EMCY COB-ID a master moved (e.g. to `0F5h`) is reported as producer `0x75`, and the per-producer
EMCY backlog (`_pendingEmcyPerProducer`) is keyed on that. Inherent to the pre-defined
connection set; worth one sentence in `EmcyReceived`'s doc. Confidence: high.

## 4. Test quality and gaps

What is good: the SDO suites drive one side with raw frames and assert on the wire
(`FrameTap`), every timer-bound claim in the life-guarding, event-queue and C12 tests runs on
`ManualTimeSource`/`VirtualClock`, and negatives are mostly decided by ordering witnesses (a
later frame on the same path) rather than by sleeping. PR #265 and #267 ran mutation checks and
say so.

Gaps (each named above): C-1 (server batch after abort), C-3 (empty block upload), C-4
(`0x22`), C-5 (block `s = 1, size 0`), C-7 (repeated toggle at the consumer), C-8 (option vs
life guarding), C-9 (duplicate 0xA3), C-11 (short EMCY), C-12 (client in Stopped), C-13 (client
toggle), C-2 (stray block control frame vs. open classic session). Also untested: the
constructor-failure path for a throwing `ApplyDeviceDescription` (C-10) and the
`HandleServerUploadSegmentRequest` toggle abort (server upload direction; only the download
direction has `Sdo_Segmented_Download_Wrong_Toggle_Aborts`).

Brittleness: `DeliverBlockFrameWhilePhase` (`CanOpenSdoCorrectnessTests.cs:567-597`) reaches
private members by name (`HandleIncoming`, `_sdoBlockClients`, `_actor`, `SdoBlockClientPhase`);
a rename silently turns those tests into reflection failures rather than protocol failures.

Timing-gated tests in this slice (wall clock; the quantity the host perturbs is actor/thread-pool
scheduling latency and `Task.Delay` overshoot, #92):

| Test | Clock dependence | Margin |
| --- | --- | --- |
| `CanOpenSdoClientSendFailureTests.An_Answer_After_The_Sdo_Timeout_Does_Not_Revive_The_Transfer` | assumes the 50 ms SDO timer has fired within `Task.Delay(200)` | 150 ms — thin on a loaded runner |
| `….A_Confirmation_That_Arrives_After_The_Sdo_Timeout_Leaves_The_Timeout_Standing` | same 50 ms / 200 ms pair, but the assertion holds either way (comment says so) | safe |
| `….A_Block_Download_That_Ends_Mid_SubBlock_Sends_No_More_Segments` | negative window 300 ms | can only pass falsely |
| `….A_Rejected_Block_Segment_Stops_The_Sub_Block` | negative window 500 ms | can only pass falsely |
| `CanOpenSdoCorrectnessTests.Sdo_BlockUpload_Server_Deadline_Measures_Peer_Idle_Time_Not_The_Whole_Transfer` | 26 × `Task.Delay(100)` against a 2 s server timeout (documented margin 1.9 s per gap; #240 recorded one CI failure) | documented |
| `CanOpenNodeIntegrationTests.Tpdo_Emission_UnderConcurrentOdWrites_NeverTears` | `Task.Delay(200)` to let the pump drain (documented, no barrier possible) | can only pass falsely |
| `CanOpenCommunicationProfileTests.Overlapping_Emcys_Reach_The_Bus_In_The_Order_The_Register_Was_Written` | `Task.Delay(200)` negative ("second EMCY waits") | can only pass falsely |
| `….A_Guarding_Reply_To_A_Poll_Queued_Behind_A_Reset_Stays_Behind_The_Bootup`, `….A_Producer_Tick_Due_During_ApplicationReset_…` | `Task.Delay(100)` negatives | can only pass falsely |
| `….A_Save_During_An_Nmt_Reset_Stores_All_Restored_Values_Not_A_Mix` | `Thread.Sleep(300)` "long enough for a save that is not held back" (a negative) | can only pass falsely |
| `….RemoveHeartbeatConsumer_Clears_The_1016h_Slot_And_The_Timeout_Stops` | 100 ms vs 300 ms real timers, but ordering on one due-time-sorted list | safe |
| `CanOpenDisposeTests.A_Node_Can_Be_Disposed_From_Its_Own_Subscriber_Without_Waiting_For_Itself` | asserts elapsed < 1.5 s against a 2 s join | 1.5 s — only a stalled host >1.5 s fails it |
| `CanOpenDisposeTests.Events_Queued_Behind_A_Subscriber_That_Disposes_The_Node_Are_Not_Delivered`, `…_That_Outlasts_Dispose_…` | `Thread.Sleep(300)`/`Task.Delay(300)` assumed *sufficient* for the second frame to be queued (a positive assumption), then 300 ms negative; the "outlasts" variants additionally pay the 2 s join | the positive half is the perturbable one |
| `CanOpenDisposeTests.A_Reset_Does_Not_Announce…`, `A_Reset_Does_Not_Call_The_Next_Subscriber…`, `The_Next_Subscriber_Of_An_Event…`, `The_Next_Subscriber_Of_A_Background_Exception…`, `A_Failure_After_The_Disposal_Has_Finished_Is_Not_Reported` | 300 ms negative windows (two of them after a 2 s join) | can only pass falsely |
| `CanOpenBlockAndGuardingTests.NodeGuarding_Timeout_FiresWhenSlaveStopsResponding`, `CanOpenNodeIntegrationTests.Heartbeat_Consumer_FiresTimeoutWhenPeerGoesSilent` | positive waits bounded by 5 s | safe |

Everything in CanOpenCriticalEventQueueTests, CanOpenLifeGuardingAndNmtStateTests,
HeartbeatConsumer/ProducerTests runs on a manual clock.

## 5. Mutations proposed (one-line each; the coordinator runs them)

| # | Mutation (exact) | Expected |
| --- | --- | --- |
| M1 | `CanOpenNode.SdoBlock.cs:600`: replace `() => session.Tcs.Task.IsCompleted` with `() => false` | **red**: `A_Block_Download_That_Ends_Mid_SubBlock_Sends_No_More_Segments` (both rows) — pins the C9 fix |
| M2 | `CanOpenNode.cs:2467`: `if (toggle != session.Toggle)` → `if (false)` | **expected green = gap** (C-13, canopenB-M5 still open); likewise `:2440` |
| M3 | `CanOpenNode.cs:1976`: `var payload = SdoFrames.ReadExpeditedPayload(data);` → `var payload = (cs & 0x01) == 0 ? Array.Empty<byte>() : SdoFrames.ReadExpeditedPayload(data);` | **expected green = gap** (C-4: no test sends `e = 1, s = 0`) |
| M4 | `CanOpenNode.cs:886`: `if (_actor.IsOnCurrentActor) CleanUpOnActor(); else _actor.Post(CleanUpOnActor);` → `_actor.Post(CleanUpOnActor);` | **red**: `A_Node_Can_Be_Disposed_From_Its_Own_Subscriber_Without_Waiting_For_Itself` (ApplicationReset rows, `transferEnded`) — pins Q9 |
| M5 | `CanOpenNode.cs:1099`: delete `\|\| newest.Value.Ordinal <= waiting.Value.Ordinal` (keep `is not { } newest`) | **red**: `A_Heartbeat_Timeout_Is_Not_Folded_Across_A_Heartbeat_From_The_Same_Producer`, `A_Recurring_Emcy_Is_Not_Folded_Into_An_Earlier_One_Across_A_Different_One` — pins the queue ordering guarantee |
| M6 | `CanOpenNode.NodeGuarding.cs:221-222`: delete the two lines (`if (consumer.HasSeenResponse && toggle == consumer.LastToggle) return;`) | **expected green = gap** (C-7: no test repeats a toggle at the consumer; `NodeGuarding_HappyPath_TogglesBetweenReplies` asks only that both values appear) |

## 6. Doc drift

* README "Layout" (`:1539-1570`) omits nine files: `CanOpenDeviceDescription.cs`,
  `CanOpenNode.DeviceDescription.cs`, `CanOpenNode.Lss.cs`, `CanOpenNode.Mpdo.cs`,
  `CanOpenNode.PdoTiming.cs`, `CanOpenNodeExtensions.cs`, `CanOpenNodeProfile.cs`,
  `CanOpenTransportException.cs`, `PeerSdoAccessException.cs` (unchanged §3 item).
* README "Dependencies" (`:1683`) omits `EdsDcfNet` (`CanKit.Pro.CANopen.csproj:10`) (unchanged).
* README "Not built" (`:109-168`) still does not name `1003h` (pre-defined error field),
  `1015h` (EMCY inhibit time), `1029h` (error behaviour); `1019h` is now built (FR-CO-052), so
  that part of the §3 item is resolved.
* README "Error control" `:606`: "`HeartbeatReceived` reports every heartbeat and boot-up" — since
  #255/#266 a reserved state byte or bit 7 is *not* reported (`CanOpenNode.cs:1704`); the event
  doc (`CanOpenEvents.cs:16-18`) says so, the README does not.
* `OdDataType.Domain` XML doc (`OdEntry.cs:72-73`): "Always exchanged via SDO segmented transfer
  regardless of length" — the server sends a 1..4-byte DOMAIN expedited (`CanOpenNode.cs:1835`).
* `CanOpenNodeOptions.RespondToNodeGuardingRtr` and README "Precedence": the option also
  switches off life guarding (C-8).
* README NMT table `:647`: SDO row describes the server only; the client is ungated (C-12).
* `EmcyReceivedEventArgs`/`EmcyMessage.ProducerNodeId`: derived id is wrong for a moved
  `1014h` (C-14).
* `SdoFrames` class remark (`:12-14`, "frames shorter than eight bytes are still accepted") is
  now only half true after #203 — segments are refused (`SegmentIsComplete`); the remark predates it.

## 7. What is done well

* Attribution before state (`#18`, `#167`): every client frame is matched by phase, specifier and
  — in the initiate phase — multiplexer before a deadline moves; the tests prove each case on the
  wire.
* Send-outcome tracking (`#197`): only the latest send decides; lost echoes, late confirmations
  and disposal are each covered with a controllable bus.
* The block receivers' one-confirm-per-sub-block rule (`#39`) with original-seqno retransmission
  and the `#134` blksize rule are implemented symmetrically on both sides and tested on both.
* The event queue: critical events never dropped, bounded by folding with a correct
  "nothing in between" rule, including reconciliation after a drop — eleven targeted tests on a
  manual clock.
* Dispose/DisposeAsync: reentrancy from every callback thread handled by thread markers rather
  than timeouts; the actor's `FinalDrain` guarantees the posted cleanup runs.
* `ObjectDictionary`: write gate + value lock, validation as one transaction, proven by a
  two-thread race test.

## 8. Open questions for the maintainer

1. **C-1** — fix the block-upload server's batch with a session token (mirror of C9)? Fold the
   empty-object case (C-3) into the same change, since both live in `SendNextBlockUploadSubBlock`?
2. **C-4** — accept `e = 1, s = 0` by taking the object's own width (reference-implementation
   behaviour), or keep refusing with `0607 0012h` and document it under "Not built"?
3. **C-7** — indicate a non-alternating guarding toggle at once (norm, from memory) or keep the
   silent drop and state the deviation in the README?
4. **C-8** — decouple life guarding from `RespondToNodeGuardingRtr`, or document the coupling?
5. **C-12** — gate the SDO client in Stopped (Table 37) or document it as tool behaviour, as was
   decided for `SendSyncAsync` (#254)?
6. **C-11** — pad a short EMCY (as SDO frames are padded) or report it; which?
