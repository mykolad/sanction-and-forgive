# Sanction & Forgive in Go

The whole application in Go: the API and pages (`cmd/api`), the CLI (`cmd/cli`: imports, manual records, review,
status, history, probe), the matcher (`matching`), the feeds (`lists`) and the database (`store`). Part of the owner's
experiment (2026-11-09): the same application in .NET, Go and Rust, one deployed at a time, compared.

It's a port of the .NET reference, not a redesign. The conformance suite (`docs/conformance.md`) is the yardstick, and
it passes in full: the same 600 + 60 screenings field for field, the same pages, the same import rules. Where the
reference's results depend on .NET details (its dictionaries' insertion order, stable sorts, UTF-16 lengths, the
invariant culture's "20 %", its casing that leaves the Turkish `İ` and `ı` alone, the C library's logarithm), the port
reproduces them; the comments say where. Against the real lists, 2,000 screenings match the reference result for
result (`ReferenceTests`, docs/conformance.md).

- **The same database.** The schema is the EF migrations'; this port never migrates. Imports hash content the same
  way, so a list imported by one implementation is `Unchanged` for the other: production can switch without
  re-importing.
- **The same configuration names** (`ConnectionStrings__Lists`, `Lists__RefreshMinutes`, `RateLimiting__PerMinute`,
  `Admin__SignInEnabled`, `Screening__*`, `App__Version`), and the API listens on 8080 (`PORT` overrides) on every
  interface; `HOST=127.0.0.1` keeps a run on a desktop to loopback, so its firewall doesn't ask about it.
- **One source of truth for data.** The dictionaries (`src/SanctionAndForgive.Matching/Data`) and the pages
  (`src/SanctionAndForgive.Api/wwwroot`) are copied in at build time, never edited here. `api/openapi.json` is the
  reference's document.
- **Not ported:** the CLI's `evaluate` and `load-inputs` (development tools; run them with the .NET CLI).

```powershell
# Build and test (copies the dictionaries and pages first):
./ports/go/build.ps1

# The image (from the repository root), and the conformance suite against it:
docker build -f ports/go/Dockerfile -t sanction-and-forgive-go .
./tests/conformance/run.ps1 -Build -Image sanction-and-forgive-go:conformance -Dockerfile ports/go/Dockerfile -CliCommand '--entrypoint /app/cli {image}'
```

The image is distroless with glibc (non-root), 68 MB against the .NET image's 356 MB; the CLI is `/app/cli`. It's
built with cgo for one reason: the word weights' logarithm must be the C library's, which .NET's `Math.Log` calls (Go's
own `math.Log` is a bit off for some arguments). A native build without a C compiler falls back to `math.Log`.

## Speed and memory

`cmd/bench` loads the lists from the database as the API does and screens the load test's inputs
(`tests/load/inputs.csv`, from the .NET CLI's `load-inputs`), with optional CPU, allocation and heap profiles and a dump
of every result to compare before and after a change:

```powershell
$env:ConnectionStrings__Lists = "Server=localhost,14333;Database=sanctions_local;User ID=sa;Password=Throwaway-container-2026!;TrustServerCertificate=True"
go run ./cmd/bench -rounds 3 -cpuprofile cpu.pprof -dump after.json                      # every list in the database
go run ./cmd/bench -lists eu,ofac-sdn,ofac-cons,uk,us-export,ua-sfms                     # production's six
```

Tuned on 2026-11-10 against the real lists, every result byte for byte the same (the dump, then `ReferenceTests`):

| | Before | After |
|---|---|---|
| Ten lists, 160,340 aliases | 8.7 ms mean, 26 ms p95 | 2.8 ms mean, 8 ms p95 |
| Allocated per screening | 5.2 MB (a third of the CPU in garbage collection) | 0.3 MB |
| Production's six lists | | 2.3 ms mean, 7 ms p95; 149 MiB of heap |

What it took, in `matching`: a search scores each alias to a percentage only, and `Explain` scores the few reported
again to explain them; the scorer's matrices, the votes and the best alias per subject are buffers reused through
pools (arrays the size of the index, reset through what was touched); a pair of words is compared once per search in
a direct-mapped cache; a word's spelling and cognate groups are looked up when it's made, a key's IDF when the index
is built; the edit distance runs on bytes for ASCII, on the stack, over the band that can stay under its limit
(`compare_test.go` checks it against the full matrix); only the reported subjects are sorted; postings are packed
int32 arrays.

**Memory limit.** The API and the CLI set the Go runtime's soft limit to 80% of the container's memory (cgroup
`memory.max`; `memlimit`), unless `GOMEMLIMIT` is set: the collector works harder near it instead of letting the heap
grow to twice what's live, the default. The API logs the limit at startup.
