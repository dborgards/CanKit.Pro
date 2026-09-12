# CanKit.Pro.Addressing

CAN-ID addressing helpers for [CanKit](https://github.com/pkuyo/CanKit) (arc42 "Adressierungs-
Helfer"; SRS FR-RAW-040/041): validated 11-bit/29-bit CAN ID construction, J1939 PGN/Priority/
PDU-Format/Source-Address composition and decomposition, J1939 NAME field accessors, and PGN
classification helpers, as pure, dependency-free helper functions — no dependency on any other
CanKit package.

Status: 1.0.0 – 1.2.3 are **withdrawn from nuget.org** — they were published as stable before
the API had been reviewed. **1.3.0 will be the first release whose API is stable**; until it is
tagged there is no listed version to install, so the `dotnet add package` line below resolves
nothing and the withdrawn releases come back only on an exact version pin. The public surface
can still change until then. See [Versioning](https://github.com/dborgards/CanKit.Pro/blob/main/docs/decisions/0001-versioning-and-api-stability.md).

This generalizes logic that previously only existed as one hard-coded case inside
`IsoTpEndpoint.CreateNormalFixed` (a single fixed diagnostics PGN) into reusable helpers any
protocol layer (ISO-TP, J1939, CANopen, ...) can call directly.

```csharp
using CanKit.Pro.Addressing;

// Validated 11/29-bit ID construction (FR-RAW-040)
CanIdRange.ValidateStandard(0x7FF);   // ok
CanIdRange.ValidateStandard(0x800);   // throws ArgumentOutOfRangeException

// J1939: build a 29-bit ID from priority/PGN/source/destination
var id = J1939Id.ComposePgn(priority: 3, pgn: 0xFED9, sourceAddress: 0x17);

// J1939: decompose a received 29-bit ID
var fields = J1939Id.Decompose(id);
fields.Priority;            // 3
fields.Pgn;                 // 0xFED9
fields.SourceAddress;       // 0x17
fields.IsPdu1;               // whether PS is a destination address or a Group Extension
fields.DestinationAddress;  // null for PDU2 (broadcast-only) PGNs

// J1939: classify common PGNs and TP.CM BAM control bytes
J1939Pgn.IsRequest(0xEA00);                   // true
J1939Pgn.IsTransportCm(J1939Pgn.TpCm);        // true
J1939Pgn.IsBam(J1939Pgn.TpCm, 0x20);          // true; BAM is a TP.CM payload control byte

// J1939: compose/decompose a 64-bit NAME and compare address-claim priority
var name = new J1939Name(
    identityNumber: 0x155555,
    manufacturerCode: 0x5AA,
    ecuInstance: 0x5,
    functionInstance: 0x12,
    function: 0xAB,
    reserved: false,
    vehicleSystem: 0x35,
    vehicleSystemInstance: 0xA,
    industryGroup: 0x3,
    arbitraryAddressCapable: true);
var sameName = J1939Name.Decompose(name.Value);
J1939Name.CompareClaimPriority(name, sameName); // 0; lower unsigned NAME wins address claiming
```

`CanKit.Pro.RawCan`'s `CanIdFilter` also gained an `Overlaps(CanIdFilter other)` method and
`ICanBusService.FindOverlappingFilterSubscriptions()` (FR-RAW-041, Should): a diagnostic to catch
misconfigured protocol instances whose ID-range/mask subscriptions were meant to be disjoint but
overlap.

## Install

```bash
dotnet add package CanKit.Pro.Addressing
```

No dependencies beyond the .NET base class library.

Part of [CanKit.Pro](https://github.com/dborgards/CanKit.Pro) — higher CAN protocol layers
built **on top of** [CanKit](https://github.com/pkuyo/CanKit), which is consumed as a NuGet
package rather than forked.

## License

MIT — see [LICENSE](https://github.com/dborgards/CanKit.Pro/blob/main/LICENSE).
CanKit itself is a separate project licensed under Apache-2.0; see
[THIRD-PARTY-NOTICES.md](https://github.com/dborgards/CanKit.Pro/blob/main/THIRD-PARTY-NOTICES.md).
