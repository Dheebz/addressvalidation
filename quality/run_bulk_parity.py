#!/usr/bin/env python3
"""Compare bulk NDJSON rows with synchronous API results on the national corpus."""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import quote


def main():
    """Compare bulk rows with synchronous responses from the same national release."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8080")
    parser.add_argument(
        "--corpus", type=Path, default=Path(__file__).with_name("golden_2026_08.json")
    )
    args = parser.parse_args()
    base = args.base_url.rstrip("/")
    corpus = json.loads(args.corpus.read_text())
    cases = {case["name"]: case for case in corpus["positive"] + corpus["negative"]}
    positives = [cases[name] for name in ("NSW unit", "VIC unit on level", "NSW shop")]
    negatives = [cases[name] for name in ("Wrong unit number", "Nonsense address")]
    failures = []

    def get(path, token=None):
        headers = {"X-Bulk-Job-Token": token} if token else {}
        request = urllib.request.Request(base + path, headers=headers)
        with urllib.request.urlopen(request, timeout=20) as response:
            return json.load(response)

    def assert_private(path, token=None):
        try:
            get(path, token)
        except urllib.error.HTTPError as error:
            assert_equal(f"private {path}", error.code, 404)
        else:
            failures.append(f"private {path}: request succeeded without the job token")

    def post(path, body):
        request = urllib.request.Request(
            base + path,
            data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(request, timeout=20) as response:
            return json.load(response)

    def assert_equal(label, actual, expected):
        if actual != expected:
            failures.append(f"{label}: bulk={actual!r}, synchronous={expected!r}")

    def bulk(operation, lines, options=""):
        """Upload NDJSON, wait for a terminal job, then parse its result rows."""
        data = b"".join(json.dumps(line).encode() + b"\n" for line in lines)
        request = urllib.request.Request(
            base + "/v1/bulk?operation=" + operation + options,
            data=data,
            headers={"Content-Type": "application/x-ndjson"},
        )
        with urllib.request.urlopen(request, timeout=20) as response:
            job = json.load(response)
        token = job["accessToken"]
        assert_private(job["statusUrl"])
        assert_private(job["statusUrl"], "0" * 64)
        for _ in range(45):
            time.sleep(1)
            state = get(job["statusUrl"], token)
            if state["status"] == "FAILED":
                raise RuntimeError(
                    f"{operation} job failed: {state.get('errorMessage')}"
                )
            if state["status"] == "COMPLETED":
                result_url = state["result"]["downloadUrl"]
                assert_private(result_url)
                assert_private(result_url, "0" * 64)
                result_request = urllib.request.Request(
                    base + result_url, headers={"X-Bulk-Job-Token": token}
                )
                with urllib.request.urlopen(result_request, timeout=20) as response:
                    rows = [json.loads(line) for line in response if line.strip()]
                assert_equal(operation + " row count", len(rows), len(lines))
                return rows, state
        raise TimeoutError(f"{operation} job did not complete in 45 seconds")

    # Parity is meaningful only when the installed release matches the corpus
    # whose known IDs and address components are asserted below.
    metadata = get("/v1/metadata")
    if metadata.get("dataset", {}).get("version") != corpus["datasetVersion"]:
        raise RuntimeError("The active release differs from the golden corpus")

    id_cases = positives + [{"pid": "GAXXX000000000"}]
    id_rows, _ = bulk("ID", [{"id": case["pid"]} for case in id_cases])
    for case, row in zip(id_cases, id_rows):
        if case["pid"] == "GAXXX000000000":
            assert_equal("ID missing status", row["status"], "NOT_FOUND")
            assert_equal("ID missing components", row["components"], None)
            continue
        sync = get("/v1/addresses/" + case["pid"])
        for key, expected in (
            ("id", sync["id"]),
            ("formattedAddress", sync["address"]["formatted"]),
            ("components", sync["components"]),
            ("location", sync["location"]),
            ("source", sync["source"]),
            ("postalDeliveryStatus", sync["postalDeliveryStatus"]),
        ):
            assert_equal(f"ID {case['name']} {key}", row[key], expected)

    # Migration results retain source references and the same ranked candidate
    # IDs, scores, and structured fields as the synchronous suggestion route.
    suggest_cases = positives + [cases["Nonsense address"]]
    suggest_lines = [
        {"reference": case["name"], "address": case["input"]} for case in suggest_cases
    ]
    suggest_rows, suggest_state = bulk(
        "SUGGEST", suggest_lines, "&minScore=0.7&limit=5"
    )
    assert_equal(
        "suggest options",
        suggest_state["suggestionOptions"],
        {"minScore": 0.7, "limit": 5},
    )
    for case, row in zip(suggest_cases, suggest_rows):
        sync = get("/v1/addresses/suggest?q=" + quote(case["input"]) + "&limit=5")
        expected = [item for item in sync["suggestions"] if item["score"] >= 0.7]
        assert_equal(
            f"SUGGEST {case['name']} reference", row["reference"], case["name"]
        )
        assert_equal(f"SUGGEST {case['name']} input", row["input"], case["input"])
        assert_equal(
            f"SUGGEST {case['name']} release",
            row["datasetVersion"],
            sync["datasetVersion"],
        )
        assert_equal(
            f"SUGGEST {case['name']} IDs",
            [item["id"] for item in row["suggestions"]],
            [item["id"] for item in expected],
        )
        assert_equal(
            f"SUGGEST {case['name']} scores",
            [item["score"] for item in row["suggestions"]],
            [item["score"] for item in expected],
        )
        assert_equal(
            f"SUGGEST {case['name']} components",
            [item["components"] for item in row["suggestions"]],
            [item["components"] for item in expected],
        )
        assert_equal(
            f"SUGGEST {case['name']} status",
            row["status"],
            "SUGGESTED" if expected else "NO_SUGGESTIONS",
        )

    address_cases = positives + negatives
    for operation in ("MATCH", "VALIDATE", "FORMAT"):
        lines = [{"address": case["input"]} for case in address_cases]
        if operation == "VALIDATE":
            lines.append({"address": ""})
        rows, state = bulk(operation, lines)
        for case, row in zip(address_cases, rows):
            sync = post(
                "/v1/addresses/" + operation.lower(), {"address": case["input"]}
            )
            if operation == "MATCH":
                match = sync["match"] or {}
                expected = {
                    "id": match.get("id"),
                    "formattedAddress": match.get("formattedAddress"),
                    "components": match.get("components"),
                    "location": match.get("location"),
                    "matchType": sync["matchType"],
                    "datasetVersion": sync["datasetVersion"],
                    "postalDeliveryStatus": sync["postalDeliveryStatus"],
                }
                if match:
                    expected["confidence"] = match["confidence"]
            elif operation == "VALIDATE":
                address = sync["address"] or {}
                expected = {
                    "id": (sync["gnaf"] or {}).get("id"),
                    "valid": sync["valid"],
                    "formattedAddress": address.get("formatted"),
                    "components": address.get("components"),
                    "location": address.get("location"),
                    "confidence": sync["validation"]["confidence"],
                    "datasetVersion": sync["datasetVersion"],
                    "postalDeliveryStatus": sync["postalDelivery"]["status"],
                    "status": sync["validation"]["status"],
                }
            else:
                expected = {
                    "id": (sync["gnaf"] or {}).get("id"),
                    "matched": sync["matched"],
                    "formattedAddress": sync["formatted"],
                    "components": sync["components"],
                    "location": sync["location"],
                    "confidence": sync["confidence"],
                    "matchType": sync["matchType"],
                    "datasetVersion": sync["datasetVersion"],
                    "postalDeliveryStatus": sync["postalDeliveryStatus"],
                }
            for key, value in expected.items():
                assert_equal(f"{operation} {case['name']} {key}", row.get(key), value)
        if operation == "VALIDATE":
            assert_equal("invalid row status", rows[-1]["status"], "INVALID_INPUT")
            assert_equal("invalid row count", state["progress"]["failed"], 1)
        print(
            f"{operation}: {len(address_cases)} address rows"
            + (" + 1 invalid row" if operation == "VALIDATE" else "")
        )

    print(
        f"ID: {len(id_cases)} rows; SUGGEST: {len(suggest_rows)} rows; parity failures: {len(failures)}"
    )
    for failure in failures:
        print("FAIL " + failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
