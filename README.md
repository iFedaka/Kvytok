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
- [x] Service Bus through Aspire; reserving a seat publishes "reservation created" and
      schedules the reservation's expiry
- [x] Payment webhook (`POST /reservations/{id}/payments`) that publishes, never writes
- [x] Worker: confirms paid reservations, expires unpaid ones; the race between them is
      decided by `rowversion`; wakes on queue length (KEDA)
- [x] Transactional outbox with EF Core: written in the reservation's transaction by a
      `SaveChangesInterceptor`, published in-process right after the commit
- [x] Outbox relay in the Api: a timer drains what the in-process send failed to publish,
      each replica claiming a disjoint batch
- [x] Idempotent consumer: each step recorded under its message id in the step's own transaction;
      retry with exponential backoff, dead-letter queue after five attempts
- [x] Load test with k6: up to 800 parallel requests for one seat against the deployed
      environment; numbers in [Results](#results)

**Planned, in order**
- [ ] Key Vault through Aspire, secrets reached via managed identity
- [ ] Application Insights through Aspire: end-to-end distributed trace, custom metric and dashboard
- [ ] Redis cache-aside
- [ ] Seat-grid demo page
- [ ] C4 diagram, levels 1–2
- [ ] ADRs for the remaining [Decisions](#decisions), in MADR format (two so far, in
      [docs/adr](docs/adr))

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
Before that, a plain read of the seat's status turns away requests for a seat already
taken. Under read committed snapshot it takes no lock and waits for none, so most of a
burst never queues for the row; the update stays the arbiter for whoever still reads `Free`.
Reservations carry a `rowversion`, since confirm, cancel and expire can race on them.
Reserving a seat also schedules an "expiry due" message for the moment the hold runs out
(`Reservation:HoldFor`: ten minutes here, thirty seconds in the demo environment). A
payment arrives as a webhook, `POST /reservations/{id}/payments`, which only puts a
message on the queue.

**Worker** — a `BackgroundService` consuming the reservations queue: confirms paid
reservations, expires unpaid ones, and ignores "reservation created", which is there only
to wake it ([Decisions](#decisions)). Both leave `Held` through a write conditional on the
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
- **Service Bus rather than Storage Queues or Event Grid.** Scheduled messages, which the
  whole expiry path rests on (and the worker's retries, which are delayed copies), and a
  built-in dead-letter queue with a delivery count. Storage Queues can only approximate a
  schedule by hiding a message for up to seven days, and have no dead-letter queue, so the
  five-attempt limit would be mine to build. Event Grid is for fan-out notifications, not work
  queues. Duplicate detection and sessions, the usual reasons given, are not used: there is no
  per-key ordering to preserve, and duplicate detection has to stay off (see the outbox below).
  The namespace is Standard because that is what Aspire's `AddAzureServiceBus` provisions, not
  by choice: nothing here needs more than Basic, which has no topics, sessions, transactions or
  duplicate detection. The bill shows Service Bus at $0.00 every day from 23 to 29 October, so
  there is nothing to save yet; if a base charge appears, Basic is the tier to move to.
- **"Reservation created" is published to wake the worker, not to tell it anything.** The
  worker ignores its content. The Worker scales to zero and KEDA wakes it on queue length,
  polled every 10 seconds, so a payment arriving at a cold worker waits for a poll plus a
  container start before it is confirmed. Publishing on every reservation starts that wait
  early. The bound: KEDA scales the worker back to zero five minutes after the queue empties,
  so only payments within about five minutes of the reservation benefit, half of a ten-minute
  hold. The price is a message and a wake-up on every reservation, including the ones
  nobody pays. The time saved has not been measured; it is intent, not a result. With a
  minimum of one worker replica the message would have no job left and could go.
- **Scheduled messages rather than a sweep for expiry.** The broker holds each expiry until
  it is due, so there is no timer to own, and an expiry that finds the reservation already
  confirmed doing nothing is the correct outcome rather than a missed case.
- **An open payment webhook.** It is the one public endpoint besides booking that changes
  state on request, and it is unauthenticated on purpose: the whole demo, pay one
  reservation and watch the other expire, runs from the API reference page with nothing
  to ask for. The data is test data, and the endpoint can only report a payment; the
  worker decides what it means. A real provider would sign its calls and the endpoint
  would verify the signature.
- **The outbox relay runs inside the Api, not the Worker or a scheduled job.** An unpublished
  row does harm only while someone wants that seat, and that person's request wakes the Api,
  whose timer drains the outbox. Replicas claim disjoint batches under a lease, and startup
  refuses a configuration whose recovery time does not fit inside the hold. Duplicate detection
  stays off because it would drop the worker's retries. [ADR 0001](docs/adr/0001-outbox-relay-in-the-api.md)
- **The worker's message key is checked under a lock, not by racing to insert it.** A second
  copy of a message waits for the first and finds its record; racing to insert is also correct
  but logs every harmless duplicate as an error. The cost is a measured ceiling of about 45
  steps a second, because time-ordered ids put every range lock in one place.
  [ADR 0002](docs/adr/0002-message-key-checked-under-a-lock.md)
- **Container Apps rather than App Service or AKS.** It runs the Api and a
  queue-driven Worker side by side with scale-to-zero, without running a cluster.

## Results

Measured with [k6](tests/load/hot-seat.js) against the deployed environment (Sweden
Central), from a laptop over the public internet. Each burst is a fresh event: every
virtual user sleeps until the same instant, then sends one reservation for seat 1; the
last request left within 21 ms of the first even at 800. Times are k6's
`http_req_duration`, from the first byte sent to the last byte received. They leave out
the TCP and TLS handshake but include one network round trip, which is most of the
35–40 ms an uncontended `409` takes.

Two versions were measured, each with the same series: four bursts of 200, then one burst
each at 50 to 800. *Lock-first* (commit `2157c79`, measured 2026-10-28) learns that a seat is
taken only from the conditional update, which first takes the row's update lock.
*Read-first* (`9b4a6c5`, 2026-10-29) reads the seat's status before taking any lock and
answers `409` there ([How it works](#how-it-works)). Raw summaries are in
[tests/load/results](tests/load/results), one folder per version. Both predate the script
recording the Api build in each summary, so the commits above were read from the Api's root
endpoint at the time.

| Scenario | Expected | Measured |
| --- | --- | --- |
| 200 concurrent bookings of one seat | 1 × `201`, 199 × `409`, no double booking | 1 × `201`, 199 × `409`, nothing else, 0 double bookings, in each of 8 runs across both versions |
| p95 of a losing request, 200 contenders, Api scaled out | — | read-first 181–381 ms, lock-first 776–1395 ms; 35–69 ms uncontended |
| p95 of a losing request, 200 contenders, one replica | — | read-first 1810 ms, lock-first 2120 ms |
| Payment message delivered twice | one confirmation, repeat skipped | one confirmation, repeat skipped, both one after the other and at the same moment ([worker tests](tests/Kvytok.Worker.IntegrationTests/ReservationLifecycleTests.cs)); also checked by hand with a local worker against the dev Service Bus namespace |
| Worker killed mid-batch | no lost messages | TODO: not yet tested against a running worker |

The four bursts of 200, in order: read-first, with lock-first in brackets. The Api runs
at 0.5 vCPU per replica and scales on the platform's default HTTP rule, from zero to at most
three replicas:

| Run | Api replicas | Loser p50 | Loser p95 | Loser max | Winner | Last answer |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | 1460 ms (1467) | 1810 ms (2120) | 1901 ms (2217) | 2009 ms (69) | 2087 ms (2368) |
| 2 | 1, then 3 | 149 ms (532) | 381 ms (776) | 417 ms (782) | 104 ms (850) | 531 ms (937) |
| 3 | 3 | 115 ms (492) | 285 ms (816) | 315 ms (863) | 77 ms (88) | 451 ms (987) |
| 4 | 3 | 76 ms (287) | 181 ms (1395) | 250 ms (1550) | 102 ms (66) | 381 ms (1656) |

Rising contention, one burst each, three replicas throughout: read-first, with lock-first
in brackets:

| Contenders | `201` / `409` / other | Loser p50 | Loser p95 | Loser max | Last answer | TLS handshake p95 |
| --- | --- | --- | --- | --- | --- | --- |
| 50 | 1 / 49 / 0 | 43 ms (48) | 90 ms (91) | 117 ms (94) | 194 ms (186) | 62 ms |
| 100 | 1 / 99 / 0 | 283 ms (119) | 1180 ms (864) | 1203 ms (940) | 1306 ms (1058) | 83 ms |
| 200 | 1 / 199 / 0 | 76 ms (287) | 181 ms (1395) | 250 ms (1550) | 381 ms (1656) | 145 ms |
| 400 | 1 / 399 / 0 | 214 ms (400) | 790 ms (1203) | 1030 ms (1482) | 1107 ms (1594) | 264 ms |
| 800 | 1 / 799 / 0 | 510 ms (712) | 1053 ms (1431) | 1442 ms (1804) | 1655 ms (2048) | 537 ms |

What the numbers say:

- **Correct at every level, in both versions.** 4,300 attempts over sixteen bursts: exactly
  one `201` per burst, every other answer a clean `409`, no timeouts, no `5xx`, and the seat
  read back as `Held`.
- **The row lock was the main cost at 200.** Lock-first, a loser learns the seat is taken only
  from `UPDATE … WHERE Status = 'Free'`, which first takes an update lock on the one row;
  update locks exclude each other, so losers are answered one at a time even after the
  winner has committed. Read-first cut the p95 at 200 on three
  replicas from 0.8–1.4 seconds to 0.18–0.29, and the median from 0.3–0.5 seconds to under
  0.12. At 400 and 800 the p95 fell by a quarter to a third.
- **Not yet as fast as an uncontended `409`.** The losing p95 is still 5–8 times the 35–40 ms
  of a `409` with no one competing, and it keeps rising with contention. On a single replica
  even the winner took 2 seconds, with no lock to wait for: the limit there is the replica,
  half a vCPU taking 200 requests at once, and its pool of 100 SQL connections. That is the
  likely remaining cost on three replicas too, unconfirmed until there is server-side tracing.
- **Single bursts are noisy.** The one burst of 100 came out slower read-first than
  lock-first (1.18 s against 0.86 s), and the four read-first bursts of 200 range from 181
  to 381 ms once scaled out. The ladder shows the shape of the curve; a single row in it is
  one sample.
- **Scale-out does not help the burst that causes it.** In both versions the first burst found
  one replica and had by far the worst p95. The replica count reached three by the end of the
  second burst.
- **Throughput, for completeness:** 800 answers in 1.66 seconds, about 480 a second
  (lock-first: 390), on a single seat.
- **Cold start.** After idle, the Api and serverless SQL are both at zero. The first request
  took over a minute: the replica started, then failed its readiness probe until the
  database resumed. The probe runs `/health`, which queries the database, with the platform's
  default 1-second timeout, and a serverless database takes 30 to 60 seconds to resume, so
  every check during the resume fails and the replica gets no traffic until one passes. The
  resume itself is the floor. What the probe adds on top is configuration: a startup probe
  with a generous failure threshold, separate from readiness, or a readiness check without
  the database, which the [runbook](docs/deploy.md) already names as the first thing to drop.
  Neither is applied or measured yet. The load test waits the cold start out before measuring.

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
deploy. `azd down` removes it all. Measured from the bill, 16 to 29 October: $7.86 in all, under the
subscription's $20 monthly budget alert. Container Apps compute was $5.93 of it, up to $1.30 a
day while the environment was being built, and under 5 cents a day from 27 October. The one
steady charge is the Container Registry (Basic), 17 cents a day, about $5 a month. SQL (free
offer), Service Bus and Log Analytics billed $0.00 ([cost notes](docs/deploy.md#cost)).

The load test needs [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/); `az`, if
logged in, adds the Api's replica count to each result:

```
tests/load/run.sh                                    # 4 × 200, then 50 to 800; RESULTS_DIR to choose where
k6 run -e CONTENDERS=200 tests/load/hot-seat.js      # one burst
```

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
