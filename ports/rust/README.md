# Sanction & Forgive in Rust

The third implementation in the owner's experiment (2026-11-09): the same application in .NET, Go and Rust, one
deployed at a time, compared (`docs/conformance.md`). Like the Go port (`ports/go`), it's a port of the .NET reference,
not a redesign, and the conformance suite is the yardstick.

**Where it stands:** the matcher (`src/matching`), every rule of `SanctionAndForgive.Matching`, with the Go port's speed
work. The API, the CLI, the feeds and the database come next; until then the CI's conformance job doesn't run it.

- **The same dictionaries**, embedded at build time from `src/SanctionAndForgive.Matching/Data` (`build.rs`), never
  copied by hand; the website's pages likewise from `src/SanctionAndForgive.Api/wwwroot`. The matcher version is the
  reference's (`566ac3d19d5b`, a unit test checks it).
- **The reference's details**, as the Go port reproduces them: dictionaries read in file order (`serde_json`'s
  `preserve_order`), stable sorts, the invariant culture's casing that leaves the Turkish `İ` and `ı` alone, FNV over
  UTF-16 units, and the C library's logarithm (`f64::ln` is glibc's `log` on Linux, as .NET's `Math.Log` is: build for
  `linux-gnu`, not musl).
- **Speed**, as in the Go port: a search scores each alias to a percentage only and explains the few it reports; a
  pair of words is compared once per search in a direct-mapped cache, emptied when the search ends (nothing entered
  stays in memory); edit distance on bytes for ASCII, on the stack, over the band within its limit (a test checks it
  against the full matrix).

## Checking it against the Go port on the real lists

The Go bench exports the lists it loaded from the database and writes every result in a common form
(`src/canonical.rs`); the Rust bench builds the same lists, screens the same inputs and writes the same form:

```powershell
$env:ConnectionStrings__Lists = "Server=localhost,14333;Database=sanctions_local;User ID=sa;Password=Throwaway-container-2026!;TrustServerCertificate=True"
cd ports/go;   go run ./cmd/bench -rounds 0 -export ../rust/lists.json -canonical ../rust/go.ndjson
cd ../rust;    cargo run --release --bin bench -- --lists lists.json --dump rust.ndjson --rounds 3
```

Then compare the two files line for line (each line is one screening's JSON). The Go port's results are the
reference's (`ReferenceTests`), so equal dumps mean the Rust matcher's are too. On 2026-11-10, against the ten real
lists (160,340 aliases) and the load test's 2,000 inputs: **2,000 of 2,000 the same**, 3.1 ms a screening (Go's
bench: 2.8 ms; both native on one Windows desktop).

```powershell
cargo test            # the matcher version, and the banded edit distance against the full matrix
cargo fmt --check; cargo clippy --all-targets -- -D warnings
```
