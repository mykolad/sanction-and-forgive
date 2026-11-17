#!/usr/bin/env python3
"""Screen a CSV of shipments through the stream endpoint and write the outcome of each.

Python 3.9+, standard library only.

    python screen_batch.py shipments.csv results.csv

The input CSV has a header with any of: reference, receiver, company, address (other columns are ignored); a row
without a reference gets one. The output has: reference, outcome, decision (see docs/decisions.md), and a column per
list with that list's best finding. Nothing it sends is printed or logged.

The stream (POST /api/v1/screenings/stream) takes NDJSON and answers NDJSON as it goes. This sends a few hundred
records per stream, reads each answer whole, and sends again from "nextLine" when a stream is cut short (its time or
record limit) or from the first unanswered record when the connection breaks. One stream runs at a time on the
server: a 429 means another is running, so it waits as Retry-After says.
"""

import csv
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE_URL = os.environ.get("SAF_BASE_URL", "http://localhost:8080").rstrip("/")
CHUNK = 200
MAX_RETRIES = 30


def stream(records):
    """Sends records (dicts) as one stream; returns (header, answers by 1-based line, end line or None)."""
    body = "".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records).encode("utf-8")
    request = urllib.request.Request(
        f"{BASE_URL}/api/v1/screenings/stream",
        data=body,
        method="POST",
        headers={"Content-Type": "application/x-ndjson", "Accept": "application/x-ndjson"},
    )
    header, answers, end = None, {}, None
    with urllib.request.urlopen(request, timeout=300) as response:
        for raw in response:
            if not raw.strip():
                continue
            message = json.loads(raw)
            if message["type"] == "header":
                header = message
            elif message["type"] in ("result", "error"):
                answers[message["line"]] = message
            elif message["type"] == "end":
                end = message
    return header, answers, end


def screen_all(records):
    """Every record's answer (result or error), in order, and the last stream's header."""
    answers, header, start, retries = [None] * len(records), None, 0, 0
    while start < len(records):
        chunk = records[start:start + CHUNK]
        try:
            header, got, end = stream(chunk)
        except urllib.error.HTTPError as error:
            if error.code in (429, 503) and retries < MAX_RETRIES:
                retries += 1
                time.sleep(float(error.headers.get("Retry-After") or 1))
                continue
            raise
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            if retries >= MAX_RETRIES:
                raise
            retries += 1
            time.sleep(3)
            got, end = {}, None
        for line, message in got.items():
            answers[start + line - 1] = message
        if end is not None and not end["truncated"]:
            sent = len(chunk)
        elif end is not None and end.get("nextLine"):
            sent = end["nextLine"] - 1
        else:
            # Cut short with nothing left to send, or the connection broke: go on from the first unanswered record.
            sent = max(got, default=0) if end is None else len(chunk)
        start += sent
        print(f"{start} of {len(records)} screened", file=sys.stderr)
    return answers, header


def decision(outcome):
    return {"Match": "hold for review", "Review": "hold for review", "Incomplete": "hold: not checked", "NoMatch": "release"}.get(outcome, "hold: not screened")


def finding(answer, code):
    """A list's best finding across the record's entries: Match, Review, No match or Not checked, with a percentage."""
    cells = [(entry["kind"], cell) for entry in answer.get("entries", []) for cell in entry["cells"] if cell["list"] == code]
    if not cells:
        return "not searched"
    hits = [(kind, cell) for kind, cell in cells if cell["state"] == "Checked" and cell["outcome"] in ("Match", "Review")]
    if hits:
        kind, cell = max(hits, key=lambda hit: hit[1]["percent"] or 0)
        name = cell["best"]["listedName"] if cell.get("best") else ""
        return f"{cell['outcome']} {cell['percent']}% {kind}: {name}".strip()
    return "not checked" if any(cell["state"] == "NotChecked" for _, cell in cells) else "no match"


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    source, target = sys.argv[1], sys.argv[2]
    with open(source, newline="", encoding="utf-8-sig") as file:
        rows = list(csv.DictReader(file))
    records = []
    for number, row in enumerate(rows, start=2):
        fields = {key.strip().lower(): (value or "").strip() for key, value in row.items() if key}
        record = {"reference": fields.get("reference") or f"batch-{os.getpid()}-{number}"}
        for column, name in (("receiver", "receiverName"), ("company", "companyName"), ("address", "address")):
            if fields.get(column):
                record[name] = fields[column]
        records.append(record)

    answers, header = screen_all(records)
    codes = [item["code"] for item in header["lists"]] if header else []
    with open(target, "w", newline="", encoding="utf-8-sig") as file:
        writer = csv.writer(file)
        writer.writerow(["reference", "outcome", "decision", *codes])
        for record, answer in zip(records, answers):
            if answer is None or answer["type"] == "error":
                writer.writerow([record["reference"], "Error", decision(None), *["" for _ in codes]])
            else:
                writer.writerow([record["reference"], answer["outcome"], decision(answer["outcome"]), *[finding(answer, code) for code in codes]])
        if header:
            writer.writerow([])
            writer.writerow(["matcher", header["matcherVersion"]])
            for item in header["lists"]:
                writer.writerow(["list", item["code"], item["name"], item["version"], item["asOf"], item["state"]])
            writer.writerow([header["disclaimer"]])
    counts = {}
    for answer in answers:
        key = answer["outcome"] if answer and answer["type"] == "result" else "Error"
        counts[key] = counts.get(key, 0) + 1
    print(f"done: {counts}", file=sys.stderr)


if __name__ == "__main__":
    main()
