# Architecture

## Main components

- `Address.Core` parses and scores addresses and projects response components. It has no database or HTTP dependency.
- `Address.Data` owns the PostgreSQL connection source, serving queries, release lifecycle, importer, maintenance jobs, bulk job records, and object storage adapter.
- `Address.Api` exposes synchronous and bulk HTTP endpoints. `Address.Worker` processes queued bulk rows through the same core service.
- `Gnaf.Admin` exposes local release controls and operational status. `Gnaf.Loader` provides the equivalent technical CLI. Synthetic CLI seeding requires an explicit disposable-stack flag.
- `api.address_search` is the stable read view. `platform.dataset_version` records release state, while each release owns a versioned schema.

## Release invariants

`Address.Api` queries `api.address_search`. Each loaded release owns an immutable `gnaf_<version>.address_search`. `DatasetLifecycle.PromoteAsync` and `RollbackAsync` repoint the view and update version metadata in one PostgreSQL transaction under an advisory lock. The API resolves the active dataset version from `platform.dataset_version` on each request, so no restart or cache invalidation is needed.

A candidate remains separate from the active view while it imports and validates. The portal activates only READY full national G-NAF releases. A release switch waits for running bulk jobs to finish, so one bulk output file does not span two versions. The main stack disables synthetic seeding; [DATABASE-SEEDING.md](DATABASE-SEEDING.md) describes the official and disposable paths.

The sample loader creates each release with PostgreSQL binary `COPY`. The official loader selects the latest GDA2020 ZIP from data.gov.au, streams selected PSV files through PostgreSQL `COPY`, and joins address detail, locality, state, street, street type and default geocode into the serving table. It builds a trigram GIN index and records a quality report. Only a passing candidate becomes `READY`. The importer builds compact street and locality alias maps for search before completed raw tables are pruned. Both locally installed national releases have these maps.

`Address.Core` owns normalization, deterministic scoring, and validation semantics and has no HTTP dependency. The current score is token overlap with prefix support; a validation score of at least 0.8 is considered an address match. This threshold is provisional. The installed national corpus catches regressions, while independent human-reviewed ambiguous inputs are still needed to calibrate false positives and negatives before production use. See [how matching works](MATCHING.md) for the full search and endpoint behaviour.

Production topology is planned as Azure Container Apps, Azure Database for PostgreSQL Flexible Server with managed HA, Blob Storage, API Management, Entra ID, and Key Vault. No Azure resources are provisioned by this foundation.

The current local test layers and coverage floors are documented in [TESTING.md](TESTING.md). API readiness requires an active full national release; a disposable synthetic seed can be queried directly but deliberately does not make the service ready for national traffic.
