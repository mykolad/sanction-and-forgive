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

### Rust, before and after its tuning (2026-11-10)

The owner asked (2026-11-10) to load-test Rust (`ports/rust`, deployable since #40) at 0.5 vCPU / 1 GiB, tune it if
needed, then test it at both sizes. The same inputs, steps (80 added between 64 and 96) and lists as the runs above,
the rate limit off only during each run.

| Implementation | Replica | Sustained screenings/s | p95 at that rate (p50) | Next step | CPU (highest minute) | Memory (working set) | Restarts |
|---|---|---|---|---|---|---|---|
| Rust rust-effe0c3 (as ported) | 0.5 vCPU / 1 GiB | **64** | 52 ms (18 ms) | 96/s: p95 4.11 s | 0.39 vCPU | 396-536 MiB, rising | 0 |
| Rust rust-906fa0d (#42) | 0.5 vCPU / 1 GiB | **96** | 164 ms (18 ms) | 128/s: p95 2.18 s | 0.46 vCPU | 373-507 MiB | 0 |
| Rust rust-906fa0d (#42) | 0.25 vCPU / 0.5 GiB | **48** | 504 ms (78 ms) | 64/s: p95 2.74 s | 0.25 vCPU | 301-448 MiB | 0 |

No request failed in these runs (31,515, 57,508 and 22,268 screenings).

All five implementations and sizes side by side (sustained screenings/s):

| | 0.5 vCPU / 1 GiB | 0.25 vCPU / 0.5 GiB |
|---|---|---|
| .NET | 24 | 12 |
| Go (#35) | 64 | 32 |
| Rust (#42) | **96** | **48** |

- **Rust as ported matched Go** (64/s), with a lower p95 at that rate (52 ms against 207 ms) and less CPU. Its working
  set grew under load, so #42 tuned it: FxHash for the matcher's maps, mimalloc, and screenings on a bounded pool
  (Tokio's default let a burst start hundreds of threads on half a vCPU). Locally 2.80 to 2.11 ms a screening, every
  result the same.
- **Tuned, Rust is 4 times .NET on the same replica** (96 against 24, 48 against 12) and 1.5 times Go. At 0.25 vCPU /
  0.5 GiB it handles twice what .NET handles at 0.5 vCPU / 1 GiB, in 301-448 MiB.
- Which implementation runs, and at what size, is the owner's decision. Production stays on .NET at 0.5 vCPU / 1 GiB.

**An open finding: a stall that isn't the app.** Three of the eleven runs on 2026-11-10 stalled at a few screenings a
second, at a different place each time: Go at 0.25 vCPU (14:17 UTC, at 2/s), Rust #42 at 0.5 vCPU (18:03, at 6/s) and
Rust #42 at 0.25 vCPU (18:45, at 4/s). Each time:

- the app was nearly idle (at most 0.04-0.08 vCPU in Azure Monitor) and screened at the rate it received requests;
- the platform's probes of `/healthz` timed out (1 s) a few minutes into the revision's life;
- the built-in sign-in's sidecar (`http-auth`, which every request passes through since the admin sign-in, #19) shut
  down and started again, and the requests then failed or waited for seconds;
- the same revision, run again a few minutes later, was clean (the rows above).

Memory doesn't explain it alone (one stall was at 470 MiB of 1 GiB). The runs that stalled were on revisions a few
minutes old; the clean re-runs on the same revisions about ten minutes old, but some first runs on fresh revisions
were clean too. Worth a look before a production size change: a run with the sign-in off (the sidecar gone), or the
sidecar's own logs.

Production went back to .NET at 0.5 vCPU / 1 GiB with the rate limit at 120 at 19:19 UTC on 2026-11-10.

**With the sign-in off (2026-11-10, 20:20-21:22 UTC).** Rust #42 at 0.25 vCPU / 0.5 GiB, the platform's sign-in
switched off (no `http-auth` sidecar; the app first deployed with `Admin__SignInEnabled` false), three runs, each on a
freshly restarted replica:

| Run | Screenings | Failed | Sustained | p95 at 48/s | Then |
|---|---|---|---|---|---|
| 1 (20:23) | 22,754 | 0 | **48/s** | 943 ms | 64/s: p95 2.48 s |
| 2 (20:50) | 23,323 | 0 | **48/s** | 36 ms | 64/s: p95 2.08 s |
| 3 (21:15) | 566 | 6 | 4/s | | stalled at 6/s; the platform started a second replica of the same revision (21:20:56) and requests waited up to 60 s |

So the sidecar isn't the cause: a run stalled without it. What every stall, with the sign-in on or off, has in common
is a probe that timed out after the platform's default 1 s while the app was nearly idle; the readiness probe timed out
73-77 times in each of the clean runs too. With one replica, three missed readiness probes in a row take it out of
service and leave the ingress nothing to send requests to. The next step, below: give the probes 5 s.

### Probes: 5 s to answer (2026-11-11)

`infra/app.bicep`'s probes waited the platform's default 1 s for `/healthz` and `/readyz`. Now each waits 5 s
(`timeoutSeconds`), and a ready replica is taken out of service after six missed readiness probes in a row (a minute)
rather than three; liveness and startup keep their thresholds. Becoming ready still takes a single answer, so a new
revision isn't slower to take traffic. The check (11:47-12:53 UTC): the same three fresh-replica runs of Rust #42 at
0.25 vCPU / 0.5 GiB, the sign-in on.

| Run | Screenings | Failed | Sustained | p95 at 48/s | Then | Probe failures (other than 503 while loading) |
|---|---|---|---|---|---|---|
| 1 (11:49, new revision) | 385 | 5 | 2/s | | stalled at 2/s, 6 minutes after the replica started | none |
| 2 (12:02, restarted) | 22,276 | 0 | **48/s** | 861 ms | 64/s: p95 4.31 s | none |
| 3 (12:27, restarted) | 22,536 | 0 | **48/s** | 193 ms | 64/s: p95 4.33 s | 8 liveness timeouts (5 s), 12:31-12:41 |

So the probe change **didn't stop the stall**:

- **Run 1 stalled with no platform event at all**: no probe failure, no restart, no replica moved (the system log has
  nothing between 11:48:52 and 12:00:36). The app screened a steady 2 a second until 11:53:40 and then received
  nothing. The sign-in sidecar logged nothing either until 11:54:37, when it failed about a dozen requests within
  four seconds, each "Unexpected end of request content" while reading the request from the ingress: their callers had
  gone (k6 stopped at 11:53:53). Requests stopped somewhere in the platform, before the app, for about a minute.
- **The readiness timeouts are gone** (none in the three runs, 73-77 a run before), so the replica is no longer taken
  out of service by a slow probe. But run 3, a clean run, had 8 liveness timeouts at 5 s while screenings were
  answered with a p95 of 26 ms and `/readyz`, on the same port, never failed: `/healthz` answers from memory on the
  app's async threads, apart from the screening pool. The probes' own path is slow at times, not the app.
- Across the two days, five runs stalled, all on Go or Rust, each within the first ten minutes of a new or restarted
  replica; runs on the same replica afterwards were clean. None of the .NET runs stalled, but there were only a few,
  too few to say .NET is immune.

The 5 s probes stay: they cost nothing and keep a slow probe from taking the only replica out of service. The stall
is the platform's (the Consumption plan's request path), and what to do about it is the owner's decision.

### Two small replicas (2026-11-11)

The owner asked (2026-11-11) for a run on **two** Rust replicas at 0.25 vCPU / 0.5 GiB: together the same vCPU and memory,
and so the same Consumption price, as one 0.5 vCPU / 1 GiB replica. `APP_REPLICAS=2` (#46) sets both `minReplicas` and
`maxReplicas`. Rust #42, the sign-in on, the 5 s probes, three runs on fresh replicas (13:21-14:49 UTC); steps up to
128 a second, with 80 added from run 2.

| Run | Screenings | Failed | Sustained | p95 there (p95 at 48/s) | Then | Probe timeouts (5 s) |
|---|---|---|---|---|---|---|
| 1 (13:21, new revision) | 32,187 | 0 | **64/s** | 94 ms (30 ms) | 96/s: p95 2.48 s | none |
| 2 (13:50, restarted) | 32,034 | 0 | **64/s** | 553 ms (52 ms) | 80/s: p95 2.08 s | 28 readiness, one replica, 13:58-14:14 |
| 3 (14:18, restarted) | 55,567 | 0 | **96/s** | 1.86 s (30 ms; 80/s: 264 ms) | 128/s: p95 3.84 s | 12 liveness, one replica, 14:29-14:37 |

Each replica: up to 0.21-0.25 vCPU (the highest 1-minute values, so at its limit at the top steps), 370-494 MiB
working set of the 512 MiB (a little more than one small replica's 301-448 MiB), no restarts. The ingress split the
requests evenly (run 1: 16,205 and 16,204).

- **No stall in the three runs**, against one in three on one small replica this morning (and in yesterday's three runs with the sign-in off). Three runs
  are too few to call it fixed; the platform still timed out probes at 5 s, now under load with the replicas at their
  CPU limit, but no request failed and no replica restarted or left service.
- **Capacity: 64-96 a second**, against 48 on one small replica and 96 on one 0.5 vCPU replica (#42): at the same
  price, two small replicas sustained no more than one large one, and in two runs of three less. Each small replica
  carries its own copy of the indexes and its own sign-in sidecar.
- Memory is the tighter limit: 494 MiB of 512 at the top steps. A list added later would need the larger size.

Production went back to .NET at 0.5 vCPU / 1 GiB, one replica, the rate limit at 120, at 14:52 UTC.

### Where the small replica's memory goes (2026-11-11)

The owner asked how to use less memory. Measured locally: Rust #42 in Docker with `--cpus 0.25 --memory 512m`,
production's six lists from a copy of the database, the rate limit and sign-in off, two minutes at 64 a second, then
two at 160 (past this desktop's capacity for a quarter core, about 100), k6's thresholds off so every run lasts as
long. Memory is `docker stats`' figure, the container's working set.

| Rust | Idle | Peak | Requests at 160/s |
|---|---|---|---|
| as deployed | 234 MiB | 283 MiB | all answered, slowly; k6 couldn't start 4,906 more for the ones waiting |
| with a cap of 4 running and 32 waiting | 231 MiB | 274 MiB | 20% refused at once, 503 busy |
| `MIMALLOC_PURGE_DELAY=0` (freed memory returned at once) | 252 MiB | 278 MiB | as deployed; back to 249 MiB after |

- **The indexes are most of it**: about 230 MiB with nothing running. Load adds 40-50 MiB however it's tuned; the
  requests waiting under overload only about 6-9 MiB of that. The cap went in anyway (owner's decision), for quick
  answers past capacity: `Screening:MaxConcurrent` and `Screening:MaxWaiting`, docs/api.md.
- **The sign-in sidecar is the next largest**: the same Rust image on Azure at 0.25 vCPU / 0.5 GiB peaked at 337-399 MiB
  with the sign-in off (2026-11-10) and 412-516 MiB with it on (2026-11-11), so about 80-120 MiB of the replica's 512
  under load. The owner decided (2026-11-11) to sign in within the app instead, with no sidecar.

### .NET and Go, one after the other (2026-11-10)

The owner's experiment (2026-11-09, docs/conformance.md): the same application in .NET and in Go (`ports/go`, after
the tuning in #35), deployed one at a time with the deploy workflow's `implementation` input (#36), each at both sizes.
The same 2,000 inputs (`tests/load/inputs.csv`, from the six production lists on 2026-11-07), the same steps
(`STAGES=1,2,4,6,8,12,16,24,32,48,64,96`; 128 and 192 also offered to Go at 0.5 vCPU), the same six lists (eu,
ofac-cons, ofac-sdn, ua-sfms, uk, us-export: 37,388 subjects), the rate limit off only during each run. CPU and memory
are the highest 1-minute values Azure Monitor reports (`UsageNanoCores`, `WorkingSetBytes`); a screening's CPU comes
in bursts that 1-minute values smooth out.

| Implementation | Replica | Sustained screenings/s | p95 at that rate (p50) | Next step | CPU (highest minute) | Memory (working set) | Restarts |
|---|---|---|---|---|---|---|---|
| .NET 7ecf223 | 0.5 vCPU / 1 GiB | **24** | 62 ms (21 ms) | 32/s: p95 2.09 s | 0.32 vCPU | 501-580 MiB | 0 |
| .NET 7ecf223 | 0.25 vCPU / 0.5 GiB | **12** | 196 ms (22 ms) | 16/s: p95 2.29 s | 0.14 vCPU | 470-545 MiB | 0 |
| Go go-7ecf223 | 0.5 vCPU / 1 GiB | **64** | 207 ms (20 ms) | 96/s: p95 4.15 s | 0.47 vCPU | 393-509 MiB | 0 |
| Go go-7ecf223 | 0.25 vCPU / 0.5 GiB | **32** | 526 ms (20 ms) | 48/s: p95 4.37 s | 0.22 vCPU | 339-423 MiB | 0 |

No request failed in the runs above (13,067, 5,080, 31,980 and 15,560 screenings), and k6 never dropped an
iteration.

- **Go screens 2.7 times as fast as .NET on the same replica**, at both sizes (64 against 24, 32 against 12): about
  what the local measurements said (7.3 ms against 14.5 ms a screening on the real lists, docs/conformance.md).
- **Go at the small size (0.25 vCPU / 0.5 GiB) outdoes .NET at the large one**: 32 screenings/s against 24, in
  339-423 MiB of the 512 MiB (the runtime's soft limit is 80% of it, `ports/go/memlimit`). The owner's question, back to
  the smaller size: with Go, yes. Which implementation runs, and at what size, is the owner's decision.
- **.NET sustained 24/s at 0.5 vCPU, against 32/s on 2026-11-07**: the same inputs and steps; the lists have grown a
  little since (ofac-sdn 17,651 subjects). Its first failing step is still within 32-48/s, as then.
- **The first Go run at 0.25 vCPU (14:17 UTC) stalled at 2/s** (p95 11 s, 2 of 435 failed) while the app itself was
  idle: Azure Monitor showed 0.04 vCPU at most and the audit log one screening per request at the offered rate. The
  platform's sign-in sidecar (`http-auth`) restarted during it ("Application is shutting down", a liveness probe timed
  out). The same revision, run again ten minutes later, gave the row above. Treated as a platform event, not the app's
  limit; a later run that stalls the same way would say otherwise.
- **The import, run by the Go CLI** while Go was deployed (03:30 UTC), failed for ofac-sdn with `driver: bad
  connection`: a pooled connection gone dead during the 2¼-minute download (fixed in #38). The five other lists were
  fine. The .NET deploy after the runs put the job back on the .NET CLI, and a manual run updated ofac-sdn.

Production went back to .NET at 0.5 vCPU / 1 GiB with the rate limit at 120 at 14:47 UTC on 2026-11-10.

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

The limit is between 32 and 48 a second, four times the 8 measured on the smaller size, but not a like-for-like
comparison: that run had 274d26e (before #25's 22% less CPU per screening) and only hard inputs, this one #25 and
half regular inputs. Twice the CPU is most of the gain; the rest is #25 and the lighter half. Memory is no longer
close to its limit (about 45% free). The per-IP rate limit (120 a minute) is a sixteenth of the capacity.

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
  the golden tests and evaluation to redo, the owner's approval), and a list that isn't loaded must still be Not checked on
  its own, so each candidate would carry its lists. The speed-up above was larger and changed nothing; combining stays
  an option if more lists are added and memory runs short.

A screening is CPU-bound and never touches the database, so on 0.25 vCPU expect roughly a quarter of one core's
throughput: the Azure run is the number that counts. Memory is the other limit to watch: if the replica restarts
for memory (Container App → Revisions → restarts, or `OOMKilled` in the system logs), that's the cap before CPU, and
the next step (0.5 vCPU / 1 GiB) is the owner's decision.
