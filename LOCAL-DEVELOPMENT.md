# Local development

Run `docker compose up --build`. The .NET 10 SDK is needed only for host-side `dotnet test AddressValidation.slnx`; Compose builds with the SDK image. Patroni manages two PostgreSQL nodes and an etcd quorum; the database is initialized by API startup. Two API containers run behind HAProxy at `http://127.0.0.1:8080/`. Open the local admin portal at `http://127.0.0.1:8081/` to manage full national releases and see both API replicas.

For a fresh database, follow [DATABASE-SEEDING.md](DATABASE-SEEDING.md). Synthetic fixtures are available only in the disposable test stack. Run `sh scripts/run-local-gate.sh` for the complete disposable test, coverage, seeded API, and formatting checks; [TESTING.md](TESTING.md) explains each layer.

`docker compose down` stops containers. `docker compose down -v` also removes the local database volume.

Compose caps PostgreSQL pools at 32 connections per API replica, 8 each for the worker and admin, and 4 for the optional loader. This leaves room within the local database's 100-connection limit for replication and health checks. The connection strings also set an application name, visible in `pg_stat_activity`. Change the total budget deliberately if adding more API replicas. The [saturation rehearsal](SATURATION-REHEARSAL.md) records the failure that led to these limits and the repeat measurements.

To run database integration tests without a host .NET SDK, use a disposable PostgreSQL instance or the isolated HA test stack. Never point `ADDRESS_TEST_CONNECTION` at the national database:

```sh
docker compose -f compose.auto-ha-test.yaml up -d pg-a pg-b
docker run --rm --network addressvalidation-auto-ha-test_default -v "$PWD:/src" -w /src \
  -e ADDRESS_TEST_CONNECTION='Host=pg-a;Database=postgres;Username=postgres;Password=address' \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test AddressValidation.slnx -c Release
docker compose -f compose.auto-ha-test.yaml down -v
```

The integration test creates and drops a uniquely named database. Set `ADDRESS_TEST_CONNECTION` only to a disposable instance where the test user may create databases.
