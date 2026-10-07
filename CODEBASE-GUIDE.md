# Codebase guide

This guide is for developers changing the local address validation service. The source comments explain decisions beside the relevant code; this page shows where to start and which invariants to preserve. For operator steps, use [OPERATIONS.md](OPERATIONS.md). For the public response contract, use [API.md](API.md). For scoring examples, use [MATCHING.md](MATCHING.md).

## Request path

| Area | Entry point | Responsibility |
| --- | --- | --- |
| HTTP API | `src/Address.Api/Program.cs` | Request limits, authorization, health, correlation IDs, response shaping, bulk intake |
| Matching rules | `src/Address.Core/AddressModels.cs` | Normalization, subaddress parsing, ranking, match classification, validation policy |
| Components | `src/Address.Core/AddressComponents.cs` | Canonical structured fields shared by synchronous and bulk responses |
| Candidate retrieval | `src/Address.Data/Database.cs` (`PostgresAddressRepository`) | Bounded exact, prefix, alias and fuzzy searches against `api.address_search` |
| Bulk work | `src/Address.Data/BulkJobs.cs`, `src/Address.Worker/Program.cs` | Durable queue, persisted suggestion filters, streaming NDJSON results, cleanup of terminal jobs |
| Admin | `src/Gnaf.Admin/Program.cs`, `AdminPage.cs`, `ClusterStatusService.cs` | Access control, release actions, operator history and local service status |
| Browser clients | `src/Address.Api/wwwroot/app.js`, `src/Gnaf.Admin/wwwroot/admin.js` | Demonstration lookups, operator status polling, and form interaction |
| Database schema | `db/migrations/001_initial.sql` | Idempotent tables, indexes, constraints, and stable API view bootstrap |
| Local infrastructure | `infra/postgres/*.sh`, `scripts/*.sh` | Database bootstrap and disposable validation/recovery rehearsals |
| Quality tools | `quality/*.py`, `scripts/ha-auto-failover-rehearsal.py` | Golden corpus, bulk parity, load, recovery, and coverage evidence |

`Address.Core` is the policy layer. PostgreSQL finds candidates; `AddressService` decides what their similarity means. The API and bulk worker both call `AddressService` so their matching behaviour stays aligned. Response components come from the canonical G-NAF row; an alias spelling is only used for scoring.

## Release path

1. `DataGovAuGnafSourceProvider` selects the GDA2020 PSV ZIP and resumes a partial download into a `.part` file.
2. `GnafArchiveImporter` checks that every required state file is present for a national release, streams raw PSV data into a release-specific schema, builds a serving table and alias maps, and validates counts and geocode coverage. It marks the result `READY`; it does not change the live view.
3. `DatasetLifecycle.PromoteAsync` serializes release switches, updates the stable `api.address_search` view and release states in one transaction, and retains the previous release for rollback. Running bulk jobs block a switch so a bulk result cannot span two releases.
4. `GnafCacheMaintenance` deletes only recognised completed-release cache files. The official ZIP can be downloaded again. Database backups are a separate concern.

The database schema is created by the embedded `db/migrations/001_initial.sql`. `Database.MigrateAsync` uses an advisory transaction lock because several processes may start at once. Keep migrations additive for live data. When changing the serving view, preserve the existing column order and append compatible fields; PostgreSQL rejects a `CREATE OR REPLACE VIEW` that drops or reorders exposed columns.

## Matching rules to preserve

- Normalize common street, unit and level notation before search; keep the canonical G-NAF spelling in responses.
- Search selectively before fuzzy matching. National-table-wide fuzzy scans are expensive and may produce weak candidates.
- Expand a street or locality alias only when it has a unique canonical target in the relevant state and locality.
- Cap a candidate below the 0.8 acceptance threshold when an explicitly supplied house number, unit, floor, locality, state or postcode conflicts. A good street text match cannot override that conflict.
- Treat confidence as similarity to the installed dataset. It does not prove postal delivery.

## Security and operations boundaries

- Compose runs the admin portal on localhost in Development. Non-Development startup requires OIDC configuration, the `Address.Admin` role, HTTPS, and persistent Data Protection keys.
- Release-changing admin forms require antiforgery tokens. Operator action outcomes are stored in `platform.admin_event`; missing audit entries are logged by `AdminAudit`.
- `/health/live` measures process liveness. `/health/ready` measures the ability to serve the role of that process. The API readiness check additionally requires an active national release.
- Database and API replicas on one computer protect against a process or container failure, not loss of the computer. See [HA-REHEARSAL.md](HA-REHEARSAL.md) for the measured failover window.

## Safe change checklist

1. Identify the owning layer above and read the comments around its invariants.
2. Add a focused test when changing matching policy, release state, import validation, or a security boundary. Use the disposable PostgreSQL test stack for database tests.
3. Run `sh scripts/run-local-gate.sh` before treating a cross-layer change as complete; follow [TESTING.md](TESTING.md) for the national corpus and load checks when relevant.
4. Update this guide, [API.md](API.md), [MATCHING.md](MATCHING.md), or the operator runbook when behaviour or a required setup step changes.

## Comment conventions

Public C# types and operations have XML summaries describing their contract. Implementation comments explain why a guard, lock, threshold, query order, retry, or cleanup boundary exists. SQL comments describe schema invariants and migration compatibility. Browser comments identify which decisions are made by the server and which are only presentation behaviour. Test and quality tools have module/function docstrings plus comments for fixture isolation and measurement methodology. When behaviour changes, update the nearby comment in the same change so documentation does not drift from code.
