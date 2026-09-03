// Day 21 - measuring what HybridCache actually changed.
//
// Two scenarios, run separately, because they answer different questions and
// would corrupt each other's numbers if they shared a run:
//
//   stampede  - one synchronised burst against a cold cache. Answers "does a
//               miss under concurrency fan out into N database hits?" The
//               number that matters is dbCommands, not latency.
//   sustained - steady load against a warm cache. Answers "what does the
//               database load and the p99 look like once the cache is doing
//               its job?" The numbers that matter are p99 and db commands/sec.
//
// Run each twice - once with Cache:Enabled=true, once false - to get the
// before/after pair. The flag is configuration, so both runs are the same
// build; see QuotesApi/Caching/QuotesCacheOptions.cs for why that matters.
//
//   k6 run --env SCENARIO=stampede  perf/cache-load-test.js
//   k6 run --env SCENARIO=sustained perf/cache-load-test.js
//
// The counters this reads come from GET /api/cache/stats, which counts EF
// commands at the database boundary rather than asking the cache to report on
// itself.

import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

const BASE = __ENV.BASE_URL || 'http://localhost:5067';
const SCENARIO = __ENV.SCENARIO || 'stampede';
const PREVIEW_SIZE = __ENV.PREVIEW_SIZE || '3';

// Every virtual user asks the identical question. Stampede protection
// deduplicates per key, so varying previewSize here would spread the load
// across keys and quietly measure something else.
const TARGET = `${BASE}/api/collections/summaries?previewSize=${PREVIEW_SIZE}`;

const SCENARIOS = {
  // 200 VUs, one iteration each, all released together. maxDuration is a
  // safety net, not a target.
  stampede: {
    executor: 'per-vu-iterations',
    vus: 200,
    iterations: 1,
    maxDuration: '60s',
  },
  // Constant arrival rate rather than constant VUs: it holds the offered load
  // fixed regardless of how fast responses come back, so the cached and
  // uncached runs are compared at the same request rate instead of the cached
  // run silently offering more load because it is faster.
  sustained: {
    executor: 'constant-arrival-rate',
    rate: 200,
    timeUnit: '1s',
    duration: '30s',
    preAllocatedVUs: 100,
    maxVUs: 400,
  },
};

export const options = {
  scenarios: { [SCENARIO]: SCENARIOS[SCENARIO] },
  thresholds: {
    // Recorded, not enforced - this run exists to produce numbers, and a
    // failing threshold on the deliberately-slow "before" run would just be
    // noise.
    http_req_duration: ['p(50)>=0', 'p(95)>=0', 'p(99)>=0'],
    http_req_failed: ['rate<=1'],
  },
};

export function setup() {
  // Zero the counters so the numbers describe this run only. This resets
  // counters, not the cache itself - see CacheDiagnosticsEndpoints for why
  // those are deliberately different operations.
  const reset = http.post(`${BASE}/api/cache/reset`);
  if (reset.status !== 204) {
    throw new Error(
      `Could not reset counters (status ${reset.status}). Is the API running on ${BASE}?`);
  }

  const before = http.get(`${BASE}/api/cache/stats`);
  const stats = before.json();

  console.log('');
  console.log(`=== ${SCENARIO} | cacheEnabled=${stats.cacheEnabled} | L2=${stats.l2} ===`);

  if (SCENARIO === 'stampede' && stats.cacheEnabled) {
    console.log('NOTE: for a true cold-cache burst, restart the API (or wait out');
    console.log('      Cache:Expiration) before this run - resetting counters does');
    console.log('      not empty the cache.');
  }

  return { cacheEnabled: stats.cacheEnabled, l2: stats.l2 };
}

export default function () {
  const res = http.get(TARGET, { timeout: '120s' });

  check(res, {
    'status is 200': (r) => r.status === 200,
    'body is a JSON array': (r) => {
      try { return Array.isArray(r.json()); } catch (e) { return false; }
    },
  });
}

export function teardown(data) {
  const stats = http.get(`${BASE}/api/cache/stats`).json();

  // Wall-clock duration of the load phase, used for the per-second rate. For
  // the sustained scenario this is the configured duration; for the burst it
  // is however long the burst actually took, so it is read from k6 rather
  // than assumed.
  const seconds = SCENARIO === 'sustained' ? 30 : null;

  console.log('');
  console.log(`--- ${SCENARIO} results (cacheEnabled=${data.cacheEnabled}, L2=${data.l2}) ---`);
  console.log(`reads (cached-read calls):      ${stats.reads}`);
  console.log(`factory invocations (db fetch): ${stats.factoryInvocations}`);
  console.log(`reads without database work:    ${stats.readsWithoutDatabaseWork}`);
  console.log(`hit rate:                       ${(stats.hitRate * 100).toFixed(2)}%`);
  console.log(`EF commands executed:           ${stats.dbCommands}`);

  if (seconds) {
    console.log(`EF commands / sec:              ${(stats.dbCommands / seconds).toFixed(2)}`);
  }

  if (SCENARIO === 'stampede') {
    console.log('');
    console.log(`Stampede check: ${stats.reads} concurrent reads produced ` +
                `${stats.factoryInvocations} factory invocation(s) and ` +
                `${stats.dbCommands} EF command(s).`);
    console.log('Without stampede protection this would be roughly 2 EF commands per read;');
    console.log('the read runs two queries (collections, then the quotes in the previews).');
  }

  console.log('');
  console.log('p50/p95/p99 for this run are in the http_req_duration line of the summary below.');
}
