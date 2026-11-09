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
  `Admin__SignInEnabled`, `Screening__*`, `App__Version`), and the API listens on 8080 (`PORT` overrides).
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
