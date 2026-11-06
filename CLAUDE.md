# Sanction & Forgive — Claude Code Guide

## Project

Prototype of sanctions screening for shipments, presented as a demo to another company. A user enters a receiver, a
company and an address (Latin or Cyrillic) on a website or through a JSON API; for each entry and each public sanctions
list, the app shows a "Match likelihood" percentage. An MVP on one person's Azure account: keep it small.

The full spec is the owner's `HANDOVER.md` in the repository root. It is **gitignored and local only** (the repository
is public), so it exists only on the owner's machine. Where it and this file disagree, it wins; where it and reality
disagree (a feed moved, a property name changed), tell the owner and propose the fix. Conventions come from
https://github.com/mykolad/lazydad, minus its production setup.

**How work runs (owner's decisions, 2026-11-06):** one phase per PR (spec section 11). Claude merges its own PRs once
the required checks pass and Copilot's review threads are resolved, then continues with the next phase, reporting at
each phase's end. Still ask the owner before upsizing the container, dropping a source, or breaking a golden test.

### Non-negotiable behaviour

1. **Fail closed.** A list that failed to load, is older than its `MaxAgeHours`, or whose index isn't ready shows
   `Not checked` (no percentage, never `0%`); overall `Incomplete` / `Unavailable`.
2. **Wording.** "No match found in: EU (as of …), …". Never "not sanctioned", "cleared", "forgiven", "absolved".
3. **Disclaimer on every result**: demo list check, not a legal determination; no ownership/control or embargoes;
   a person must review possible matches.
4. **Missing data is never evidence** (a missing address neither raises nor lowers a result).
5. **No LLM** in the request path, importer or scheduled job (`LlmBoundaryTests` checks the packages). Same input and
   list version, same output.
6. **Explainable**: every hit states list, listed name or alias, which tokens aligned and how.
7. **Every list answered separately**; the percentage is an estimate, never certainty or proof of a lawful shipment.
8. **Input is never stored or logged**: no request bodies in logs, traces or exceptions. Responses carry list
   versions and the matcher version.

A screening request never touches the database: matching runs in memory, one index per list.

## Solution layout

```
src/SanctionAndForgive.Api        ASP.NET Core API, index load and refresh, wwwroot (plain JS, no build step)
src/SanctionAndForgive.Matching   Pure matching library: no I/O, no EF, no HTTP, no LLM packages
src/SanctionAndForgive.Lists      Feed downloaders, streaming parsers, CSV manual-record reader
src/SanctionAndForgive.Data       EF Core DbContext, entities, migrations, repositories
src/SanctionAndForgive.Cli        Console app: imports, manual records, probe, evaluate
tests/SanctionAndForgive.Tests        xUnit: matching golden tests, parser tests on small fixtures, HTTP tests
tests/SanctionAndForgive.SmokeTests   Run against the deployed app by the deploy workflow
tests/load                k6 script and input generator
infra/                    Bicep (infra/README.md)
tools/lists.ps1           The script the owner runs to manage lists
```

## Lists (feeds and parsers)

`docs/sources.md` holds each feed's format, real counts, terms of reuse and the sizing. Keep it current.

- `ListSources.All` is the catalogue: code, name, URL, `MaxAgeHours` (72 for the daily feeds), `FeedStatus`, parser.
  Only `Active` feeds are imported automatically. `TermsUnclear` (un, ch-seco, ca-sema) wait for the owner's
  decision, and `NeedsAccess` (au-dfat, ua-nsdc) wait for a file or an API key. The EU URL's `{token}` comes from
  `LISTS_EU_TOKEN` (the public token by default).
- One `IListParser` per format reduces a file to `ParsedSubject`s: external id, `SubjectType` (`Unknown` when the
  source doesn't say: US export records), primary name, every name with its script and weak flag, addresses as one
  free-text string plus an ISO alpha-2 code when the source gives one. Parsers stream (`Parsers/Xml.Records`: one
  record element at a time) and leave out vessels and aircraft. A wrong root element is a `ListFormatException`.
- `FeedDownloader` identifies itself honestly (`sanction-and-forgive/<version>`). Never disguise the client as a
  browser to get past a site that refuses it: report the source as `NeedsAccess` instead.
- `probe` (`dotnet SanctionAndForgive.Cli.dll probe [--source <code>|all] [--file <path>] [--keep <dir>]`) downloads,
  parses and prints counts per feed. It writes nothing else. It exits 1 if an `Active` feed failed.
- Parser tests: `tests/SanctionAndForgive.Tests/Lists/ParserTests.cs`, one small fictional fixture per format.

## Docker

The image (`Dockerfile`) holds both `SanctionAndForgive.Api.dll` (the entry point) and `SanctionAndForgive.Cli.dll`
(the import job overrides the command), published to one folder. Base: `aspnet:10.0-noble-chiseled-extra` (no shell:
prepare files in the build stage), non-root, port 8080. Version metadata is baked in from build args (`VERSION`,
`REVISION`, `COMMIT_DATE`, `SOURCE_URL` → `App__Version` etc.). **A new project needs its `.csproj` copied before
`dotnet restore` in the Dockerfile.**

```
docker build -t sanction-and-forgive --build-arg VERSION=local .
docker run --rm --entrypoint dotnet sanction-and-forgive SanctionAndForgive.Cli.dll probe --source ua-sfms
docker run --rm -p 8080:8080 sanction-and-forgive
```

## Code style rules

`.editorconfig` holds the style, and the build enforces it (`Directory.Build.props`: `EnforceCodeStyleInBuild`, the
`latest-recommended` analyzers, warnings as errors). A few style rules run only in the IDE and in `dotnet format`, so
Build and Test also runs `dotnet format style --verify-no-changes`. Suppress a rule in `.editorconfig` or with a
`#pragma`, always with the reason next to it.

- **No underscore prefix** on private fields. Enforced (IDE1006).
- **`this.` only when required** to disambiguate a field from a same-named parameter. Enforced (IDE0003).
- **No default arguments** on method parameters: callers always pass explicitly (e.g. `CancellationToken
  cancellationToken`, never `= default`). No analyzer has this rule: review keeps it.
- **Tests check behaviour, not logs.** Assert on outcomes, never on log messages.

## Building and testing

```
dotnet build SanctionAndForgive.slnx
dotnet test  tests/SanctionAndForgive.Tests
dotnet format style SanctionAndForgive.slnx --verify-no-changes --severity warn
```

Shared settings live at the root: `Directory.Build.props` (framework, nullable, implicit usings, analyzers),
`Directory.Packages.props` (every package version) and `global.json` (a .NET 10 SDK from 10.0.200, any feature band).

## Build and Test (CI)

`.github/workflows/build-and-test.yml` (**Build and Test**) runs on every PR and push to `master`, two jobs, both
required on `master`:

- `build-and-test`: build, code style, tests, and `az bicep lint` / `build` on every file in `infra/`.
- `clean-database-migrations`: against a throwaway SQL Server 2022 service container (SQL auth; the password is not a
  secret), `has-pending-model-changes`, then the migration bundle applied to an empty database, rolled back to `0`
  and applied again, then the `SqlServerFact` import tests (`Tests.Data`), which are skipped elsewhere unless
  `SQLSERVER_TEST_CONNECTION` is set.

## Data and import (`SanctionAndForgive.Data`, CLI)

Spec sections 4 ("Import rules") and 6. The database holds parsed lists only, never anything a user enters.

- **Tables:** `Sources` (code, name, URL or null for manual, `MaxAgeHours` or null for manual, `ActiveImportId`,
  `LastCheckedAt`), `Imports` (status `Running` / `Completed` / `Failed` / `Rejected`, `ContentHash`, `RecordCount`,
  `Message`), `Subjects`, `SubjectNames` (with `IsWeak` and `Script`), `SubjectAddresses`. Normalised forms are
  never stored: a dictionary change needs no re-import.
- **`ImportStore.ImportAsync`:** an application lock per source; the canonical hash (records sorted by external id,
  generation dates don't count) → `Unchanged` only moves `LastCheckedAt`; the sanity guard (zero records, or a drop of
  more than 20%; `--force` overrides the drop, never zero) → `Rejected`, old list kept, `LastCheckedAt` not moved, so a
  persistent problem turns the list stale; otherwise `SqlBulkCopy` in batches of 5,000, then one `SaveChanges` flips
  `ActiveImportId` (a single SaveChanges is one transaction and works with EF's retrying strategy; a user-started
  transaction doesn't). The active and the previous import are kept; older ones are deleted in chunks.
- **Staleness** counts from `LastCheckedAt`, the last successful check, not publication.
- **CLI commands** (`SanctionAndForgive.Cli`, via `tools/lists.ps1`): `probe`, `import --source <code>|all [--file]
  [--force]` (`all` = `Active` feeds; naming a `TermsUnclear` feed imports it: naming it is the decision), `status`,
  `source-add`, `add` (CSV upsert by external id), `remove`. A manual change is a new import too. One failed source
  doesn't stop the others; any failure or rejection exits 1. Connection string: `ConnectionStrings:Lists` (environment
  or user secrets, id `sanction-and-forgive-api`, shared with the API).
- **`tools/lists.ps1`** opens the SQL firewall for your IP, runs the CLI as your `az login`
  (`Authentication=Active Directory Default`), and closes the firewall. With `$env:ConnectionStrings__Lists` set it
  uses that database instead and leaves the firewall alone.
- **Manual CSV:** `external_id,type,name,aliases,address,country,programme,listed_on,remarks` (`ManualCsvReader`).

## EF Core migrations

Always supply `--startup-project` so EF tools use the API's DI (no design-time factory):

```
dotnet tool restore
dotnet ef migrations add <Name> --project src/SanctionAndForgive.Data --startup-project src/SanctionAndForgive.Api
```

Migrations never run on startup: the deploy workflow runs the bundle.

## Local SQL Server

```
docker run -d --name sanctions-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Throwaway-container-2026!" -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
$env:ConnectionStrings__Lists = "Server=localhost,14333;Database=sanctions_local;User ID=sa;Password=Throwaway-container-2026!;TrustServerCertificate=True"
dotnet ef database update --project src/SanctionAndForgive.Data --startup-project src/SanctionAndForgive.Api --connection $env:ConnectionStrings__Lists
./tools/lists.ps1 import -Source all
$env:SQLSERVER_TEST_CONNECTION = "Server=localhost,14333;User ID=sa;Password=Throwaway-container-2026!;TrustServerCertificate=True"   # runs the SQL tests
```

## Azure resources

All in `sanction-and-forgive-rg` (Sweden Central), all from Bicep (`infra/README.md` lists each file and the
bootstrap commands). Deployed so far: the bootstrap and the platform (Log Analytics, Container Apps environment
`sanction-and-forgive-cae`, identity `sanction-and-forgive-app`, SQL server `sanction-and-forgive-sql` with database
`sanction-and-forgive-db`, Basic 5 DTU / 2 GB, Entra-only, the owner as admin). The Container App and import job come
with `app.bicep` in Phase 4.

- **Deploy identity** `sanction-and-forgive-github-cd`: Contributor on the resource group only, trusted from the
  GitHub `production` environment only (master). The workflows log in with OIDC using the repository variables
  `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. No secrets in GitHub.
- **Database users** (`infra/sql/grant-identities.sql`, run with `infra/sql/Invoke-Sql.ps1` as the owner):
  `sanction-and-forgive-app` (read/write) and `sanction-and-forgive-github-cd` (ddladmin, read/write, for migrations).
- **Nothing personal in the repo**: `platform.bicepparam` reads the admin's object ID and login from environment
  variables (repository variables in CI). Alerts notify the subscription's Owner role: no email address anywhere.
- **Claude may deploy** `platform.bicep` (and later `app.bicep`) with the owner's `az login` after a clean what-if; the
  owner runs `bootstrap.bicep`. Touch nothing outside `sanction-and-forgive-rg`.

## Pull requests and GitHub

- Default branch `master`. Rulesets: `master: PR + checks` (PR required, `build-and-test` passing and up to date,
  review threads resolved, no approvals required; admins can bypass), `master: no force-push/delete`, and
  `copilot-review` (Copilot reviews every PR and every push to it). `.github/copilot-instructions.md` steers it.
- **Every change to an open PR is a new commit on top.** Don't amend, squash or force-push pushed commits.
- **To bring a PR up to date with `master`, merge `master` into its branch**; don't rebase.
- Address Copilot's comments (fix, or reply why not), resolve the threads, then merge.
