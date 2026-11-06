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
