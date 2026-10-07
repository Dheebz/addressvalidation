#!/bin/sh
# Recreates the named disposable API/PostgreSQL stack and removes it on exit.
# Do not run while someone is inspecting that test stack.
set -eu

cd "$(dirname "$0")/.."
docker compose -f compose.auto-ha-test.yaml down -v
trap 'docker compose -f compose.auto-ha-test.yaml down -v' EXIT
docker compose build address-api gnaf-loader
docker compose -f compose.auto-ha-test.yaml up -d pg-a pg-b api-ha
docker compose -f compose.auto-ha-test.yaml run --rm loader-ha seed ha_auto
docker compose -f compose.auto-ha-test.yaml run --rm loader-ha promote ha_auto
python3 scripts/ha-auto-failover-rehearsal.py
