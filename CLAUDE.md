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

## Matching (`SanctionAndForgive.Matching`)

Pure and deterministic: no I/O except its embedded dictionaries, no EF, HTTP or LLM. Spec section 5 is the
reference. The pipeline:

- **Dictionaries** (`Data/*.json`, embedded, loaded once by `MatcherData`): `translit/ru.json` and `translit/uk.json`
  (a new language is a new table plus golden rows; register it in `MatcherData.TableFiles`), `legal-forms.json`
  (classes, ambiguous spellings, multi-word phrases), `generic.json`, `words.json` (stop words, honorifics, address
  words), `spelling-pairs.json`, `cognates.json`, `countries.json` (244 countries: English variants as the feeds write
  them, Russian, Ukrainian, native names from ICU), `places.json` (exonyms with their country). Keys are normalised like
  the text they're compared with. **`MatcherVersion`** = a hash of every dictionary file plus `MatchingOptions`.
- **Text** (`Text/`): `Normalizer.Words` (NFKC, lower case, apostrophes inside words deleted, punctuation as
  separators, `&` → and); `Tokenizer` (legal phrases → short form, single-letter runs joined, letters kept as
  initials); `TokenFactory` (Latin forms: one per transliteration table for Cyrillic, plus a look-alike reading;
  diacritics stripped from Latin only, never before transliterating); `FoldKey` (the spec's fold rules).
- **Names** (`Names/`): `NameParser` gives two readings, `Entity` (legal forms stripped from the ends with the
  ambiguity guard, generic words aside, legal-only flagged) and `Person` (honorifics dropped, initials kept, an
  initials run is never a legal form). `TokenComparer` is section 5.4's table. `NameScorer` aligns (best pairs first),
  weights by IDF, maps to the percentage bands (`Bands`), applies caps, contained names and initials.
- **Addresses** (`Addresses/`): country (names, or a final capitalised ISO code), places, words and numbers;
  `NotCompared` / `Corroborated` / `Conflicting`, plus address-only screening.
- **`ListIndex`**: one per list, built from `ListedSubject`s (`Individual`, `Entity`, or `Unknown`: both readings).
  Interned tokens, document frequencies, aliases by fold key, a trigram index over the fold-key vocabulary (typos
  are found by trigrams, then checked by edit distance), address postings. Immutable.
- **`Screener.Screen(request, lists)`**: one entry per field entered (receiver, company, address), one cell per list.
  A list passed with `Index = null` is `Not checked` (no percentage); overall `Match` > `Review` > `Incomplete` >
  `NoMatch`. Throws `ArgumentException` when every field is empty.

Every number is in `MatchingOptions` (bands, caps, initials, candidate limit). Tests: `Matching/GoldenTests.cs` (every
row of spec 5.10, outcomes and bands, not exact percentages), `PerListTests.cs`, `TextTests.cs`. Calibration must
not break a golden test without the owner's approval.

Measured on 2026-11-06 with real feeds: OFAC SDN, EU, UK and US export together are 110 MB of managed heap and
build in about 3 s; a request against all four takes about 20 ms (p50) and 60 ms (p95) on a desktop.

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

`.github/workflows/build-and-test.yml` (**Build and Test**, job `build-and-test`, the required check on `master`) runs
on every PR and push to `master`: build, code style, tests, and `az bicep lint` / `build` on every file in `infra/`.
To add in Phase 2: the migrations check (`dotnet ef migrations has-pending-model-changes`).

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
