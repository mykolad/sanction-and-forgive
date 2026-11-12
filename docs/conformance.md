# Conformance suite

A black-box test of the whole application, in whatever language it's written: its HTTP API, its CLI and the database
they share. The .NET implementation is the reference. The golden files record its answers, and another implementation
(Go, Rust: the owner's experiment, 2026-11-09) must give the same answers, field for field. Only one implementation is
deployed at a time.

`tests/SanctionAndForgive.Conformance` (xUnit) references none of the app's projects. `tests/conformance/run.ps1` runs it
against a container image, and the CI job `conformance` runs that script on every pull request.

## Running it

With Docker and the local SQL Server container (CLAUDE.md, "Local SQL Server") running:

```powershell
# The .NET implementation, built from this repository (about 5 minutes once the image is cached):
./tests/conformance/run.ps1 -Build

# Another implementation's image, whose CLI is a binary called "sanctions" in it:
./tests/conformance/run.ps1 -Image sanction-and-forgive-go:dev -CliCommand '--entrypoint /app/sanctions {image}'

# One test, or one group of tests:
./tests/conformance/run.ps1 -TestArguments '--filter', 'FullyQualifiedName~GoldenTests'
```

The script:

1. Builds the image (`-Build`).
2. Drops and re-creates the database `conformance` with the EF migrations.
3. Starts the API container with the settings below, its console output going to a log file.
4. Runs the suite, which:
   - writes the fictional feeds;
   - imports them through the CLI, in containers from the same image with the feeds mounted at `/feeds`;
   - compares, then changes things and puts them back.

A plain `dotnet test` of the solution skips every conformance test: they need `CONFORMANCE_BASE_URL`.

## Implementations

| Implementation | Where | Image | CLI in the image | Conformance |
|---|---|---|---|---|
| .NET (reference) | `src/` | `Dockerfile`, 356 MB | `dotnet SanctionAndForgive.Cli.dll` | 23 of 23 |
| Go | `ports/go/` | `ports/go/Dockerfile`, 68 MB | `/app/cli` | 23 of 23 |
| Rust | `ports/rust/` | `ports/rust/Dockerfile`, 60 MB | `/app/cli` | 23 of 23 |

The CI job `conformance` runs the suite against each of them on every pull request, and all three are required on `master`.

## What an implementation must provide

**One image**: the API on port 8080 as its entry point, and the CLI in the same image (the job runs it with another
command).

**The same configuration names** (environment variables, `__` for `:`):

| Setting | Conformance run | Meaning |
|---|---|---|
| `ConnectionStrings__Lists` | SQL auth to the test database | Production uses the app's user-assigned managed identity (`Authentication=Active Directory Managed Identity;User Id=<client id>`): an implementation must support that too |
| `Lists__RefreshMinutes` | `60` | How often the API reloads the lists from the database in any case; a fraction is allowed |
| `Lists__ChangeCheckSeconds` | `1` | How often the API reads `ChangesSql` (a value that moves with any source, import or allow-list change; the same query in every implementation, the reference's `ListLoader.ChangesSql`) and reloads when it moved; 2 by default, 0 is off. With the full reload an hour away, every change the suite makes reaches the API only through this check, as an administrator's change made on another replica does |
| `RateLimiting__PerMinute` | `0` | Screenings per client IP a minute; 0 is off |
| `Admin__ClientId`, `Admin__Authority` | `conformance-admin`, the suite's stand-in issuer (`Harness/Issuer.cs`, served by the suite on `-IssuerPort` 18090, reached at `host.docker.internal`) | The admin sign-in (docs/api.md, "Admin"); production sets `Admin__TenantId` instead of an authority |
| `Screening__MaxConcurrent`, `Screening__MaxWaiting` | defaults (4, 32) | Single screenings at once and waiting; beyond them 503 `busy` |
| `Screening__StreamMaxRecords` | `1000` | A stream's record limit (50,000 by default) |
| `Screening__StreamMaxSeconds`, `Screening__ConcurrentStreams`, `App__Version`, `App__Revision`, `LISTS_EU_TOKEN` | defaults | As in CLAUDE.md |

**The same database schema.** The EF migrations (`src/SanctionAndForgive.Data/Migrations`) are its single source of
truth. The deploy workflow applies them; no other implementation migrates. They can produce a SQL script for review:
`dotnet ef migrations script --idempotent --project src/SanctionAndForgive.Data --startup-project src/SanctionAndForgive.Api`.
The suite reads and sets the database directly only where the API can't, for example a list last checked three days ago.

**The same CLI**:
- the commands and options in `src/SanctionAndForgive.Cli/Program.cs`'s usage text;
- exit 0 on success, 1 when a source failed, was rejected or was held, and 2 for bad arguments;
- in its output, `Imported`, `Unchanged`, `HeldForReview`, `Rejected` and `FAILED`, which the Azure import alert matches.

**The same HTTP API** (`docs/api.md`) and **the same pages**: `wwwroot` can be copied as it is.

**The same logs**: one JSON object per line, one audit line per screening with its reference, and never the entered text.

## What it checks

- **Golden** (`tests/conformance/golden`, run first, on the base state):
  - `stream.ndjson`: 600 screenings through the stream, every cell, percentage, alignment, note and address result. Half are listed parties written the ways people write them (exactly, Cyrillic, a typo, words swapped, initials, another legal form, shortened, an alias, a near miss). The rest are addresses alone and ordinary names.
  - `screenings.ndjson`: every tenth one through the single endpoint (summary, row order, candidate details).
  - `sources.json`, `status.json`, `examples.json`: the list catalogue, the status and the examples.
  - `browse.ndjson`: five pages or filters of every list.
- **Behaviour**:
  - health, readiness and the OpenAPI description; every page and its menu;
  - validation, with nothing entered echoed back;
  - the reference, the disclaimer and the wording;
  - the stream's framing, errors and record limit;
  - each list's full history with change counts;
  - sign-in from the platform's header;
  - with `CONFORMANCE_API_LOG` (the API's standard output and error files, separated by `;`), that each screening
    has exactly one audit line under its reference and its input is in neither.
- **Changes**, each put back afterwards:
  - who may switch lists, review updates and edit the allow-list;
  - switching a list off and on;
  - a list past its allowed age (searched, with the warning);
  - the allow-list's confirm, allow and remove steps, and its refusal of a listed party;
  - an update's added, removed and changed counts, and an unchanged re-import;
  - a drop of more than a fifth held, then rejected or confirmed by an administrator;
  - a forced drop, an empty feed rejected, a broken feed failed;
  - the manual list's CSV add and remove;
  - the CLI's status and history.

**The lists are fictional.** `Feeds/Fixtures.cs` makes 600 parties (people with Cyrillic names, people romanised from
other scripts, companies with legal forms in Latin and Cyrillic) from a fixed seed with its own generator, so they're
the same everywhere. `Feeds/FeedFiles.cs` writes nine feeds in their publishers' formats: EU, OFAC SDN and consolidated,
UN, UK, US export (JSON), SECO, Canada and SFMS. Each feed also has the records a parser must leave out: vessels, a
delisted target, another agency's records. A manual CSV list makes ten lists, overlapping as real ones do.

**Normalised before comparing:**
- times, in fields and in text ("<time>");
- import ids (database identities);
- the matcher and allow-list version hashes;
- the history, which grows with every run.

Everything else must match exactly. A failure names the input and the first differing field, for example
`golden-0005: $.entries[0].cells[0].percent: expected 75, found 74`.

**Not covered**:
- downloading the real feeds, including retries (unit tests cover these);
- the rate limit;
- Entra ID itself (the platform's job);
- `probe`, `evaluate` and `load-inputs`;
- speed, which the load test covers (`docs/load-test.md`).

## Against the reference, on real lists

The fictional lists can't hold every quirk of real data. With `CONFORMANCE_REFERENCE_URL` set to the reference
(.NET) running against the same database as the implementation under test, `ReferenceTests` builds 2,000 screenings
from that database's own lists (names as listed, in lower case, with a letter dropped, and ordinary names with an
address) and compares every result with the reference's, field for field. Against a copy of the real lists it found
two things the golden set didn't: .NET's invariant casing leaves the Turkish `İ` and `ı` as they are, and Go's
`math.Log` differs from the C library's in the last bit for some arguments, which turned a 99% into a 98% on a band's
edge (the Go image now calls the C library's `log`, as .NET does). Run it with both images on the real lists:

```powershell
$cs = "Server=host.docker.internal,14333;Database=sanctions_local;User ID=sa;Password=Throwaway-container-2026!;TrustServerCertificate=True"
docker run -d --name ref --add-host=host.docker.internal:host-gateway -p 5299:8080 -e "ConnectionStrings__Lists=$cs" -e Screening__StreamMaxRecords=100000 -e RateLimiting__PerMinute=0 sanction-and-forgive:conformance
docker run -d --name go --add-host=host.docker.internal:host-gateway -p 5399:8080 -e "ConnectionStrings__Lists=$cs" -e Screening__StreamMaxRecords=100000 -e RateLimiting__PerMinute=0 sanction-and-forgive-go:conformance
$env:CONFORMANCE_BASE_URL = 'http://localhost:5399'; $env:CONFORMANCE_REFERENCE_URL = 'http://localhost:5299'
dotnet test tests/SanctionAndForgive.Conformance --filter FullyQualifiedName~ReferenceTests
```

It also prints each one's time per screening, one stream at a time. On the real lists (both as Linux containers on
one desktop, all ten lists loaded): on 2026-11-09, .NET 14.3 ms and Go 16.5 ms, about 350 MiB each; on 2026-11-10,
after the Go port's tuning (`ports/go/README.md`), Go 7.3 ms against .NET's 14.5 ms, 346 MiB in a 512 MiB container,
every result still the reference's.

## Changing the golden files

A deliberate change in what the reference answers (matching, wording, a field) changes the golden files in the same
pull request: `./tests/conformance/run.ps1 -Build -UpdateGolden`, then review the diff. A matching change still needs
the evaluation and the owner's approval where CLAUDE.md says so. An implementation in another language never rewrites
the golden files: it changes until it matches them.
