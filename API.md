# API v1

Swagger: `/swagger`. Local development does not require credentials. Production configuration requires a JWT authority and audience, and endpoints check `Address.Read` or `Address.Admin` roles.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/v1/metadata` | Dataset and service information |
| GET | `/health/live` | Process liveness; does not query the database |
| GET | `/health/ready` | Serving readiness; checks the database and active national release |
| GET | `/v1/addresses/{gnafPid}` | Exact ID lookup |
| GET | `/v1/addresses/suggest?q=...&limit=10` | Ranked canonical suggestions; see [matching guide](MATCHING.md) |
| POST | `/v1/addresses/match` | Best candidate |
| POST | `/v1/addresses/validate` | Dataset matching decision |
| POST | `/v1/addresses/format` | Comparison-normalized text |
| GET | `/admin/gnaf/status` | Release list |
| POST | `/v1/bulk?operation=VALIDATE` | Queue NDJSON input; also accepts ID, MATCH, FORMAT, SUGGEST |
| GET | `/v1/bulk/{jobId}` | Job status and progress |
| GET | `/v1/bulk/{jobId}/result` | Download completed NDJSON result |

POST bodies for match, validate and format are `{ "address": "..." }`. Errors use `{ "error": { "code", "message", "correlationId" } }`. Match confidence is deterministic token similarity, never postal deliverability. Validation always reports `POSTAL_DELIVERY_NOT_VERIFIED`.

If PostgreSQL is temporarily unavailable during automatic takeover, the API returns HTTP 503 with `DATASTORE_UNAVAILABLE`, the request correlation ID, and `Retry-After: 2`. Clients may retry read-only lookups and the idempotent match, validate, and format operations. Bulk submission is not idempotent: if a client loses its `202` response and access token, retrying creates another job; the inaccessible first job expires automatically.

Each API replica admits at most 16 concurrent `/v1/addresses` requests by default. When all slots are occupied it returns HTTP 429 with `API_BUSY`, the correlation ID, and `Retry-After: 1` immediately. No in-process request queue is used. Change `API_CONCURRENCY_PER_REPLICA` in the local `.env` to tune this limit after measurement; `/v1/metadata` reports the configured value. Clients should back off with jitter before retrying a 429.

Suggestions, exact ID, match, validation, and successful format responses include structured address components where available: `buildingName`, `flatType`, `flatNumber`, `levelType`, `levelNumber`, `numberFirst`, `numberLast`, `streetName`, `streetType`, `streetSuffix`, `locality`, `state`, and `postcode`. Absent components are `null`. Type values use the G-NAF codes retained in the serving table, such as `SE` for suite and `L` for level. `numberLast` is derived from a numbered range in the canonical formatted line. The current serving table does not retain street suffix, so `streetSuffix` is always `null` for the installed releases. Matched responses include G-NAF ID and coordinates where available; match, validation, and successful format responses include confidence. A validation that does not meet the match threshold has `address: null`; formatting without a confident match has `components: null`, `location: null`, and `confidence: null`. These fields describe the matched G-NAF record, not postal delivery eligibility.

`/health/ready` returns HTTP 200 only when PostgreSQL is reachable and a full national G-NAF release is active. It includes `checks.database`, `checks.dataset`, and the active dataset version. Otherwise it returns HTTP 503 with safe status values. The gateway uses the HTTP status to route requests; `/health/live` remains independent of database availability so a database failover does not cause the API process to restart. `/v1/metadata` also lists supported bulk operations, input limits, and the health URLs.

Search first uses indexed exact and prefix lookup. For incomplete or misspelled input it narrows candidates by street name, house number, and postcode before ranking. Formatting returns the canonical G-NAF text and `matched: true` when the input matches confidently; otherwise it returns comparison-normalized text with `matched: false`.
Street and locality aliases from the installed release are kept in compact dictionaries when the source archive is imported. When a query uses an unambiguous alias, search maps it to the canonical address and reports `matchType: "ALIAS"` for a confident result. An alias with multiple canonical targets in the same state and locality is not treated as an exact mapping. House number, unit, level, state, postcode, and locality checks still apply. Older releases require the `enrich-aliases` backfill command described in [database seeding](DATABASE-SEEDING.md).

Bulk input is UTF-8 newline-delimited JSON, with one object per line: `{ "address": "..." }` for MATCH, VALIDATE, FORMAT, and SUGGEST, or `{ "id": "GAACT714845933" }` for ID. SUGGEST also accepts an optional `reference` string (up to 200 characters) per row, returned in its result so migration pipelines can join candidates to source records. Upload it as `application/x-ndjson` (100 MB maximum). The Address Desk bulk jobs tab accepts an NDJSON file directly, or converts pasted lines to NDJSON; file rows retain their `reference` values. The site shows only the current upload and does not keep a browser job history. The API returns `202` with a job ID and a secret `accessToken`. Job status and result requests require that token in `X-Bulk-Job-Token`; the site uses a job-scoped, HttpOnly cookie set by the submit response. Production also requires the same signed-in identity that submitted the job. Missing or incorrect credentials return 404. Clients must keep the token securely until they download the result; older jobs created before this access control was added cannot be reopened. The worker streams each row through the same `Address.Core` service used by the synchronous API and writes one result per input row; malformed rows have `INVALID_INPUT` status. Input and result files are stored in Azurite locally. A release switch waits until running bulk jobs finish.

For migration review, use `POST /v1/bulk?operation=SUGGEST&minScore=0.7&limit=5`. `minScore` is an inclusive candidate filter from 0 to 1; it defaults to 0, so all returned candidates are visible. `limit` is the maximum candidates per address from 1 to 20 and defaults to 5. These options are stored with the job, appear in the job status as `suggestionOptions`, and apply throughout worker retries. They are rejected for other operations. A score is ranking evidence, not a validation or postal-delivery decision. Keep the source record until a migration reviewer or a separate decision rule accepts a candidate.
Completed and failed jobs, including their input and output blobs, are retained for **24 hours by default**. The administrator can change the shared database setting to 1–168 hours in the [Control Centre](http://127.0.0.1:8081/), with no service restart. The new setting applies to existing jobs: older results may disappear at the next cleanup check. The job status response includes `expiresAt` based on the current setting, and `/v1/metadata` reports `limits.bulkResultRetentionHours`. The worker checks for expired jobs each minute and retries blob deletion after a storage error or restart. Queued jobs older than the retention period and running jobs with an expired heartbeat are failed and cleaned up; actively processing jobs are allowed to finish. After cleanup, status and result URLs return 404. A delayed or backlogged sweep may leave a job visible briefly after `expiresAt`.

Each result row has `row` and `status`. ID, MATCH, VALIDATE, and FORMAT rows include the same `components` and `location` projections as their synchronous counterparts when an address is found, plus the G-NAF `id`, canonical `formattedAddress`, release context, and `postalDeliveryStatus`. MATCH includes `matchType` and `confidence`; VALIDATE includes `valid` and `confidence`; FORMAT includes `matched`, `matchType`, and a confidence only when it found a confident match. Missing or unconfirmed addresses have `id`, `components`, and `location` set to `null`. A SUGGEST row has `input`, `reference`, `datasetVersion`, and `suggestions`, each with `id`, `formattedAddress`, `score`, `components`, and `location`; its status is `SUGGESTED` or `NO_SUGGESTIONS`. An invalid input row has `status: "INVALID_INPUT"` and an `error` message. Bulk results use camel-case JSON field names.

```sh
printf '%s\n' '{"address":"6 Packham Place Charnwood ACT 2615"}' > input.ndjson
curl -H 'Content-Type: application/x-ndjson' --data-binary @input.ndjson \
  'http://localhost:8080/v1/bulk?operation=VALIDATE'
# Copy jobId and accessToken from the 202 response. Keep the token private.
curl -H 'X-Bulk-Job-Token: ACCESS_TOKEN' http://localhost:8080/v1/bulk/JOB_ID
curl -H 'X-Bulk-Job-Token: ACCESS_TOKEN' http://localhost:8080/v1/bulk/JOB_ID/result
```

Migration example:

```sh
printf '%s\n' '{"reference":"customer-123","address":"100 George St Sydney NSW 2000"}' > migration.ndjson
curl -H 'Content-Type: application/x-ndjson' --data-binary @migration.ndjson \
  'http://localhost:8080/v1/bulk?operation=SUGGEST&minScore=0.7&limit=5'
```
