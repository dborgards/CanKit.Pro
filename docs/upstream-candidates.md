# Changes worth sending upstream to CanKit

CanKit.Pro consumes [CanKit](https://github.com/pkuyo/CanKit) from nuget.org and deliberately does
not fork it. Its predecessor, [CanKit.Pro.legacy](https://github.com/dborgards/CanKit.Pro.legacy),
*was* a fork, and it improved CanKit itself along the way. Those improvements stayed behind under
Apache-2.0 when the product moved here, which is correct — but it leaves them stranded in a fork
nobody consumes.

This page lists them so they can be offered upstream as pull requests rather than quietly lost.
None of them is required for CanKit.Pro to build today; the first is the only one CanKit.Pro
actively works around.

## 1. Protocol error codes (worked around here)

`CanKitErrorCode` reserves the 6000 range for transport and protocol errors and defines
`TransportOperationFailed = 6001`. The fork added four more, and CanKit.Pro's ISO-TP, J1939-TP,
CANopen, J1939 and UDS layers all report them:

```csharp
ProtocolTimeout          = 6002   // ISO-TP N_As/N_Bs/N_Cr, J1939-TP session timeout, UDS P2/P2*
ProtocolPeerAbort        = 6003   // ISO-TP Overflow/WFTmax, J1939-TP Abort, CANopen SDO abort
ProtocolNegativeResponse = 6004   // UDS 0x7F
AddressClaimFailed       = 6005   // J1939 address claim lost with no arbitrary address available
```

Until they exist upstream, `CanKit.Pro.RawCan.ProtocolErrorCodes` declares them as constants of the
enum type with these exact values, so the numbers callers observe are already the ones an upstream
adoption would produce. Adopting them upstream makes that file redundant and nothing else changes.

These codes are useful beyond CanKit.Pro: any transport or protocol layer built on CanKit needs to
distinguish "the peer aborted" from "we timed out" from "the driver refused the frame".

## 2. Frame ownership and lifetime

The fork made the frame-ownership contract explicit and enforced it (arc42 §8.1, ADR-9):

- `CanFrame.Duplicate(IBufferAllocator)` — an independent copy over a freshly rented buffer, so one
  consumer's disposal cannot invalidate another's frame.
- `CanFrame.Dispose()` honouring `OwnMemory` instead of disposing unconditionally.
- `VirtualBusHub.Broadcast` handing each recipient its own copy, and `VirtualBus.InternalDeliver`
  disposing frames it drops on a filter miss rather than leaking their pooled buffers.

This is the difference between "multiple consumers per bus happens to work" and "multiple consumers
per bus is a contract", which is what any demultiplexing layer needs.

## 3. Loopback adapter fidelity

- `CanKit.Adapter.Virtual` declaring `CanFeature.Echo` in `StaticFeatures`. It genuinely has the
  capability through `ChannelWorkMode.Echo`; not declaring it means capability-gated echo behaviour
  cannot be exercised on hardware-free CI at all.
- Marking its self-echo with `IsEcho = true`, so consumers can tell an echo from bus traffic —
  without it, any TX-confirm implementation on top of CanKit silently cannot match echoes.
- `VirtualBusHub` registry rework: atomic join, and removing a hub when its last member leaves
  (the registry otherwise grows without bound across sessions).

## 4. Registry thread safety

`CanRegistry` was reworked to be safe under parallel late registration while readers call
`TryOpenEndPoint`/`EnumerateEndPoints` on the shared singleton (NFR-008), and
`RegisterEndPoint`/`RawEndpointRegistration` grew alongside it. Its stress test —
`RawCanConcurrencyTests.CanRegistry_Parallel_Registration_And_Readers_Do_Not_Race` — stayed in the
legacy repository rather than moving here: it drives CanKit's registry through internals the
published package does not expose, so it is a test of CanKit. It should travel with this change.

CanKit.Pro kept the half of that file it actually owns, the demultiplex-service stress test.

## 5. Assorted core and adapter fixes

- `IPeriodicTx.Faulted`, so a periodic transmit that starts failing is observable rather than
  silently stopping.
- `AsyncFramePipe`, `SoftwarePeriodicTx` and `PreciseDelay` robustness work, including falling back
  to a coarse sleep when `clock_nanosleep` keeps failing.
- SocketCAN: treating `errno 0` as a missing `ctrlmode` on open, and aligning BCM operations with
  the Linux API.
- ZLG and ControlCAN: freeing the auto-send index when a `PeriodicTx` constructor throws.

## How to use this list

Each item is a self-contained change with its own commits in the legacy repository's `develop`
branch. Whoever opens the pull requests should take the commits from there rather than re-deriving
them — the tests came with them.
