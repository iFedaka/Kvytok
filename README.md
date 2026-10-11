# Kvytok

Seat reservation for events with a fixed number of seats. A study in what happens
when hundreds of requests compete for the same seat, and in how such a service is
operated on Azure.

**Live API:** https://TODO · **API reference:** https://TODO/scalar · ![CI](https://github.com/TODO/kvytok/actions/workflows/ci.yml/badge.svg)

> A portfolio project, not a product. It exists to demonstrate concurrency control,
> asynchronous processing and cloud operations end to end, on a domain small enough
> to read in one sitting.

**Status:** work in progress. Done: TODO. Next: TODO.

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

TODO: C4 container diagram (levels 1-2)

**Api** — ASP.NET Core minimal APIs, organised as vertical slices: one folder per
use case, each holding its endpoint, handler and data access. Optimistic concurrency
on the seat row (`rowversion`) is what decides the winner of a contested seat.

**Worker** — a `BackgroundService` consuming the reservations queue: confirms paid
reservations, expires unpaid ones. Idempotent by message key, with retry and a
dead-letter queue for poison messages.

**Data** — Azure SQL (serverless). Reservations and the transactional outbox live in
the same database, so publishing an event and changing state happen in one transaction.

**Messaging** — Azure Service Bus. The Api never calls the Worker; they share a
message contract and nothing else.

**Operations** — secrets in Key Vault reached through a managed identity, Redis for
cache-aside on the event list, Application Insights with distributed tracing across
the queue boundary, health endpoints at `/health` and `/ready`.

## Decisions

- [ADR-001](docs/adr/001-vertical-slices.md) — vertical slices rather than layers
- [ADR-002](docs/adr/002-service-bus.md) — Service Bus rather than Storage Queues or Event Grid
- [ADR-003](docs/adr/003-container-apps.md) — Container Apps rather than App Service or AKS

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

This provisions everything in `infra/` (Bicep) and deploys both services. Takes about
TODO minutes. `azd down` removes it all; running cost is roughly TODO per month, most
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
