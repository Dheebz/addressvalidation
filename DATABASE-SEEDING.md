# Database setup and seeding

There are two separate workflows. The **main local stack** imports a complete official national G-NAF release. The **disposable test stack** can load three synthetic addresses for quick API checks. Synthetic seeding is disabled in the main stack.

## Main local stack: install national G-NAF

Start from the project directory. Docker Compose creates the PostgreSQL volumes and API startup applies the idempotent SQL migration in `db/migrations/001_initial.sql`.

```sh
docker compose up -d --build
docker compose run --rm gnaf-loader status
docker compose run --rm gnaf-loader check
```

On a fresh database, `status` prints no releases and `/health/ready` returns 503 until a national release is activated. `check` reads the official data.gov.au catalogue and prints its latest version. To download and prepare that full release:

```sh
docker compose run --rm gnaf-loader upgrade
docker compose run --rm gnaf-loader status
```

To rehearse an older national release, use its exact version. `check --version` and `upgrade --version` search the current and previous-version official catalogues; neither silently substitutes the newest release. For example, the November 2025 N−3 baseline is:

```sh
docker compose run --rm gnaf-loader check --version 2025_11
docker compose run --rm gnaf-loader upgrade --version 2025_11
```

The main Patroni cluster creates the `address` database in its first-primary bootstrap hook. The standard PostgreSQL image's `POSTGRES_DB` hook does not run under Patroni.

The loader downloads the GDA2020 ZIP into the `gnaf-cache` volume, resumes partial downloads, imports the national PSV files into a versioned schema, builds the serving table and indexes, and checks counts and geocode coverage. `upgrade` leaves the candidate **READY**; it does not change the live API. This is a large national import, so allow substantial disk space and time on both replicated PostgreSQL nodes.

Review the candidate's address count and geocode coverage at [the local admin portal](http://127.0.0.1:8081/). Activate it there, or use the exact version shown by `status`:

```sh
docker compose run --rm gnaf-loader promote VERSION
docker compose run --rm gnaf-loader status
curl -fsS http://127.0.0.1:8080/v1/metadata
curl -fsS http://127.0.0.1:8080/health/ready
```

The expected active release has dataset `G-NAF`, scope `ALL`, and a national address count. Promotion changes the stable API view in a database transaction. The previous national serving schema is retained for rollback. Completed raw tables and obsolete source archives are pruned by the release lifecycle; [DATASET-UPGRADE.md](DATASET-UPGRADE.md) explains the retention details.

To import an already downloaded **official** GDA2020 ZIP, mount it into the loader and supply its release version:

```sh
docker compose run --rm -v "/absolute/path/g-naf_aug26_allstates_gda2020_psv_110.zip:/data/g-naf_aug26_allstates_gda2020_psv_110.zip:ro" \
  gnaf-loader upgrade --archive /data/g-naf_aug26_allstates_gda2020_psv_110.zip --version 2026_08
```

Use the actual official archive filename and matching version for the release you install; the pre-ingestion check rejects a mismatch. Do not use `docker compose down -v` on the main stack: it removes the national database volumes. Use [OPERATIONS.md](OPERATIONS.md) for normal upgrades and recovery.

### Enrich aliases in an installed release

New imports retain compact street and locality alias dictionaries before their raw tables are pruned. Older installed releases can be backfilled without replacing their serving table or switching the active release:

```sh
docker compose run --rm gnaf-loader enrich-aliases 2026_08
docker compose run --rm gnaf-loader enrich-aliases 2026_05
```

The loader downloads the exact official archive for that version, verifies its recorded checksum, imports only the supporting street/locality and alias PSV files into a temporary schema, atomically replaces that release's alias dictionaries, and removes the downloaded ZIP and temporary schema. Allow temporary room for the source ZIP and import on both replicated database nodes. If you already have the matching archive, mount it and use `--archive /data/gnaf.zip`; the loader leaves an explicitly supplied file alone. A failed download may leave a `.part` file for resumption; `gnaf-loader prune-cache` removes obsolete completed-release cache files.

## Disposable test stack: seed synthetic addresses

This workflow uses its own PostgreSQL volumes and port `18080`. It never changes the main database. Build the images once, then start the isolated stack:

```sh
docker compose --profile tools build address-api gnaf-loader
docker compose -f compose.auto-ha-test.yaml up -d pg-a pg-b api-ha admin-ha
docker compose -f compose.auto-ha-test.yaml run --rm loader-ha seed demo_1
docker compose -f compose.auto-ha-test.yaml run --rm loader-ha promote demo_1
docker compose -f compose.auto-ha-test.yaml run --rm loader-ha status
python3 quality/run_seed_smoke.py --version demo_1
python3 quality/run_admin_smoke.py
```

`seed` creates `SAMPLE-1` (unit in Melbourne), `SAMPLE-2` (Sydney), and `SAMPLE-3` (Brisbane) as a synthetic candidate. The API smoke check exercises readiness, metadata, ID lookup, suggestion, matching, validation, and a missing ID. Direct address requests work on port `18080`; `/health/ready` deliberately returns 503 because a synthetic sample is not a full national release. The disposable admin portal uses port `18081`; its smoke check verifies the dashboard, cluster status, and form protection.

When finished, remove **only the disposable** stack and its volumes:

```sh
docker compose -f compose.auto-ha-test.yaml down -v
```

The test stack alone sets `Seed__AllowSynthetic=true`. The main stack does not, so `gnaf-loader seed` there fails with a clear error. For integration tests, set `ADDRESS_TEST_CONNECTION` only to a disposable PostgreSQL instance; the tests create and drop their own database.

## Verify rows directly

For technical diagnosis, query the current view and release metadata on the main stack:

```sh
docker compose exec -T postgres psql -U address -d address -c \
  "SELECT source_version, dataset_name, scope, status FROM platform.dataset_version ORDER BY created_at DESC;"
docker compose exec -T postgres psql -U address -d address -c \
  "SELECT count(*) FROM api.address_search;"
```

The row count may take time on a national table. The admin portal's validated release count is faster to inspect during routine operation.
