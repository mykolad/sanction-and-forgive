# Integration samples

How a booking system (TMS, WMS, ERP) plugs into the screening API. Every name in them is fictional. Set
`SAF_BASE_URL` to the service's address (the default is `http://localhost:8080`, a local run).

| File | What it shows |
|---|---|
| `screening.http` | The requests: readiness, status, sources, a single screening (Latin and Cyrillic), a small stream. Opens in VS Code's REST Client or a JetBrains IDE. |
| `screen_booking.py` | Screening a booking before it's confirmed: a reference per check, 503 (`warmingUp`, `busy`) and 429 waited out with `Retry-After`, the booking held on `Match`, `Review` and `Incomplete`, released only on `NoMatch`. Exit codes for a workflow engine. Prints no names. |
| `screen_batch.py` | A CSV of shipments through the stream endpoint: a few hundred records per stream, resuming from `nextLine`, a results CSV with each list's best finding and the lists as searched. |

Both scripts are Python 3.9+ with the standard library only. What each answer means for the booking:
[docs/decisions.md](../docs/decisions.md). The API itself: [docs/api.md](../docs/api.md).

```
python screen_booking.py --booking 2026-000123 --company "Northwind Trading LLC" --address "Hamburg, Germany"
python screen_batch.py shipments.csv results.csv
```
