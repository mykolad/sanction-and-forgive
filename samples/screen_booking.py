#!/usr/bin/env python3
"""Screen a booking before it is confirmed: the pattern for a TMS, WMS or ERP calling Sanction & Forgive.

Python 3.9+, standard library only. Every name in the examples is fictional.

    python screen_booking.py --booking 2026-000123 --company "Northwind Trading LLC" --address "Hamburg, Germany"

Exit code: 0 = release the booking, 2 = hold it (Match or Review: a person decides), 3 = hold it (Incomplete or the
API couldn't answer: not checked is never cleared), 1 = a usage error. What each outcome means: docs/decisions.md.

The script never prints or logs what it sends: only the reference, the outcome and the decision. The reference is the
key to the API's audit log, so it carries the booking id, never a name.
"""

import argparse
import json
import os
import re
import secrets
import sys
import time
import urllib.error
import urllib.request

BASE_URL = os.environ.get("SAF_BASE_URL", "http://localhost:8080").rstrip("/")
# How long to keep trying while the service is warming up, busy or rate-limited, before holding the booking.
GIVE_UP_AFTER_SECONDS = 60

RELEASE, HOLD_FOR_REVIEW, HOLD_NOT_CHECKED = 0, 2, 3


def screen(reference, receiver, company, address):
    """POST /api/v1/screenings, waiting out 503 (warmingUp, busy) and 429 (rate limit) as Retry-After says.
    Returns the response, or None if the API couldn't answer in time."""
    body = {"reference": reference}
    if receiver:
        body["receiverName"] = receiver
    if company:
        body["companyName"] = company
    if address:
        body["address"] = address
    data = json.dumps(body).encode("utf-8")
    deadline = time.monotonic() + GIVE_UP_AFTER_SECONDS
    # Every attempt, wait and read fits in what's left of the deadline: past it, the booking is held, never later.
    left = lambda: deadline - time.monotonic()
    while left() > 1:
        request = urllib.request.Request(
            f"{BASE_URL}/api/v1/screenings",
            data=data,
            method="POST",
            headers={"Content-Type": "application/json", "Accept": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=min(30, left())) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            wait = float(error.headers.get("Retry-After") or (3 if error.code == 503 else 1))
            if error.code in (429, 503) and wait < left():
                print(f"{reference}: the service answered {error.code}, trying again in {wait:g} s", file=sys.stderr)
                time.sleep(wait)
                continue
            if error.code == 400:
                # Validation errors name the field, never quote its value.
                problem = json.load(error)
                print(f"{reference}: rejected: {json.dumps(problem.get('errors', {}))}", file=sys.stderr)
            else:
                print(f"{reference}: the service answered {error.code}", file=sys.stderr)
            return None
        except (urllib.error.URLError, ConnectionError, TimeoutError, ValueError) as error:
            # No connection, a reset (ConnectionError), a timeout, or an answer cut off (ValueError: not JSON).
            if left() > 4:
                print(f"{reference}: no answer ({type(error).__name__}), trying again in 3 s", file=sys.stderr)
                time.sleep(3)
                continue
            return None
    return None


def decide(result):
    """The booking's decision from the overall outcome (docs/decisions.md)."""
    if result is None:
        return HOLD_NOT_CHECKED, "hold: not checked (the API couldn't answer)"
    outcome = result.get("outcome")
    if outcome in ("Match", "Review"):
        return HOLD_FOR_REVIEW, f"hold for review: {outcome}"
    if outcome == "NoMatch":
        return RELEASE, "release: no match found in the lists searched"
    # Incomplete (a list couldn't be searched), or an outcome this script doesn't know: never a clean answer.
    return HOLD_NOT_CHECKED, f"hold: not checked ({outcome or 'no outcome'})"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--booking", required=True, help="the booking's id (goes into the reference; no personal data)")
    parser.add_argument("--receiver", help="the receiving person")
    parser.add_argument("--company", help="the receiving company")
    parser.add_argument("--address", help="the delivery address, any format")
    args = parser.parse_args()
    if not (args.receiver or args.company or args.address):
        parser.error("give at least one of --receiver, --company, --address")

    # Unique per check: the same booking screened again (after an edit, say) gets its own reference. The booking id is
    # cut to leave room for the random part (64 characters in all), and characters a reference can't hold become "-".
    booking = re.sub(r"[^A-Za-z0-9._:/-]", "-", args.booking)[:64 - len("booking--") - 6]
    reference = f"booking-{booking}-{secrets.token_hex(3)}"
    result = screen(reference, args.receiver, args.company, args.address)
    code, decision = decide(result)
    print(f"{reference}: {decision}")
    if result is not None:
        # What reproduces it: the lists as of when, their states, and the versions. Not the summary: it can quote an
        # allow-listed name, which is what was entered.
        lists = ", ".join(f"{item['code']} {item['version']} as of {item['asOf']} ({item['state']})" for item in result.get("lists", []))
        print(f"  matcher {result.get('matcherVersion')}, allow-list {result.get('allowListVersion')}, lists {lists}")
        print(f"  {result.get('disclaimer', '')}")
    return code


if __name__ == "__main__":
    sys.exit(main())
