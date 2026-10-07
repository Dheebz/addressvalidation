#!/bin/sh
# Allow the disposable standby to take a base backup and stream WAL from A.
set -eu
printf '%s\n' 'host replication address all scram-sha-256' >> "$PGDATA/pg_hba.conf"
