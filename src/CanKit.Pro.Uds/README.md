# CanKit.Pro.Uds

Unified Diagnostic Services (UDS, ISO 14229-1:2020) client for CanKit.Pro. Sits
directly on top of `CanKit.Pro.IsoTp`'s `IIsoTpChannel`, so anything that speaks ISO-TP
(virtual loopback, PCAN, SocketCAN, Vector, Kvaser, ZLG, ControlCAN, ...) can be driven with
the same client.

Status: **1.3.0 is the first stable release**: from 1.3.0 on the public API follows SemVer, so a breaking
change costs a major version. 1.0.0 – 1.2.3 were published as stable before the API had been reviewed
against the specifications; they are unlisted and deprecated on nuget.org and should not be used. See
[Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

## What is validated, and what is not

**Validated:** Each service listed under *Service coverage* (some of them thinly), negative-response handling and the timing options, by the test suite in `tests/CanKit.Pro.Tests`, against `SimulatedUdsEcu` — a scripted ECU simulation in this repository whose responses each test defines — over `CanKit.Adapter.Virtual`. The thinnest coverage is the mapping of negative response codes onto `UdsNegativeResponseCode`: only a few codes are asserted by name. `SendRawAsync` and the timing options are exercised heavily, but likewise only against the simulation.

**Not validated:** Any real ECU, whose timing, negative-response usage and deviations from the standard the simulation does not reproduce, and conformance as a tester would judge it. Nothing in this package has run against real CAN hardware, a conformance tester or a third-party implementation: the test project references `CanKit.Adapter.Virtual` and no hardware adapter.

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
// Uncompressed and unencrypted (DFI 0x00), memorySize must be the payload's length: a
// mismatch is refused before RequestDownload.
byte[] firmware = File.ReadAllBytes("app.bin");
int size = firmware.Length;
await uds.DownloadAsync(
    dataFormatIdentifier: 0x00,                            // no compression / no encryption
    addressAndLengthFormatIdentifier: 0x44,                // 4-byte address, 4-byte size
    memoryAddress: new byte[] { 0x00, 0x10, 0x00, 0x00 },
    memorySize:    new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size },
    data: firmware);
```

## Design notes

* One client = one tester ↔ ECU relationship. Requests are serialized through an internal
  `SemaphoreSlim` so at most one UDS transaction is on the wire (ISO 14229-1 §7.3).
* The client never buffers responses; each `ReceiveAsync` is a bounded wait derived from the
  active timing budget (P2 first, then P2* after every 0x78).
* Stray or mismatched responses received while a request is pending are silently discarded;
  the wait continues inside the *same* budget so a chatty ECU cannot extend a P2 window.
  "Mismatched" is decided on the positive response SID *and* on the request bytes the
  service's positive response echoes — the sub-function, the DID, the routine identifier,
  the block sequence counter — so a late answer to the previous request for the same service
  (one that timed out or was cancelled) with another DID is a stray, not this request's answer
  and not a protocol error (#340). What the echo cannot tell apart is the same request
  repeated: when a polled DID's previous request timed out and its answer arrives after the
  next request for that DID went out, that answer is taken as the next request's, and the
  previous request's late NRC 0x78 restarts P2\* for the next one. Setting
  `UdsClientOptions.QuarantineLateResponses = true` (default `false`) closes that: a request
  that times out or is cancelled quarantines its service for the P2\* budget in force, the
  next request for the service is sent only once that window has passed, and what arrives
  meanwhile — the late answer, its 0x78 — is discarded; a 0x78 inside the window moves its
  end out by P2\*. Requests for other services are not held up. The price is up to P2\* of
  delay for that next request; an answer later still than the window is taken as before. A
  conformant server never answers later than the P2Server_max / P2\*Server_max it declares,
  and the client applies those (next point).
* A successful `DiagnosticSessionControlAsync` sets the client's budgets from the session
  parameter record the server returns — P2Server_max (bytes 0..1, 1 ms units) and
  P2\*Server_max (bytes 2..3, 10 ms units): P2 becomes the longer of `P2ClientMax` and
  P2Server_max + `UdsClientOptions.SessionTimingMargin` (50 ms, the network latency ISO
  14229-2 calls ΔP2), P2\* likewise the longer of `P2StarClientMax` and P2\*Server_max + the
  margin. A budget you configured is only ever lengthened, never shortened, so the
  `P2ClientMax = 50 ms` of the quick start still holds in a session that declares less, and a
  programming session declaring P2\*Server_max = 10 s is waited for the 10 s it asks for. Each
  successful session change starts again from the configured values (a record shorter than
  4 bytes leaves them), and a successful `EcuResetAsync` returns to them; `Options` keeps
  reporting what was configured (#340).
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
  waiting for a response and returns empty. A suppressed send may still draw a negative
  response, up to P2 after it: the next request for the same service waits that window out
  rather than taking the negative response as its own, one window per service (#57).
* Functional addressing: `UdsFunctionalClient` wraps an `IsoTpFunctionalClient` — one request on
  the functional identifier, every ECU's Single-Frame answer collected within a window and read
  as UDS (`UdsFunctionalResponse` with source identifier, bytes, `IsNegative` and the NRC). Only
  answers correlated to the request are attributed: a positive response echoing the request's
  leading bytes (sub-function, DID, routine identifier, block counter, mode of operation,
  memory address and size), or a negative response naming the service; for a service whose
  positive response echoes nothing of the request (0x14, 0x23, 0x34, 0x35, 0x37, 0x84) the
  service identifier is the whole correlation, and another tester's answer to the same
  service inside the window is not told apart. An NRC 0x78 in the window is returned as a
  negative response like any other, although that ECU's final answer is still to come; filter
  `NegativeResponseCode == 0x78` out when counting refusals. Calls run one at a time. Every send starts a listener for its service
  that stays up for the ECUs' P2 (`Create`'s `responseWindow`, default P2) and moves the
  window out by P2* (`responsePendingWindow`, default P2*) on each NRC 0x78 it hears, so
  nothing in the window goes unobserved — a suppressed send included — and a call for that
  service waits the window out before it collects, rather than taking a late answer to the
  earlier request as its own. A read for more than one DID is refused, a Single Frame holding
  no more. The keep-alive to everyone is `TesterPresentAsync()` (`3E 80`, not collected for)
  (#57).
* `Dispose` cancels the request in flight and the keep-alive, then waits up to five seconds for
  the keep-alive loop to end and up to five more for the request to release the request lock —
  ten in all where both outlast the cancellation. A holder that outlasts the wait keeps an
  undisposed semaphore, so its eventual release does not throw into an operation that was
  merely slow (#57). `await uds.DisposeAsync()` (`UdsClientExtensions`) does the same without
  holding a thread while it waits (#271).
* `SecurityAccessAsync` treats a seed of all zeroes — of any length, including zero — as
  *already unlocked* (ISO 14229-1 §9.4.5.3) and returns without sending a key; the ECU would
  answer a key for that seed with NRC 0x24.
* Transport-layer failures are re-thrown as they are, never wrapped: the ISO-TP protocol errors
  (timeout, overflow, WFTmax, etc.) as their `IsoTpException` subclasses, so callers can tell
  "ECU said no" from "wire broken". A channel disposed during a send surfaces
  `ObjectDisposedException`, one disposed during a receive `InvalidOperationException`, and a
  failed driver call the bus layer's own exception; those are not `IsoTpException`s
  (`UdsException` says the same).

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
