# National N−3 release replay — 6 October 2026

The official current catalogue lists August 2026 as N. The official previous-version catalogue lists May 2026, February 2026, and November 2025 as N−1, N−2, and N−3. This rehearsal starts from an empty local operational stack, prepares and activates those four complete national GDA2020 PSV releases in chronological order, and checks serving counts, storage, API readiness, and rollback retention at each switch.

The [November 2025 release report](https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc/resource/afb23dc5-e4f2-45af-acd2-f4ca9daa36f7/download/g-naf-release-report-nov-2025.pdf) lists 15,827,416 current addresses. The [February 2026 release report](https://data.gov.au/data/dataset/19432f89-dc3a-4ef3-b943-5326ef1dbecc/resource/28f95bdc-664e-42d0-ad04-0e5b172b7e6b/download/gnaf_release-en-feb-2026.pdf) lists 15,860,127. These provide independent count checks for the first two imports.

## Reset boundary

The stack was stopped, then the two PostgreSQL volumes, Azurite bulk volume, G-NAF cache, and three etcd volumes were removed. The separate old `addressvalidation_backups` volume was retained as a safety copy during the replay; after August and May were verified, it too was removed. No prior operational or backup volume remains. A fresh startup exposed that Patroni did not create the `address` database; the first-primary `post_bootstrap` hook now creates it. A second clean bootstrap verified automatic creation. The new database began with zero installed releases, zero bulk jobs, and zero operator events.

## Replay sequence

| Stage | Release | Archive bytes | Prepared addresses | Geocoded | Serving table | Database per node | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| N−3 | 2025_11 | 1,700,610,288 | 15,827,416 | 100.00% | 6,221,127,680 B | 6,242,571,955 B | ACTIVE; API ready |
| N−2 | 2026_02 | 1,705,544,671 | 15,860,127 | 100.00% | 6,228,992,000 B | 12,484,482,739 B | ACTIVE; API ready |
| N−1 | 2026_05 | 1,706,838,674 | 15,901,249 | 100.00% | 6,244,237,312 B | 12,520,502,963 B | ACTIVE; API ready |
| N | 2026_08 | 1,854,931,188 | 15,949,543 | 100.00% | 6,159,278,080 B | 12,458,571,443 B | ACTIVE; API ready |

Each `upgrade --version VERSION` resolves the exact official archive in the current or previous-version catalogue, verifies the filename and PSV schema before ingestion, and leaves a READY candidate. Each promotion is a separate database transaction. The public API should report the previous active release while the next is being prepared. After the final switch, only N and N−1 should remain installed; completed raw tables and cached ZIPs should be pruned.

November's validated address count exactly matched the official release report. Before activation, the database occupied 11,153,815,219 bytes on the primary, including raw import tables. After activation and raw-table pruning it occupied 6,242,571,955 bytes; the cache volume was empty. The public `/health/ready` and `/v1/metadata` endpoints reported `2025_11`, and an exact G-NAF ID lookup returned a structured November address.

February's validated count exactly matched its official release report. November stayed active during the February download and import; a public ID lookup returned HTTP 200 while the national transform ran. The database occupied 17,411,200,691 bytes before activation and 12,484,482,739 afterward. February is ACTIVE, November is RETIRED, both releases' raw tables are pruned, and the cache is empty. The API reported `2026_02` and served a structured February G-NAF ID.

May's validated count matched the prior installed May national release. The database occupied 23,688,279,731 bytes before activation and 12,520,502,963 afterward. May is ACTIVE, February is RETIRED, and November's metadata and schema were removed. The cache is empty, both retained releases' raw tables are pruned, and the API served a structured May G-NAF ID.

During May's transform, local Docker and the public API stopped responding for a period while the Mac/OrbStack runtime was under heavy I/O. Docker's basic daemon ping still returned OK, but container-list and exec calls stalled and an API lookup timed out. The running loader later resumed without a restart and completed successfully. A host-awake assertion was started for the remainder of the rehearsal. This incident limits the claim of uninterrupted service during a full import on this particular single-host setup.

The admin portal found August in the official catalogue and queued its preparation. The job ran from 09:32:51 to 10:02:57 UTC (30 minutes 7 seconds). It checked the archive and active May headers, imported and indexed the national candidate, then passed its quality gate with 15,949,543 current addresses and 100% geocode coverage. The portal showed May live throughout preparation and offered August for review only after it was READY. The API readiness probes returned 599/599 and 577/577 successes in two successive ten-minute windows, then 556 successes and **one two-second timeout** in the third window at 10:02:46 UTC, during final validation. The first two windows had no gap between successes over 1.16 seconds; the third had a 3.5-second gap. The full run therefore did **not** prove uninterrupted service on this single host. A separate request probe is needed to measure lookup latency under import load.

August's activation used the loader CLI after the portal review. The browser confirmation for the portal rollback button interrupted automation, so the rollback rehearsal also used the loader CLI; the public API and release table verified both switches. Activation took the database from 23,652,775,603 bytes per node with three release schemas and August raw tables to 12,458,571,443 bytes per node with only August ACTIVE and May RETIRED. February's schema, all completed raw tables, and the cached August ZIP were removed. The API reported August, then May after rollback, then August again after the second switch. The final national corpus passed 18 positive and 11 negative cases with zero failures; all five bulk operations matched synchronous results with zero parity failures.

The isolated local gate passed 40 core tests, 12 PostgreSQL integration tests, coverage floors (Core 92.7% lines / 63.6% branches; Data 87.5% lines / 66.3% branches), API and admin smoke checks, and .NET formatting. ShellCheck and Compose configuration validation also passed. The gate's disposable test stack is removed after the run.
The five parity-test bulk jobs were marked expired; the normal worker removed their database rows and input/output blobs. The main stack ended with zero bulk jobs.

The final two-release database is about 11.60 GiB **per PostgreSQL node**. Two full replicas therefore use about 23.20 GiB of database storage on this one host, before PostgreSQL WAL, volume overhead, backups, or temporary import space. The portal's per-node label is intentional; an operator planning host storage must multiply by the number of full replicas. The pre-activation database reached about 22.03 GiB per node. The temporary peak and the slow later stages of the national transform deserve a separate capacity and import-performance pass before sizing Azure.

## Measurements

Use these read-only queries after each promotion. `quality_report` supplies the validated count; the serving table size includes its indexes. The database size is **per node** and PostgreSQL streaming replication stores a second copy.

```sql
SELECT source_version, status, scope,
       (quality_report->>'Addresses')::bigint AS addresses,
       round(((quality_report->>'GeocodeCoverage')::numeric * 100), 4) AS geocoded_percent,
       pg_total_relation_size(to_regclass(schema_name || '.address_search')) AS serving_bytes,
       file_size AS source_zip_bytes, source_schema IS NOT NULL AS has_header_manifest
FROM platform.dataset_version ORDER BY source_version;
SELECT pg_database_size(current_database()) AS database_bytes;
```

The API checks were `/health/ready`, `/v1/metadata`, ID/suggest/match/validate/format in the national regression corpus, and bulk parity for the final August release. Rollback from August to May and the return to August both passed. The final database has only those two national releases.
