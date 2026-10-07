#!/bin/bash
# Patroni owns role election and PostgreSQL startup; the entrypoint only fixes
# data-directory ownership before dropping privileges to the postgres user.
set -euo pipefail
install -d -o postgres -g postgres -m 0700 "$PGDATA"
exec gosu postgres /opt/patroni/bin/patroni /etc/patroni.yml
