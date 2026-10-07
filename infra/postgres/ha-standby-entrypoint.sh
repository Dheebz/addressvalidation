#!/bin/bash
# Bootstrap B from A only when its data directory is empty; on restart, keep
# its existing replicated state rather than taking another full base backup.
set -euo pipefail

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    install -d -o postgres -g postgres -m 0700 "$PGDATA"
    export PGPASSWORD="$POSTGRES_PASSWORD"
    until gosu postgres pg_isready -h pg-a -U address -d address >/dev/null 2>&1; do
        sleep 1
    done
    gosu postgres pg_basebackup -h pg-a -U address -D "$PGDATA" -Fp -Xs -R
fi

exec docker-entrypoint.sh postgres
