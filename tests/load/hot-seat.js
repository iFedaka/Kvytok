// One hot seat: CONTENDERS virtual users each send one reservation for the same seat,
// released at the same instant. Correctness is already proven by the integration test
// (ReserveSeatTests); this measures what that test cannot: how fast the losing path is
// under contention, on the deployed environment.
//
//   k6 run tests/load/hot-seat.js                        # 200 contenders, live API
//   k6 run -e CONTENDERS=400 -e BASE_URL=https://localhost:7001 tests/load/hot-seat.js

import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE_URL = (__ENV.BASE_URL || 'https://api.agreeablebay-afb9f153.swedencentral.azurecontainerapps.io').replace(/\/$/, '');
const CONTENDERS = parseInt(__ENV.CONTENDERS || '200', 10);
const BASELINE_SAMPLES = parseInt(__ENV.BASELINE_SAMPLES || '20', 10);
const HOT_SEAT = 1;
// time between setup returning and the burst; every VU sleeps until the same instant
const RELEASE_DELAY_MS = 3000;

const created = new Counter('reserve_created');
const conflicts = new Counter('reserve_conflict');
const unexpected = new Counter('reserve_unexpected');
const winnerDuration = new Trend('winner_duration', true);
const loserDuration = new Trend('loser_duration', true);
// the same two paths with nobody competing, measured in setup on a warm replica
const baselineWin = new Trend('baseline_win_duration', true);
const baselineLoss = new Trend('baseline_loss_duration', true);
// how far after the release instant each request actually left: proof the burst was a burst
const startSkew = new Trend('start_skew', true);
// from the release instant until this contender had its answer; the max is the whole burst
const answeredAfter = new Trend('answered_after', true);

export const options = {
    scenarios: {
        hot_seat: {
            executor: 'per-vu-iterations',
            vus: CONTENDERS,
            iterations: 1,
            maxDuration: '2m',
        },
    },
    setupTimeout: '5m',
    summaryTrendStats: ['min', 'med', 'p(95)', 'p(99)', 'max'],
    thresholds: {
        reserve_created: [`count==1`],
        reserve_conflict: [`count==${CONTENDERS - 1}`],
        reserve_unexpected: ['count==0'],
        checks: ['rate==1'],
        // no limit; declared so the summary carries the burst's own handshake times
        'http_req_tls_handshaking{phase:burst}': ['max>=0'],
    },
};

const json = { headers: { 'Content-Type': 'application/json' }, timeout: '60s' };

function createEvent(seatCount) {
    const res = http.post(`${BASE_URL}/events`, JSON.stringify({
        name: `k6 hot seat ${new Date().toISOString()}`,
        startsAt: new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString(),
        seatCount,
    }), json);
    if (res.status !== 201) fail(`create event: ${res.status} ${res.body}`);
    return res.json('eventId');
}

function reserve(eventId, seat, email, tags) {
    return http.post(`${BASE_URL}/events/${eventId}/reservations`,
        JSON.stringify({ seatNumber: seat, holderEmail: email }), { ...json, tags });
}

export function setup() {
    // Scale-to-zero on both the Api and serverless SQL: the first request after idle can take
    // over a minute while the replica starts and the database resumes. Wait that out here so
    // the burst measures contention, not a cold start.
    const deadline = Date.now() + 4 * 60 * 1000;
    let warm = false;
    while (!warm && Date.now() < deadline) {
        const res = http.get(`${BASE_URL}/`, { timeout: '90s', tags: { phase: 'warmup' } });
        warm = res.status === 200;
        if (!warm) sleep(5);
    }
    if (!warm) fail('api did not become ready within 4 minutes');

    // touches the database, so it is awake too
    const baselineEvent = createEvent(BASELINE_SAMPLES);
    for (let n = 1; n <= BASELINE_SAMPLES; n++) {
        const win = reserve(baselineEvent, n, `baseline${n}@example.com`, { phase: 'baseline' });
        if (win.status !== 201) fail(`baseline win: ${win.status} ${win.body}`);
        baselineWin.add(win.timings.duration);
    }
    for (let i = 0; i < BASELINE_SAMPLES; i++) {
        const loss = reserve(baselineEvent, 1, `baseline-loser${i}@example.com`, { phase: 'baseline' });
        if (loss.status !== 409) fail(`baseline loss: ${loss.status} ${loss.body}`);
        baselineLoss.add(loss.timings.duration);
    }

    return { eventId: createEvent(1), releaseAt: Date.now() + RELEASE_DELAY_MS };
}

export default function (data) {
    const wait = data.releaseAt - Date.now();
    if (wait > 0) sleep(wait / 1000);
    startSkew.add(Date.now() - data.releaseAt);

    const res = reserve(data.eventId, HOT_SEAT, `contender${__VU}@example.com`, { phase: 'burst' });
    answeredAfter.add(Date.now() - data.releaseAt);

    // http_req_duration: from the first byte sent to the last byte received; excludes the
    // TCP and TLS handshakes, which every contender pays on its own fresh connection
    if (res.status === 201) {
        created.add(1);
        winnerDuration.add(res.timings.duration);
    } else if (res.status === 409) {
        conflicts.add(1);
        loserDuration.add(res.timings.duration);
    } else {
        unexpected.add(1);
        console.error(`VU ${__VU}: ${res.status} ${res.error || ''} ${(res.body || '').slice(0, 200)}`);
    }
}

export function teardown(data) {
    // what the database says, independently of what the responses said
    const ev = http.get(`${BASE_URL}/events/${data.eventId}`, json);
    check(ev, {
        'hot seat is held': r => r.json('seats.0.status') === 'Held',
        'no seat left free': r => r.json('freeSeatCount') === 0,
    });
}

export function handleSummary(data) {
    const m = data.metrics;
    const count = name => (m[name] ? m[name].values.count : 0);
    const ms = (name, stat) => (m[name] && m[name].values[stat] !== undefined
        ? `${Math.round(m[name].values[stat])} ms` : '—');
    const wins = count('reserve_created');
    const result = {
        timestamp: new Date().toISOString(),
        baseUrl: BASE_URL,
        contenders: CONTENDERS,
        created: wins,
        conflicts: count('reserve_conflict'),
        unexpected: count('reserve_unexpected'),
        doubleBookings: Math.max(0, wins - 1),
        winnerMs: m.winner_duration ? m.winner_duration.values.max : null,
        loser: m.loser_duration ? m.loser_duration.values : null,
        baselineWin: m.baseline_win_duration ? m.baseline_win_duration.values : null,
        baselineLoss: m.baseline_loss_duration ? m.baseline_loss_duration.values : null,
        startSkew: m.start_skew ? m.start_skew.values : null,
        answeredAfter: m.answered_after ? m.answered_after.values : null,
        tlsHandshake: m['http_req_tls_handshaking{phase:burst}'] ? m['http_req_tls_handshaking{phase:burst}'].values : null,
        thresholdsPassed: Object.values(m).every(x => !x.thresholds
            || Object.values(x.thresholds).every(t => t.ok)),
    };

    const table = [
        `Hot seat, ${CONTENDERS} contenders, ${result.timestamp}`,
        '',
        '| Metric | Value |',
        '| --- | --- |',
        `| Concurrent attempts | ${CONTENDERS} |`,
        `| Confirmed reservations (201) | ${wins} |`,
        `| Clean conflicts (409) | ${result.conflicts} |`,
        `| Anything else | ${result.unexpected} |`,
        `| Double bookings | ${result.doubleBookings} |`,
        `| Losing request, p50 / p95 / p99 / max | ${ms('loser_duration', 'med')} / ${ms('loser_duration', 'p(95)')} / ${ms('loser_duration', 'p(99)')} / ${ms('loser_duration', 'max')} |`,
        `| Winning request | ${ms('winner_duration', 'max')} |`,
        `| Uncontended loss, p50 / p95 | ${ms('baseline_loss_duration', 'med')} / ${ms('baseline_loss_duration', 'p(95)')} |`,
        `| Uncontended win, p50 / p95 | ${ms('baseline_win_duration', 'med')} / ${ms('baseline_win_duration', 'p(95)')} |`,
        `| Burst spread (last send after release) | ${ms('start_skew', 'max')} |`,
        `| TLS handshake per contender, p95 | ${ms('http_req_tls_handshaking{phase:burst}', 'p(95)')} |`,
        `| Last answer after release | ${ms('answered_after', 'max')} |`,
        '',
    ].join('\n');

    const out = __ENV.RESULTS_DIR;
    const files = { stdout: table + '\n' };
    if (out) {
        files[`${out}/hot-seat-${CONTENDERS}.json`] = JSON.stringify(result, null, 2);
        files[`${out}/hot-seat-${CONTENDERS}.md`] = table;
    }
    return files;
}
