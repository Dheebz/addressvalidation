#!/usr/bin/env python3
"""Check continuous seeded API reads across a disposable database failover."""

import argparse
import json
import time
import urllib.error
import urllib.request
from collections import Counter
from pathlib import Path


def main():
    """Measure the interruption window while a separate script fails over PostgreSQL."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:18080")
    parser.add_argument("--seconds", type=int, default=90)
    parser.add_argument("--rate", type=int, default=10)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.seconds < 30 or args.rate < 1:
        parser.error("seconds must be at least 30 and rate must be positive")

    started = time.monotonic()
    samples = []
    url = args.base_url.rstrip("/") + "/v1/addresses/SAMPLE-1"
    for index in range(args.seconds * args.rate):
        due = started + index / args.rate
        if due > time.monotonic():
            time.sleep(due - time.monotonic())
        status, error_code, correct_id = None, None, False
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                status = response.status
                correct_id = json.load(response).get("id") == "SAMPLE-1"
        except urllib.error.HTTPError as error:
            status = error.code
            try:
                error_code = json.load(error).get("error", {}).get("code")
            except (ValueError, AttributeError):
                pass
        except (urllib.error.URLError, TimeoutError, OSError):
            pass
        samples.append(
            {
                "second": round(time.monotonic() - started, 3),
                "status": status,
                "errorCode": error_code,
                "correctId": correct_id,
            }
        )

    # The time between successful reads captures the caller-visible outage;
    # 503s with DATASTORE_UNAVAILABLE are expected during promotion.
    successes = [sample["second"] for sample in samples if sample["correctId"]]
    gaps = [right - left for left, right in zip(successes, successes[1:])]
    summary = {
        "seconds": args.seconds,
        "requests": len(samples),
        "statuses": dict(Counter(str(sample["status"]) for sample in samples)),
        "errorCodes": dict(
            Counter(sample["errorCode"] for sample in samples if sample["errorCode"])
        ),
        "correctIds": len(successes),
        "longestSuccessGapSeconds": round(max(gaps, default=0), 2),
        "last15SecondsSuccessful": all(
            sample["correctId"]
            for sample in samples
            if sample["second"] >= args.seconds - 15
        ),
        "unexpectedFailures": sum(
            sample["status"] not in (200, 503)
            or (sample["status"] == 200 and not sample["correctId"])
            or (
                sample["status"] == 503
                and sample["errorCode"] != "DATASTORE_UNAVAILABLE"
            )
            for sample in samples
        ),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps({"summary": summary, "samples": samples}, indent=2) + "\n"
    )
    print(json.dumps(summary, indent=2))
    return (
        0
        if (
            len(successes) >= len(samples) * 0.7
            and summary["longestSuccessGapSeconds"] < 30
            and summary["last15SecondsSuccessful"]
            and summary["unexpectedFailures"] == 0
        )
        else 1
    )


if __name__ == "__main__":
    raise SystemExit(main())
