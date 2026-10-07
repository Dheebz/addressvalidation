#!/bin/sh
# Restore a consistent logical snapshot of the installed national database to a disposable PostgreSQL container.
set -eu

cd "$(dirname "$0")/.."
restore_container=addressvalidation-national-restore-rehearsal
if docker inspect "$restore_container" >/dev/null 2>&1; then
    echo "Restore rehearsal container already exists; inspect it before retrying." >&2
    exit 2
fi

backup_dir=$(mktemp -d /private/tmp/gnaf-national-restore.XXXXXX)
container_created=false
cleanup() {
    if [ "$container_created" = true ]; then docker stop "$restore_container" >/dev/null 2>&1 || true; fi
    rm -rf "$backup_dir"
}
trap cleanup EXIT HUP INT TERM

source_snapshot=$(docker compose exec -T postgres psql -U address -d address -At -c "
    SELECT string_agg(source_version || ':' || status, ',' ORDER BY source_version)
    FROM platform.dataset_version WHERE dataset_name='G-NAF' AND scope='ALL';
    SELECT count(*) FROM api.address_search;
    SELECT count(*) FROM platform.street_alias;
    SELECT count(*) FROM platform.locality_alias;
    SELECT gnaf_pid FROM api.address_search WHERE gnaf_pid='GAACT714904529';")

echo "Creating a consistent compressed national backup..."
docker compose exec -T postgres pg_dump -U address -d address -Fc --no-owner --no-acl > "$backup_dir/address.dump"
test -s "$backup_dir/address.dump"
echo "Backup size: $(du -h "$backup_dir/address.dump" | cut -f1)"

docker run --rm -d --name "$restore_container" --network none \
    -e POSTGRES_PASSWORD=address -v "$backup_dir:/backup:ro" postgres:17 >/dev/null
container_created=true
ready=false
for _ in $(seq 1 60); do
    if docker exec "$restore_container" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
        ready=true
        break
    fi
    sleep 1
done
if [ "$ready" != true ]; then echo "Disposable PostgreSQL did not start" >&2; exit 1; fi
docker exec "$restore_container" createdb -U postgres address
echo "Restoring the national backup into the disposable database..."
docker exec "$restore_container" pg_restore -U postgres -d address --jobs=4 \
    --no-owner --no-acl --exit-on-error /backup/address.dump

restored_snapshot=$(docker exec "$restore_container" psql -U postgres -d address -At -c "
    SELECT string_agg(source_version || ':' || status, ',' ORDER BY source_version)
    FROM platform.dataset_version WHERE dataset_name='G-NAF' AND scope='ALL';
    SELECT count(*) FROM api.address_search;
    SELECT count(*) FROM platform.street_alias;
    SELECT count(*) FROM platform.locality_alias;
    SELECT gnaf_pid FROM api.address_search WHERE gnaf_pid='GAACT714904529';")
if [ "$source_snapshot" != "$restored_snapshot" ]; then
    echo "Restored national release, count, alias, or ID differs from the source" >&2
    echo "Source: $source_snapshot" >&2
    echo "Restored: $restored_snapshot" >&2
    exit 1
fi
echo "National backup restore passed: active/previous releases, address count, alias counts, sample ID"
echo "$restored_snapshot"
