# API

A JSON API with no authentication (a demo), rate-limited per client IP. The website uses the same API. The
machine-readable description is `/openapi/v1.json`.

## Screening references

Every screening carries the caller's own **`reference`**: a transaction id, say. It is required, 1-64 characters
of letters, digits and `. _ : / -`, starting with a letter or digit. It's echoed back in the response, and it is how a
screening is found in the logs later. **Never put personal data in it**: it's logged as it is.

## What's logged, and what never is

**Nothing entered is stored or logged**: no receiver, company or address, in any log, trace or error. A test checks
every log record of a screening for the entered text (`ApiTests.Screening_IsLoggedByReference_…`).

Each screening writes one audit line instead:

```
Screening TX-2026-0001: Match; matcher 7ea1dd809393; lists eu@1, ofac-sdn@2, uk@4, …;
Receiver: eu Review 70% [EU.1234.56]; ofac-sdn NoMatch 18%; uk NotChecked | Company: eu Match 99% [EU.987.12]; …
```

The reference, the overall outcome, the matcher version, each list's version (its import id), and for each entry kind
(Receiver, Company, Address) and list: the outcome, the percentage, and, from 20%, the best candidate's id on that list
(the list's own id, not a name). A list that wasn't searched says `NotChecked`. With the versions, the same result
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

- `state`: `Current` (searched now), `NotChecked` (loaded, but older than `maxAgeHours` or not built yet: its
  cells say Not checked, and `reason` says which), or `NotImported`.
- `status`: the catalogue's decision: `Active`, `TermsUnclear` (the terms seem to rule out this use; waiting for
  the owner), `NeedsAccess` (an API key, or a site that refuses automated clients), or `Manual` (kept by hand).
- `lastCheckedAt`: the last successful check, changed or not; freshness counts from it. `updatedAt`: when the
  list's content last changed here. `version`: the active import's id, as in the screening responses.

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
