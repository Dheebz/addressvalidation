-- This file is embedded in Address.Data and rerun under an advisory lock at
-- process startup. Keep statements idempotent and compatible with live data.
-- This file is embedded in Address.Data and rerun under an advisory lock at
-- process startup. Keep statements idempotent and compatible with live data.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE SCHEMA IF NOT EXISTS platform;
CREATE SCHEMA IF NOT EXISTS api;

-- Each release owns an immutable schema; status and scope control whether it
-- can be promoted. A partial or synthetic release is never a national candidate.
-- Each release owns an immutable schema; status and scope control whether it
-- can be treated as a national candidate.
CREATE TABLE IF NOT EXISTS platform.dataset_version (
    id uuid PRIMARY KEY,
    dataset_name text NOT NULL DEFAULT 'G-NAF',
    source_version text NOT NULL UNIQUE,
    schema_name text NOT NULL UNIQUE,
    status text NOT NULL CHECK (status IN ('DISCOVERED','DOWNLOADING','DOWNLOADED','VERIFYING','EXTRACTING','CREATING_SCHEMA','LOADING','TRANSFORMING','BUILDING_INDEXES','VALIDATING','READY','PROMOTING','ACTIVE','RETIRED','FAILED')),
    source_url text,
    source_filename text,
    source_etag text,
    source_last_modified timestamptz,
    checksum text,
    file_size bigint,
    discovered_at timestamptz NOT NULL DEFAULT now(),
    downloaded_at timestamptz,
    loaded_at timestamptz,
    validated_at timestamptz,
    promoted_at timestamptz,
    retired_at timestamptz,
    error_message text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
-- There is one serving release at a time, independent of how many candidates
-- or retired versions remain installed.
-- There is one serving release at a time, independent of prepared candidates.
CREATE UNIQUE INDEX IF NOT EXISTS one_active_dataset ON platform.dataset_version ((status)) WHERE status = 'ACTIVE';
ALTER TABLE platform.dataset_version ADD COLUMN IF NOT EXISTS scope text NOT NULL DEFAULT 'SAMPLE';
ALTER TABLE platform.dataset_version ADD COLUMN IF NOT EXISTS quality_report jsonb;
ALTER TABLE platform.dataset_version ADD COLUMN IF NOT EXISTS source_schema jsonb;
ALTER TABLE platform.dataset_version ADD COLUMN IF NOT EXISTS raw_pruned_at timestamptz;
UPDATE platform.dataset_version SET dataset_name='SYNTHETIC', scope='SAMPLE'
WHERE source_version LIKE 'sample\_%' ESCAPE '\' AND dataset_name='G-NAF';

-- New release serving tables inherit this shape. Preserve the API view's
-- existing column order when extending it; CREATE OR REPLACE cannot drop it.
-- New serving tables inherit this shape. Preserve existing API view column
-- order when extending it; CREATE OR REPLACE cannot drop exposed columns.
CREATE TABLE IF NOT EXISTS platform.empty_address_search (
    gnaf_pid text PRIMARY KEY,
    formatted_address text NOT NULL,
    flat_number text NOT NULL,
    number_first text NOT NULL,
    street_name text NOT NULL,
    street_type text NOT NULL,
    locality_name text NOT NULL,
    state text NOT NULL,
    postcode text NOT NULL,
    latitude double precision,
    longitude double precision,
    search_text text NOT NULL,
    flat_type text NOT NULL DEFAULT '',
    flat_number_prefix text NOT NULL DEFAULT '',
    flat_number_suffix text NOT NULL DEFAULT '',
    level_type text NOT NULL DEFAULT '',
    level_number text NOT NULL DEFAULT '',
    level_number_prefix text NOT NULL DEFAULT '',
    level_number_suffix text NOT NULL DEFAULT '',
    building_name text NOT NULL DEFAULT ''
);
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS flat_type text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS flat_number_prefix text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS flat_number_suffix text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS level_type text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS level_number text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS level_number_prefix text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS level_number_suffix text NOT NULL DEFAULT '';
ALTER TABLE platform.empty_address_search ADD COLUMN IF NOT EXISTS building_name text NOT NULL DEFAULT '';
-- Provide a stable empty view before the first promotion so repository queries
-- have a fixed name even on a fresh, unseeded database.
-- Provide a stable empty view before the first release is promoted.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_views WHERE schemaname='api' AND viewname='address_search') THEN
        EXECUTE 'CREATE VIEW api.address_search AS SELECT * FROM platform.empty_address_search';
    END IF;
END $$;

-- Bulk payloads live in object storage; this table tracks durable work and
-- permits recovery after a worker restart.
-- Bulk payloads live in object storage; this table tracks durable work and
-- permits recovery after a worker restart.
CREATE TABLE IF NOT EXISTS platform.bulk_job (
    id uuid PRIMARY KEY,
    operation text NOT NULL CHECK (operation IN ('ID','MATCH','VALIDATE','FORMAT','SUGGEST')),
    status text NOT NULL CHECK (status IN ('QUEUED','RUNNING','COMPLETED','FAILED','CANCELLED')),
    input_blob_uri text NOT NULL,
    output_blob_uri text,
    total_records bigint,
    processed_records bigint NOT NULL DEFAULT 0,
    failed_records bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz,
    completed_at timestamptz,
    heartbeat_at timestamptz,
    error_message text,
    requested_by text,
    access_token_hash text,
    suggest_min_score double precision,
    suggest_limit integer
);
-- CREATE TABLE IF NOT EXISTS does not update older installations. Evolve the
-- durable queue without touching existing jobs or the national address data.
ALTER TABLE platform.bulk_job ADD COLUMN IF NOT EXISTS suggest_min_score double precision;
ALTER TABLE platform.bulk_job ADD COLUMN IF NOT EXISTS suggest_limit integer;
ALTER TABLE platform.bulk_job ADD COLUMN IF NOT EXISTS access_token_hash text;
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'platform.bulk_job'::regclass
          AND conname = 'bulk_job_operation_check'
          AND pg_get_constraintdef(oid) NOT LIKE '%SUGGEST%'
    ) THEN
        ALTER TABLE platform.bulk_job DROP CONSTRAINT bulk_job_operation_check;
        ALTER TABLE platform.bulk_job ADD CONSTRAINT bulk_job_operation_check
            CHECK (operation IN ('ID','MATCH','VALIDATE','FORMAT','SUGGEST'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS bulk_job_queue ON platform.bulk_job(created_at) WHERE status = 'QUEUED';

-- One database-backed setting is shared by every API replica, worker, and
-- administrator process. Changing it in the portal takes effect without a restart.
CREATE TABLE IF NOT EXISTS platform.bulk_retention_settings (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    retention_hours integer NOT NULL CHECK (retention_hours BETWEEN 1 AND 168),
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by text NOT NULL DEFAULT 'system'
);
INSERT INTO platform.bulk_retention_settings (singleton, retention_hours)
VALUES (true, 24) ON CONFLICT (singleton) DO NOTHING;

-- Compact alias dictionaries survive raw-table pruning and are keyed by release.
-- Search reads only the active release, so preparing a candidate cannot affect it.
CREATE TABLE IF NOT EXISTS platform.street_alias (
    source_version text NOT NULL,
    alias_name text NOT NULL,
    alias_type text NOT NULL,
    canonical_name text NOT NULL,
    canonical_type text NOT NULL,
    locality_name text NOT NULL,
    state text NOT NULL,
    PRIMARY KEY (source_version, alias_name, alias_type, canonical_name, canonical_type, locality_name, state)
);
CREATE INDEX IF NOT EXISTS street_alias_lookup ON platform.street_alias(source_version, alias_name, state);
CREATE TABLE IF NOT EXISTS platform.locality_alias (
    source_version text NOT NULL,
    alias_name text NOT NULL,
    canonical_name text NOT NULL,
    state text NOT NULL,
    PRIMARY KEY (source_version, alias_name, canonical_name, state)
);
CREATE INDEX IF NOT EXISTS locality_alias_lookup ON platform.locality_alias(source_version, alias_name, state);

-- One open national preparation at a time. Workers claim jobs through SQL,
-- while the portal reads these stages to explain long-running imports.
-- One open national preparation at a time. The portal reads these stages to
-- explain an import without changing the current serving release.
CREATE TABLE IF NOT EXISTS platform.maintenance_job (
    id uuid PRIMARY KEY,
    source_version text NOT NULL,
    source_url text NOT NULL,
    source_filename text NOT NULL,
    source_size bigint,
    status text NOT NULL CHECK (status IN ('QUEUED','RUNNING','COMPLETED','FAILED')),
    stage text NOT NULL,
    error_message text,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz,
    completed_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS one_open_maintenance_job ON platform.maintenance_job ((true))
WHERE status IN ('QUEUED','RUNNING');

-- Operator actions remain visible after a maintenance job or release switch finishes.
-- Never store tokens, secrets, or full request bodies in this history.
CREATE TABLE IF NOT EXISTS platform.admin_event (
    id uuid PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    actor text NOT NULL,
    action text NOT NULL CHECK (action IN ('PREPARE','PROMOTE','ROLLBACK','BULK_RETENTION','DELETE_RELEASE')),
    outcome text NOT NULL CHECK (outcome IN ('SUCCEEDED','REJECTED','FAILED')),
    source_version text,
    request_id text NOT NULL,
    detail text NOT NULL
);
CREATE INDEX IF NOT EXISTS admin_event_recent ON platform.admin_event (occurred_at DESC);
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'platform.admin_event'::regclass
          AND conname = 'admin_event_action_check'
          AND pg_get_constraintdef(oid) NOT LIKE '%DELETE_RELEASE%'
    ) THEN
        ALTER TABLE platform.admin_event DROP CONSTRAINT admin_event_action_check;
        ALTER TABLE platform.admin_event ADD CONSTRAINT admin_event_action_check
            CHECK (action IN ('PREPARE','PROMOTE','ROLLBACK','BULK_RETENTION','DELETE_RELEASE'));
    END IF;
END $$;
