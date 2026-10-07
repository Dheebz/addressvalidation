#!/usr/bin/env python3
"""Run a fixed-rate local API mix and save request-level latency/error evidence."""

import argparse
import concurrent.futures
import datetime as dt
import gzip
import json
import math
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter
from pathlib import Path

# Repeat one fixed mix so before/after runs exercise the same API operations.
CASES = (
    ("id", "GET", "/v1/addresses/GAACT714874496", None),
    (
        "suggest",
        "GET",
        "/v1/addresses/suggest?q="
        + urllib.parse.quote("Unit 1 26 Military Rd North Bondi NSW 2026"),
        None,
    ),
    (
        "match",
        "POST",
        "/v1/addresses/match",
        {"address": "12 Traill Place Charnwood ACT 2615"},
    ),
    (
        "validate",
        "POST",
        "/v1/addresses/validate",
        {"address": "Shop 11 296-298 Windsor Street Richmond NSW 2753"},
    ),
    (
        "validate_negative",
        "POST",
        "/v1/addresses/validate",
        {"address": "Unit 99999 26 Military Rd North Bondi NSW 2026"},
    ),
)


def percentile(values, fraction):
    """Return a nearest-rank latency percentile in milliseconds."""
    if not values:
        return None
    values = sorted(values)
    return round(values[max(0, math.ceil(len(values) * fraction) - 1)], 1)


def main():
    """Record request-level evidence and aggregate latency by operation."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8080")
    parser.add_argument("--seconds", type=int, default=45)
    parser.add_argument(
        "--rate", type=float, default=20, help="Target requests per second"
    )
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--label", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.seconds < 1 or args.rate <= 0 or args.concurrency < 1:
        parser.error("seconds, rate, and concurrency must be positive")

    base = args.base_url.rstrip("/")
    started_utc = dt.datetime.now(dt.timezone.utc).isoformat()
    started = time.monotonic()

    def call(index):
        """Issue one scheduled request and retain status, replica and timing."""
        name, method, path, body = CASES[index % len(CASES)]
        request = urllib.request.Request(
            base + path,
            data=None if body is None else json.dumps(body).encode(),
            method=method,
            headers={"Content-Type": "application/json"} if body else {},
        )
        begin = time.monotonic()
        code, instance, error, error_code, retry_after = None, None, None, None, None
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                code = response.status
                instance = response.headers.get("X-Api-Instance")
                response.read()
        except urllib.error.HTTPError as ex:
            code = ex.code
            instance = ex.headers.get("X-Api-Instance")
            retry_after = ex.headers.get("Retry-After")
            try:
                error_code = json.load(ex).get("error", {}).get("code")
            except (ValueError, OSError):
                pass
            error = str(ex)
        except (urllib.error.URLError, TimeoutError, OSError) as ex:
            error = str(ex)
        return {
            "at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "second": round(time.monotonic() - started, 3),
            "operation": name,
            "status": code,
            "instance": instance,
            "latencyMs": round((time.monotonic() - begin) * 1000, 1),
            "error": error,
            "errorCode": error_code,
            "retryAfter": retry_after,
        }

    # Bound submitted futures as well as running workers; otherwise the
    # client can build an unbounded local queue during server overload.
    pending = set()
    samples = []
    count = math.ceil(args.seconds * args.rate)
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        for index in range(count):
            due = started + index / args.rate
            delay = due - time.monotonic()
            if delay > 0:
                time.sleep(delay)
            while len(pending) >= args.concurrency * 2:
                done, pending = concurrent.futures.wait(
                    pending, return_when=concurrent.futures.FIRST_COMPLETED
                )
                samples.extend(future.result() for future in done)
            pending.add(pool.submit(call, index))
        for future in concurrent.futures.as_completed(pending):
            samples.append(future.result())

    samples.sort(key=lambda sample: sample["second"])
    durations = [sample["latencyMs"] for sample in samples]
    successes = [sample for sample in samples if sample["status"] == 200]
    errors = [sample for sample in samples if sample["status"] != 200]
    by_operation = {}
    for name in {case[0] for case in CASES}:
        selected = [sample for sample in samples if sample["operation"] == name]
        latencies = [sample["latencyMs"] for sample in selected]
        by_operation[name] = {
            "requests": len(selected),
            "errors": sum(s["status"] != 200 for s in selected),
            "p50Ms": percentile(latencies, 0.50),
            "p95Ms": percentile(latencies, 0.95),
            "p99Ms": percentile(latencies, 0.99),
        }
    success_seconds = [sample["second"] for sample in successes]
    gaps = [right - left for left, right in zip(success_seconds, success_seconds[1:])]
    result = {
        "label": args.label,
        "startedUtc": started_utc,
        "finishedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "targetRate": args.rate,
        "concurrency": args.concurrency,
        "durationSeconds": round(time.monotonic() - started, 2),
        "requests": len(samples),
        "successes": len(successes),
        "errors": len(errors),
        "actualRps": round(len(samples) / max(0.001, time.monotonic() - started), 1),
        "p50Ms": percentile(durations, 0.50),
        "p95Ms": percentile(durations, 0.95),
        "p99Ms": percentile(durations, 0.99),
        "longestSuccessGapSeconds": round(max(gaps, default=0), 2),
        "statusCounts": dict(Counter(str(sample["status"]) for sample in samples)),
        "instanceCounts": dict(Counter(str(sample["instance"]) for sample in samples)),
        "byOperation": by_operation,
        "samples": samples,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.output.suffix == ".gz":
        with gzip.open(args.output, "wt", encoding="utf-8") as stream:
            json.dump(result, stream, separators=(",", ":"))
            stream.write("\n")
    else:
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(
        f"{args.label}: {result['requests']} requests, {result['errors']} errors, "
        f"{result['actualRps']} rps, p50/p95/p99 {result['p50Ms']}/{result['p95Ms']}/{result['p99Ms']} ms, "
        f"longest success gap {result['longestSuccessGapSeconds']} s"
    )
    print(f"Saved {args.output}")


if __name__ == "__main__":
    main()
