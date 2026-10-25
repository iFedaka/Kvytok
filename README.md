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
- [x] Service Bus through Aspire; the Api publishes "reservation created" and schedules its expiry
- [x] Payment webhook (`POST /reservations/{id}/payments`) that publishes, never writes
- [x] Worker: confirms paid reservations, expires unpaid ones; the race between them is
      decided by `rowversion`; wakes on queue length (KEDA)
- [x] Transactional outbox with EF Core: written in the reservation's transaction by a
      `SaveChangesInterceptor`, published in-process right after the commit

**Planned, in order**
- [ ] Outbox drain: publish what the in-process send failed to
- [ ] Duplicate detection and dead-letter queues; idempotent consumer with retry
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
- a message delivered twice produces one reservation, not two;
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
delivered twice finds the reservation already confirmed and changes nothing. Retry and
a dead-letter queue for poison messages.

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
- **Container Apps rather than App Service or AKS.** It runs the Api and a
  queue-driven Worker side by side with scale-to-zero, without running a cluster.

## Results

Measured with TODO (k6 / NBomber) against the deployed environment:

| Scenario | Expected | Measured |
| --- | --- | --- |
| 200 concurrent bookings of one seat | 1 success, 199 × `409` | TODO |
| p95 latency under that load | — | TODO ms |
| Duplicate message delivery | one reservation | TODO |
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
