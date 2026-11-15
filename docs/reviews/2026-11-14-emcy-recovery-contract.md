# Optional bus-off recovery indication (#423)

Basis: user-supplied `301v04020008.pdf`, CiA 301 V4.2.0, §7.2.7.1, Table 26
(printed pp. 65–66) and Figure 37 (p. 67). The supplied copy includes corrigenda.
Table 26 calls 8140h "recovered from bus off". Codes beyond 0000h and 1000h are
optional. Figure 37 describes new errors, partial repair and complete repair; it
does not give 8140h a concrete lifetime or order relative to a passive-controller
cause. No additional normative requirement is inferred from that omission.

## Library contract

`EmitBusOffRecoveryEmcy`, default false, is independent of `EmitControllerErrorEmcy`.
The observation history belongs to the node actor and its current monitor. An
observed bus-off, including a fresh initial bus-off sample, arms one recovery.
Error-active, error-warning, error-passive or no-error establishes that the
controller left bus-off. Unknown does not. A repeated healthy reading without a
new bus-off produces no event. NMT reset invalidates history and queued readings
from an older monitor; a new baseline can observe a new bus-off.

Recovery is a transient communication-error cause. First queue 8140h with the
communication/generic bits, retaining application bits. Reconcile passive state,
then end the transient cause. When another communication cause is still active,
no 0000h is emitted. When the transient is the last cause, queue 0000h with the
remaining application bits. The existing EMCY chain snapshots node bits when an
event is decided and preserves frame order. Existing producer-disable,
NMT Stopped coalescing and reset/disposal rules continue to apply. In particular,
Stopped retains the most recent event rather than guaranteeing all transient
events, and a disabled producer does not replay an already-ended recovery.

This is an explicitly chosen opt-in implementation contract, not a claim that
CiA 301 requires this exact sequence.

## Remaining 8110h scope

The supplied text does not add a persistent repair indication to the adapter's
RxOverflow/TxOverflow notifications. Consequently this change does not infer
repair from a normal controller state and does not implement 8110h. That part of
#423 remains open for a reliable adapter indication or a separately specified
application acknowledgement API.

## Validation

Tests in `CanOpenControllerEmcyTests.cs` exercise recovery observation, optional
enablement, repeated events, unknown readings, reset history, monitor teardown,
copy methods and aggregation with a passive cause and application bits. The
existing controller tests cover the monitor's reset/retry machinery.

## Correction on 2026-11-15

The earlier reference to existing EMCY inhibit rules was inaccurate: 1015h is
data-only and reported NotImplemented by the device-description loader; the node
does not enforce EMCY inhibit timing. The recovery feature adds no such timing.
The producer-disable and Stopped rules above remain the implemented contract.
