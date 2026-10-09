# AddressDesk security audit

**Reviewed:** 9 October 2026

**Scope:** API, browser UI, admin portal, bulk worker, G-NAF loader, PostgreSQL schema and access, source ingestion, local HA stack, CI, and relevant tests.

**Assessment type:** source/configuration review plus safe, local read-only HTTP checks. This is not a penetration test or a certification of the future Azure deployment.

## Executive summary

The application has useful controls already: production bearer-token validation, an admin role policy, CSRF checks on operator mutations, per-job bulk access tokens, HTML encoding in the admin page, parameterized address lookups, bounded synchronous address inputs, schema-drift checks, and atomic release switches. The local Compose stack binds its published API, admin, PostgreSQL, and Azurite ports to `127.0.0.1`; its Development authentication bypass is currently intended for that local setup.

The main security work is reducing the blast radius of a compromised service, bounding bulk and archive processing, protecting release provenance, and making the planned Azure deployment fail closed. **The local stack should not be treated as an internet-facing production configuration.**

| Priority | Findings | What to do first |
| --- | ---: | --- |
| High | 3 | Split database privileges; add bulk admission quotas; prohibit Development-mode exposure in deployment. |
| Medium | 9 | Bound row/archive processing; constrain source downloads; fix worker ownership; clean orphaned objects; schedule recoverable backups; add dependency checks. |
| Low | 2 | Set browser security headers and explicitly run app containers as non-root. |

Severity reflects likely impact **and the stated deployment boundary**. Some findings require a compromised official catalogue, a service foothold, or an unsafe future Azure configuration; those preconditions are called out below. No remote exploit was attempted.

## Findings and recommendations

### SA-01 — Application services use a PostgreSQL superuser — High

**Evidence.** `infra/postgres/patroni-main.yml:11-17` configures `address` as the Patroni superuser, replication, and rewind user. `compose.yaml:53-62,112-165` passes that same role and password to both APIs, the bulk worker, admin portal, and loader. `src/Address.Data/Database.cs:28-43` also runs migrations from application startup. The SQL migration has no separate application-role grants (`db/migrations/001_initial.sql`).

**Impact/preconditions.** If any service process or its connection string is compromised, the attacker gets a PostgreSQL role that bypasses permission checks and can change or delete the entire national dataset, job metadata, and other roles. The local default password `address` increases the risk if the database port or internal network is exposed. A standby does not protect against malicious writes because they replicate.

**Remediation.** Introduce separate database roles for API read, bulk intake, worker, admin release operations, and migration/bootstrap. Move `MigrateAsync` into an explicit deployment/migrator step so API replicas need no DDL rights. Grant only required schema/table/function permissions; keep the Patroni superuser/replication/rewind credentials distinct and outside application containers. Supply unique secrets through a secret manager in Azure, rotate existing shared credentials, and verify that an API-role session cannot `DROP SCHEMA`, change `platform.dataset_version`, or read unrelated bulk jobs. PostgreSQL explicitly advises routine work without superuser rights: [PostgreSQL role attributes](https://www.postgresql.org/docs/17/role-attributes.html).

### SA-02 — Bulk intake lacks caller quotas and a bounded queue — High

**Evidence.** `src/Address.Api/Program.cs:31-54` limits concurrency only for `/v1/addresses`, explicitly selecting no limiter for other paths. `src/Address.Api/Program.cs:333-378` accepts each bulk upload up to 100 MiB and creates one durable job. `db/migrations/001_initial.sql:93-130` has no per-caller, active-job, total-byte, or queue-depth constraint. `src/Address.Worker/Program.cs:31-50` processes one job at a time per worker.

**Impact/preconditions.** A principal with `Address.Read` can submit many valid maximum-size jobs faster than workers can complete them, exhausting object storage, database metadata, and processing capacity. In the local Development mode the same path is available without a bearer token, though the published port is loopback-bound. The 100 MiB *individual request* limit does not cap aggregate consumption.

**Remediation.** Add per-identity limits for requests, concurrent uploads, queued/running jobs, and outstanding bytes; add a global queue/storage budget and return `429`/`503` with retry guidance before storing an upload. Keep the 100 MiB body limit, but enforce a server-side streamed byte count and request deadline as a second guard. Consider an authenticated ingress limit for Azure and load-test the chosen quotas against real bulk workloads. Microsoft documents partitioned rate limits and admission testing: [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).

### SA-03 — Development auth bypass becomes dangerous if the stack is exposed — High, conditional deployment risk

**Evidence.** `src/Address.Api/Program.cs:14-20,135-140` grants every API role when `Authentication:Mode=Development` and the host environment is Development. `src/Gnaf.Admin/Program.cs:15-63` configures admin sign-in and the `Address.Admin` fallback policy only outside Development. `compose.yaml:112-157` sets both services to Development, while binding the published ports to `127.0.0.1`. A local unauthenticated `GET /v1/metadata` returned HTTP 200 during this review, consistent with the intended mode.

**Impact/preconditions.** The current loopback-only binding is the key compensating control. If the same configuration is copied behind a public ingress, deployed on a host with broader port publishing, or exposed through a tunnel, address APIs and release-management controls become unauthenticated. CSRF tokens do not replace operator authentication.

**Remediation.** Make the Azure deployment use a distinct production configuration and reject startup when Development mode is combined with a nonlocal deployment marker. Add CI/deployment policy checks for `ASPNETCORE_ENVIRONMENT`, API `Authentication:Mode`, Entra authority/audience, admin OIDC configuration, and public routes. Restrict the admin portal to its own authenticated private ingress. Add an integration smoke test proving anonymous `Address.Read` and admin mutations are denied in production mode. Keep local Development use on loopback only.

### SA-04 — Bulk worker has no per-line memory bound — Medium

**Evidence.** `src/Address.Worker/Program.cs:103-117` uses `StreamReader.ReadLineAsync` and then `JsonDocument.Parse(line)`. The worker limits the parsed `address`/`id` value to 500 characters at `:115-120`, but only **after** loading and parsing the full line. The upload can be up to 100 MiB (`src/Address.Api/Program.cs:352-354`).

**Impact/preconditions.** A valid-size upload containing one very long line can force a large managed string and JSON parse allocation. Parallel workers or repeated jobs can cause memory pressure and restarts even though each file passes the upload limit.

**Remediation.** Parse NDJSON through a bounded line reader or streaming `Utf8JsonReader` with a maximum row size (for example, a few KiB based on the 500-character input and 200-character reference contract). Reject or emit `INVALID_INPUT` for overlong rows without allocating the entire line. Add a test with a near-100-MiB single line and verify bounded memory.

### SA-05 — Catalogue-provided download URLs are not host constrained — Medium, source-compromise precondition

**Evidence.** `src/Address.Data/GnafSourceProvider.cs:58-83` accepts any absolute HTTPS resource URL from the catalogue. `:112-132` sends a request to that URL using a default `HttpClient`; redirect destinations are not revalidated. The admin's HTTP client is constructed at `src/Gnaf.Admin/Program.cs:74-79`.

**Impact/preconditions.** A malicious or compromised catalogue response can direct the admin service to an attacker host or a reachable internal HTTPS service. That can disclose request metadata and, depending on response/redirect behavior, make the importer a server-side request forgery and storage-exhaustion primitive. This is **not** directly controlled by an ordinary address API caller.

**Remediation.** Allowlist the official publisher/CDN hosts and exact HTTPS schemes, disable automatic redirects or validate every hop, and reject loopback, link-local, private, and metadata-service destinations after DNS resolution. Apply egress firewall rules as a second boundary. Make catalogue URL overrides explicit maintenance settings, not normal runtime knobs. See [OWASP SSRF prevention](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).

### SA-06 — Downloaded release checksum is self-computed, not independently verified — Medium

**Evidence.** `src/Address.Data/GnafSourceProvider.cs:147-152` computes SHA-256 from the downloaded file for provenance. `:93-94` reuses a cached file when its length matches the catalogue size. `src/Address.Data/GnafArchiveImporter.cs:150-160` calculates a checksum when one is absent, then records it. No publisher-signed digest or separately trusted checksum is checked before ingesting or reusing bytes. Schema and count checks at `GnafArchiveImporter.cs:44-92,215-220` protect format and broad quality, but do not authenticate the source bytes.

**Impact/preconditions.** A compromised catalogue/CDN, poisoned cache, or altered local archive with the expected size could be imported and its *own* hash recorded as if it proved authenticity. The risk is data integrity rather than arbitrary code execution; the ZIP is streamed into typed PostgreSQL tables.

**Remediation.** Where the publisher supplies a trusted digest/signature, verify it before import. Otherwise pin an operator-reviewed SHA-256 for each release in an independent manifest, check cached bytes against that expected value on every reuse, and show the verified digest in the admin review. Keep the existing schema and quality gates as separate checks. Establish an explicit exception process if no independent digest is available.

### SA-07 — Archive decompression and source download lack explicit budgets — Medium

**Evidence.** `src/Address.Data/GnafArchiveImporter.cs:59-67,171-173,484-512` opens ZIP entries and streams their uncompressed content into PostgreSQL without caps on entry count, total uncompressed bytes, compression ratio, PSV row length, or elapsed import time. `src/Address.Data/GnafSourceProvider.cs:89-144` has no absolute download-byte budget when catalogue size is absent or false, uses up to 200 attempts, and the admin `HttpClient` has an infinite timeout (`src/Gnaf.Admin/Program.cs:74-78`).

**Impact/preconditions.** A malicious upstream archive or a mistaken catalogue resource can consume database disk, local cache space, CPU, and operator time. The required PSV-file checks reject partial archives but do not bound their contents. This depends on malicious/erroneous source content or explicit maintenance import, not a normal API request.

**Remediation.** Set generous but finite publisher-specific compressed and uncompressed budgets, per-entry/row limits, compression-ratio alarms, minimum free-space checks, and download/import deadlines. Fail a candidate safely before it consumes the remaining volume. Exercise a synthetic high-ratio archive in an isolated test. [OWASP file-upload guidance](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html) calls for limits after decompression as well as on compressed files.

### SA-08 — Bulk stale-job recovery has no ownership token — Medium

**Evidence.** `src/Address.Data/BulkJobs.cs:82-106` requeues any `RUNNING` job whose heartbeat is older than five minutes, then another worker can claim it. `:109-127` updates progress and completion using only `id` and `status='RUNNING'`. `src/Address.Worker/Program.cs:123-147` heartbeats every 10 processed rows and writes the output blob with `overwrite: true` (`src/Address.Data/BulkJobs.cs:208-213`).

**Impact/preconditions.** If a legitimate worker stalls for five minutes during a slow query, storage call, or failover and then resumes, another worker can process the same job simultaneously. Both can overwrite the same output; the old worker can update the new worker's job state. This can corrupt or prematurely publish a bulk result.

**Remediation.** Add a monotonically increasing claim generation or random lease token to `platform.bulk_job`; require it in progress, completion, and failure updates. Maintain a periodic heartbeat independent of row count, stop work when ownership is lost, and publish output to a claim-specific temporary object before an atomic winner check. Test two workers across a forced stale-heartbeat scenario.

### SA-09 — A new admin replica can mark another replica's import as failed — Medium, future multi-replica risk

**Evidence.** `src/Gnaf.Admin/ReleasePreparationWorker.cs:10-19` calls `MarkInterruptedAsync` on every process startup. `src/Address.Data/MaintenanceJobs.cs:67-74` marks **all** `RUNNING` release jobs failed, without a worker identity, lease, or stale-heartbeat condition. The database claim at `:78-89` prevents concurrent *initial* claims but does not protect an already running import from another replica's startup.

**Impact/preconditions.** The local Compose file runs one admin replica. If Azure adds a second admin/worker replica or restarts one while the other imports, the new process can falsely fail the active job. The original process can continue heavy database work while operators see a failed state and attempt a retry.

**Remediation.** Separate the release worker from the web portal, and use a durable worker lease with owner ID, heartbeat, and fencing token. Recover only stale jobs whose owner lease has expired. Until that exists, enforce exactly one release-worker replica in the deployment and document that constraint. Add a two-replica restart test before claiming high availability for release ingestion.

### SA-10 — A crash between upload and job creation leaves orphaned bulk data — Medium

**Evidence.** `src/Address.Api/Program.cs:361-379` uploads the input object before creating the database job and deletes it only if a catchable exception occurs. `src/Address.Worker/Program.cs:64-87` retention cleanup iterates database jobs, so a process crash or kill in that gap leaves an object with no row to enumerate.

**Impact/preconditions.** Rare process crashes or repeated induced restarts can leave address lists in object storage beyond the configured 24-hour job retention and can consume storage. This is also a privacy issue because the bulk data may be user-supplied.

**Remediation.** Periodically reconcile `input/` and `output/` objects against job rows and delete unreferenced objects after a short grace period. For Azure, add a defensive blob lifecycle rule as a backstop; ensure it does not delete active uploads/results. Test a killed API immediately after upload and verify eventual cleanup.

### SA-11 — Restore is rehearsed, but production backup scheduling is absent — Medium

**Evidence.** `scripts/rehearse-backup-restore.sh:28-45` and `scripts/rehearse-national-restore.sh:28-63` create and validate *temporary* logical backups, then remove their temporary directories. The local Compose stack (`compose.yaml:170-177`) defines persistent volumes and PostgreSQL replication, but no scheduled, off-host, access-controlled backup/archive job. The CI workflow tests containers but does not schedule backups (`.github/workflows/ci.yml`).

**Impact/preconditions.** Both PostgreSQL replicas replicate corruption and unauthorized deletes. A host/storage loss or malicious release deletion cannot be recovered from failover alone. Bulk objects live in a separate volume and are not covered by the database-only rehearsal.

**Remediation.** Set backup retention and recovery objectives; schedule encrypted off-host database backups with WAL/PITR where feasible and a separate object-storage protection policy. Limit deletion rights for backups, alert on backup failure, and rehearse restoration of database *and* bulk storage to an isolated environment. In Azure, configure and test managed PostgreSQL point-in-time restore and appropriate Blob protection; [Microsoft's PostgreSQL backup documentation](https://learn.microsoft.com/en-nz/azure/postgresql/backup-restore/concepts-backup-restore) distinguishes backups from high availability.

### SA-12 — CI does not check dependency or container vulnerabilities — Medium, assurance gap

**Evidence.** `.github/workflows/ci.yml:1-125` runs formatting, tests/coverage, build, and smoke checks, but no NuGet advisory, dependency review, software bill of materials, or container-image scan. `src/Address.Data/Address.Data.csproj` and other projects pin direct package versions, while `infra/docker/Dockerfile:1,6` and `compose.yaml:3,101,131` use mutable image tags. The Patroni PostgreSQL base image is digest-pinned (`infra/postgres/Dockerfile.patroni:1`), which is a useful existing control.

**Impact/preconditions.** Known vulnerable transitive dependencies or base-image packages can reach a release without a pipeline alert. This review could not establish whether the current versions have published advisories because a local `dotnet` SDK is unavailable and Docker socket access was denied; **no current CVE is asserted**.

**Remediation.** Add a CI NuGet vulnerability check including transitives, dependency review for pull requests, SBOM generation, and image scanning after build. Fail on an agreed severity threshold, record exceptions with expiry, and regularly refresh base images. Pin production base images by digest while retaining an update workflow. Verify scanner findings against vendor advisories before remediation.

### SA-13 — Browser security headers are not explicitly set — Low, hardening

**Evidence.** The HTML UI and Swagger are served by `src/Address.Api/Program.cs:82-101`; the admin page is served by `src/Gnaf.Admin/Program.cs:87-94,149-183` and `AdminPage.cs:215-223`. No Content Security Policy, `frame-ancestors`/`X-Frame-Options`, `X-Content-Type-Options`, or production HSTS policy is configured in the app or `infra/haproxy/haproxy.cfg:20-31`. A safe local API-root header check also did not show these headers. The current admin renderer encodes variable HTML and the JavaScript uses `textContent`, reducing immediate XSS exposure.

**Impact/preconditions.** Missing headers increase the impact of a future injection, content-type confusion, or same-site framing issue. HSTS applies only after HTTPS is configured at the future ingress; local HTTP testing is not itself a defect.

**Remediation.** Add a restrictive CSP for both UIs, deny framing for the admin portal, set `nosniff`, and configure HTTPS/HSTS at the production edge. Test Swagger's inline assets against the CSP and avoid weakening the admin policy to accommodate Swagger. See [ASP.NET Core HTTPS/HSTS guidance](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0).

### SA-14 — Application container privilege is not explicitly constrained — Low, hardening

**Evidence.** `infra/docker/Dockerfile:6-11` and `infra/docker/Dockerfile.loader` do not set a non-root `USER`. `compose.yaml` does not set `user`, `read_only`, dropped capabilities, or `no-new-privileges` for the app services. Actual runtime UID depends on the chosen base image and was not verified in this review.

**Impact/preconditions.** A successful application exploit may have broader filesystem/container access than necessary. This is a defense-in-depth gap, not evidence of a current container escape.

**Remediation.** Assert a non-root UID in service images, make filesystems read-only except known cache/key/temp mounts, drop unneeded capabilities, and apply resource limits. Test the importer and Data Protection key path under those constraints. Keep the database image's own privilege requirements separate from the .NET application images.

## Verified controls and observations

- Production API authentication is required when Development mode is not selected, with issuer, audience, and lifetime validation (`src/Address.Api/Program.cs:14-20,67-79`); route handlers check `Address.Read` or `Address.Admin`.
- Production admin startup requires OIDC settings and a persistent Data Protection key location, then applies an `Address.Admin` fallback authorization policy (`src/Gnaf.Admin/Program.cs:18-63`). Operator POSTs use antiforgery validation (`:94-110`).
- Bulk job reads require a per-job random token and, outside Development, the submitting identity (`src/Address.Api/Program.cs:148-166,391-430`). The stored token is SHA-256 hashed; the cookie is `HttpOnly` and `SameSite=Strict`.
- Synchronous address inputs are length-checked; address-search values are parameterized (`src/Address.Api/Program.cs:215-325`, `src/Address.Data/Database.cs:57-147`). Dynamic importer columns and versions have validation (`src/Address.Data/GnafArchiveImporter.cs:37-40,515-524`). No direct user-controlled SQL injection path was identified in the reviewed code.
- Admin-rendered external strings use HTML encoding; browser code inserts fetched values with `textContent` (`src/Gnaf.Admin/AdminPage.cs:27-29`, `src/Gnaf.Admin/wwwroot/admin.js`, `src/Address.Api/wwwroot/app.js`).
- The official-release flow separates prepare from promote, checks PSV headers against the active release, and changes the serving view transactionally (`src/Address.Data/GnafArchiveImporter.cs:44-92`, `src/Address.Data/Database.cs:445-489`). These controls protect availability and gross data quality, subject to findings SA-05 through SA-07.
- Local ports observed and configured on loopback; local unauthenticated responses are expected in Development. These observations **do not validate** production Entra/OIDC, ingress, TLS, or Azure resource configuration.

## Validation limits and recommended next checks

1. **Run live security integration tests in a disposable production-mode stack:** anonymous/incorrect-role API calls, admin fallback authorization, CSRF rejection, bulk job cross-user isolation, token expiry, and cookie flags over HTTPS. Current tests cover some renderer escaping and data lifecycle behavior, but not the full production identity flow.
2. **Run dependency and image scans in CI.** The host has no `dotnet` command and Docker API access was denied to this audit session, so I did not claim an advisory-free dependency set or inspect running container UIDs.
3. **Exercise bounded abuse cases** in isolation: repeated maximum-size bulk uploads, one huge NDJSON line, malformed/oversized archive, redirect to a disallowed host, stale bulk lease with two workers, and release-worker replica restart.
4. **Review the Azure deployment design when it is implemented.** Azure provisioning has intentionally been deferred. Verify private endpoints, TLS, managed identities and secret distribution, backups, ingress and admin separation, audit/alerting, and role configuration before any public launch.

This audit records review findings and recommended fixes; it does not change runtime code or deployment state.
