#!/bin/sh
# Patroni invokes this once on the first primary with its superuser URL and
# PGPASSFILE set. Create the application DB before API migrations start.
set -eu
connection_url=$1
if [ "$(psql "$connection_url" -Atqc "SELECT 1 FROM pg_database WHERE datname='address'")" != 1 ]; then
    psql "$connection_url" -v ON_ERROR_STOP=1 -c 'CREATE DATABASE address OWNER address'
fi
