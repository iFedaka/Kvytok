---
status: accepted
date: 2026-10-28
---

# Check the worker's message key under a lock, not by racing to insert it

## Context and Problem Statement

Service Bus delivers at least once, and the outbox can send a message twice. The worker records
each step under its message id, in the step's own transaction, and skips a message whose id is
already recorded. Two copies of the same message can be handled at the same moment by different
handlers. How does the second learn that the first has it?

## Decision Drivers

* A duplicate must never take effect twice: a late payment changes no row, and only the message
  key tells its second delivery from a second payment that would need a second refund.
* A harmless duplicate must not look like a failure in the logs.
* Throughput has to cover this system's load, with room to spare.

## Considered Options

* Read the key with `UPDLOCK, HOLDLOCK` before the step
* Insert the key and catch the unique-key violation

## Decision Outcome

Chosen option: "Read the key under a lock". Each step first reads its message id from
`ProcessedMessages` with `UPDLOCK, HOLDLOCK`, so a second copy handled at the same moment waits
for the first to commit, then finds its record.

### Consequences

* Good, because a duplicate is an ordinary outcome, not an exception.
* Bad, because it serialises steps. An id not yet recorded is locked as a range, up to the next
  key in the index, and message ids begin with a reservation id that is a version 7 GUID,
  ordered by time. So almost every new id falls in the same open range above the highest key,
  and steps queue one behind another whatever the concurrency. The ordering that keeps inserts
  at the end of the index, which is why version 7 GUIDs exist, is exactly what puts every lock
  in one place.
* Bad, because a lock timeout (2 seconds) cannot be read as "a duplicate". The range lock may
  belong to a different message entirely, and skipping on it would drop a message nobody
  processed, so a timeout is retried.

### Confirmation

Measured locally against SQL Server in a container, two runs, every id fresh at the tail, which
is the worst case for this lock:

* about 45 steps a second from four concurrent handlers on, and lower at sixteen (34–41) than at
  four or eight. Past that point more replicas or a higher `MaxConcurrentCalls` cost throughput
  rather than add it;
* one step alone takes about 35 ms; the ceiling puts the part spent holding the lock at about
  22 ms;
* with random ids the same steps reached at least twice the ceiling before something else on the
  machine limited them.

Four round trips to the database happen inside the lock, so on Azure SQL each millisecond of
round trip adds about 4 ms: at 2 ms, an estimate of about 33 steps a second. That is an
estimate, not a measurement on Azure, and still far more than this system needs.

Correctness is covered by the worker's integration tests, including
`The_Same_Message_Delivered_Twice_At_Once_Is_Handled_Once` and
`A_Delivery_Held_Up_By_Another_Times_Out_Then_Retries`.

## Pros and Cons of the Options

### Insert the key and catch the unique-key violation

* Good, because it does not serialise: concurrent inserts of different keys do not wait on each
  other.
* Bad, because EF logs the violation as an error before any catch sees it, so every harmless
  duplicate would look like a failure.
* Bad, because that log cannot be filtered for this case alone. EF emits it as `CommandError`
  from inside its command pipeline, so suppressing it means downgrading that event for every
  save in the application, real failures included.

## More Information

If the ceiling is ever reached, the first step is to key the table by a hash of the message id.
That gives up an ordering this table has no use for, scatters the range locks and keeps the
check as it is. Racing to insert, log noise included, comes after that.
