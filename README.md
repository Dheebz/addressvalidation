# G-NAF Address Validation Platform

This repository is a local Australian address matching service using national G-NAF data. Matching scores express similarity to the loaded dataset only. They do not verify postal delivery.

## Quick start

Prerequisites: Docker Engine or OrbStack with Compose.

```sh
cp .env.example .env
docker compose up --build
```

Address Desk: http://localhost:8080/  
API: http://localhost:8080/swagger  
G-NAF Control Centre: http://localhost:8081/  
Live: http://localhost:8080/health/live

Use the [local operations guide](OPERATIONS.md) to check for national releases, prepare an update, activate it, and restore the previous release. The API stays running while data is prepared and switched. The local stack has two API containers behind a health-checking gateway at port 8080 and two PostgreSQL nodes with [automatic failover](HA-REHEARSAL.md).
For a fresh database, follow [database setup and seeding](DATABASE-SEEDING.md). It covers the official national import and a separate disposable synthetic test database.

Run the [national address quality baseline](QUALITY.md) with `python3 quality/run_golden.py` after starting the local stack.
The [matching guide](MATCHING.md) explains suggestions, candidate search, aliases, scores, subaddress checks, and validation decisions.

Open the local Address Desk at `http://127.0.0.1:8080/` after starting Compose. Its bulk jobs tab supports ranked suggestions, an optional minimum score, and NDJSON migration file upload with source references. It shows only the current upload and result; it does not list past jobs. Open the served URL rather than the `wwwroot/index.html` file directly so API calls reach the local service.
The [codebase guide](CODEBASE-GUIDE.md) maps the source modules and the release, matching, and security rules that changes must preserve.
The [test and coverage guide](TESTING.md) gives the disposable PostgreSQL test commands, coverage floors, and seeded API and admin smoke checks.
The [GitHub CI workflow](.github/workflows/ci.yml) gates formatting, lint, Release build, tests, coverage, container builds, and synthetic API and admin smoke tests. It does not deploy to Azure.
The [local traffic and failover rehearsal](LOAD-REHEARSAL.md) records measured API, database, and release-switch behaviour under sustained requests.
The [local saturation rehearsal](SATURATION-REHEARSAL.md) records the first throughput boundary and PostgreSQL connection exhaustion under heavy traffic.
The [project review](PROJECT-REVIEW.md) lists the current local capabilities, completed cleanup, and next product work.

Compose runs the admin portal without sign-in only under `ASPNETCORE_ENVIRONMENT=Development` and binds its published port to localhost. All release actions require antiforgery tokens. Non-Development startup requires configured OIDC administrator authentication and a persistent Data Protection key directory; see [access and operator history](OPERATIONS.md#access-and-operator-history). This is a local product and has not been deployed or validated behind Azure ingress.

## Current scope

Implemented: a local browser interface, API foundation, health endpoints, OpenAPI/Swagger, sample and official PSV ZIP loaders, immutable release schemas, stable serving view, transactional promotion/rollback, ID/suggest/match/validate/format endpoints, asynchronous NDJSON bulk processing including scored migration suggestions, and unit and PostgreSQL integration tests. The real-data loader checks the official catalogue, downloads with resume support, streams PSV files into release-specific raw tables, builds a serving table, and checks candidate counts before marking it READY. Bulk jobs use PostgreSQL queueing, Azurite storage, and the same `Address.Core` service as synchronous requests. See [API.md](API.md) for the bulk workflow.

The August 2026 national G-NAF archive is active locally with 15,949,543 current addresses. The May 2026 national release is retained for rollback. Both releases have street and locality alias search maps; the national golden corpus and local load rehearsals are in place. Admin role enforcement, CSRF protection, health checks, and operator history are implemented locally; identity provider integration, central telemetry, and Azure Bicep remain to be validated or built. **Do not deploy this foundation as a production validation service.**

## Attribution

Incorporates or developed using G-NAF © Geoscape Australia, licensed by the Commonwealth of Australia under the Open Geo-coded National Address File (G-NAF) End User Licence Agreement. Review the applicable licence before loading or redistributing actual G-NAF data.
