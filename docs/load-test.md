# Load test results

Full tables, method and caveats behind the [README](../README.md#load-test) summary; how to run it is at the end.

Measured with [k6](../tests/load/hot-seat.js) against the deployed environment (Sweden
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
answers `409` there ([How it works](../README.md#how-it-works)). Raw summaries are in
[tests/load/results](../tests/load/results), one folder per version. Both predate the script
recording the Api build in each summary, so the commits above were read from the Api's root
endpoint at the time.

| Scenario | Expected | Measured |
| --- | --- | --- |
| 200 concurrent bookings of one seat | 1 × `201`, 199 × `409`, no double booking | 1 × `201`, 199 × `409`, nothing else, 0 double bookings, in each of 8 runs across both versions |
| p95 of a losing request, 200 contenders, Api scaled out | — | read-first 181–381 ms, lock-first 776–1395 ms; 35–69 ms uncontended |
| p95 of a losing request, 200 contenders, one replica | — | read-first 1810 ms, lock-first 2120 ms |
| Payment message delivered twice | one confirmation, repeat skipped | one confirmation, repeat skipped, both one after the other and at the same moment ([worker tests](../tests/Kvytok.Worker.IntegrationTests/ReservationLifecycleTests.cs)); also checked by hand with a local worker against the dev Service Bus namespace |
| Worker killed mid-batch | no lost messages | TODO: not yet tested against a running worker |

The four bursts of 200 (the fourth is the same run as the 200 row of the next table), in order: read-first, with lock-first in brackets. The Api runs
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
  replicas from 0.8–1.4 seconds to 0.18–0.38 (the three bursts after the first, one of which scaled out mid-flight), and the
  median from 0.3–0.5 seconds to under 0.15. At 400 and 800 the p95 fell by a quarter to a third.
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
  the database, which the [runbook](deploy.md) already names as the first thing to drop.
  Neither is applied or measured yet. The load test waits the cold start out before measuring.


## Running it

Needs [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/). `az`, if logged in, adds the
Api's replica count to each result (that is where the "Api replicas" column comes from).

```
tests/load/run.sh                                    # 4 x 200, then 50 to 800
k6 run -e CONTENDERS=200 tests/load/hot-seat.js      # one burst
```

`RESULTS_DIR` chooses where the summaries go (default `tests/load/results`); `LEVELS`, `REPEATS`
and `RESOURCE_GROUP` are read by `run.sh`.
