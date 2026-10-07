#!/usr/bin/env python3
"""Check the disposable admin portal without changing its release state."""

import argparse
import http.cookiejar
import json
import re
import time
import urllib.error
import urllib.parse
import urllib.request


def main():
    """Verify readiness, dashboard, cluster status, and form protection."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:18081")
    parser.add_argument("--api-base-url", default="http://127.0.0.1:18080")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")

    # The container can be running before migrations and HTTP startup finish.
    for attempt in range(90):
        try:
            with urllib.request.urlopen(base + "/health/ready", timeout=5) as response:
                ready = json.load(response)
                if response.status == 200 and ready.get("status") == "ready":
                    break
        except (urllib.error.URLError, TimeoutError):
            pass
        if attempt == 89:
            raise RuntimeError("Admin portal did not become ready")
        time.sleep(1)

    with urllib.request.urlopen(base + "/", timeout=10) as response:
        dashboard = response.read().decode()
        assert response.status == 200
        assert "AddressDesk" in dashboard
        assert "Database and failover status" in dashboard
        assert "address validation" in dashboard.lower()
        assert "Delete previous release data" in dashboard
        assert "/releases/demo_1/delete" not in dashboard
        assert 'id="release-select"' in dashboard
        assert 'id="check-latest"' in dashboard
        assert 'name="version" id="prepare-version"' in dashboard
        assert dashboard.index("Choose and prepare a release") < dashboard.index(
            "Database and failover status"
        )

    with urllib.request.urlopen(base + "/api/system", timeout=10) as response:
        system = json.load(response)
        assert response.status == 200
        assert system["cluster"]["coordinatorsTotal"] == 3
        assert len(system["cluster"]["databases"]) == 2

    # No token means no release mutation, even in local Development mode.
    for path in ("/rollback", "/releases/demo_1/delete"):
        request = urllib.request.Request(base + path, data=b"", method="POST")
        try:
            urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            assert error.code == 400, (
                f"Expected missing-token HTTP 400 for {path}, got {error.code}"
            )
        else:
            raise AssertionError(f"Admin accepted {path} without a form token")

    # A portal save must be visible to the API immediately, without a restart.
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar())
    )
    with opener.open(base + "/", timeout=10) as response:
        page = response.read().decode()
    token = re.search(r"name='__RequestVerificationToken' value='([^']+)'", page)
    assert token is not None, "Admin dashboard did not render a form token"
    wrong_confirmation = urllib.parse.urlencode(
        {"__RequestVerificationToken": token.group(1), "confirmVersion": "wrong"}
    ).encode()
    with opener.open(
        base + "/releases/demo_1/delete", data=wrong_confirmation, timeout=10
    ) as response:
        assert "Type the exact release version" in response.read().decode()
    invalid_release = urllib.parse.urlencode(
        {"__RequestVerificationToken": token.group(1), "version": "not-a-release"}
    ).encode()
    with opener.open(base + "/prepare", data=invalid_release, timeout=10) as response:
        assert "Choose a release from the official catalogue" in response.read().decode()
    for hours in (12, 24):
        data = urllib.parse.urlencode(
            {"__RequestVerificationToken": token.group(1), "retentionHours": hours}
        ).encode()
        with opener.open(
            base + "/settings/bulk-retention", data=data, timeout=10
        ) as response:
            assert response.status == 200  # The action redirects to the dashboard.
        with urllib.request.urlopen(
            args.api_base_url + "/v1/metadata", timeout=10
        ) as response:
            metadata = json.load(response)
            assert metadata["limits"]["bulkResultRetentionHours"] == hours

    print(
        "Admin smoke passed: readiness, dashboard, cluster, deletion safeguards, live retention"
    )


if __name__ == "__main__":
    main()
