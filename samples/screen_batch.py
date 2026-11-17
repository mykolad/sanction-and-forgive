#!/usr/bin/env python3
"""Screen a CSV of shipments through the stream endpoint and write the outcome of each.

Python 3.9+, standard library only.

    python screen_batch.py shipments.csv results.csv

The input CSV has a header with any of: reference, receiver, company, address (other columns are ignored); a row
without a reference gets one. The output has: reference, outcome, decision (see docs/decisions.md), a column per
list with that list's best finding, and the snapshot of the lists it was screened against. Nothing it sends is
printed or logged.

The stream (POST /api/v1/screenings/stream) takes NDJSON and answers NDJSON as it goes. This sends a few hundred
records per stream, keeps every answer as it arrives, and sends again from "nextLine" when a stream is cut short (its
time or record limit) or from the first unanswered record when the connection breaks. One stream runs at a time on
the server: a 429 means another is running (or the last one is still closing), so it waits as Retry-After says.
"""

import csv
import http.client
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE_URL = os.environ.get("SAF_BASE_URL", "http://localhost:8080").rstrip("/")
CHUNK = 200
# Tries in a row without progress before giving up; any answer received starts the count again.
MAX_RETRIES = 30


def stream(records):
    """Sends records (dicts) as one stream; returns (header, answers by 1-based line, end line or None). A connection
    that breaks off, or an answer cut inside a line, keeps the answers before it and returns no end line."""
    body = "".join(json.dumps(record, ensure_ascii=False) + "\n" for record in records).encode("utf-8")
    request = urllib.request.Request(
        f"{BASE_URL}/api/v1/screenings/stream",
        data=body,
        method="POST",
        headers={"Content-Type": "application/x-ndjson", "Accept": "application/x-ndjson"},
    )
    header, answers, end = None, {}, None
    with urllib.request.urlopen(request, timeout=300) as response:
        try:
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
        except (ConnectionError, TimeoutError, http.client.IncompleteRead, ValueError):
            end = None
    return header, answers, end


def screen_all(records):
    """Every record's answer (result or error) in order, each with the index of its stream's header, and the headers."""
    answers, headers, start, retries = [None] * len(records), [], 0, 0
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
            continue
        # Each stream pins its own snapshot of the lists: its answers keep it.
        if header is not None:
            headers.append(header)
        for line, message in got.items():
            answers[start + line - 1] = (message, len(headers) - 1)
        if end is not None and not end["truncated"]:
            sent = len(chunk)
        elif end is not None and end.get("nextLine"):
            sent = end["nextLine"] - 1
        else:
            # The connection broke: go on from the first unanswered record.
            sent = 0
            while sent + 1 in got:
                sent += 1
        if sent > 0:
            retries = 0
        else:
            if retries >= MAX_RETRIES:
                raise RuntimeError("The stream kept breaking off without an answer.")
            retries += 1
            time.sleep(3)
        start += sent
        print(f"{start} of {len(records)} screened", file=sys.stderr)
    return answers, headers


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
        allowed = " (allowed)" if cell.get("allowed") else ""
        return f"{cell['outcome']}{allowed} {cell['percent']}% {kind}: {name}".strip()
    if any(cell["state"] == "NotChecked" for _, cell in cells):
        return "not checked"
    return f"no match {max(cell.get('percent') or 0 for _, cell in cells)}%"


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

    answers, headers = screen_all(records)
    # A column per list any snapshot had, the latest's first.
    codes = []
    for header in reversed(headers):
        codes += [item["code"] for item in header["lists"] if item["code"] not in codes]
    used = sorted({snapshot for answer in answers if answer for _, snapshot in [answer] if snapshot >= 0})
    with open(target, "w", newline="", encoding="utf-8-sig") as file:
        writer = csv.writer(file)
        writer.writerow(["reference", "outcome", "decision", *codes, "snapshot"])
        for record, answer in zip(records, answers):
            message, snapshot = answer if answer else (None, -1)
            label = f"snapshot {used.index(snapshot) + 1}" if snapshot in used else ""
            if message is None or message["type"] == "error":
                writer.writerow([record["reference"], "Error", decision(None), *["" for _ in codes], label])
            else:
                searched = {item["code"] for item in headers[snapshot]["lists"]}
                cells = [finding(message, code) if code in searched else "" for code in codes]
                writer.writerow([record["reference"], message["outcome"], decision(message["outcome"]), *cells, label])
        # Each snapshot the rows were screened against: usually one; more if a list changed between streams.
        writer.writerow([])
        writer.writerow(["snapshot", "matcher", "allow-list", "list", "name", "version", "as of", "state"])
        for i, snapshot in enumerate(used, start=1):
            header = headers[snapshot]
            for item in header["lists"]:
                writer.writerow([f"snapshot {i}", header["matcherVersion"], header.get("allowListVersion", ""), item["code"], item["name"], item["version"], item["asOf"], item["state"]])
        if headers:
            writer.writerow([headers[-1]["disclaimer"]])
    counts = {}
    for answer in answers:
        key = answer[0]["outcome"] if answer and answer[0]["type"] == "result" else "Error"
        counts[key] = counts.get(key, 0) + 1
    print(f"done: {counts}", file=sys.stderr)


if __name__ == "__main__":
    main()
