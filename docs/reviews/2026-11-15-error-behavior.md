# Optional CANopen error behavior

The supplied CiA 301 V4.2.0 §7.5.2.32 (printed pp. 123–125) defines optional
1029h and the default autonomous transition from Operational to Pre-Operational
on a serious device failure. Mandatory sources are CAN bus-off, heartbeat timeout
and local life-guarding timeout. Table 63 allows 0 (Pre-Operational), 1 (unchanged)
and 2 (Stopped), with reserved and manufacturer-specific ranges above these.
The communication entry is mandatory when the array exists, UNSIGNED8, non-PDO-
mappable, default 0 (or profile-specific), rw or constant if not configurable.

`EnableErrorBehavior` defaults false for 1.x compatibility. Opt-in creates a fixed
array with highest sub-index 1 and the communication policy at sub-index 1. No
existing public signature changes. Reserved and undefined manufacturer policies
are rejected; profile/manufacturer error classes are not implemented. EDS/DCF
loading reports corrections and omissions rather than declaring unsupported
behavior. The generic profile's storage and reset paths restore this object.

The scope is the three mandatory sources. Other error notifications and application
error-register bits do not automatically become serious device failures. Existing
EMCY options and register/history semantics remain independent. Thus an observed
failure can select an NMT reaction even with all optional EMCYs or the producer
disabled. This option does not claim complete device-profile conformance for a
legacy node that has not opted in.

Tracking stays on the actor: existing heartbeat-timeout observations also serve
1029h, life guarding owns its active flag, and the shared controller monitor keeps
bus-off history through unknown readings until a known recovery. Reconfiguration
and reception clear only the corresponding source. Controller monitoring retains
its polling/hint granularity, fresh reset baseline, retry and teardown contracts.

Resolve the target before entering Operational, rather than arming PDO/SRDO and
then demoting the node. Re-evaluate an active fault on policy writes, without an
automatic restart on recovery or a less restrictive policy. Use the normal NMT
transition path for producer shutdown, state-change heartbeat and SDO-server abort
on Stopped. Completed Segmented/Block receptions are retired before their OD
commit with this capability, so a synchronous policy reaction cannot abort the
transfer committing that policy and then acknowledge it a second time. The
regression was observed for both transports before this correction. Unfinished
transfers still abort. Do not broadcast an NMT command for a local reaction.

Change the state before reporting a newly generated EMCY, so Stopped uses the
existing latest-reason hold. The existing EMCY gate also pauses pre-existing
queued sends when this capability is enabled, without activating 1015h timing.
Reset retires queued messages and observation history; disposal releases waits.
An already started transport retains its existing completion/cancellation contract.

`CanOpenErrorBehaviorTests` uses a manual clock and positive frame/actor barriers.
Executed checks, source mutations, API comparison and coverage belong in the PR.
