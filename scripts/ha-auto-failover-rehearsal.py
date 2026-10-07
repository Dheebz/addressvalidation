#!/usr/bin/env python3
"""Kill Patroni A and measure automatic promotion plus API recovery."""

import json
import subprocess
import time
import urllib.error
import urllib.request

COMPOSE = ["docker", "compose", "-f", "compose.auto-ha-test.yaml"]
URL = "http://127.0.0.1:18080/v1/addresses/SAMPLE-1"


def run(*args):
    """Run one Compose command against the disposable HA stack."""
    return subprocess.run(
        [*COMPOSE, *args], check=True, capture_output=True, text=True
    ).stdout.strip()


def role(node):
    """Read a node's direct PostgreSQL recovery role without routed connections."""
    return run(
        "exec",
        "-T",
        node,
        "psql",
        "-U",
        "postgres",
        "-d",
        "postgres",
        "-At",
        "-c",
        "SELECT pg_is_in_recovery()",
    )


def probe():
    """Check that the seeded ID can still be read through the test API."""
    try:
        with urllib.request.urlopen(URL, timeout=1) as response:
            return (
                response.status == 200 and json.load(response).get("id") == "SAMPLE-1"
            )
    except (urllib.error.URLError, TimeoutError, ValueError):
        return False


if role("pg-a") != "f" or role("pg-b") != "t" or not probe():
    raise SystemExit("Expected A primary, B streaming standby, and a working test API")

for _ in range(60):
    replicated = run(
        "exec",
        "-T",
        "pg-b",
        "psql",
        "-U",
        "postgres",
        "-d",
        "postgres",
        "-At",
        "-c",
        "SELECT count(*) FROM api.address_search WHERE gnaf_pid='SAMPLE-1'",
    )
    if replicated == "1":
        break
    time.sleep(0.5)
else:
    raise SystemExit("B did not receive the test address before failover")

# SIGKILL models a hard process loss; B must promote without manual action.
run("kill", "-s", "SIGKILL", "pg-a")
failed_at = time.monotonic()
failed_probes = 0
attempts = 0
while time.monotonic() - failed_at < 75:
    attempts += 1
    if probe():
        recovered_at = time.monotonic()
        break
    failed_probes += 1
    time.sleep(0.25)
else:
    raise SystemExit("API did not recover automatically within 75 seconds")

if role("pg-b") != "f":
    raise SystemExit("API recovered, but B is not the primary")
write = run(
    "exec",
    "-T",
    "pg-b",
    "psql",
    "-U",
    "postgres",
    "-d",
    "postgres",
    "-At",
    "-c",
    "CREATE TEMP TABLE failover_write (id integer); INSERT INTO failover_write VALUES (1); SELECT count(*) FROM failover_write",
)
if not write.endswith("1"):
    raise SystemExit("Promoted B did not accept a write")

print(
    json.dumps(
        {
            "a_killed": True,
            "b_promoted_without_manual_action": True,
            "api_recovered": True,
            "b_accepted_write": True,
            "seconds_from_a_failure_to_api_recovery": round(
                recovered_at - failed_at, 2
            ),
            "failed_api_probes": failed_probes,
            "total_api_probes": attempts,
        },
        indent=2,
    )
)
