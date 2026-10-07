using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace Address.Data;

/// <summary>Provenance recorded with an imported archive for audit and exact-release recovery.</summary>
public sealed record GnafSourceMetadata(string? Url = null, string? Filename = null, string? ETag = null,
    DateTime? LastModified = null, string? Checksum = null, long? FileSize = null);

/// <summary>Checks a candidate against its raw source and the previously active release.</summary>
public sealed record GnafQualityReport(long Addresses, long Streets, long Localities, long Geocodes,
    long States, long Postcodes, long AddressAliases, long StreetAliases, long LocalityAliases,
    double GeocodeCoverage, long? PreviousAddresses, bool HistoricalReplay = false);

/// <summary>Archive identity and canonical PSV header sets checked before database ingestion.</summary>
public sealed record GnafSchemaReport(string Version, string? BaselineVersion,
    IReadOnlyDictionary<string, string[]> Headers);

/// <summary>Imports official G-NAF PSV ZIPs into a release-specific schema. Never changes the active view.</summary>
public sealed class GnafArchiveImporter(Database database)
{
    private sealed record CandidateInfo(string SchemaName, string Scope, string Status);
    private sealed record AliasCandidateInfo(string Scope, string Status, string? Checksum);
    private sealed record BaselineSchema(string SourceVersion, string? SourceSchema);
    private sealed record BaselineQuality(string SourceVersion, string? QualityReport);
    private sealed record FailedCandidate(string SchemaName, string Scope, string Status,
        string? ErrorMessage, string? Checksum);
    private static readonly string[] RequiredTypes = ["ADDRESS_DETAIL", "ADDRESS_DEFAULT_GEOCODE", "STREET_LOCALITY", "LOCALITY", "STATE"];
    private static readonly string[] States = ["ACT", "NSW", "NT", "QLD", "SA", "TAS", "VIC", "WA"];
    private static readonly string[] OptionalTypes = ["ADDRESS_ALIAS", "STREET_LOCALITY_ALIAS", "LOCALITY_ALIAS"];
    private static readonly Regex FilePattern = new("^(ACT|NSW|NT|OT|QLD|SA|TAS|VIC|WA)_(ADDRESS_DETAIL|ADDRESS_DEFAULT_GEOCODE|STREET_LOCALITY|LOCALITY|STATE|ADDRESS_ALIAS|STREET_LOCALITY_ALIAS|LOCALITY_ALIAS)_psv\\.psv$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex NationalVersionPattern = new(@"^20\d{2}_(0[1-9]|1[0-2])$", RegexOptions.Compiled);
    private static readonly Regex ColumnPattern = new("^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex NumericArchiveVersion = new(@"(?:^|[_-])(20\d{2})[_-](0[1-9]|1[0-2])(?=[_.-]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NamedArchiveVersion = new(@"(?:^|[_-])(JAN(?:UARY)?|FEB(?:RUARY)?|MAR(?:CH)?|APR(?:IL)?|MAY|JUN(?:E)?|JUL(?:Y)?|AUG(?:UST)?|SEP(?:TEMBER)?|OCT(?:OBER)?|NOV(?:EMBER)?|DEC(?:EMBER)?)[_-]?(20\d{2}|\d{2})(?=[_.-]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string StreetTypeFile = "Authority_Code_STREET_TYPE_AUT_psv.psv";

    /// <summary>Fails on mismatched release identity, missing fields, or changed PSV headers before any candidate tables are created.</summary>
    public async Task<GnafSchemaReport> PreflightAsync(string version, string archivePath, string scope,
        string? sourceFilename = null, CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        scope = scope.ToUpperInvariant();
        if (scope != "ALL" && !States.Contains(scope)) throw new ArgumentException("Scope must be ALL or an Australian state/territory", nameof(scope));
        if (!File.Exists(archivePath)) throw new FileNotFoundException("G-NAF archive not found", archivePath);
        var filename = sourceFilename ?? Path.GetFileName(archivePath);
        var archiveVersion = VersionFromFilename(filename);
        if (archiveVersion is null && scope == "ALL")
            throw new InvalidDataException($"Cannot verify G-NAF release version from archive filename '{filename}'. Keep the official versioned filename.");
        if (archiveVersion is not null && !archiveVersion.Equals(version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive filename identifies {archiveVersion}, but requested release is {version}");

        using var archive = ZipFile.OpenRead(archivePath);
        var selected = SelectEntries(archive, scope);
        var headers = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (type, entries) in selected)
        {
            foreach (var entry in entries)
            {
                var columns = await ReadHeaderAsync(entry, ct);
                ValidateColumns(type, columns);
                var canonical = columns.Order(StringComparer.Ordinal).ToArray();
                if (headers.TryGetValue(type, out var previous) && !previous.SequenceEqual(canonical))
                    throw new InvalidDataException($"Schema drift: {entry.FullName} has a different {type} header from other state files");
                headers[type] = canonical;
            }
        }

        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        var baseline = await connection.QuerySingleOrDefaultAsync<BaselineSchema>(new CommandDefinition("""
            SELECT source_version AS SourceVersion, source_schema::text AS SourceSchema
            FROM platform.dataset_version
            WHERE status='ACTIVE' AND dataset_name='G-NAF' AND scope=@scope
            """, new { scope }, cancellationToken: ct));
        if (baseline?.SourceSchema is { } baselineJson)
        {
            var expected = JsonSerializer.Deserialize<Dictionary<string, string[]>>(baselineJson)
                ?? throw new InvalidDataException("Active release has an unreadable schema manifest");
            var changed = expected.Keys.Union(headers.Keys).Where(type =>
                !expected.TryGetValue(type, out var oldColumns) ||
                !headers.TryGetValue(type, out var newColumns) ||
                !oldColumns.SequenceEqual(newColumns)).Order(StringComparer.Ordinal).ToArray();
            if (changed.Length > 0)
                throw new InvalidDataException($"Schema drift from active {baseline.SourceVersion}: {string.Join(", ", changed)}. Review importer compatibility before preparing this release.");
        }
        return new GnafSchemaReport(version, baseline?.SourceSchema is null ? null : baseline?.SourceVersion, headers);
    }

    private static string? VersionFromFilename(string filename)
    {
        var numeric = NumericArchiveVersion.Match(filename);
        if (numeric.Success) return $"{numeric.Groups[1].Value}_{numeric.Groups[2].Value}";
        var named = NamedArchiveVersion.Match(filename);
        if (!named.Success) return null;
        var months = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        var month = Array.IndexOf(months, named.Groups[1].Value[..3].ToUpperInvariant()) + 1;
        var year = named.Groups[2].Value;
        return $"{(year.Length == 2 ? "20" + year : year)}_{month:00}";
    }

    /// <summary>Removes only a failed or interrupted candidate so a portal retry can start cleanly.</summary>
    public async Task ResetIncompleteCandidateAsync(string version, CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct)))
            throw new InvalidOperationException("Another G-NAF import is running");
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
            var candidate = await connection.QuerySingleOrDefaultAsync<CandidateInfo>(new CommandDefinition(
                "SELECT schema_name AS SchemaName,scope AS Scope,status AS Status FROM platform.dataset_version WHERE source_version=@version AND dataset_name='G-NAF' FOR UPDATE",
                new { version }, transaction, cancellationToken: ct));
            if (candidate is null) return;
            if (candidate.SchemaName != "gnaf_" + version.Replace('-', '_').ToLowerInvariant() ||
                candidate.Status is not ("FAILED" or "CREATING_SCHEMA" or "LOADING" or "TRANSFORMING" or "BUILDING_INDEXES" or "VALIDATING"))
                throw new InvalidOperationException("Only a failed or interrupted candidate can be retried");
            await connection.ExecuteAsync(new CommandDefinition($"DROP SCHEMA {candidate.SchemaName} CASCADE", transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM platform.street_alias WHERE source_version=@version; DELETE FROM platform.locality_alias WHERE source_version=@version",
                new { version }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM platform.dataset_version WHERE source_version=@version", new { version }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    /// <summary>Builds and validates an isolated candidate; callers must promote it separately after review.</summary>
    public async Task<GnafQualityReport> ImportAsync(string version, string archivePath, string scope,
        double maxCountDropFraction = 0.10, double minGeocodeCoverage = 0.90,
        GnafSourceMetadata? source = null, CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        scope = scope.ToUpperInvariant();
        if (scope != "ALL" && !States.Contains(scope)) throw new ArgumentException("Scope must be ALL or an Australian state/territory", nameof(scope));
        if (maxCountDropFraction is < 0 or >= 1 || minGeocodeCoverage is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(maxCountDropFraction), "Quality thresholds must be fractions from 0 to 1");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("G-NAF archive not found", archivePath);
        var preflight = await PreflightAsync(version, archivePath, scope, source?.Filename, ct);
        if (source?.Checksum is null)
        {
            await using var input = File.OpenRead(archivePath);
            var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            source = (source ?? new GnafSourceMetadata()) with
            {
                Filename = source?.Filename ?? Path.GetFileName(archivePath),
                Checksum = checksum,
                FileSize = input.Length
            };
        }

        var schema = "gnaf_" + version.Replace('-', '_').ToLowerInvariant();
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        // A session lock spans streaming COPY and index builds, which cannot
        // fit in one practical transaction for a national release.
        var locked = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct));
        if (!locked) throw new InvalidOperationException("Another G-NAF import is running");
        var created = false;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var selected = SelectEntries(archive, scope);
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
                var existing = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT status FROM platform.dataset_version WHERE source_version=@version", new { version }, transaction, cancellationToken: ct));
                if (existing is not null) throw new InvalidOperationException($"Release {version} already exists ({existing})");
                await connection.ExecuteAsync(new CommandDefinition($"CREATE SCHEMA {schema}", transaction: transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO platform.dataset_version
                      (id,dataset_name,source_version,schema_name,status,scope,source_url,source_filename,source_etag,source_last_modified,checksum,file_size,source_schema,downloaded_at)
                    VALUES (@id,'G-NAF',@version,@schema,'CREATING_SCHEMA',@scope,@url,@filename,@etag,@lastModified,@checksum,@fileSize,@sourceSchema::jsonb,now())
                    """, new
                {
                    id = Guid.NewGuid(),
                    version,
                    schema,
                    scope,
                    url = source?.Url,
                    filename = source?.Filename,
                    etag = source?.ETag,
                    lastModified = source?.LastModified,
                    checksum = source?.Checksum,
                    fileSize = source?.FileSize,
                    sourceSchema = JsonSerializer.Serialize(preflight.Headers)
                }, transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                created = true;
            }

            // These stages are durable so an interrupted import is visible and
            // can be reset without touching the currently serving schema.
            await SetStatusAsync(connection, version, "LOADING", ct);
            foreach (var (type, entries) in selected)
                await LoadTypeAsync(connection, schema, type, entries, ct);
            await SetStatusAsync(connection, version, "BUILDING_INDEXES", ct);
            await BuildRawIndexesAsync(connection, schema, ct);
            await SetStatusAsync(connection, version, "TRANSFORMING", ct);
            await BuildServingAsync(connection, schema, ct);
            await BuildAliasMapsAsync(connection, schema, version, ct);
            await SetStatusAsync(connection, version, "BUILDING_INDEXES", ct);
            await BuildIndexesAsync(connection, schema, ct);
            await SetStatusAsync(connection, version, "VALIDATING", ct);
            var report = await ValidateAsync(connection, schema, scope, version, maxCountDropFraction, minGeocodeCoverage, ct);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE platform.dataset_version SET status='READY',quality_report=@report::jsonb,
                    loaded_at=now(),validated_at=now(),updated_at=now() WHERE source_version=@version
                """, new { report = JsonSerializer.Serialize(report), version }, cancellationToken: ct));
            return report;
        }
        catch (Exception ex)
        {
            if (created)
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE platform.dataset_version SET status='FAILED',error_message=@error,updated_at=now()
                    WHERE source_version=@version
                    """, new { error = ex.Message[..Math.Min(ex.Message.Length, 2000)], version }));
            throw;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    /// <summary>Rechecks a fully built historical candidate that failed only the forward count-drop rule.</summary>
    public async Task<GnafQualityReport?> TryRevalidateFailedHistoricalAsync(string version,
        string? checksum, double maxCountDropFraction = 0.10, double minGeocodeCoverage = 0.90,
        CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        if (checksum is null) return null;
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct)))
            throw new InvalidOperationException("Another G-NAF import is running");
        try
        {
            // A recheck must see one stable serving release throughout its
            // comparisons and must never reuse tables from another archive.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(714845933)", cancellationToken: ct));
            try
            {
                var candidate = await connection.QuerySingleOrDefaultAsync<FailedCandidate>(new CommandDefinition("""
                    SELECT schema_name AS SchemaName, scope AS Scope, status AS Status,
                           error_message AS ErrorMessage, checksum AS Checksum
                    FROM platform.dataset_version
                    WHERE source_version=@version AND dataset_name='G-NAF'
                    """, new { version }, cancellationToken: ct));
                if (candidate is null || candidate.Status != "FAILED" ||
                    candidate.SchemaName != "gnaf_" + version.Replace('-', '_').ToLowerInvariant() ||
                    !string.Equals(candidate.Checksum, checksum, StringComparison.OrdinalIgnoreCase) ||
                    candidate.ErrorMessage is null ||
                    !candidate.ErrorMessage.Contains(" count dropped more than ", StringComparison.Ordinal) ||
                    !candidate.ErrorMessage.EndsWith("from active release", StringComparison.Ordinal))
                    return null;
                var activeVersion = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
                    SELECT source_version FROM platform.dataset_version
                    WHERE status='ACTIVE' AND dataset_name='G-NAF' AND scope=@scope
                    """, new { scope = candidate.Scope }, cancellationToken: ct));
                if (!IsHistoricalReplay(version, activeVersion)) return null;
                var built = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT to_regclass(@serving) IS NOT NULL AND to_regclass(@raw) IS NOT NULL
                    """, new { serving = candidate.SchemaName + ".address_search", raw = candidate.SchemaName + ".raw_address_detail" },
                    cancellationToken: ct));
                if (!built) return null;
                var report = await ValidateAsync(connection, candidate.SchemaName, candidate.Scope, version,
                    maxCountDropFraction, minGeocodeCoverage, ct);
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE platform.dataset_version SET status='READY',quality_report=@report::jsonb,
                        validated_at=now(),error_message=NULL,updated_at=now()
                    WHERE source_version=@version AND status='FAILED'
                    """, new { version, report = JsonSerializer.Serialize(report) }, cancellationToken: ct));
                return report;
            }
            finally
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845933)");
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    /// <summary>Rebuilds an unpromoted candidate from its retained raw tables after importer changes.</summary>
    public async Task<GnafQualityReport> RebuildCandidateAsync(string version,
        double maxCountDropFraction = 0.10, double minGeocodeCoverage = 0.90, CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct)))
            throw new InvalidOperationException("Another G-NAF import is running");
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(714845933)", cancellationToken: ct));
            var candidate = await connection.QuerySingleOrDefaultAsync<CandidateInfo>(new CommandDefinition(
                "SELECT schema_name AS SchemaName,scope AS Scope,status AS Status FROM platform.dataset_version WHERE source_version=@version",
                new { version }, cancellationToken: ct));
            if (candidate is null || candidate.Status != "READY" || !candidate.SchemaName.Equals(
                    "gnaf_" + version.Replace('-', '_').ToLowerInvariant(), StringComparison.Ordinal) ||
                candidate.Scope is not ("ALL" or "ACT" or "NSW" or "NT" or "QLD" or "SA" or "TAS" or "VIC" or "WA"))
                throw new InvalidOperationException("Only an unpromoted official G-NAF candidate can be rebuilt");
            var schema = candidate.SchemaName;
            // Rebuild only the candidate serving table. The stable API view
            // still points at the active release throughout this work.
            await SetStatusAsync(connection, version, "TRANSFORMING", ct);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition($"DROP TABLE {schema}.address_search", cancellationToken: ct, commandTimeout: 0));
                await BuildServingAsync(connection, schema, ct);
                await BuildAliasMapsAsync(connection, schema, version, ct);
                await SetStatusAsync(connection, version, "BUILDING_INDEXES", ct);
                await BuildIndexesAsync(connection, schema, ct);
                await SetStatusAsync(connection, version, "VALIDATING", ct);
                var report = await ValidateAsync(connection, schema, candidate.Scope, version, maxCountDropFraction, minGeocodeCoverage, ct);
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE platform.dataset_version SET status='READY',quality_report=@report::jsonb,
                        validated_at=now(),error_message=NULL,updated_at=now() WHERE source_version=@version
                    """, new { report = JsonSerializer.Serialize(report), version }, cancellationToken: ct));
                return report;
            }
            catch (Exception ex)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE platform.dataset_version SET status='FAILED',error_message=@error,updated_at=now()
                    WHERE source_version=@version
                    """, new { error = ex.Message[..Math.Min(ex.Message.Length, 2000)], version }));
                throw;
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845933)");
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    /// <summary>Rebuilds the active release's serving table beside the live one, then swaps atomically.</summary>
    public async Task UpgradeActiveServingAsync(CancellationToken ct = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct)))
            throw new InvalidOperationException("Another G-NAF import is running");
        try
        {
            var active = await connection.QuerySingleOrDefaultAsync<CandidateInfo>(new CommandDefinition(
                "SELECT schema_name AS SchemaName, scope AS Scope, status AS Status FROM platform.dataset_version WHERE status='ACTIVE' AND dataset_name='G-NAF'",
                cancellationToken: ct));
            if (active is null || !Regex.IsMatch(active.SchemaName, "^gnaf_[a-z0-9_]+$"))
                throw new InvalidOperationException("No active G-NAF release to upgrade");
            var alreadyUpgraded = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='api' AND table_name='address_search' AND column_name='flat_type')",
                cancellationToken: ct));
            if (alreadyUpgraded)
            {
                Console.WriteLine("Active serving table already includes subaddress fields");
                return;
            }
            var schema = active.SchemaName;
            var rawAvailable = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT to_regclass(@table) IS NOT NULL", new { table = schema + ".raw_address_detail" }, cancellationToken: ct));
            if (!rawAvailable)
                throw new InvalidOperationException("Raw source tables were removed after activation. Prepare a new release from the official ZIP instead.");
            await connection.ExecuteAsync(new CommandDefinition($"DROP TABLE IF EXISTS {schema}.address_search_next", cancellationToken: ct, commandTimeout: 0));
            // This legacy path requires retained raw tables. Build beside the
            // serving table and compare row counts before changing the view.
            await BuildServingAsync(connection, schema, ct, "address_search_next");
            await BuildIndexesAsync(connection, schema, ct, "address_search_next");
            var oldCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {schema}.address_search", cancellationToken: ct, commandTimeout: 0));
            var newCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {schema}.address_search_next", cancellationToken: ct, commandTimeout: 0));
            if (oldCount != newCount || newCount == 0)
                throw new InvalidDataException($"Serving table count changed: {oldCount} -> {newCount}");
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition($"ALTER TABLE {schema}.address_search RENAME TO address_search_previous", transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition($"ALTER TABLE {schema}.address_search_next RENAME TO address_search", transaction: transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition($"CREATE OR REPLACE VIEW api.address_search AS SELECT * FROM {schema}.address_search", transaction: transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition($"DROP TABLE {schema}.address_search_previous", cancellationToken: ct, commandTimeout: 0));
            Console.WriteLine($"Upgraded {newCount:N0} active G-NAF addresses");
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    /// <summary>Restores compact street/locality alias dictionaries from the exact installed ZIP.
    /// Address serving rows and the active release are never rewritten.</summary>
    public async Task<(long Streets, long Localities)> EnrichAliasesAsync(
        string version, string archivePath, CancellationToken ct = default)
    {
        if (!VersionPattern.IsMatch(version)) throw new ArgumentException("Invalid release version", nameof(version));
        if (!File.Exists(archivePath)) throw new FileNotFoundException("G-NAF archive not found", archivePath);
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_lock(714845934)", cancellationToken: ct)))
            throw new InvalidOperationException("Another G-NAF import is running");
        var stage = "alias_stage_" + Guid.NewGuid().ToString("N");
        try
        {
            var release = await connection.QuerySingleOrDefaultAsync<AliasCandidateInfo>(new CommandDefinition("""
                SELECT scope AS Scope,status AS Status,checksum AS Checksum
                FROM platform.dataset_version WHERE source_version=@version AND dataset_name='G-NAF'
                """, new { version }, cancellationToken: ct));
            if (release is null || release.Status is not ("READY" or "ACTIVE" or "RETIRED"))
                throw new InvalidOperationException("Alias backfill requires an installed official G-NAF release");
            // Backfill from the archive recorded for this release; mixing
            // alias dictionaries from a different ZIP could alter live matches.
            if (!string.IsNullOrWhiteSpace(release.Checksum))
            {
                await using var input = File.OpenRead(archivePath);
                var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
                if (!checksum.Equals(release.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Alias archive checksum differs from the installed release");
            }
            using var archive = ZipFile.OpenRead(archivePath);
            var selected = SelectEntries(archive, release.Scope);
            await connection.ExecuteAsync(new CommandDefinition($"CREATE SCHEMA {stage}", cancellationToken: ct));
            foreach (var type in new[] { "STREET_TYPE_AUT", "STATE", "LOCALITY", "STREET_LOCALITY",
                "STREET_LOCALITY_ALIAS", "LOCALITY_ALIAS" })
                if (selected.TryGetValue(type, out var entries))
                    await LoadTypeAsync(connection, stage, type, entries, ct);
            await connection.ExecuteAsync(new CommandDefinition($"""
                CREATE INDEX ON {stage}.raw_state(state_pid);
                CREATE INDEX ON {stage}.raw_locality(locality_pid);
                CREATE INDEX ON {stage}.raw_street_locality(street_locality_pid);
                CREATE INDEX ON {stage}.raw_street_type_aut(code)
                """, cancellationToken: ct, commandTimeout: 0));
            return await BuildAliasMapsAsync(connection, stage, version, ct);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition($"DROP SCHEMA IF EXISTS {stage} CASCADE"));
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(714845934)");
        }
    }

    private static Dictionary<string, List<ZipArchiveEntry>> SelectEntries(ZipArchive archive, string scope)
    {
        // For ALL, every required PSV type must appear exactly once for every
        // state and territory; a partial archive must never become READY.
        var result = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var filename = Path.GetFileName(entry.FullName);
            var type = filename.Equals(StreetTypeFile, StringComparison.OrdinalIgnoreCase) ? "STREET_TYPE_AUT" : null;
            var match = FilePattern.Match(filename);
            if (match.Success && (scope == "ALL" || match.Groups[1].Value.Equals(scope, StringComparison.OrdinalIgnoreCase)))
                type = match.Groups[2].Value.ToUpperInvariant();
            if (type is null) continue;
            if (!result.TryGetValue(type, out var list)) result[type] = list = [];
            list.Add(entry);
        }
        if (!result.TryGetValue("STREET_TYPE_AUT", out var streetTypes) || streetTypes.Count != 1)
            throw new InvalidDataException("ZIP must contain exactly one authority street type PSV");
        foreach (var type in RequiredTypes)
        {
            var entries = result.GetValueOrDefault(type) ?? [];
            var expected = scope == "ALL" ? States : [scope];
            foreach (var state in expected)
                if (entries.Count(e => Path.GetFileName(e.FullName).StartsWith(state + "_", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidDataException($"ZIP must contain one {state}_{type} PSV");
        }
        return result;
    }

    private static async Task LoadTypeAsync(NpgsqlConnection connection, string schema, string type,
        List<ZipArchiveEntry> entries, CancellationToken ct)
    {
        var table = "raw_" + type.ToLowerInvariant();
        string[]? expected = null;
        foreach (var entry in entries)
        {
            var columns = await ReadHeaderAsync(entry, ct);
            if (expected is null)
            {
                expected = columns;
                var definitions = string.Join(',', columns.Select(c => $"\"{c.ToLowerInvariant()}\" text"));
                await connection.ExecuteAsync(new CommandDefinition($"CREATE TABLE {schema}.{table} ({definitions})", cancellationToken: ct));
                ValidateColumns(type, columns);
            }
            else if (!expected.Order().SequenceEqual(columns.Order()))
                throw new InvalidDataException($"Inconsistent PSV header: {entry.FullName}");

            var columnList = string.Join(',', columns.Select(c => $"\"{c.ToLowerInvariant()}\""));
            // Stream PSV contents into PostgreSQL instead of materialising a
            // national table in managed memory.
            using var writer = connection.BeginTextImport($"COPY {schema}.{table} ({columnList}) FROM STDIN WITH (FORMAT csv, HEADER true, DELIMITER '|')");
            await using var content = entry.Open();
            using var input = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[65536];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
                await writer.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<string[]> ReadHeaderAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = await reader.ReadLineAsync(ct) ?? throw new InvalidDataException($"Empty PSV: {entry.FullName}");
        var columns = header.TrimStart('\uFEFF').Split('|').Select(x => x.Trim().ToUpperInvariant()).ToArray();
        // Validate identifiers before they can become dynamic SQL column names.
        if (columns.Length == 0 || columns.Length > 150 || columns.Any(c => !ColumnPattern.IsMatch(c)) || columns.Distinct().Count() != columns.Length)
            throw new InvalidDataException($"Invalid PSV header: {entry.FullName}");
        return columns;
    }

    private static void ValidateColumns(string type, string[] columns)
    {
        var required = type switch
        {
            "ADDRESS_DETAIL" => ["ADDRESS_DETAIL_PID", "LOCALITY_PID", "STREET_LOCALITY_PID", "FLAT_TYPE_CODE", "FLAT_NUMBER_PREFIX", "FLAT_NUMBER", "FLAT_NUMBER_SUFFIX", "LEVEL_TYPE_CODE", "LEVEL_NUMBER_PREFIX", "LEVEL_NUMBER", "LEVEL_NUMBER_SUFFIX", "BUILDING_NAME", "NUMBER_FIRST", "NUMBER_LAST", "POSTCODE", "CONFIDENCE"],
            "ADDRESS_DEFAULT_GEOCODE" => ["ADDRESS_DETAIL_PID", "LATITUDE", "LONGITUDE"],
            "STREET_LOCALITY" => ["STREET_LOCALITY_PID", "STREET_NAME", "STREET_TYPE_CODE", "LOCALITY_PID"],
            "LOCALITY" => ["LOCALITY_PID", "LOCALITY_NAME", "STATE_PID"],
            "STATE" => ["STATE_PID", "STATE_ABBREVIATION"],
            "STREET_TYPE_AUT" => ["CODE", "NAME"],
            "STREET_LOCALITY_ALIAS" => ["STREET_LOCALITY_PID", "STREET_NAME", "STREET_TYPE_CODE"],
            "LOCALITY_ALIAS" => ["LOCALITY_PID", "NAME"],
            _ => Array.Empty<string>()
        };
        foreach (var column in required)
            if (!columns.Contains(column)) throw new InvalidDataException($"{type} is missing {column}");
    }

    private static Task SetStatusAsync(NpgsqlConnection connection, string version, string status, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status=@status,updated_at=now() WHERE source_version=@version",
            new { version, status }, cancellationToken: ct));

    private static async Task BuildServingAsync(NpgsqlConnection connection, string schema, CancellationToken ct,
        string table = "address_search")
    {
        await connection.ExecuteAsync(new CommandDefinition($"CREATE TABLE {schema}.{table} (LIKE platform.empty_address_search INCLUDING ALL)", cancellationToken: ct));
        // Raw tables remain through validation and alias construction; completed
        // national releases prune them after promotion to reclaim storage.
        var sql = $"""
            INSERT INTO {schema}.{table}
              (gnaf_pid,formatted_address,flat_number,number_first,street_name,street_type,
               locality_name,state,postcode,latitude,longitude,search_text,flat_type,
               flat_number_prefix,flat_number_suffix,level_type,level_number,
               level_number_prefix,level_number_suffix,building_name)
            SELECT a.address_detail_pid, parts.formatted,
                   coalesce(a.flat_number,''),coalesce(a.number_first,''),
                   coalesce(sl.street_name,''),coalesce(st.name,sl.street_type_code,''),
                   l.locality_name,s.state_abbreviation,coalesce(a.postcode,''),
                   nullif(g.latitude,'')::double precision,nullif(g.longitude,'')::double precision,
                   trim(upper(regexp_replace(parts.formatted,'[^[:alnum:]]+',' ','g'))),
                   coalesce(a.flat_type_code,''),coalesce(a.flat_number_prefix,''),coalesce(a.flat_number_suffix,''),
                   coalesce(a.level_type_code,''),coalesce(a.level_number,''),
                   coalesce(a.level_number_prefix,''),coalesce(a.level_number_suffix,''),coalesce(a.building_name,'')
            FROM {schema}.raw_address_detail a
            JOIN {schema}.raw_locality l ON l.locality_pid=a.locality_pid
            JOIN {schema}.raw_state s ON s.state_pid=l.state_pid
            LEFT JOIN {schema}.raw_street_locality sl ON sl.street_locality_pid=a.street_locality_pid
            LEFT JOIN {schema}.raw_street_type_aut st ON st.code=sl.street_type_code
            LEFT JOIN {schema}.raw_address_default_geocode g ON g.address_detail_pid=a.address_detail_pid
            CROSS JOIN LATERAL (SELECT concat_ws(' ',
                CASE WHEN nullif(a.flat_number,'') IS NOT NULL THEN
                    concat_ws(' ', CASE coalesce(a.flat_type_code,'')
                        WHEN 'APT' THEN 'APARTMENT' WHEN 'SE' THEN 'SUITE' WHEN 'OFFC' THEN 'OFFICE'
                        WHEN 'STU' THEN 'STUDIO' WHEN 'TNHS' THEN 'TOWNHOUSE' WHEN 'VLLA' THEN 'VILLA'
                        WHEN 'PTHS' THEN 'PENTHOUSE' WHEN 'FCTY' THEN 'FACTORY'
                        WHEN '' THEN 'UNIT' ELSE a.flat_type_code END,
                        concat(coalesce(a.flat_number_prefix,''),a.flat_number,coalesce(a.flat_number_suffix,''))) END,
                CASE WHEN nullif(a.level_type_code,'') IS NOT NULL OR nullif(a.level_number,'') IS NOT NULL THEN
                    concat_ws(' ', CASE coalesce(a.level_type_code,'')
                        WHEN '' THEN 'LEVEL'
                        WHEN 'L' THEN 'LEVEL' WHEN 'FL' THEN 'FLOOR' WHEN 'G' THEN 'GROUND'
                        WHEN 'B' THEN 'BASEMENT' WHEN 'LG' THEN 'LOWER GROUND' WHEN 'UG' THEN 'UPPER GROUND'
                        WHEN 'M' THEN 'MEZZANINE' ELSE a.level_type_code END,
                        nullif(concat(coalesce(a.level_number_prefix,''),coalesce(a.level_number,''),coalesce(a.level_number_suffix,'')),'')) END,
                nullif(a.building_name,''),
                CASE WHEN nullif(a.number_last,'') IS NOT NULL THEN concat_ws('-',nullif(a.number_first,''),a.number_last) ELSE nullif(a.number_first,'') END,
                nullif(sl.street_name,''),coalesce(st.name,sl.street_type_code),
                l.locality_name,s.state_abbreviation,nullif(a.postcode,'')) AS formatted) parts
            WHERE a.confidence <> '-1'
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: 0));
    }

    private static async Task<(long Streets, long Localities)> BuildAliasMapsAsync(
        NpgsqlConnection connection, string schema, string version, CancellationToken ct)
    {
        // Keep small, release-scoped dictionaries after the large raw PSV tables
        // are pruned. Replace both dictionaries in one transaction so a backfill
        // of the active release never exposes a half-built mapping.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM platform.street_alias WHERE source_version=@version; DELETE FROM platform.locality_alias WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        var streetSource = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass(@tableName) IS NOT NULL", new { tableName = $"{schema}.raw_street_locality_alias" },
            transaction, cancellationToken: ct));
        if (streetSource)
            await connection.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO platform.street_alias
                  (source_version,alias_name,alias_type,canonical_name,canonical_type,locality_name,state)
                SELECT DISTINCT @version,
                    trim(upper(regexp_replace(sa.street_name,'[^[:alnum:]]+',' ','g'))),
                    trim(upper(coalesce(sta.name,sa.street_type_code,''))),
                    trim(upper(regexp_replace(sl.street_name,'[^[:alnum:]]+',' ','g'))),
                    trim(upper(coalesce(stc.name,sl.street_type_code,''))),
                    trim(upper(l.locality_name)),trim(upper(s.state_abbreviation))
                FROM {schema}.raw_street_locality_alias sa
                JOIN {schema}.raw_street_locality sl ON sl.street_locality_pid=sa.street_locality_pid
                JOIN {schema}.raw_locality l ON l.locality_pid=sl.locality_pid
                JOIN {schema}.raw_state s ON s.state_pid=l.state_pid
                LEFT JOIN {schema}.raw_street_type_aut sta ON sta.code=sa.street_type_code
                LEFT JOIN {schema}.raw_street_type_aut stc ON stc.code=sl.street_type_code
                WHERE nullif(sa.street_name,'') IS NOT NULL AND nullif(sl.street_name,'') IS NOT NULL
                """, new { version }, transaction, cancellationToken: ct, commandTimeout: 0));
        var localitySource = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass(@tableName) IS NOT NULL", new { tableName = $"{schema}.raw_locality_alias" },
            transaction, cancellationToken: ct));
        if (localitySource)
            await connection.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO platform.locality_alias(source_version,alias_name,canonical_name,state)
                SELECT DISTINCT @version,
                    trim(upper(regexp_replace(la.name,'[^[:alnum:]]+',' ','g'))),
                    trim(upper(regexp_replace(l.locality_name,'[^[:alnum:]]+',' ','g'))),
                    trim(upper(s.state_abbreviation))
                FROM {schema}.raw_locality_alias la
                JOIN {schema}.raw_locality l ON l.locality_pid=la.locality_pid
                JOIN {schema}.raw_state s ON s.state_pid=l.state_pid
                WHERE nullif(la.name,'') IS NOT NULL
                """, new { version }, transaction, cancellationToken: ct, commandTimeout: 0));
        var streets = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.street_alias WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        var localities = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.locality_alias WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return (streets, localities);
    }

    private static async Task BuildRawIndexesAsync(NpgsqlConnection connection, string schema, CancellationToken ct)
    {
        var keys = new[]
        {
            (Table: "locality", Column: "locality_pid"),
            (Table: "state", Column: "state_pid"),
            (Table: "street_locality", Column: "street_locality_pid"),
            (Table: "address_default_geocode", Column: "address_detail_pid"),
            (Table: "street_type_aut", Column: "code")
        };
        foreach (var (table, column) in keys)
            await connection.ExecuteAsync(new CommandDefinition(
                $"CREATE UNIQUE INDEX raw_{table}_key ON {schema}.raw_{table} ({column})", cancellationToken: ct, commandTimeout: 0));
    }

    private static async Task BuildIndexesAsync(NpgsqlConnection connection, string schema, CancellationToken ct,
        string table = "address_search")
    {
        var index = table;
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_prefix ON {schema}.{table} (search_text text_pattern_ops)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_postcode ON {schema}.{table} (postcode)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_number_first ON {schema}.{table} (number_first)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_street_prefix ON {schema}.{table} (street_name text_pattern_ops)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_street_trgm ON {schema}.{table} USING gin (street_name gin_trgm_ops)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX {index}_trgm ON {schema}.{table} USING gin (search_text gin_trgm_ops)", cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition($"ANALYZE {schema}.{table}", cancellationToken: ct, commandTimeout: 0));
    }

    private static bool IsHistoricalReplay(string candidateVersion, string? activeVersion) =>
        NationalVersionPattern.IsMatch(candidateVersion) && activeVersion is not null &&
        NationalVersionPattern.IsMatch(activeVersion) &&
        string.CompareOrdinal(candidateVersion, activeVersion) < 0;

    private static async Task<GnafQualityReport> ValidateAsync(NpgsqlConnection connection, string schema, string scope,
        string version, double maxDrop, double minGeocode, CancellationToken ct)
    {
        // A READY candidate must agree with its imported raw address count,
        // meet geocode coverage, and avoid an unexplained drop on forward upgrades.
        async Task<long> Count(string query) => await connection.ExecuteScalarAsync<long>(new CommandDefinition(query, cancellationToken: ct, commandTimeout: 0));
        var addresses = await Count($"SELECT count(*) FROM {schema}.address_search");
        // Geoscape's release totals exclude unsupported addresses (confidence -1).
        var rawAddresses = await Count($"SELECT count(*) FROM {schema}.raw_address_detail WHERE confidence <> '-1'");
        var streets = await Count($"SELECT count(*) FROM {schema}.raw_street_locality");
        var localities = await Count($"SELECT count(*) FROM {schema}.raw_locality");
        var geocodes = await Count($"SELECT count(*) FROM {schema}.address_search WHERE latitude IS NOT NULL AND longitude IS NOT NULL");
        var states = await Count($"SELECT count(DISTINCT state) FROM {schema}.address_search");
        var postcodes = await Count($"SELECT count(DISTINCT postcode) FROM {schema}.address_search WHERE postcode<>''");
        async Task<long> OptionalCount(string type)
        {
            var table = $"{schema}.raw_{type.ToLowerInvariant()}";
            var exists = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT to_regclass(@table)::text", new { table }, cancellationToken: ct));
            return exists is null ? 0 : await Count($"SELECT count(*) FROM {table}");
        }
        var aliases = await OptionalCount(OptionalTypes[0]);
        var streetAliases = await OptionalCount(OptionalTypes[1]);
        var localityAliases = await OptionalCount(OptionalTypes[2]);
        var baseline = await connection.QuerySingleOrDefaultAsync<BaselineQuality>(new CommandDefinition(
            "SELECT source_version AS SourceVersion, quality_report::text AS QualityReport FROM platform.dataset_version WHERE status='ACTIVE' AND dataset_name='G-NAF' AND scope=@scope",
            new { scope }, cancellationToken: ct));
        var previous = baseline?.QualityReport is null ? null : JsonSerializer.Deserialize<GnafQualityReport>(baseline.QualityReport);
        var historicalReplay = IsHistoricalReplay(version, baseline?.SourceVersion);
        long? previousAddresses = previous?.Addresses;
        var coverage = addresses == 0 ? 0 : (double)geocodes / addresses;
        var report = new GnafQualityReport(addresses, streets, localities, geocodes, states, postcodes,
            aliases, streetAliases, localityAliases, Math.Round(coverage, 4), previousAddresses, historicalReplay);
        if (addresses == 0 || addresses != rawAddresses || streets == 0 || localities == 0 ||
            states < (scope == "ALL" ? States.Length : 1) || postcodes == 0)
            throw new InvalidDataException($"Candidate counts failed validation: {JsonSerializer.Serialize(report)}");
        if (coverage < minGeocode)
            throw new InvalidDataException($"Geocode coverage {coverage:P2} is below {minGeocode:P2}");
        if (previous is not null && !historicalReplay)
        {
            var metrics = new (string Name, long Current, long Prior)[]
            {
                ("addresses", addresses, previous.Addresses), ("streets", streets, previous.Streets),
                ("localities", localities, previous.Localities), ("geocodes", geocodes, previous.Geocodes),
                ("states", states, previous.States), ("postcodes", postcodes, previous.Postcodes),
                ("address aliases", aliases, previous.AddressAliases),
                ("street aliases", streetAliases, previous.StreetAliases),
                ("locality aliases", localityAliases, previous.LocalityAliases)
            };
            foreach (var (name, current, prior) in metrics)
                if (prior > 0 && current < prior * (1 - maxDrop))
                    throw new InvalidDataException($"{name} count dropped more than {maxDrop:P2} from active release");
        }
        return report;
    }
}
