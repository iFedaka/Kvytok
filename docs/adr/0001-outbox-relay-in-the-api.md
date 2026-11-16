---
status: accepted
date: 2026-10-26
---

# Run the outbox relay inside the Api

## Context and Problem Statement

Reserving a seat writes the hold and its outgoing messages ("reservation created" and the
scheduled "expiry due") to an outbox in one transaction, then publishes them in-process right
after the commit. That send can fail. A row it failed to publish has to be sent by something
else, before the hold it belongs to runs out. Where does that something run?

## Decision Drivers

* A lost expiry keeps a seat held that should have returned to the pool; it must go out within
  the hold (ten minutes, thirty seconds in the demo environment).
* Everything scales to zero; there is no always-on process to host a timer.
* Up to three Api replicas run at once, and any of them can die mid-drain.
* No new infrastructure unless it buys correctness the system needs.

## Considered Options

* A relay inside the Api, on a timer
* A relay inside the Worker
* A scheduled Container Apps job
* An always-on replica

## Decision Outcome

Chosen option: "A relay inside the Api", because an unpublished row does harm only while
someone wants that seat, and that person's request is what wakes the Api, whose timer then
drains the outbox. The system is wrong only while nobody can see it, and repairs itself as soon
as somebody can.

### Claiming a batch

Each replica runs a relay, so a drain first claims a batch with
`UPDATE … WITH (READPAST) … OUTPUT inserted.*`. A claim older than its lease belongs to a
replica that died mid-drain and is taken over. A row never claimed counts from its creation, so
the relay stays off rows whose own request is still sending them; one lease is both that grace
and the recovery time.

Lease times are the database's clock, never a replica's: `CreatedAt` is set by the database,
and a cutoff computed in a container would be off by that container's skew.

### Bounding the send

A claim must outlive the operation it protects: sending a row and recording that it went. So
neither sender acts past the lease: both finish by the lease or the 10-second publish timeout,
whichever comes first, and sending stops a second before that to leave the record room to land.
A drain that reaches that point stops and leaves the rest of its batch to the next.

A lost publish stays lost for up to the lease, plus one drain interval, plus one send. That must
fit inside the hold, and startup refuses a configuration where it does not. The 30-second demo
hold runs with a 5-second lease: 5 + 10 + 4 = 19 seconds.

* 5 is the lease. The relay leaves a row alone until it is that old, so the request's own send
  has time to finish; it is also how long a replica that died mid-drain holds its claim.
* 10 is the drain interval. A row that becomes claimable just after a drain waits up to a full
  interval for the next one.
* 4 is the send limit: the lease (or the 10-second publish timeout, if that is shorter) less the
  one second kept for recording the row as sent.

This is the worst case. In the relayed trace the relay sent both messages at the first drain
after the lease, 10.2 s after the request.

### Consequences

* Good, because there is no extra process to deploy, scale or pay for.
* Good, because the recovery bound is computed and enforced at startup, not hoped for.
* Bad, because with no traffic at all a lost row waits for the next request of any kind. If a
  replica crashes after committing a hold and before sending, that seat stays held until then.
* Bad, because duplicates can still happen at the edges: a cut-off send the broker had already
  accepted, or a record that fails to land. The consumer absorbs them (see
  [ADR 0002](0002-message-key-checked-under-a-lock.md)).
* Bad, because duplicate detection on the queue must stay off. It would catch the edge
  duplicates a layer earlier, but it would also drop the worker's retries, which are copies of
  the message under the same id. `requiresDuplicateDetection` cannot be changed on an existing
  queue, so this decision rules the option out until the queue is recreated
  ([deploy.md](../deploy.md)).

### Confirmation

* Startup refuses a configuration whose recovery time does not fit the hold. The 30-second demo
  hold with a 5-second lease passes: 5 + 10 + 4 = 19 seconds.
* An Api integration test, `A_Relayed_Message_Continues_The_Trace_Of_The_Request_That_Owed_It`,
  forces the immediate publish to fail and checks that the relay's spans join the request's
  trace.
* The relayed trace in [observability.md](../observability.md) shows the outcome: the relay sends
  both messages 10.2 s after the request, and the expiry is still handled on time, at 30.07 s
  for the 30-second hold.

## Why not the alternatives

### A relay inside the Worker

* Good, because the queue code already lives there, and the Api would stay free of a background
  timer.
* Bad, because KEDA wakes the Worker on messages in the queue, and creating those messages is
  the relay's job. A scheduled expiry is not counted until it falls due, so nothing would wake
  the Worker to send it.

### A scheduled Container Apps job

* Good, because it is correct with no traffic at all.
* Bad, because it is more infrastructure than this needs today. It becomes the right answer once
  something has to be correct with no traffic.

### An always-on replica

* Good, because the timer always runs.
* Bad, because it gives up scale-to-zero and pays for an idle replica around the clock.
