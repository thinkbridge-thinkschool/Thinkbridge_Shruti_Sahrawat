// The hot path, under load.
//
// The measured call is POST /api/collections/{id}/publish. Everything else in
// an iteration is setup, and is tagged separately so it cannot contaminate the
// number: a collection can only be published once, so each iteration has to
// create one and add an item to it first. That is three requests per iteration
// and only the third one is the subject.
//
// Followers are registered in setup() rather than left at zero. A publish with
// no followers leaves the relay with nothing to write, and the relay writing
// while the API writes is the contention this is trying to see - measuring the
// endpoint with the background half of the system idle would produce a p99
// that no deployment ever experiences.
//
//   k6 run --env BASE=http://localhost:5000 publish-load-test.js
//   k6 run --env VUS=20 --env DURATION=90s publish-load-test.js
//
// Start from an empty database. The outbox is never pruned - that is the point
// of a non-destructive drain - so a run against yesterday's file is measuring a
// different table from the one the last run measured.

import http from 'k6/http';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE = __ENV.BASE || 'http://localhost:5000';
const VUS = Number(__ENV.VUS || 10);
const FOLLOWERS_PER_CURATOR = Number(__ENV.FOLLOWERS || 20);

const publishDuration = new Trend('publish_duration', true);
const outboxReadDuration = new Trend('outbox_read_duration', true);
const publishFailures = new Counter('publish_failures');

export const options = {
  vus: VUS,
  duration: __ENV.DURATION || '60s',

  // Thresholds rather than notes in a README, so a regression fails the run
  // instead of being noticed by whoever happens to read the numbers.
  //
  // p95 is the guard that matters, and it is set from the measurement rather
  // than from taste: 8.50ms after the day-31 fix, 153.73ms before it. Thirty
  // milliseconds catches a return of that specific regression with room for a
  // slower machine, and catches it long before p99 would move at all.
  //
  // p99 is a gross-regression guard only, and deliberately loose. Its floor is
  // 465ms, caused by the API contending with itself for SQLite's single writer
  // - three writes per iteration, ten virtual users - which this fix does not
  // address. A tight line there would fail every run and be commented out
  // within a week, and a threshold that is always red is not a threshold.
  //
  // Both numbers come from one laptop running the API and the load generator
  // together. Right shape for catching a regression there; wrong numbers to
  // quote as anyone's capacity.
  thresholds: {
    publish_duration: ['p(95)<30', 'p(99)<750'],
    checks: ['rate>0.99'],
  },
};

const JSON_HEADERS = { headers: { 'Content-Type': 'application/json' } };

function tagged(name) {
  return { headers: JSON_HEADERS.headers, tags: { name } };
}

function curatorFor(vu) {
  return `curator-${vu}`;
}

export function setup() {
  // Every curator a VU will use, given some followers, so that each publish
  // produces real fan-out work for the relay.
  for (let vu = 1; vu <= VUS; vu++) {
    for (let f = 0; f < FOLLOWERS_PER_CURATOR; f++) {
      http.post(
        `${BASE}/api/follows`,
        JSON.stringify({ curatorId: curatorFor(vu), followerId: `follower-${vu}-${f}` }),
        tagged('setup-follow'),
      );
    }
  }

  // The outbox read on an empty table. Paired with the one in teardown(), this
  // is the whole measurement of a read that has no upper bound on how much it
  // returns: same request, same code, a table that the run itself grew.
  const empty = http.get(`${BASE}/api/outbox`, tagged('outbox-before'));
  outboxReadDuration.add(empty.timings.duration);

  console.log(
    `outbox read on an empty table: ${empty.timings.duration.toFixed(1)} ms, ` +
      `${empty.body.length} bytes`,
  );

  return { startedAt: new Date().toISOString() };
}

export default function () {
  const curator = curatorFor(__VU);

  const created = http.post(
    `${BASE}/api/collections`,
    JSON.stringify({ curatorId: curator, name: `Load ${__VU}-${__ITER}` }),
    tagged('create'),
  );

  if (!check(created, { 'created 200': (r) => r.status === 200 })) {
    return;
  }

  const id = created.json('collectionId');

  const added = http.post(
    `${BASE}/api/collections/${id}/items`,
    JSON.stringify({ quoteId: 1 }),
    tagged('add-item'),
  );

  if (!check(added, { 'item added 200': (r) => r.status === 200 })) {
    return;
  }

  // The subject.
  const published = http.post(
    `${BASE}/api/collections/${id}/publish`,
    JSON.stringify({ curatorId: curator }),
    tagged('publish'),
  );

  const ok = check(published, { 'published 200': (r) => r.status === 200 });

  if (!ok) {
    publishFailures.add(1);
    console.error(`publish ${published.status}: ${published.body}`);
    return;
  }

  // Recorded from the tagged request rather than read off the summary, so the
  // trend holds only successful publishes. A 400 that returns in 2ms would
  // otherwise flatter the percentile it is supposed to be caught by.
  publishDuration.add(published.timings.duration);
}

export function teardown() {
  const full = http.get(`${BASE}/api/outbox`, tagged('outbox-after'));
  outboxReadDuration.add(full.timings.duration);

  console.log(
    `outbox read after the run:    ${full.timings.duration.toFixed(1)} ms, ` +
      `${full.body.length} bytes`,
  );

  // The number the first baseline run did not print, and the one that turned
  // out to matter most. The relay reads at most BatchSize rows per poll, so its
  // ceiling is BatchSize/PollInterval messages per second no matter how fast
  // publishes arrive - 20 per 250ms, which is 80/s. A run that publishes faster
  // than that does not slow the relay down; it builds a backlog the relay works
  // off after the load stops. Counting what is still unsent at the end is how
  // that ceiling becomes visible instead of theoretical.
  //
  // Slightly optimistic by construction: k6's graceful stop means the relay
  // gets a second or two of quiet before this request, so the true backlog at
  // the moment load ended was larger than what this prints.
  const rows = full.json();
  const staged = rows.length;
  const unsent = rows.filter((row) => !row.delivered).length;

  console.log(`outbox rows: ${staged} staged, ${staged - unsent} delivered, ${unsent} still unsent`);
}
