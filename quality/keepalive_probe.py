#!/usr/bin/env python3
"""Drive the address API from a separate client process using reused HTTP connections."""

import argparse
import concurrent.futures
import gzip
import http.client
import json
import math
import time
from collections import Counter
from pathlib import Path

from load_probe import CASES


def percentile(values, fraction):
    """Return a nearest-rank latency percentile in milliseconds."""
    if not values:
        return None
    values = sorted(values)
    return round(values[max(0, math.ceil(len(values) * fraction) - 1)], 1)


def main():
    """Schedule a fixed request count through persistent client connections."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="api-gateway")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--rate", type=int, default=1600)
    parser.add_argument("--workers", type=int, default=64)
    parser.add_argument("--label", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if min(args.port, args.seconds, args.rate, args.workers) < 1:
        parser.error("port, seconds, rate, and workers must be positive")

    started = time.monotonic()
    count = args.seconds * args.rate

    def work(worker):
        """Give each worker every Nth request while reusing its HTTP connection."""
        connection = None
        samples = []
        for index in range(worker, count, args.workers):
            # Monotonic schedule keeps the offered rate independent of the
            # previous response time, exposing overload instead of hiding it.
            due = started + index / args.rate
            delay = due - time.monotonic()
            if delay > 0:
                time.sleep(delay)
            name, method, path, body = CASES[index % len(CASES)]
            encoded = None if body is None else json.dumps(body).encode()
            headers = {"Content-Type": "application/json"} if encoded else {}
            begin = time.monotonic()
            status, error_code, retry_after, error = None, None, None, None
            try:
                if connection is None:
                    connection = http.client.HTTPConnection(
                        args.host, args.port, timeout=5
                    )
                connection.request(method, path, body=encoded, headers=headers)
                response = connection.getresponse()
                status = response.status
                retry_after = response.getheader("Retry-After")
                payload = response.read()
                if status != 200:
                    try:
                        error_code = json.loads(payload).get("error", {}).get("code")
                    except (ValueError, AttributeError):
                        pass
            except (OSError, TimeoutError, http.client.HTTPException) as exception:
                error = str(exception)
                if connection is not None:
                    connection.close()
                connection = None
            samples.append(
                {
                    "second": round(time.monotonic() - started, 3),
                    "operation": name,
                    "status": status,
                    "errorCode": error_code,
                    "retryAfter": retry_after,
                    "error": error,
                    "latencyMs": round((time.monotonic() - begin) * 1000, 1),
                }
            )
        if connection is not None:
            connection.close()
        return samples

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        samples = [
            sample for rows in pool.map(work, range(args.workers)) for sample in rows
        ]
    elapsed = time.monotonic() - started
    statuses = Counter(str(sample["status"]) for sample in samples)
    errors = Counter(sample["errorCode"] for sample in samples if sample["errorCode"])
    timings = [sample["latencyMs"] for sample in samples]
    summary = {
        "label": args.label,
        "requestedRatePerSecond": args.rate,
        "elapsedSeconds": round(elapsed, 2),
        "completedRequests": len(samples),
        "achievedRatePerSecond": round(len(samples) / elapsed, 1),
        "statuses": dict(statuses),
        "errorCodes": dict(errors),
        "transportErrors": sum(sample["status"] is None for sample in samples),
        "p50Ms": percentile(timings, 0.5),
        "p95Ms": percentile(timings, 0.95),
        "p99Ms": percentile(timings, 0.99),
        "retryAfterMissing": sum(
            sample["status"] == 429 and sample["retryAfter"] != "1"
            for sample in samples
        ),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result = {"summary": summary, "samples": samples}
    if args.output.suffix == ".gz":
        with gzip.open(args.output, "wt") as output:
            json.dump(result, output)
    else:
        args.output.write_text(json.dumps(result))
    print(json.dumps(summary, indent=2))
    return 0 if summary["transportErrors"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
