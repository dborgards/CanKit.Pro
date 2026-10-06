# Echo behaviour measured on hardware (#249)

Dated record, 5 October 2026, Windows, CanKit 0.5.6 adapters, measured with
`tools/CanKit.Pro.EchoProbe` against a second adapter as peer. The raw probe reports are in
[`echo-probe/`](echo-probe/). Vector was not available and is **not measured**.

| Adapter | Mode | Own frames visible to A | Flagged `IsEcho` | `SendConfirmedAsync` | J1939 claim |
| --- | --- | --- | --- | --- | --- |
| PCAN USB Pro FD, classic | Normal | 0/5 | - | confirmed, approximated | claims |
| PCAN USB Pro FD, classic | Echo | 5/5 | **0 of 5** | **Timeout** (1 s, three of three) | **NotClaimed** |
| PCAN USB Pro FD, FD | Normal | 0/5 | - | confirmed, approximated | not run |
| PCAN USB Pro FD, FD | Echo | 5/5 | **0 of 5** | **Timeout** (three of three) | not run |
| Kvaser Leaf Light v2 | Normal | 0/5 | - | confirmed, approximated | claims |
| Kvaser Leaf Light v2 | Echo | **0/5** | - | **Timeout** (three of three) | **NotClaimed** |

In every row the peer B saw all frames, so the bus carried them and the rows measure A.

## What this settles

- **PCAN**: in Echo mode the adapter hands back its own frames *unflagged*. The contract "Echo mode
  delivers flagged echoes" does not hold, and every confirmed send times out. Normal mode has no
  echo and works with the approximated confirmation.
- **Kvaser**: the review assumed flagged echoes (`canMSG_TXACK`). The hardware does produce them,
  but CanKit 0.5.6 never hears about them: `KvaserBus.cs` registers the notify callback with
  `canNOTIFY_RX` only, and TX acknowledgements need `canNOTIFY_TX` (0x2). With mask 0x3 the probe
  saw three callbacks, with 0x1 none. The echoes stay queued and surface only when a foreign
  frame arrives, so on a quiet bus Echo mode delivers nothing. (The "late but flagged on a busy
  bus" half is read from the source, not measured.) See `docs/upstream-candidates.md`.
- **Vector**: not measured. From the adapter source, Vector Classic drops its own TX event in
  Echo mode and delivers it *flagged* in Normal mode; Vector FD never delivers it.

## What CanKit.Pro does about it

- **J1939** no longer decides "this bus echoes" by `WorkMode` alone. A claim sent on a bus that
  declares `CanFeature.Echo` outside Echo mode leaves a marker that only a *flagged* frame may
  spend, for a bounded time, so Vector Classic in Normal mode no longer arbitrates its own claim
  against itself. Tested in the `EchoWorld.FlaggedNormal` world; the `FlaggedOnly` guard and the
  recording are each mutation-checked.
- **RawCan** keeps the two-condition rule (`CanFeature.Echo` and `WorkMode == Echo`): with
  those adapters Echo mode cannot be made to confirm without a content-based guess for unflagged
  frames, which would also swallow a peer's identical frame. The contract now says so, and names
  Normal mode as the supported way to use PCAN and Vector (and Kvaser until upstream changes).
