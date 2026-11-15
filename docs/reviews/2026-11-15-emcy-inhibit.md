# Optional EMCY inhibit time

The supplied CiA 301 V4.2.0 §4.4.1 defines inhibit time as the minimum between
transmission-service invocations. §7.2.7.2.1 lists the EMCY attribute; §7.5.2.18
(printed p. 107) specifies optional 1015h:00, rw UNSIGNED16, not PDO-mappable,
100 µs units, default 0 and 0 disabling inhibit. Earlier #528/#530 review records
described the then-unimplemented object. This change adds an explicit capability.

`EnableEmcyInhibit` defaults false to preserve existing absent/data-only objects,
including an application that already declared 1015h. Opt-in creates and manages
the object, including EDS/DCF loading, SDO/local writes and normal persistence.
No existing public signature changes; options-copy methods retain the flag.

All EMCYs keep FIFO order. The interval starts conservatively at completion of the
previous send attempt, including failures: a delayed driver handoff or confirmation
must not compress subsequent invocations. This is an implementation choice within
the norm's minimum; neither maximum latency nor wire timestamps are promised.
Cancelled waiting requests reserve no interval. Error register/history changes stay
at their original place on the send chain. A producer disabled at the send step still
permits node-only register updates and discards application sends without changing
application bits.

An interval write wakes and re-evaluates the head. Stopped pauses work that was
already queued; new Stopped reasons use the existing latest-reason hold. Reset
drops all pre-reset queued EMCYs and invalidates timer/completion history. Completion
stamps carry the reset epoch, so even a late old stamp cannot rearm a fresh node.
Disposal releases waits. An in-flight transport attempt retains its existing
transport completion/cancellation contract; inhibit does not cancel the driver.

`CanOpenEmcyInhibitTests.cs` uses a manual clock and explicit waits for the scheduled
gap, not host timing. It covers limits/order, interval changes, cancellation, both
resets, disposal, Stopped, producer-disable, delayed handoff, node-error aggregation,
history, SDO width checks and optional description loading. Source mutations and
executed verification results belong in the PR.
