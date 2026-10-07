#!/bin/sh
# Reproduce the disposable test, coverage, seed, API, and format gates locally.
set -eu

cd "$(dirname "$0")/.."
test_compose=compose.auto-ha-test.yaml
test_project=addressvalidation-auto-ha-test

# Never reuse or remove an existing test stack that someone may be inspecting.
if [ -n "$(docker compose -f "$test_compose" ps -q --all)" ] ||
   docker volume ls --format '{{.Name}}' | grep -q "^${test_project}_"; then
    echo "Disposable test stack already exists; inspect and remove it before running the gate." >&2
    exit 2
fi

cleanup() { docker compose -f "$test_compose" down -v; }
trap cleanup EXIT HUP INT TERM

docker compose --profile tools build address-api gnaf-admin gnaf-loader
if guard_output=$(docker run --rm addressvalidation-gnaf-loader seed forbidden 2>&1); then
    echo "Synthetic seed guard unexpectedly succeeded without a database." >&2
    exit 1
else
    guard_status=$?
    if [ "$guard_status" -ne 2 ] || ! printf '%s' "$guard_output" | grep -q 'Synthetic seeding is disabled'; then
        printf '%s\n' "$guard_output" >&2
        echo "Synthetic seed guard failed unexpectedly." >&2
        exit 1
    fi
fi
docker compose -f "$test_compose" up -d pg-a pg-b api-ha admin-ha

mkdir -p quality/coverage
coverage_dir=$(mktemp -d quality/coverage/local.XXXXXX)
docker run --rm --network "${test_project}_default" -v "$PWD:/src" -w /src \
    -e ADDRESS_TEST_CONNECTION='Host=pg-a;Database=postgres;Username=postgres;Password=address' \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test AddressValidation.slnx -c Release --collect:'XPlat Code Coverage' \
    --settings quality/coverage.runsettings \
    --results-directory "/src/$coverage_dir" --nologo
python3 quality/check_coverage.py "$coverage_dir"

docker compose -f "$test_compose" run --rm loader-ha seed demo_1
docker compose -f "$test_compose" run --rm loader-ha promote demo_1
python3 quality/run_seed_smoke.py --version demo_1
python3 quality/run_admin_smoke.py

docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet format AddressValidation.slnx --verify-no-changes --no-restore --verbosity quiet
echo "Local test gate passed"
