# What to do with each answer

For a booking system calling the API (`samples/screen_booking.py` follows this table). The API never says a shipment
is lawful: it says which lists it searched, as of when, and how close the closest listed party is. The decision
below is what a shipment workflow does with that; the compliance team owns it.

## The overall outcome

| Outcome | What it means | The booking | Who acts |
|---|---|---|---|
| `Match` (website: "Possible match found") | At least one list has a party that is very likely the one entered (90% or more), or one whose listed identifier equals one entered (`identifiers`; docs/api.md). | **Hold.** Don't ship. | Compliance reviews the hit against the official record before anything else happens. |
| `Review` ("Review needed") | A list has a party that might be the one entered (50-89%): a similar name, a common surname, initials. Or an address is in a destination under broad restrictions, or possibly is (an entry's `destination`; docs/api.md). | **Hold.** | A person compares the details (address, date of birth, registration number) and either releases the booking or escalates. If it's a known false positive, an administrator can add the name to the allow-list. A destination is never allow-listed: a person checks the rules for the goods and the exact place. |
| `Incomplete` | At least one list couldn't be searched (still loading, or its load failed). The others may have answered `NoMatch`. | **Hold**, and try again later. Not checked is never clear. | The system retries; a person is told if it stays incomplete. |
| `NoMatch` ("No match found") | No list has a party close enough, in the lists searched, as of their dates; or the only possible matches (`Review`) are ones an administrator allowed for this name (the allow-list; a `Match` is never allowed). | **Release.** Keep the record: the reference, the list versions and the as-of dates, and any allowed possible matches (`allowed` cells, `allowedBy` with the entry and reason, `allowListVersion`). | No one, unless something else about the shipment raises a question. |

`NoMatch` is a statement about these lists on this date, never "cleared" or "not sanctioned". It doesn't cover
ownership or control by listed parties, or export controls on the goods. Of embargoes, only the destinations in the
table (`GET /api/v1/destinations`, where the API lists the `destinations` feature) raise a `Review`.

## Answers that aren't outcomes

| Answer | What it means | What to do |
|---|---|---|
| 503 `{"state":"warmingUp"}` | The service has just started and is loading the lists. | Wait (`Retry-After`, or a few seconds) and send it again. Don't release in the meantime. |
| 503 `{"state":"busy"}` with `Retry-After: 1` | Too many screenings at this moment. | Wait a second and send it again. Batches belong on the stream endpoint. |
| 429 with `Retry-After` | Too many requests from this address in a minute (single screenings), or another stream is running (streams). | Wait as `Retry-After` says. |
| 400 with `errors` | A field is missing, too long (over 300 characters) or the reference is invalid. The messages name the field, never quote its value. | Fix the request. The booking stays held: it wasn't screened. |
| No answer (timeout, connection refused) | The service is down or unreachable. | Hold the booking and alert; never fall back to releasing. |

## A list's state in the answer

Every answer lists the lists searched (`lists`: `code`, `version`, `asOf`, `state`):

- **`Current`**: searched, and updated within its allowed age.
- **`Overdue`**: searched, but its last successful update is older than its allowed age (72 hours for the daily
  lists). The answer still counts, and its summary warns about it. Tell compliance if it lasts.
- **`NotChecked`**: not searched. The outcome is `Incomplete`, unless another list already gave a `Match` or a
  `Review` (those come first), so check every list's state yourself: a `Match` or `Review` doesn't mean every list
  was searched, and the booking stays held until the ones not checked have been.
- **`SwitchedOff`**: an administrator switched it off on purpose (the summary gives the reason); it has no cells
  and doesn't make the outcome `Incomplete`.

## Keeping a record

Store, with the booking: the `reference` you sent, the `outcome`, the `summary`, `matcherVersion`,
`allowListVersion`, `destinationsVersion` where there is one, and each list's `version` and `asOf`. Those reproduce the answer: the same input against the same
list versions gives the same result. The service keeps no copy of what was entered: its audit log has the reference,
the outcome and the versions only.
