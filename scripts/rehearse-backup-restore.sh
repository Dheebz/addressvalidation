#!/bin/sh
# Prove a logical backup restores an active release in an isolated HA test stack.
set -eu

cd "$(dirname "$0")/.."
test_compose=compose.auto-ha-test.yaml
test_project=addressvalidation-auto-ha-test
if [ -n "$(docker compose -f "$test_compose" ps -q --all)" ] ||
   docker volume ls --format '{{.Name}}' | grep -q "^${test_project}_"; then
    echo "Disposable test stack already exists; inspect it before running the restore rehearsal." >&2
    exit 2
fi

backup_dir=$(mktemp -d "${TMPDIR:-/tmp}/gnaf-restore.XXXXXX")
probe_pid=
cleanup() {
    if [ -n "$probe_pid" ]; then kill "$probe_pid" 2>/dev/null || true; fi
    docker compose -f "$test_compose" down -v
    rm -rf "$backup_dir"
}
trap cleanup EXIT HUP INT TERM

docker compose --profile tools build address-api gnaf-loader
docker compose -f "$test_compose" up -d --wait pg-a pg-b api-ha
docker compose -f "$test_compose" run --rm loader-ha seed demo_1
docker compose -f "$test_compose" run --rm loader-ha promote demo_1

before=$(docker compose -f "$test_compose" exec -T pg-a psql -U postgres -d postgres -At -c \
    "SELECT source_version || ':' || (SELECT count(*) FROM api.address_search) FROM platform.dataset_version WHERE status='ACTIVE'")
docker compose -f "$test_compose" exec -T pg-a pg_dump -U postgres -d postgres -Fc > "$backup_dir/address.dump"
test -s "$backup_dir/address.dump"
docker compose -f "$test_compose" exec -T pg-a createdb -U postgres restore_rehearsal
docker compose -f "$test_compose" exec -T pg-a pg_restore -U postgres -d restore_rehearsal \
    --no-owner --no-acl --exit-on-error < "$backup_dir/address.dump"
after=$(docker compose -f "$test_compose" exec -T pg-a psql -U postgres -d restore_rehearsal -At -c \
    "SELECT source_version || ':' || (SELECT count(*) FROM api.address_search) FROM platform.dataset_version WHERE status='ACTIVE'")
if [ "$before" != "$after" ]; then
    echo "Restored release/count mismatch: original=$before restored=$after" >&2
    exit 1
fi
restored_id=$(docker compose -f "$test_compose" exec -T pg-a psql -U postgres -d restore_rehearsal -At -c \
    "SELECT gnaf_pid FROM api.address_search WHERE gnaf_pid='SAMPLE-1'")
if [ "$restored_id" != SAMPLE-1 ]; then
    echo "Restored address lookup failed" >&2
    exit 1
fi

# Keep requests flowing before, during, and after takeover. The probe records
# retryable errors and bounds the recovery gap rather than requiring every
# in-flight request to survive promotion.
python3 quality/recovery_probe.py --seconds 90 --rate 10 \
    --output quality/results/recovery_rehearsal.json > "$backup_dir/recovery-summary.json" &
probe_pid=$!
sleep 5

# The restored database must also replicate and remain readable after B takes
# over. Restarting A proves it can rejoin as a standby after the hard failure.
docker compose -f "$test_compose" kill -s KILL pg-a
promoted=false
for _ in $(seq 1 60); do
    role=$(docker compose -f "$test_compose" exec -T pg-b psql -U postgres -d restore_rehearsal -At -c \
        "SELECT pg_is_in_recovery()" 2>/dev/null || true)
    if [ "$role" = f ]; then promoted=true; break; fi
    sleep 2
done
if [ "$promoted" != true ]; then
    echo "Standby did not promote with the restored database" >&2
    exit 1
fi
docker compose -f "$test_compose" start pg-a
rejoined=false
for _ in $(seq 1 60); do
    role=$(docker compose -f "$test_compose" exec -T pg-a psql -U postgres -d restore_rehearsal -At -c \
        "SELECT pg_is_in_recovery()" 2>/dev/null || true)
    if [ "$role" = t ]; then rejoined=true; break; fi
    sleep 2
done
if [ "$rejoined" != true ]; then
    echo "Former primary did not rejoin as standby" >&2
    exit 1
fi
api_recovered=false
for _ in $(seq 1 60); do
    if python3 quality/run_seed_smoke.py --version demo_1 >/dev/null 2>&1; then
        api_recovered=true
        break
    fi
    sleep 2
done
if [ "$api_recovered" != true ]; then
    echo "API did not recover after database takeover" >&2
    exit 1
fi
if ! wait "$probe_pid"; then
    cat "$backup_dir/recovery-summary.json"
    echo "Continuous API recovery probe failed; details in quality/results/recovery_rehearsal.json" >&2
    exit 1
fi
probe_pid=
cat "$backup_dir/recovery-summary.json"
echo "Backup restore and failover passed: $after, SAMPLE-1, B promoted, A rejoined, 90-second API probe passed"
