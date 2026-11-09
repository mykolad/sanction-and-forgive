# API

A JSON API with no authentication (a demo), rate-limited per client IP. The website uses the same API. The
machine-readable description is `/openapi/v1.json`.

## Screening references

Every screening carries the caller's own **`reference`**: a transaction id, say. It is required, 1-64 characters
of letters, digits and `. _ : / -`, starting with a letter or digit. It's echoed back in the response, and it is how a
screening is found in the logs later. **Never put personal data in it**: it's logged as it is.

## Lists that couldn't be updated

Each daily list is checked once a day (03:30 UTC, each download retried for up to 10 minutes after a passing fault)
and has an allowed age, 72 hours. A list that couldn't be updated within it is **still searched, as last
downloaded**, however old (owner's decision, 2026-11-09): its `state` in a response's `lists` (and a stream's header)
is `Overdue` instead of `Current`, its cells are answered as usual, and the summary ends with a warning, e.g.
"Warning: EU couldn't be updated within 72 hours and was searched as of 7 Nov 2026 03:30 UTC." A list that isn't
loaded (its load failed, or it's still loading) is `NotChecked`: no percentage, never 0%, and the result is
`Incomplete`.

## Batches: `POST /api/v1/screenings/stream`

For many records at once. The request is **NDJSON**: one screening per line, each the same object as the single
endpoint takes (`reference`, `receiverName`, `companyName`, `address`), with content type `application/x-ndjson`
(`application/jsonl` and `text/plain` are accepted too). The response is NDJSON as well, written as each record is
screened, so a client can read results while it's still sending. Nothing is held but the line in hand, and nothing
is stored.

```
POST /api/v1/screenings/stream
Content-Type: application/x-ndjson

{"reference":"TX-1","companyName":"ТОВ Ромашка"}
{"reference":"TX-2","receiverName":"Shevchenko T.G.","address":"Kyiv, Ukraine"}
```

```
{"type":"header","matcherVersion":"c0d2b94021a5","disclaimer":"…","lists":[…],"maxRecords":50000,"maxSeconds":200}
{"type":"result","line":1,"reference":"TX-1","outcome":"Match","entries":[{"kind":"Company","cells":[{"list":"eu","state":"Checked","percent":99,"outcome":"Match","best":{"listedName":"…","alias":"…","externalId":"…","sourceUrl":"…","percent":99,"words":[{"entered":1,"listed":"romashka","how":"Transliterated"}],"notes":[],"addressState":"NotCompared"}},{"list":"uk","state":"NotChecked","percent":null,"outcome":null,"best":null}]}]}
{"type":"result","line":2,"reference":"TX-2","outcome":"Review","entries":[…]}
{"type":"end","screened":2,"errors":0,"truncated":false,"nextLine":null}
```

- **`header`** first: the matcher version, the disclaimer, and every list with its version and freshness. Every
  result in the stream is computed against this one snapshot of the lists.
- **`result`**, one per record, in order: `line` (its 1-based line number in the request; blank lines are skipped but
  counted), your `reference`, the overall `outcome`, and per entry (Receiver, Company, Address) one cell per list:
  `state` (`Checked`, or `NotChecked` with no percentage, never 0%), `percent`, `outcome` (`Match`, `Review`,
  `NoMatch`) and, from 20%, the `best` candidate: enough to find it on the list, and how it aligned. Nothing you
  sent is echoed back (you have it already, keyed by reference), so the explanation names your words by position:
  `words` has, per entered word (1-based, among the words compared: legal forms, generic words and honorifics
  aside), the listed word it aligned with and `how` (`Same`, `Transliterated`, `SpellingVariant`, `Romanisation`,
  `Cognate`, `OneLetterDiffers`, `TwoLettersDiffer`; `NotListed` for an entered word with no partner, `NotEntered` for
  a listed one; `Initial`, `InitialContradicts`, `InitialUnchecked` for initials, with the entered word's position
  when the initial is the listed one). An address-only hit lists the address parts that aligned the same way, plus
  `SamePlace` and `SameNumber`. `notes` says the rest: why a percentage was capped, or how many address parts
  aligned. The single endpoint gives the same explanation in plain words. Matching is deterministic, so screening
  one record again with the same list versions gives the same answer.
- **`error`** for a record that couldn't be screened (not JSON, a line over 4,096 bytes, a missing reference, nothing
  entered, a field over 300 characters): `line`, the `reference` if there was one, and `errors` by field. The
  messages never quote the input. The stream goes on.
- **`end`** last: how many were screened, how many were errors, and whether it was `truncated`.

**Limits.** A stream stops after **50,000 records** (`Screening:StreamMaxRecords`) or **200 seconds**
(`Screening:StreamMaxSeconds`), whichever comes first: the Container Apps ingress ends any request at 240 seconds.
The time limit counts reading too, so a slow upload stops at it like a long one. Then `end` says
`"truncated":true` and `nextLine`, the first line it didn't read (or didn't finish reading); send the rest from that
line in a new stream. A client that stops reading the answer is disconnected 20 seconds after the limit. A stream that has screened its maximum ends at once, even if the upload is still open, so `truncated` can be true
with nothing left to send. A client should always check `end`: no `end` line means the connection broke. Then the
records after the last `result` or `error` have an **unknown** status: one may have been screened (and audited)
just before the break without its answer arriving. Send them again from the line after the last answer;
a record screened twice has two audit lines under its reference, with the same result for the same list versions. **One stream at a time** across all clients (`Screening:ConcurrentStreams`):
a second one gets 429 with `Retry-After: 1` until the first ends, so a batch can't starve the website on the demo's
single small replica. The permit comes back as the first stream's request completes, a moment after its end line,
so a client that starts the next stream straight away may need that one retry. (The per-minute limit's 429 carries
its own `Retry-After`.)

**Throughput.** Measured on 2026-11-06 on a desktop, against all 10 fetched lists, one stream at a time: about
**32 records a second** (5,000 in 158 s, 417 MB working set, flat). A record is screened on one core. The
production container (0.25 vCPU, 6 active lists) will be slower: a 50,000-record batch is several streams and
tens of minutes there, so it is a job to run in the background, not while someone waits. More CPU is the lever
(a decision for after the load test).

Each record writes its own audit line, exactly like a single screening (below).

## What's logged, and what never is

**Nothing entered is stored or logged**: no receiver, company or address, in any log, trace or error. A test checks
every log record of a screening for the entered text (`ApiTests.Screening_IsLoggedByReference_…`).

Each screening writes one audit line instead:

```
Screening TX-2026-0001: Match; matcher 7ea1dd809393; lists eu@1, ofac-sdn@2, uk@4 overdue, …;
Receiver: eu Review 70% [EU.1234.56]; ofac-sdn NoMatch 18%; uk NotChecked | Company: eu Match 99% [EU.987.12]; …
```

The reference, the overall outcome, the matcher version, each list's version (its import id), and for each entry kind
(Receiver, Company, Address) and list: the outcome, the percentage, and, from 20%, the best candidate's id on that list
(the list's own id, not a name). A list that wasn't searched says `NotChecked`; one searched past its allowed age has
`overdue` after its version. With the versions, the same result
can be reproduced later from the same lists.

### Finding a screening in the logs

Logs are one JSON object per line in the Container Apps environment's Log Analytics workspace
(`sanction-and-forgive-logs`). In the portal (*Logs*), or with `az monitor log-analytics query`:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "sanction-and-forgive"
| where Log_s has "TX-2026-0001"
| project TimeGenerated, Log_s
```

Logs are kept 30 days.

## List status: `GET /api/v1/sources` and `/status`

Every list a screening searches, then every known feed that isn't imported, with the reason. The website's
`/status` page shows the same. It reads memory and the catalogue (`ListSources`), never the database, and answers
503 `{"state":"warmingUp"}` until the lists have loaded.

```
{"now":"2026-11-06T10:00:00+00:00","lists":[
  {"code":"eu","name":"EU consolidated financial sanctions","state":"Current","status":"Active","reason":null,
   "recordCount":6269,"version":19,"lastCheckedAt":"…","updatedAt":"…","maxAgeHours":72,
   "homepage":"https://data.europa.eu/…","fileUrl":"https://webgate.ec.europa.eu/…","termsUrl":"https://commission.europa.eu/legal-notice_en"},
  …,
  {"code":"un","name":"UN Security Council consolidated","state":"NotImported","status":"TermsUnclear",
   "reason":"The UN's terms allow personal, non-commercial use only. …","recordCount":null, …}]}
```

- `state`: `Current` (searched now), `Overdue` (searched as last downloaded, but not updated within `maxAgeHours`:
  results warn, and `reason` says so), `NotChecked` (loaded but not built yet, or its load failed: its cells say Not
  checked, and `reason` says which), or `NotImported`.
- `status`: the catalogue's decision: `Active`, `TermsUnclear` (the terms seem to rule out this use; waiting for
  the owner), `NeedsAccess` (an API key, or a site that refuses automated clients), or `Manual` (kept by hand).
- `lastCheckedAt`: the last successful check, changed or not; freshness counts from it. `updatedAt`: when the
  list's content last changed here. `version`: the active import's id, as in the screening responses.
- `held`: an update held for review, if one is waiting (`importId`, `recordCount`, `downloadedAt`, `reason`, and
  `added`, `removed`, `changed` against the list it was compared with: the list in use when it was downloaded), however
  many checks came after it. An update with more than 20% fewer records than the list it would replace is written but
  not searched. Screenings keep using the active list, past its allowed age too (as `Overdue`). An administrator confirms or rejects the held one (`tools/lists.ps1 confirm|reject -Import <id>`).
- `history`: the latest 10 updates, newest first, as of the API's last refresh (every 15 minutes), and
  `historyCount`, how many there are in all: `importId`,
  `result` (`Active`: the list in use; `Replaced`, `HeldForReview`, `Rejected`, `Failed`, `Running`), `startedAt`,
  `completedAt`, `recordCount`, `note` (why it was held or rejected; a failure says only that the download or parse
  failed, as the details stay in `tools/lists.ps1 history`), `reviewed` (whether a person confirmed or rejected
  it; who, isn't public), and what it changed against the list in use when it was downloaded: `added`, `removed` (by
  external id) and `changed` (kept, with any field, name or address different, compared exactly); a list's first
  update added everything. Null where not recorded: an update from before 2026-11-07 whose records were already
  gone, a failure, or a count that failed. A row is an update, a failure, a hold or a rejection; a daily check that
  finds the list unchanged only moves `lastCheckedAt`.
- `GET /api/v1/sources/{code}/history`: every update of one loaded list, newest first (`code`, `name`, `history`),
  from memory; 404 for a list the API doesn't load, 503 before the first load. The status page's "Show all".

## Browsing a list: `POST /api/v1/lists/{code}/browse` and `/lists`

A page of one loaded list's entries, as the demo holds them and screens against: from memory, never the database.
The website's **Browse lists** page (`/lists`, or `/lists?list=eu` to open one) uses it. The filter is in the body,
never the URL, and is neither stored nor logged, like a screening's input.

```
POST /api/v1/lists/eu/browse
Content-Type: application/json

{"query":"romashka","type":"Entity","offset":0,"limit":50}
```

- `query` (optional, up to 100 characters): text found in any of an entry's names, ignoring case, accents and extra
  spaces (Cyrillic stays Cyrillic: this is a text filter, not matching), or an entry's exact id.
- `type` (optional): `Individual`, `Entity` or `Unknown` (the list doesn't say, as in the US export list).
- `offset`, `limit`: paging, 1 to 100 entries a page.

The answer has the `list` (as in the other responses: version, freshness, state), the `total` that match, and the
`entries` on that page in name order (leading quotes ignored), each with its id, type, primary name, every `names`
entry (`isWeak` where the list marks an alias as low quality), `addresses`, `programme` and `sourceUrl` (the
official record). A list that is Not checked can still be browsed; its `state` says so. 404 for an unknown list code,
503 before the lists have loaded, 400 for a bad filter or page, and the per-minute limit applies as for screenings.

## Admin: `/api/v1/admin` and `/admin`

For the demo's administrators. Sign-in is Container Apps' built-in Microsoft Entra ID sign-in (`/.auth/login/aad`,
`/.auth/logout`), set up by `infra/admin-sign-in.ps1`; screening never needs it. The app reads the signed-in user from
the platform's `X-MS-CLIENT-PRINCIPAL` header, which clients can't set, and only where the deployment turned sign-in
on (`Admin:SignInEnabled`): otherwise nobody is signed in, whatever a request says.

- `GET /api/v1/admin/me`: `enabled` (sign-in set up), `signedIn`, `name`, `isAdmin` (holds the `Lists.Admin` role),
  `signInUrl`, `signOutUrl`.
- `POST /api/v1/admin/imports/{id}/confirm` and `…/reject`: review an update held for review (its `importId` from
  `held` in `/api/v1/sources`). Needs the `Lists.Admin` role and the header `X-Requested-By: sanction-and-forgive-admin`
  (which another site can't send, so a signed-in admin's cookie can't be used against them). 200
  `{importId, result, reloaded}` (`reloaded` false: the review stands, and the next refresh, within 15 minutes,
  shows it); 401 not signed in; 403 without the role; 400 without the header; 404 sign-in off or no such
  import; 409 `NotPending` (already reviewed, superseded, or held against a list that has changed since). Confirming
  makes it the active list (searched while it's current: the status page shows whether it is) and reloads the lists
  at once; the review is logged with the reviewer's sign-in name.

## The allow-list

Names known to be legitimate parties that resemble a listed one (owner's decision, 2026-11-06). When the receiver or
company entered **is** an allow-listed name, word for word as screening reads it (case, punctuation, apostrophes and
spacing don't matter, but every word does, the legal form included: "Huamei Co., Ltd." is "HUAMEI CO LTD", not
"Huamei Limited"; initials are not a word: "A. B. Corp" is not "AB Corp"; Latin and Cyrillic spellings are
separate entries), each of its
**possible matches** (Review) is still shown, with its percentage, but marked `allowed`, and doesn't raise the
outcome. A **Match** is never allowed, the address entry never is, and a name that is itself a Match on a list
can't be added, nor anything while a list can't be checked (503: loading, or not current). An entry allows that
name's possible matches on every list, whichever listed party they point to, so those are the administrator's
decision (owner's decision, 2026-11-06): a name that is a possible match for listed parties is added only once each
of them has been shown and confirmed.

- Screening responses: `allowListVersion`; per entry `allowedBy` (`id`, `name`, `reason`); per cell `allowed`. The
  summary says which possible matches were allowed and why. The audit line has `allow-list <version>`, the entry by
  id (`(allow-list entry 5)`) and `allowed` on its cells, never the name.
- Streams: `allowListVersion` in the header; per entry `allowListEntryId` (never the name: that's the input); per
  cell `allowed`.
- Administrators only (`/allow-list`, or the API with the `Lists.Admin` role and, for changes, the
  `X-Requested-By: sanction-and-forgive-admin` header): `GET /api/v1/admin/allow-list` (every entry, newest first,
  removed ones with who and when), `POST /api/v1/admin/allow-list` `{name, reason, confirmed}` (1-300 and 1-500
  characters), `POST /api/v1/admin/allow-list/{id}/remove`. A refusal is `{code, message, possibleMatches}`:
  - 409 `confirm`: the name is a possible match for listed parties, and not all of them were confirmed. Nothing was
    added. `possibleMatches`: each listed party once, the closest first: `key` (`<list>/<external id>`), `list`,
    `listName`, `listedName`, `alias` (the name or alias that matched), `percent`, `programme`. Every listed party at
    Review the name finds, not only the five closest a screening cell shows: an entry allows the whole cell. Send the
    same request again with `confirmed`: the keys accepted. If a list has changed since and brings a possible match
    not among them, it's 409 `confirm` again.
  - 409 `tooBroad`: the name is a possible match for more than 100 listed parties (the closest 100 are listed): too
    generic to allow-list.
  - 409 `listed`: the name is a Match on a list; never added.
  - 409 `exists`: the name is already an entry in force (the same words as screening reads them; checked again when
    it's stored, under a lock, so two additions of one name can't both succeed).
  - 503 `unavailable`: a list can't be checked now (loading, or not current).

  The log line for an addition names the possible matches confirmed by key (never the name).
  Both answer `{id, reloaded}`: a committed change is reported as such; `reloaded` false means the next refresh, within
  15 minutes, applies an addition (a removal stops applying at once regardless). An entry is never deleted, so the
  history stays; the version is the number of additions plus removals.
