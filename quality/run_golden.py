#!/usr/bin/env python3
"""Exercise address outcomes against a running, full national API release."""

import argparse
import json
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def main():
    """Check positive and negative corpus cases against the installed release."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8080")
    parser.add_argument(
        "--corpus", type=Path, default=Path(__file__).with_name("golden_2026_08.json")
    )
    args = parser.parse_args()
    corpus = json.loads(args.corpus.read_text())
    base = args.base_url.rstrip("/")
    timings = {}
    failures = []

    def request(method, path, body=None):
        """Keep status and payload for both successful and rejected API calls."""
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(
            base + path,
            data=data,
            method=method,
            headers={"Content-Type": "application/json"} if data else {},
        )
        started = time.perf_counter()
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                result = json.load(response)
                code = response.status
        except urllib.error.HTTPError as error:
            code = error.code
            result = json.load(error)
        elapsed = (time.perf_counter() - started) * 1000
        timing_key = path.split("?")[0]
        if timing_key.startswith("/v1/addresses/") and timing_key not in (
            "/v1/addresses/suggest",
            "/v1/addresses/match",
            "/v1/addresses/validate",
            "/v1/addresses/format",
        ):
            timing_key = "/v1/addresses/{gnafPid}"
        timings.setdefault(timing_key, []).append(elapsed)
        return code, result

    def check(name, condition, detail):
        if not condition:
            failures.append(f"{name}: {detail}")

    def check_components(name, actual, expected, endpoint):
        """Compare only components specified by this corpus case."""
        if expected is None:
            return
        actual = actual or {}
        differences = {
            key: (value, actual.get(key))
            for key, value in expected.items()
            if actual.get(key) != value
        }
        check(name, not differences, f"{endpoint} component differences: {differences}")

    code, metadata = request("GET", "/v1/metadata")
    check(
        "release",
        code == 200
        and metadata.get("dataset", {}).get("version") == corpus["datasetVersion"],
        f"expected {corpus['datasetVersion']}, got {metadata.get('dataset')}; HTTP {code}",
    )
    if failures:
        print("\n".join(failures), file=sys.stderr)
        return 2

    # Positive cases must resolve to one known G-NAF ID across all supported
    # synchronous operations; negatives must never be reported valid.
    for case in corpus["positive"]:
        name, address, pid, canonical = (
            case[key] for key in ("name", "input", "pid", "canonical")
        )
        code, exact = request("GET", "/v1/addresses/" + urllib.parse.quote(pid))
        check(
            name,
            code == 200 and exact.get("address", {}).get("formatted") == canonical,
            f"ID lookup returned HTTP {code}: {exact.get('address')}",
        )
        expected_components = case.get("components")
        check_components(
            name, exact.get("components"), expected_components, "ID lookup"
        )
        code, suggestions = request(
            "GET",
            "/v1/addresses/suggest?"
            + urllib.parse.urlencode({"q": address, "limit": 20}),
        )
        check(
            name,
            code == 200
            and any(
                item.get("id") == pid for item in suggestions.get("suggestions", [])
            ),
            f"expected PID absent from suggestions; HTTP {code}",
        )
        suggestion = next(
            (
                item
                for item in suggestions.get("suggestions", [])
                if item.get("id") == pid
            ),
            {},
        )
        check_components(
            name, suggestion.get("components"), expected_components, "suggest"
        )
        code, match = request("POST", "/v1/addresses/match", {"address": address})
        matched = match.get("match") or {}
        check(
            name,
            code == 200
            and matched.get("id") == pid
            and matched.get("confidence", 0) >= 0.8,
            f"match HTTP {code}: {match.get('matchType')} {matched.get('id')} {matched.get('confidence')}",
        )
        if "matchType" in case:
            check(
                name,
                match.get("matchType") == case["matchType"],
                f"expected match type {case['matchType']}, got {match.get('matchType')}",
            )
        check_components(name, matched.get("components"), expected_components, "match")
        code, validation = request(
            "POST", "/v1/addresses/validate", {"address": address}
        )
        check(
            name,
            code == 200
            and validation.get("valid") is True
            and validation.get("validation", {}).get("status") == "GNAF_MATCHED"
            and validation.get("gnaf", {}).get("id") == pid
            and validation.get("postalDelivery", {}).get("status")
            == "POSTAL_DELIVERY_NOT_VERIFIED",
            f"validation HTTP {code}: {validation.get('validation')} {validation.get('gnaf')}",
        )
        check_components(
            name,
            (validation.get("address") or {}).get("components"),
            expected_components,
            "validate",
        )
        code, formatted = request("POST", "/v1/addresses/format", {"address": address})
        check(
            name,
            code == 200
            and formatted.get("matched") is True
            and formatted.get("gnaf", {}).get("id") == pid
            and formatted.get("formatted") == canonical
            and formatted.get("confidence", 0) >= 0.8,
            f"format HTTP {code}: {formatted.get('matched')} {formatted.get('gnaf')} {formatted.get('formatted')}",
        )
        check_components(
            name, formatted.get("components"), expected_components, "format"
        )

    for case in corpus["negative"]:
        name, address = case["name"], case["input"]
        code, validation = request(
            "POST", "/v1/addresses/validate", {"address": address}
        )
        check(
            name,
            code == 200
            and validation.get("valid") is False
            and validation.get("gnaf") is None,
            f"unexpected validation HTTP {code}: {validation.get('validation')} {validation.get('gnaf')}",
        )
        code, formatted = request("POST", "/v1/addresses/format", {"address": address})
        check(
            name,
            code == 200
            and formatted.get("matched") is False
            and formatted.get("gnaf") is None,
            f"unexpected formatting HTTP {code}: {formatted.get('matched')} {formatted.get('gnaf')}",
        )

    for path, samples in timings.items():
        ordered = sorted(samples)
        p95 = ordered[max(0, int(len(ordered) * 0.95 + 0.999) - 1)]
        print(
            f"{path}: n={len(samples)} median={statistics.median(samples):.0f}ms p95={p95:.0f}ms"
        )
    print(
        f"Cases: {len(corpus['positive'])} positive, {len(corpus['negative'])} negative; failures: {len(failures)}"
    )
    for failure in failures:
        print("FAIL " + failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
