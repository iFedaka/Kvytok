# Design decisions

Why the design is the way it is: the alternatives refused, the costs accepted, the caching
decision and what is out of scope. The design itself is in [How it works](../README.md#how-it-works).

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
  duplicate detection. The October invoice has no Service Bus line at all, though three Standard
  namespaces existed on the subscription at the end of the month (one deployed, two from Aspire local runs; one of those since deleted, see [the cost notes](deploy.md#service-bus-namespaces-not-billed)): the Standard base
  charge, billed hourly, never appeared, for reasons not yet understood. Cost Analysis shows $0.00 for it on each of 1 to 13 November, with all three alive; until
  the November invoice confirms it, assume the charge may still appear, and that Basic is where
  the saving is.
- **"Reservation created" is published to wake the worker, not to tell it anything.** The
  worker ignores its content. The Worker scales to zero and KEDA wakes it on queue length,
  polled every 10 seconds, so a payment arriving at a cold worker waits for a poll plus a
  container start before it is confirmed. Publishing on every reservation starts that wait
  early. The bound: KEDA scales the worker back to zero five minutes after the queue empties,
  so only payments within about five minutes of the reservation benefit, half of a ten-minute
  hold. The demo shortens the hold to 30 seconds so expiry can be watched. With a 30-second
  hold, every payment arrives within those five minutes. The price is a message and a wake-up on every reservation, including the ones
  nobody pays. In the [relayed trace](observability.md#one-trace-across-the-queue) the handler takes 9 µs
  and settling the message with the broker 56 ms, where the expiry's real work is 28 ms
  against 24 ms to settle. The time saved has not been measured; it is intent, not a result. With a
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
- **The seat grid only books.** `POST /events` is as open as the webhook, and the
  [seat grid](observability.md#two-windows-one-seat) could have offered it as a button. It does not: each click
  would write an event and its seats into a free-offer database that pauses for the rest of the
  month once its allowance is spent, and nothing rate-limits the endpoint. Making an event stays on
  the API reference page; the grid lists upcoming events and books seats in one. Its two-second
  re-read is an uncached query too, so a hidden tab stops polling and reads again when shown.
- **The outbox relay runs inside the Api, not the Worker or a scheduled job.** An unpublished
  row does harm only while someone wants that seat, and that person's request wakes the Api,
  whose timer drains the outbox. Replicas claim disjoint batches under a lease, and startup
  refuses a configuration whose recovery time does not fit inside the hold. Duplicate detection
  stays off because it would drop the worker's retries. Only reserving goes through the outbox.
  The payment webhook writes nothing, so it has no transaction to share: it sends straight to
  the queue and answers `503` if it cannot, and the provider, which retries a webhook that did
  not succeed, is what makes the payment durable. [ADR 0001](adr/0001-outbox-relay-in-the-api.md)
- **The worker's message key is checked under a lock, not by racing to insert it.** A second
  copy of a message waits for the first and finds its record; racing to insert is also correct
  but logs every harmless duplicate as an error. The cost is a measured ceiling of about 45
  steps a second, because time-ordered ids put every range lock in one place.
  [ADR 0002](adr/0002-message-key-checked-under-a-lock.md)
- **An in-process cache, not Redis.** The one cached read is the event list, which is not on
  the path anyone is waiting on. A managed Redis would be the first thing in this architecture that
  cannot scale to zero. [Caching the event list](#caching-the-event-list)
- **Container Apps rather than App Service or AKS.** It runs the Api and a
  queue-driven Worker side by side with scale-to-zero, without running a cluster.


## Caching the event list

The plan said Redis cache-aside on the event list. What was built is smaller: `GET /events`
(up to 100 upcoming events, soonest first) through .NET's `HybridCache` with no second layer,
so each Api replica keeps its own copy in memory for 30 seconds.

**Cached:** the event list, and only what in it is fixed when an event is created: id, name,
start time, seat count. A stale copy can be missing an event but never wrong about one. Creating
an event clears the copy on the replica that created it; the other replicas keep theirs until it
runs out, so a new event can take up to 30 seconds to appear everywhere. An
[integration test](../tests/Kvytok.Api.IntegrationTests/Features/ListEventsTests.cs) pins both
behaviours: a new event is listed at once, and one written past this replica is not.

**Refused:**

- **Seat status, and the free-seat count** (`GET /events/{eventId}`). It changes on every
  reservation and every expiry. A stale `Free` sends someone into a `409`; a stale `Held` hides a
  seat that came back.
- **The status read in reserve.** It is the one read in the hot path, and the tempting one. Cached,
  it saves a read under snapshot isolation that takes no lock and waits for none. A stale `Free`
  would only fall through to the update, harmless and useless; a stale `Held` would turn a buyer
  away from a seat that is free. The update decides who wins; this read only turns losers away
  sooner, and only while it is current.
- **Reservations** (`GET /reservations/{id}`). The worker changes them behind the Api's back, and
  a client polls one to see `Confirmed`.

What the cache does on its own endpoint: one request a second for 100 seconds made 13 queries in
all, one per replica per 30 seconds, each about 2 ms. A hit takes 0.3 ms inside the Api. From the
laptop both take 41 ms at the median, which is the network round trip. Each miss's query appears
as a trace of its own, not under the request that missed: HybridCache runs the factory on behalf
of every waiting caller, outside any one request's trace.

**The decision is the part worth keeping.** The second layer is a configuration change away:
register an `IDistributedCache` (`AddRedisDistributedCache` with a Redis resource in the
AppHost) and HybridCache uses it with no change to the endpoint. It is not added. The smallest
managed Redis on Azure is about $11–12 a month and runs all the time, against a registry that
alone costs about $5 a month, with compute on top (see [the cost notes](deploy.md#cost)). It
would be the first thing here that cannot scale to zero. What it would buy: one network call to
Redis in place of a 2 ms query, the replicas sharing one copy, and a warm cache after a cold
start, for a read that is not on the path anyone is waiting on. Worth revisiting if the event
list ever becomes the hot path, or if the database becomes the bottleneck, and neither is true now.

## Not in scope

- **No full front end.** The API reference page is the interface; there is a single
  static page with a seat grid purely to make contention visible. This is a backend study.
- **No authentication.** Identity would add a lot of surface and demonstrate nothing
  the rest of the project does not already cover.
- **Single region, no DR.** The naming and infrastructure assume one region on purpose;
  multi-region would change the design and the bill without changing the lesson.
- **No payment provider.** Payment confirmation is modelled as an incoming message.
- **No Key Vault, because nothing here authenticates with a secret.** SQL, Service Bus
  and Application Insights are all reached through managed identities (Service Bus and
  Application Insights have local auth disabled), so there are no credentials to keep.
  The Application Insights connection string each app receives only names the endpoint;
  whether telemetry is accepted is decided by the app's Entra ID token. The one secret
  that exists is the health probe key, a Container App secret that the platform's probe
  configuration repeats in plain text anyway, because probe headers cannot reference
  secrets. A vault with nothing in it would be decoration.
