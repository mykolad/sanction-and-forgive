# Load test

How fast one replica (0.25 vCPU / 0.5 GiB, always on) can screen, and what limits it. Spec section 7, "Load test".

## How to run it

From the owner's machine, with k6 installed:

```powershell
# 1. Inputs from the live lists (about 30% listed names, 20% near misses, 20% Cyrillic, 20% unlisted, 10% addresses
#    or surname + initials; common surnames on purpose: they have the most candidates).
./tools/lists.ps1 load-inputs -File tests/load/inputs.csv

# 2. The rate limit off (it would answer 429 long before the app is the limit):
$env:IMAGE_TAG = '<the running sha>'; $env:RATE_LIMIT_PER_MINUTE = '0'
az deployment group create -g sanction-and-forgive-rg -n app --parameters infra/app.bicepparam
#    Then wait until the new revision has all the traffic and has loaded its lists: until then the old one answers
#    (and k6's readiness check passes on it). latestReadyRevisionName can still name the old one: look at the traffic.
az containerapp revision list -g sanction-and-forgive-rg -n sanction-and-forgive --query "[?properties.trafficWeight==``100``].{name:name, created:properties.createdTime, rate:properties.template.containers[0].env[?name=='RateLimiting__PerMinute'].value | [0]}" -o table

# 3. The run: screenings per second in steps of 2 minutes (1, 2, 4, 6, 8, 12, 16, 24, 32), stopping by itself when
#    p95 passes 2 s or more than 2% fail. Note the CPU and memory in the portal (Container App → Metrics) meanwhile.
k6 run -e BASE_URL=https://<the app's address> tests/load/screening.js

# 4. The rate limit back on: the same deployment without it.
Remove-Item Env:RATE_LIMIT_PER_MINUTE
az deployment group create -g sanction-and-forgive-rg -n app --parameters infra/app.bicepparam
```

Every request is tagged with its step (`step:<rate>`), and each step has its own thresholds, so k6's summary shows
`http_req_duration{step:N}` and `http_req_failed{step:N}` per plateau, and one bad step can't hide behind good ones.
The run also stops if k6 itself drops scheduled iterations (`dropped_iterations`): the rate asked for wasn't the rate
measured. It refuses to start unless the app is ready and at least one list is Current.

Report below: the highest step sustained (its own p95 under 2 s and failures under 2%), its p95, CPU and memory, and
what stopped the run.

## Results

### Azure, one 0.25 vCPU replica

| Date | Image | Lists | Sustained screenings/s | p95 at that rate | CPU | Memory | Stopped by |
|---|---|---|---|---|---|---|---|
| 2026-11-06 | 358c9b0 | 6 (eu, ofac-cons, ofac-sdn, ua-sfms, uk, us-export: 37,362 subjects) | **2** | 0.86 s (p50 39 ms) | 0.07-0.14 vCPU (highest 1-minute value) | 380-450 MB working set of 512 MiB, no restarts | p95 5.6 s at 4/s |

Per step (2,000 inputs from the live lists; no request failed, none rate limited, no iteration dropped):

| Step | Screenings | p50 | p95 | p99 | max |
|---|---|---|---|---|---|
| 1/s | 139 | 68 ms | 0.77 s | 1.9 s | 4.5 s |
| 2/s | 270 | 39 ms | 0.86 s | 3.4 s | 4.6 s |
| 4/s | 78 (stopped 31 s in) | 112 ms | 5.6 s | 7.1 s | 7.5 s |

Reading it:

- **One 0.25 vCPU replica sustains about 2 screenings a second** (120 a minute, which is also the per-IP rate limit).
  At 4 a second requests queue: p95 climbs past 5 s within half a minute.
- **The limit is the CPU quota, not the average.** The per-minute CPU never passed 0.14 vCPU of the 0.25, but a
  screening is a burst of CPU (20 ms median on a desktop core, more for common surnames) and a 0.25 vCPU quota lets
  the container use a quarter of each scheduling period: a burst that needs more waits for the next period, and
  bursts that arrive together queue behind each other. This is the most likely reading; the platform doesn't
  report throttling.
- **The tail at 1 and 2 a second** (p99 2-3 s) is partly the start: the run's first half minute went to the
  previous revision, and the new one was still building its indexes during the first steps (the procedure above now
  waits for that). Step 4 ran on a settled replica.
- **Memory has the least headroom:** 380-450 MB working set of the 512 MiB, with six lists. No restarts, but adding
  the TermsUnclear or NeedsAccess lists would leave little room.
- **The stream** (`/api/v1/screenings/stream`, measured the same day with the rate limit back on: one stream of
  1,000 of the same inputs) answered all 1,000 in 154 s, **6.5 records a second**, no errors, the first after 0.3 s.
  One record at a time doesn't queue, so it does better than concurrent single requests. A stream's 200 s therefore
  covers about 1,300 records here: a 50,000-record batch is about 40 streams, each resuming from `nextLine`, roughly
  2 hours on one replica.

The next step (spec section 7) is 0.5 vCPU / 1 GiB, about twice the cost of the replica: the owner's decision.
Rerun this test after any change in size.

### Local baseline (2026-11-06, a desktop with many cores, so not the limit to plan with)

Every one of the 10 fetched lists loaded (more than production's active ones), the rate limit off, workstation GC:

| Rate | Screenings | p50 | p95 | p99 | Failures | API memory |
|---|---|---|---|---|---|---|
| Up to 20/s for 20 s, after 5/s | 809 | 20 ms | 126 ms | 283 ms | 0 | 415 MB working set, 327 MB private |

## Profiling (2026-11-06)

After the Azure run: production's six lists from a local copy of the database, the same 2,000 inputs, one thread,
the API's GC settings, and `dotnet-trace`. A screening never touches the database; it's all CPU.

| | Before | After (#23) |
|---|---|---|
| Mean per screening, one desktop core | 21.8 ms | 9.3 ms |
| p50 / p95 / p99 | 11.2 / 80 / 179 ms | 5.8 / 28 / 62 ms |
| Screenings a second on one core | 46 | 108 |
| Allocated per screening | 22 MB | 8 MB |

Where the time went: scoring about 1,560 candidate aliases per screening (up to 2,000 a list, six lists), each
aligned word by word; finding the candidates was about 1 ms. The changes, each leaving every result identical (a full
dump of the 2,000 results, every percentage, alignment and explanation, compared byte for byte, and the evaluation
report unchanged):

- The best one-to-one word pairing searched every subset of the listed name's words (up to 4,096 states, in a new
  two-dimensional array per candidate: 58% of the time). It now searches only the listed words that can pair with an
  entered word at all, which gives the same pairs.
- A search compares the same entered words with the same listed words (an index interns its tokens) many times: each
  pair is compared once per search (23,000 comparisons a screening, a third of them distinct), and each word's
  spelling and cognate groups, and its weight, are looked up once.
- Jaro-Winkler is computed only for the edit distances that use it, and both edit-distance functions keep their rows
  on the stack.

On Azure after the deploy (9e8173f, the same replica, the same stream of 1,000 inputs as above): 1,000 in 71 s,
**14.0 records a second** (6.5 before), no errors, so a 200 s stream covers about 2,800 records and a 50,000-record
batch about an hour. Concurrent single screenings weren't rerun (that needs the rate limit off again); expect
roughly twice the 2 a second above.

Left as it is: building every candidate's explanation (about a third of what remains) could wait until a candidate is
kept, a larger change for perhaps 25% more.

**Combining lists into one index** (the owner's question): many listed parties appear on several lists, so one index
with each entry referring to its lists would hold fewer names.

- Overlap, by primary name: 37,362 subjects, 32,202 distinct names, 5,160 rows (14%) repeating a name another list
  has. ua-sfms 95% (mostly UN, EU and UK entries), uk 46%, ofac-cons 33%, eu 24%, ofac-sdn 13%, us-export 8%. Of all
  114,723 names and aliases, 21% repeat.
- So at most about 15-20% less memory (of 128 MB) and fewer aliases to score, but not for free: each list's index
  weights a word by how rare it is in that list, so one index would change every percentage (a calibration change,
  the golden tests and evaluation to redo, the owner's approval), and a list that's stale must still be Not checked on
  its own, so each candidate would carry its lists. The speed-up above was larger and changed nothing; combining stays
  an option if more lists are added and memory runs short.

A screening is CPU-bound and never touches the database, so on 0.25 vCPU expect roughly a quarter of one core's
throughput: the Azure run is the number that counts. Memory is the other limit to watch: if the replica restarts
for memory (Container App → Revisions → restarts, or `OOMKilled` in the system logs), that's the cap before CPU, and
the next step (0.5 vCPU / 1 GiB) is the owner's decision.
