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
import exec from 'k6/execution';
import { SharedArray } from 'k6/data';

const BASE_URL = (__ENV.BASE_URL || '').replace(/\/$/, '');
if (!BASE_URL) throw new Error('Set BASE_URL, e.g. -e BASE_URL=https://sanction-and-forgive.<environment domain>');

// Screenings per second, step by step. STAGES='1,2,4' overrides; STEP is each step's length.
const STAGES = (__ENV.STAGES || '1,2,4,6,8,12,16,24,32').split(',').map(Number);
const STEP = __ENV.STEP || '2m';
const RAMP = '20s';
// A k6 duration ("2m", "90s", "1m30s", "1h") in seconds; anything else stops the run before it starts, since a step
// tag that matches no threshold would silently bypass them.
function seconds(value) {
  const match = /^(?:(\d+)h)?(?:(\d+)m(?!s))?(?:(\d+)s)?$/.exec(value);
  if (!match || !value) throw new Error(`Unsupported duration ${value}: use h, m and s, e.g. 2m or 1m30s`);
  return Number(match[1] || 0) * 3600 + Number(match[2] || 0) * 60 + Number(match[3] || 0);
}
const STEP_SECONDS = seconds(RAMP) + seconds(STEP);
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
        { target: rate, duration: RAMP },
        { target: rate, duration: STEP },
      ]),
      gracefulStop: '30s',
    },
  },
  // Per step (every request is tagged with its step's rate), so one step's p95 and failures are judged on their own,
  // and the summary reports each plateau. Stop once a step clearly can't keep up (judged from 30 s into that step:
  // delayAbortEval counts from the start of the run, so each step's delay is offset by its own start), or once k6
  // itself can't keep the arrival rate (dropped iterations: then what was measured isn't the rate asked for).
  thresholds: Object.assign(
    { dropped_iterations: [{ threshold: 'count<10', abortOnFail: true }] },
    ...STAGES.map((rate, i) => ({
      [`http_req_duration{step:${rate}}`]: [{ threshold: 'p(95)<2000', abortOnFail: true, delayAbortEval: `${i * STEP_SECONDS + 30}s` }],
      [`http_req_failed{step:${rate}}`]: [{ threshold: 'rate<0.02', abortOnFail: true, delayAbortEval: `${i * STEP_SECONDS + 30}s` }],
    }))),
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
  const status = http.get(`${BASE_URL}/api/v1/status`).json();
  if (status.state !== 'ready') throw new Error(`The app isn't ready: ${status.state}`);
  // Lists that aren't Current are Not checked: every screening would answer without matching anything.
  if (!status.lists.some(list => list.state === 'Current')) throw new Error('No list is Current: import the lists first.');
  console.log(`Lists: ${status.lists.map(list => `${list.code} ${list.state} ${list.recordCount}`).join(', ')}`);
}

// The step this moment belongs to (its rate), from the time into the test.
function currentStep() {
  const elapsed = exec.instance.currentTestRunDuration / 1000;
  return STAGES[Math.min(STAGES.length - 1, Math.floor(elapsed / STEP_SECONDS))];
}

export default function () {
  const input = inputs[Math.floor(Math.random() * inputs.length)];
  const response = http.post(`${BASE_URL}/api/v1/screenings`, JSON.stringify({
    reference: `load-${exec.vu.idInTest}-${exec.vu.iterationInScenario}`,
    receiverName: input.receiverName,
    companyName: input.companyName,
    address: input.address,
  }), {
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    tags: { name: 'screening', kind: input.kind, step: String(currentStep()) },
  });
  check(response, {
    'screening 200': r => r.status === 200,
    'not rate limited': r => r.status !== 429,
  });
}
