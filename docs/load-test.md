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

*Not run yet: the owner runs it (Phase 5).*

| Date | Image | Lists | Sustained screenings/s | p95 at that rate | CPU | Memory | Stopped by |
|---|---|---|---|---|---|---|---|
| | | | | | | | |

### Local baseline (2026-11-06, a desktop with many cores, so not the limit to plan with)

Every one of the 10 fetched lists loaded (more than production's active ones), the rate limit off, workstation GC:

| Rate | Screenings | p50 | p95 | p99 | Failures | API memory |
|---|---|---|---|---|---|---|
| Up to 20/s for 20 s, after 5/s | 809 | 20 ms | 126 ms | 283 ms | 0 | 415 MB working set, 327 MB private |

A screening is CPU-bound and never touches the database, so on 0.25 vCPU expect roughly a quarter of one core's
throughput: the Azure run is the number that counts. Memory is the other limit to watch: if the replica restarts
for memory (Container App → Revisions → restarts, or `OOMKilled` in the system logs), that's the cap before CPU, and
the next step (0.5 vCPU / 1 GiB) is the owner's decision.
