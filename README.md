# Kvytok

Seat reservation for events with a fixed number of seats. A study in what happens
when hundreds of requests compete for the same seat, and in how such a service is
operated on Azure.

**Live API:** https://api.agreeablebay-afb9f153.swedencentral.azurecontainerapps.io/ · **API reference:** https://api.agreeablebay-afb9f153.swedencentral.azurecontainerapps.io/scalar · [![CI](https://github.com/iFedaka/Kvytok/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/iFedaka/Kvytok/actions/workflows/ci.yml?query=branch%3Amain)

> A portfolio project, not a product. It exists to demonstrate concurrency control,
> asynchronous processing and cloud operations end to end, on a domain small enough
> to read in one sitting.

## Status

Work in progress. [How it works](#how-it-works) describes the target design; this list
shows what is built and what comes next.

**Done**
- [x] Aspire AppHost: Api, Worker and SQL Server (container locally)
- [x] Domain model: events, seats, reservations; EF Core migrations; one active
      reservation per seat enforced by a filtered unique index; `rowversion` on reservations
- [x] Slices: create event, get event, reserve seat (conditional update → `409`), get reservation
- [x] OpenAPI with a Scalar reference page
- [x] Integration tests against a real SQL Server (Testcontainers)
- [x] GitHub Actions: build and tests, CI badge in this README
- [x] Deployed to Azure Container Apps with `azd`; see the [deployment runbook](docs/deploy.md)
- [x] Migrations as a separate Container Apps job, run by azd after each deploy
- [x] Health checks on a separate port behind a key, used by the platform's liveness and readiness probes
- [x] Infrastructure as code in the repository (Bicep generated from the AppHost)
- [x] Service Bus through Aspire; the Api publishes "reservation created", which wakes the
      worker before the payment arrives (see the [runbook](docs/deploy.md#when-the-app-looks-dead)),
      and schedules its expiry
- [x] Payment webhook (`POST /reservations/{id}/payments`) that publishes, never writes
- [x] Worker: confirms paid reservations, expires unpaid ones; the race between them is
      decided by `rowversion`; wakes on queue length (KEDA)
- [x] Transactional outbox with EF Core: written in the reservation's transaction by a
      `SaveChangesInterceptor`, published in-process right after the commit
- [x] Outbox relay in the Api: a timer drains what the in-process send failed to publish,
      each replica claiming a disjoint batch
- [x] Idempotent consumer: each step recorded under its message id in the step's own transaction;
      retry with exponential backoff, dead-letter queue after five attempts

**Planned, in order**
- [ ] Load test (k6 or NBomber): 200 parallel requests for one seat
- [ ] Key Vault through Aspire, secrets reached via managed identity
- [ ] Application Insights through Aspire: end-to-end distributed trace, custom metric and dashboard
- [ ] Redis cache-aside
- [ ] Seat-grid demo page
- [ ] C4 diagram, levels 1–2
- [ ] ADRs for the [Decisions](#decisions), in MADR format

## The problem

An event has 200 seats. Hundreds of people press "book" on the same seat within the
same second. A reservation that is not paid within 10 minutes expires and the seat
returns to the pool. Payment confirmations arrive asynchronously, out of order, and
occasionally twice.

Three things have to hold:

- exactly one booking wins a contested seat, and the losers get a clear `409`;
- a message delivered twice takes effect once: a payment confirms once, and a duplicate
  is never mistaken for a second payment;
- if the worker dies mid-processing, nothing is lost and nothing is double-charged.

Everything else in this repository is the machinery required to make those three
statements true in production rather than on a laptop.

## How it works

This is the target design; see [Status](#status) for what's built.

TODO: C4 container diagram (levels 1-2)

**Api** — ASP.NET Core minimal APIs, organised as vertical slices: one folder per
use case, each holding its endpoint, handler and data access. A contested seat is
decided by a single conditional update (`SET Status = 'Held' WHERE Status = 'Free'`):
SQL Server locks the row, so exactly one request changes it and the rest get `409`.
Reservations carry a `rowversion`, since confirm, cancel and expire can race on them.
Reserving a seat also schedules an "expiry due" message for the moment the hold runs out
(`Reservation:HoldFor`: ten minutes here, thirty seconds in the demo environment). A
payment arrives as a webhook, `POST /reservations/{id}/payments`, which only puts a
message on the queue.

**Worker** — a `BackgroundService` consuming the reservations queue: confirms paid
reservations, expires unpaid ones. Both leave `Held` through a write conditional on the
reservation's `rowversion`, so when a payment lands while the expiry is in flight,
whichever commits first decides and the other finds it is too late: a paid seat is never
released, and a late payment never resurrects an expired reservation. A payment
delivered twice is recognised by its message id, recorded in the same transaction as the
step it took, and skipped. A step that fails is retried as a delayed copy of the message,
2, 4, 8 and 16 seconds apart; the fifth failure, or a message no retry can help (a subject
nobody handles, a body that is not its contract), goes to the dead-letter queue.

**Data** — Azure SQL (serverless). Reservations and the transactional outbox live in
the same database, so publishing an event and changing state happen in one transaction.

**Messaging** — Azure Service Bus. The Api never calls the Worker; they share a
message contract and nothing else.

**Operations** — secrets in Key Vault reached through a managed identity, Redis for
cache-aside on the event list, Application Insights with distributed tracing across
the queue boundary, health endpoints `/health` and `/alive` on a port the public
ingress does not route to.

## Decisions

- **Vertical slices rather than layers.** Each use case is one folder holding its
  endpoint, handler and data access, so a change touches one place.
- **Service Bus rather than Storage Queues or Event Grid.** Duplicate detection,
  sessions and a built-in dead-letter queue; Storage Queues have none of these, and
  Event Grid is for fan-out notifications, not work queues.
- **Scheduled messages rather than a sweep for expiry.** The broker holds each expiry until
  it is due, so there is no timer to own, and an expiry that finds the reservation already
  confirmed doing nothing is the correct outcome rather than a missed case.
- **An open payment webhook.** It is the one public endpoint besides booking that changes
  state on request, and it is unauthenticated on purpose: the whole demo, pay one
  reservation and watch the other expire, runs from the API reference page with nothing
  to ask for. The data is test data, and the endpoint can only report a payment; the
  worker decides what it means. A real provider would sign its calls and the endpoint
  would verify the signature.
- **The outbox relay runs inside the Api, not the Worker or a scheduled job.** A row the
  in-process send failed to publish does harm only while someone wants that seat, and that
  person's request is what wakes the Api, whose timer then drains the outbox. So the system
  is wrong only while nobody can see it, and repairs itself as soon as somebody can. The
  Worker cannot host it: KEDA wakes the Worker on messages in the queue, and creating those
  messages is the relay's job, while a scheduled expiry is not counted until it falls due.
  Up to three Api replicas each run a relay, so a drain first claims a batch with
  `UPDATE … WITH (READPAST) … OUTPUT inserted.*`; a claim older than its lease belongs to a
  replica that died mid-drain and is taken over. A row never claimed counts from its creation,
  so the relay stays off rows whose own request is still sending them; one lease is both that
  grace and the recovery time. Lease times are the database's clock, never a replica's:
  `CreatedAt` is set by the database, and a cutoff computed in a container would be off by
  that container's skew. A claim must outlive the operation it protects, and that operation is
  sending a row and recording that it went, so neither sender acts past the lease: both finish
  by the lease or the 10-second publish timeout, whichever comes first, and sending stops a
  second before that to leave the record room to land. A drain that reaches that point stops
  and leaves the rest of its batch to the next. A lost publish stays lost for up to the lease,
  plus one drain interval, plus one send; that must fit inside the hold, and startup refuses a
  configuration where it does not. The 30-second demo hold runs with a 5-second lease:
  5 + 10 + 4 = 19 seconds. Duplicates can still happen at the edges (a cut-off send the broker
  had already accepted, a record that fails to land), and the consumer absorbs them: every
  message is keyed by its id, so a second delivery is skipped. Duplicate detection on the queue
  would catch them one layer earlier, but it would also drop the worker's retries, which are
  copies of the message under the same id, so it stays off. A scheduled Container Apps job or an
  always-on replica would also work, but both cost more infrastructure than this needs.
  The job becomes the right answer once something has to be correct with no traffic at all.
- **The worker's message key is checked under a lock, not by racing to insert it.** Each
  step first reads its message id from `ProcessedMessages` with `UPDLOCK, HOLDLOCK`, so a
  second copy of the same message handled at the same moment waits for the first to commit,
  then finds its record. Racing to insert and catching the key violation is also correct, but
  EF logs the violation as an error before any catch sees it, so every harmless duplicate
  would look like a failure. The cost is serialisation. An id not yet recorded is locked as a
  range, up to the next key in the index, and message ids begin with a reservation id that is
  a version 7 GUID, ordered by time. So almost every new id falls in the same open range above
  the highest key, and steps queue one behind another whatever the concurrency. The ordering
  that keeps inserts at the end of the index, which is why version 7 GUIDs exist, is exactly
  what puts every lock in one place. Measured locally against SQL Server in a container, two
  runs, every id fresh at the tail, which is the worst case for this lock: about 45 steps a
  second from four concurrent handlers on, and lower at sixteen (34–41) than at four or eight, so past that point more replicas or a higher
  `MaxConcurrentCalls` cost throughput rather than add it. One step alone takes about 35 ms;
  the ceiling puts the part spent holding the lock at about 22 ms. Four round trips to the
  database happen in that window, so on Azure SQL each millisecond of round trip adds about
  4 ms: at 2 ms, an estimate of about 33 steps a second. With random ids the same steps reached at least twice
  the ceiling before something else on the machine limited them. That is still far more than
  this system needs. Past it, the first step is to key the table by a hash of the message id,
  which gives up an ordering this table has no use for, scatters the range locks and keeps
  the check as it is; racing to insert, log noise included, comes after that. The same range
  lock is why a lock timeout (2 seconds) is retried and never taken for a duplicate: the lock
  may belong to a different message entirely, and skipping on it would drop a message
  nobody processed.
- **Container Apps rather than App Service or AKS.** It runs the Api and a
  queue-driven Worker side by side with scale-to-zero, without running a cluster.

## Results

Measured with TODO (k6 / NBomber) against the deployed environment:

| Scenario | Expected | Measured |
| --- | --- | --- |
| 200 concurrent bookings of one seat | 1 success, 199 × `409` | TODO |
| p95 latency under that load | — | TODO ms |
| Payment message delivered twice | one confirmation, repeat skipped | TODO |
| Worker killed mid-batch | no lost messages | TODO |

TODO: 10-second GIF — two browser windows booking the same seat, one wins.

## Running it

Locally, with .NET Aspire orchestrating the dependencies in containers:

```
dotnet run --project src/Kvytok.AppHost
```

The Aspire dashboard lists every resource and its traces. No Azure subscription is
needed for this path.

On Azure, with the Azure Developer CLI:

```
azd auth login
azd up
```

This provisions everything in `infra/` (Bicep), deploys the Api and the Worker, and
runs the migrations job. Takes about 6 minutes: roughly 5 to provision, under 1 to
deploy. `azd down` removes it all; running cost is roughly TODO per month, most
of which is Redis and Service Bus.

## Not in scope

- **No full front end.** The API reference page is the interface; there is a single
  static page with a seat grid purely to make contention visible. This is a backend study.
- **No authentication.** Identity would add a lot of surface and demonstrate nothing
  the rest of the project does not already cover.
- **Single region, no DR.** The naming and infrastructure assume one region on purpose;
  multi-region would change the design and the bill without changing the lesson.
- **No payment provider.** Payment confirmation is modelled as an incoming message.

## Stack

.NET 10 · ASP.NET Core minimal APIs · EF Core · .NET Aspire · Azure SQL · Azure
Service Bus · Azure Cache for Redis · Key Vault · Application Insights ·
Azure Container Apps · Bicep · GitHub Actions
