# Load test

How fast one replica (always on; 0.5 vCPU / 1 GiB since 2026-11-07, 0.25 vCPU / 0.5 GiB before) can screen, and
what limits it. Spec section 7, "Load test".

## How to run it

From the owner's machine, with k6 installed:

```powershell
# 1. Inputs from the live lists. Half hard (listed names, near misses, Cyrillic, common surnames, addresses, initials:
#    the most candidates), half regular (ordinary Latin company and person names, and listed companies shortened as
#    people write them, "AAA Inc." or "AAA"); k6 reports the two halves as mix:hard and mix:regular.
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

### Azure, one 0.5 vCPU / 1 GiB replica (since 2026-11-07)

Half hard, half regular inputs (#26): 2,000 from the 6 active lists (37,370 subjects), 979 of them regular.

| Date | Image | Sustained screenings/s | p95 at that rate | CPU | Memory | Stopped by |
|---|---|---|---|---|---|---|
| 2026-11-07 | f86757c (after #25) | **32** (every step passed) | 0.22 s (p50 23 ms) | up to 0.38 of 0.5 vCPU (highest 1-minute value) | 510-570 MiB working set of 1 GiB, no restarts | p95 2.2 s at 48/s (a second run with steps 48, 64, 96) |

| Step | Screenings | p50 | p95 | p99 |
|---|---|---|---|---|
| 1-16/s | 139-2,200 a step | 21-23 ms | 45-55 ms | 100-153 ms |
| 24/s | 3,279 | 21 ms | 95 ms | 259 ms |
| 32/s | 4,401 | 23 ms | 219 ms | 457 ms |
| 48/s | 945 (stopped 31 s in) | 778 ms | 2.2 s | 2.6 s |

By half, over the whole first run (14,390 screenings, none failed, none dropped): hard p50 23 ms, p95 128 ms, p99
361 ms; regular p50 20 ms, p95 97 ms, p99 281 ms. So the regular half is only a little cheaper than the hard one:
most of a screening's cost is scoring each name's candidates on six lists, which ordinary names have too.

The limit is between 32 and 48 a second, four times the 8 of the smaller size with the same code: twice the CPU, and
the memory no longer close to its limit (about 45% free). The per-IP rate limit (120 a minute) is a sixteenth of it.

### Azure, one 0.25 vCPU replica

| Date | Image | Lists | Sustained screenings/s | p95 at that rate | CPU | Memory | Stopped by |
|---|---|---|---|---|---|---|---|
| 2026-11-06 | 358c9b0 | 6 (eu, ofac-cons, ofac-sdn, ua-sfms, uk, us-export: 37,362 subjects) | **2** | 0.86 s (p50 39 ms) | 0.07-0.14 vCPU (highest 1-minute value) | 380-450 MB working set of 512 MiB, no restarts | p95 5.6 s at 4/s |
| 2026-11-06 | 274d26e (after #23) | the same 6 | **8** | 0.25 s (p50 26 ms) | up to 0.14 vCPU (highest 1-minute value) | 430-540 MiB working set, no restarts | p95 2.9 s at 12/s |

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

| | Before | After #23 | After #25 |
|---|---|---|---|
| Mean per screening, one desktop core | 21.8 ms | 9.3 ms | 7.3 ms |
| p50 / p95 / p99 | 11.2 / 80 / 179 ms | 5.8 / 28 / 62 ms | 4.3 / 23 / 56 ms |
| Screenings a second on one core | 46 | 108 | 138 |
| Allocated per screening | 22 MB | 8 MB | 5.4 MB |

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

The second run (after #23, the procedure's revision wait included), per step: 1/s p95 284 ms, 2/s 121 ms, 4/s 126 ms,
6/s 200 ms, 8/s 253 ms (p50 26 ms; 1,100 screenings), then 12/s p95 2.9 s, where it stopped; no request failed,
none dropped. Four times the first run's 2 a second. The working set rose with the load to 540 MiB at 8-12 a
second (the platform's figure; the replica didn't restart), so memory, not CPU, is now the closer limit on this
size: #25 allocates a third less per screening.

On Azure after the deploy (9e8173f, the same replica, the same stream of 1,000 inputs as above): 1,000 in 71 s,
**14.0 records a second** (6.5 before), no errors, so a 200 s stream covers about 2,800 records and a 50,000-record
batch about an hour. Concurrent single screenings weren't rerun (that needs the rate limit off again); expect
roughly twice the 2 a second above.

- (#25) Explanations are built only when read: every candidate alias is scored (percentage and caps), but only the
  subjects a search reports are explained, the best one and those at Review or above, which is all a cell or the
  allow-list's possible matches use. `SearchName` no longer returns the subjects below Review behind the best.

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
