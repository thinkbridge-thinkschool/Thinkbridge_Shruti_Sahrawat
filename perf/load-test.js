// Day 11 - baseline load profile for the slow endpoint.
//
// Low VU count deliberately: the slow endpoint takes ~14s per request against
// SQLite, which serialises writes and does not benefit from concurrency here.
// Hammering it would measure queueing rather than the endpoint.

import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://localhost:5067';
const TARGET = __ENV.TARGET || 'slow';

export const options = {
  vus: 5,
  duration: '60s',
  thresholds: {
    // Recorded rather than enforced - the point is to observe the baseline.
    http_req_duration: ['p(50)>=0', 'p(95)>=0', 'p(99)>=0'],
  },
};

export default function () {
  const res = http.get(`${BASE}/api/profiling/author-stats-${TARGET}`, {
    timeout: '120s',
  });

  check(res, {
    'status is 200': (r) => r.status === 200,
  });
}