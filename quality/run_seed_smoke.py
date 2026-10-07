#!/usr/bin/env python3
"""Check the API contract against an isolated synthetic seed (never national data)."""

import argparse
import json
import urllib.error
import urllib.parse
import urllib.request


def request(base, method, path, body=None):
    """Return status and JSON for both normal and error responses."""
    payload = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(
        base + path,
        data=payload,
        method=method,
        headers={"Content-Type": "application/json"} if payload else {},
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


def require(condition, message):
    """Fail the disposable-stack smoke check with a useful contract message."""
    if not condition:
        raise AssertionError(message)


def main():
    """Prove synthetic data works for tests without satisfying national readiness."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:18080")
    parser.add_argument("--version", default="demo_1")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")

    code, ready = request(base, "GET", "/health/ready")
    require(
        code == 503 and ready["checks"]["dataset"] == "no_active_national_release",
        "synthetic seed must not make national readiness healthy",
    )

    code, metadata = request(base, "GET", "/v1/metadata")
    require(
        code == 200
        and metadata["dataset"] == {"name": "SYNTHETIC", "version": args.version},
        "metadata does not identify the synthetic seed",
    )

    code, address = request(base, "GET", "/v1/addresses/SAMPLE-1")
    require(
        code == 200
        and address["id"] == "SAMPLE-1"
        and address["components"]["flatNumber"] == "4"
        and address["source"]["version"] == args.version,
        "seeded address lookup or components failed",
    )

    query = urllib.parse.quote("Unit 4 10 Collins St Melbourne VIC 3000")
    code, suggestions = request(base, "GET", "/v1/addresses/suggest?q=" + query)
    require(
        code == 200
        and any(item["id"] == "SAMPLE-1" for item in suggestions["suggestions"]),
        "seeded address was not suggested",
    )

    code, matched = request(
        base,
        "POST",
        "/v1/addresses/match",
        {"address": "Unit 4 10 Collins St Melbourne VIC 3000"},
    )
    require(
        code == 200 and matched["match"]["id"] == "SAMPLE-1",
        "seeded address did not match",
    )

    code, validated = request(
        base,
        "POST",
        "/v1/addresses/validate",
        {"address": "Unit 4 10 Collins St Melbourne VIC 3000"},
    )
    require(
        code == 200
        and validated["valid"]
        and validated["validation"]["status"] == "SAMPLE_MATCHED"
        and validated["postalDelivery"]["status"] == "POSTAL_DELIVERY_NOT_VERIFIED",
        "seeded address validation contract failed",
    )

    code, missing = request(base, "GET", "/v1/addresses/NO-SUCH-ID")
    require(
        code == 404 and missing["error"]["code"] == "NOT_FOUND",
        "missing ID error contract failed",
    )
    print(
        f"Synthetic API smoke passed for {args.version}: readiness, metadata, ID, suggest, match, validate, 404"
    )


if __name__ == "__main__":
    main()
