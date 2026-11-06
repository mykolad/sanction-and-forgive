// Screening load test (spec section 7, "Load test"). Run with k6 (https://k6.io) from your machine, against an app
// deployed with the rate limit off (infra/README.md, "Load tests"), after generating the inputs from the live lists:
//
//   ./tools/lists.ps1 load-inputs -File tests/load/inputs.csv
//   k6 run -e BASE_URL=https://<the app's address> tests/load/screening.js
//
// Each iteration is one screening, as the page sends it, with a row of inputs.csv (listed names, near misses,
// Cyrillic, unlisted names, addresses and initials). The arrival rate grows in steps (STAGES, screenings per second)
// until the app clearly can't keep up: k6 stops the run when p95 passes 2 s or more than 2% of requests fail.
// Report the last step it sustained, with its p95, in docs/load-test.md.
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';

const BASE_URL = (__ENV.BASE_URL || '').replace(/\/$/, '');
if (!BASE_URL) throw new Error('Set BASE_URL, e.g. -e BASE_URL=https://sanction-and-forgive.<environment domain>');

// Screenings per second, step by step. STAGES='1,2,4' overrides; STEP is each step's length.
const STAGES = (__ENV.STAGES || '1,2,4,6,8,12,16,24,32').split(',').map(Number);
const STEP = __ENV.STEP || '2m';
const VUS = Math.max(20, Math.ceil(Math.max(...STAGES) * 3));

// receiverName,companyName,address,kind: every field quoted or empty (load-inputs writes it that way).
const inputs = new SharedArray('inputs', () => {
  const lines = open(__ENV.INPUTS || './inputs.csv').split(/\r?\n/).slice(1).filter(line => line.trim());
  return lines.map(line => {
    const fields = [];
    let field = '';
    let quoted = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (quoted) {
        if (c === '"' && line[i + 1] === '"') { field += '"'; i++; }
        else if (c === '"') quoted = false;
        else field += c;
      } else if (c === '"') quoted = true;
      else if (c === ',') { fields.push(field); field = ''; }
      else field += c;
    }
    fields.push(field);
    return { receiverName: fields[0] || null, companyName: fields[1] || null, address: fields[2] || null, kind: fields[3] };
  });
});

export const options = {
  scenarios: {
    screenings: {
      executor: 'ramping-arrival-rate',
      timeUnit: '1s',
      startRate: 1,
      preAllocatedVUs: VUS,
      maxVUs: VUS,
      stages: STAGES.flatMap(rate => [
        { target: rate, duration: '20s' },
        { target: rate, duration: STEP },
      ]),
      gracefulStop: '30s',
    },
  },
  thresholds: {
    // Stop once the app clearly can't keep up (after a minute, so a cold start doesn't count).
    http_req_duration: [{ threshold: 'p(95)<2000', abortOnFail: true, delayAbortEval: '1m' }],
    http_req_failed: [{ threshold: 'rate<0.02', abortOnFail: true, delayAbortEval: '1m' }],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const status = http.get(`${BASE_URL}/api/v1/status`).json();
  if (status.state !== 'ready') throw new Error(`The app isn't ready: ${status.state}`);
  console.log(`Lists: ${status.lists.map(list => `${list.code} ${list.state} ${list.recordCount}`).join(', ')}`);
}

export default function () {
  const input = inputs[Math.floor(Math.random() * inputs.length)];
  const response = http.post(`${BASE_URL}/api/v1/screenings`, JSON.stringify({
    receiverName: input.receiverName,
    companyName: input.companyName,
    address: input.address,
  }), {
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    tags: { name: 'screening', kind: input.kind },
  });
  check(response, {
    'screening 200': r => r.status === 200,
    'not rate limited': r => r.status !== 429,
  });
}
