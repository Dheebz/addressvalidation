# Local PostgreSQL automatic failover

The normal `docker compose up --build` stack has PostgreSQL A (`postgres`), PostgreSQL B (`postgres-b`), and three etcd members. Patroni selects one database primary and keeps the other as a streaming standby. The API, worker, and admin portal use Npgsql's multi-host, primary-only connection. If A fails, Patroni promotes B; clients reconnect to B without an operator command. The old A rejoins as a standby when it starts under Patroni.

On 2 October 2026, disposable fixture tests killed A. Patroni promoted B automatically, the API recovered in 20.16 and 20.55 seconds on two runs, and B accepted a write. A separate test on the 15,949,543-address national stack also killed A after verifying zero replay lag. B promoted automatically, the API returned ready, B accepted a temporary write, and A rejoined as a standby. Both nodes later reported zero replay lag. The exact national recovery time was not measured.

Run `sh scripts/run-auto-ha-rehearsal.sh` for a repeatable fixture test. It creates a separate stack and removes its test containers and volumes on exit. Never run destructive integration tests against the national database; use the isolated test stack.

Check the live roles with:

```sh
docker compose exec -T postgres psql -U address -d address -At -c 'SELECT pg_is_in_recovery()'
docker compose exec -T postgres-b psql -U address -d address -At -c 'SELECT pg_is_in_recovery()'
```

`f` means primary; `t` means standby. Exactly one node should report `f`. On the primary, `SELECT application_name,state,pg_wal_lsn_diff(pg_current_wal_lsn(),replay_lsn) FROM pg_stat_replication` shows the standby and replay lag. Use normal `docker compose` commands so Patroni remains in control. Do not start a PostgreSQL process directly on either data volume.

The host's `127.0.0.1:5432` port connects to A specifically. After failover A may be the standby, so direct database clients must use both host names or a primary-aware proxy. The .NET services already use both names.

The local replica is asynchronous, so a write that has not reached B at the moment A fails can be lost. An in-flight request can fail during promotion and should be retried where the operation is safe to retry. Both database copies and the etcd quorum run on one computer, so this does not protect against that computer or Docker engine failing. The .NET API is still a separate container. A future deployment needs redundant API instances and a managed database HA service.
