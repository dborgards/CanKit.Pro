# CanKit.Pro.Uds

Unified Diagnostic Services (UDS, ISO 14229-1:2020) client for CanKit.Pro. Sits
directly on top of `CanKit.Pro.IsoTp`'s `IIsoTpChannel`, so anything that speaks ISO-TP
(virtual loopback, PCAN, SocketCAN, Vector, Kvaser, ZLG, ControlCAN, ...) can be driven with
the same client.

Status: 1.0.0 – 1.2.3 are **withdrawn from nuget.org** — they were published as stable before
the API had been reviewed. **1.3.0 will be the first release whose API is stable**. Until it is
tagged there is no listed version to install, so the `dotnet add package` line below resolves
nothing and the withdrawn releases come back only on an exact version pin. The
public surface can still change until then — `SendRawAsync`, the timing options and the
NRC-mapping types most of all. See
[Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

## Service coverage (SRS FR-UDS-001..012)

| SRS ID | Service | MVP support |
|---|---|---|
| FR-UDS-001 | 0x10 DiagnosticSessionControl | Yes — `DiagnosticSessionControlAsync(UdsSessionType | byte)` |
| FR-UDS-002 | 0x22 ReadDataByIdentifier | Yes — single-DID `ReadDataByIdentifierAsync(ushort)` |
| FR-UDS-003 | 0x2E WriteDataByIdentifier | Yes — `WriteDataByIdentifierAsync(ushort, ReadOnlyMemory<byte>)` |
| FR-UDS-004 | 0x31 RoutineControl (Start/Stop/RequestResults) | Yes — `RoutineControlAsync(UdsRoutineControlType, ushort, ...)` |
| FR-UDS-005 | 0x11 ECUReset | Yes — `EcuResetAsync(UdsEcuResetType)` |
| FR-UDS-006 | 0x27 SecurityAccess (seed/key with caller-supplied algorithm) | Yes — `SecurityAccessAsync(byte, Func<byte[], byte[]>)` |
| FR-UDS-007 | 0x3E TesterPresent + keep-alive | Yes — `TesterPresentAsync(bool)` + `StartTesterPresentKeepAlive(TimeSpan?)` |
| FR-UDS-008 | P2 / P2* timing | Yes — configurable `UdsClientOptions.P2ClientMax` / `P2StarClientMax`; `UdsTimeoutException` on expiry |
| FR-UDS-009 | NRC 0x78 responsePending | Yes — client stays inside P2* while the ECU keeps replying 0x78, bounded by `MaxResponsePendingCount` |
| FR-UDS-010 | Structured NRC | Yes — `UdsNegativeResponseException` carries requested SID + raw NRC byte + named enum |
| FR-UDS-011 | Multi-DID `0x22` | Yes (SHOULD) — `ReadDataByIdentifierAsync(IReadOnlyList<ushort>, IReadOnlyDictionary<ushort, int>)` (caller supplies per-DID `dataRecord` lengths per ISO 14229-1 §9.3.4.4) |
| FR-UDS-012 | 0x34 / 0x35 / 0x36 / 0x37 upload/download | Yes (COULD) — `RequestDownloadAsync` / `RequestUploadAsync` / `TransferDataAsync(byte bsc, ReadOnlyMemory<byte>)` / `RequestTransferExitAsync(ReadOnlyMemory<byte>)`, plus one-shot `DownloadAsync` that negotiates `maxNumberOfBlockLength`, chunks the payload and walks the BSC with `0xFF → 0x00` wrap |

## Quick start

```csharp
using CanKit.Core;
using CanKit.Abstractions.API.Can.Definitions;
using CanKit.Pro.IsoTp;
using CanKit.Pro.Uds;
using IsoTpFactory = CanKit.Pro.IsoTp.IsoTp;

using var bus = CanBus.Open(
    "virtual://demo/0",
    cfg => cfg.SetProtocolMode(CanProtocolMode.Can20).Baud(500_000));

var endpoint = IsoTpEndpoint.Normal(txCanId: 0x7E0, rxCanId: 0x7E8);
using var isoTp = IsoTpFactory.Open(bus, endpoint);
using var uds = UdsClient.Create(isoTp, new UdsClientOptions
{
    P2ClientMax = TimeSpan.FromMilliseconds(50),
    P2StarClientMax = TimeSpan.FromSeconds(2),
});

await uds.DiagnosticSessionControlAsync(UdsSessionType.Extended);
using var _ = uds.StartTesterPresentKeepAlive();

byte[] vin = await uds.ReadDataByIdentifierAsync(0xF190);
await uds.SecurityAccessAsync(
    requestSeedLevel: 0x01,
    computeKey: seed => YourAlgorithm.ComputeKey(seed));

// One-shot download (FR-UDS-012): negotiate maxNumberOfBlockLength, chunk the payload,
// walk the block-sequence counter (0x01..0xFF, wraps to 0x00), close with RequestTransferExit.
await uds.DownloadAsync(
    dataFormatIdentifier: 0x00,                            // no compression / no encryption
    addressAndLengthFormatIdentifier: 0x44,                // 4-byte address, 4-byte size
    memoryAddress: new byte[] { 0x00, 0x10, 0x00, 0x00 },
    memorySize:    new byte[] { 0x00, 0x00, 0x02, 0x00 }, // 512 bytes
    data: firmwareChunk);
```

## Design notes

* One client = one tester ↔ ECU relationship. Requests are serialized through an internal
  `SemaphoreSlim` so at most one UDS transaction is on the wire (ISO 14229-1 §7.3).
* The client never buffers responses; each `ReceiveAsync` is a bounded wait derived from the
  active timing budget (P2 first, then P2* after every 0x78).
* Stray or mismatched responses received while a request is pending are silently discarded;
  the wait continues inside the *same* budget so a chatty ECU cannot extend a P2 window.
* P2 and P2* end with the **first frame** of the response (ISO 14229-2), not its last: a
  multi-frame response whose First Frame arrived inside the budget — and whose first byte is
  this request's positive response SID — is waited for beyond it, and the remainder of the
  transfer is bounded by the ISO-TP `NCr` timer instead. A 4 KB record paced at STmin 5 ms
  takes seconds on the wire and is not a P2 timeout. A transfer for another service does not
  extend the budget: the peer is busy with it, so the answer cannot start in time anyway. A
  response that began before the request's last frame was handed to the driver answers an
  earlier request and is a stray: a peer answers only a complete request. The bound is the
  channel's handoff instant, not the transmit stamp — that one is "no later than the driver
  accepted the frame", and a fast peer can be stamped before it (#146) — and not a reading
  taken before entering the channel, whose transmission would leave a window.
* NRC 0x21 (busyRepeatRequest) is what it says: the request is repeated, up to
  `UdsClientOptions.MaxBusyRepeatRequests` times (default 3, after `BusyRepeatRequestDelay`,
  default zero), each with a fresh P2; the negative response surfaces only once the repeats are
  used up (#57).
* `UdsTimeoutException.Elapsed` is the budget of the timer that expired (P2 or P2*) on every
  path, never a measurement of how late the client noticed (#57).
* `DiagnosticSessionControlAsync(byte)` rejects 0x00 and any value with bit 7 set rather than
  masking it; `SendRawAsync` sends a request with suppressPosRspMsgIndication set without
  waiting for a response and returns empty (#57).
* Functional addressing: `UdsFunctionalClient` wraps an `IsoTpFunctionalClient` — one request on
  the functional identifier, every ECU's Single-Frame answer collected within a window and read
  as UDS (`UdsFunctionalResponse` with source identifier, bytes, `IsNegative` and the NRC). The
  keep-alive to everyone is `TesterPresentAsync()` (`3E 80`, not collected for) (#57).
* `Dispose` waits up to five seconds for a request in flight to release the request lock; a
  holder that outlasts the wait keeps an undisposed semaphore, so its eventual release does not
  throw into an operation that was merely slow (#57).
* `SecurityAccessAsync` treats a seed of all zeroes — of any length, including zero — as
  *already unlocked* (ISO 14229-1 §9.4.5.3) and returns without sending a key; the ECU would
  answer a key for that seed with NRC 0x24.
* Transport-layer failures (ISO-TP timeout, overflow, WFTmax, etc.) are re-thrown as their
  original `IsoTpException` subclasses so callers can distinguish "ECU said no" from "wire
  broken".

## Documentation

* Requirements: `docs/requirements/SRS-CanKit.Pro.md` §4.3.1
* Architecture: `docs/architecture/arc42-CanKit.Pro.md` §6.5 (e) — UDS request/response with NRC 0x78

## Install

```bash
dotnet add package CanKit.Pro.Uds

# plus a CanKit adapter for the hardware you actually talk to, e.g.
dotnet add package CanKit.Adapter.Virtual   # loopback, no hardware
```

Dependencies: `CanKit.Abstractions`, `CanKit.Pro.Actor`, `CanKit.Pro.IsoTp`, `CanKit.Pro.RawCan`, `CanKit.Pro.Reliability`.

Part of [CanKit.Pro](https://github.com/dborgards/CanKit.Pro) — higher CAN protocol layers
built **on top of** [CanKit](https://github.com/pkuyo/CanKit), which is consumed as a NuGet
package rather than forked.

## License

MIT — see [LICENSE](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE).
CanKit itself is a separate project licensed under Apache-2.0; see
[THIRD-PARTY-NOTICES.md](https://github.com/dborgards/CanKit.Pro/blob/main/THIRD-PARTY-NOTICES.md).
