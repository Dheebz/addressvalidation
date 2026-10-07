using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Address.Core;
using Dapper;
using Npgsql;

namespace Address.Data;

/// <summary>Creates primary-targeted PostgreSQL connections and applies additive platform schema changes.</summary>
public sealed class Database(string connectionString)
{
    /// <summary>Primary-targeted connection source shared by repositories.</summary>
    public NpgsqlDataSource DataSource { get; } = CreateDataSource(connectionString);

    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host?.Contains(',') != true) return NpgsqlDataSource.Create(connectionString);
        // Every writable service uses this primary-targeted multi-host source.
        // After Patroni promotes the standby, new pooled connections can find
        // the new primary without changing the connection string.
        settings.Remove("Target Session Attributes");
        return new NpgsqlDataSourceBuilder(settings.ConnectionString).BuildMultiHost()
            .WithTargetSession(TargetSessionAttributes.Primary);
    }

    /// <summary>Runs the embedded idempotent migration under a cross-process transaction lock.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        // Several processes may start together after a failover. The transaction
        // lock lets only one apply additive schema changes at a time.
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Address.Data.initial.sql")
            ?? throw new InvalidOperationException("Embedded migration not found");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);
        await using var connection = await DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(714845932)", connection, transaction))
            await advisoryLock.ExecuteNonQueryAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
            await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

/// <summary>Retrieves a bounded candidate set from the stable serving view for core scoring.</summary>
public sealed class PostgresAddressRepository(Database database) : IAddressRepository
{
    private sealed record StreetAliasRow(string AliasName, string AliasType, string CanonicalName,
        string CanonicalType, string LocalityName);
    private sealed record LocalityAliasRow(string AliasName, string CanonicalName);
    private sealed record AliasQuery(string Query, string? CanonicalStreet = null, string? AliasStreet = null,
        string? CanonicalLocality = null, string? AliasLocality = null);

    /// <summary>Reads one canonical row through the release-independent API view.</summary>
    public async Task<AddressRecord?> GetByIdAsync(string id, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        const string sql = """
            SELECT gnaf_pid AS GnafPid, formatted_address AS FormattedAddress, flat_number AS FlatNumber,
                   number_first AS NumberFirst, street_name AS StreetName, street_type AS StreetType,
                   locality_name AS LocalityName, state AS State, postcode AS Postcode,
                   latitude AS Latitude, longitude AS Longitude, search_text AS SearchText,
                   flat_type AS FlatType, flat_number_prefix AS FlatNumberPrefix,
                   flat_number_suffix AS FlatNumberSuffix, level_type AS LevelType,
                   level_number AS LevelNumber, level_number_prefix AS LevelNumberPrefix,
                   level_number_suffix AS LevelNumberSuffix, building_name AS BuildingName
            FROM api.address_search WHERE gnaf_pid = @id
            """;
        return await connection.QuerySingleOrDefaultAsync<AddressRecord>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    /// <summary>Tries selective indexed lookups before bounded fuzzy candidate retrieval.</summary>
    public async Task<IReadOnlyList<AddressRecord>> SearchAsync(string normalizedQuery, int limit, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        // Enough memory for an exact bitmap intersection across the national street/number indexes.
        await connection.ExecuteAsync(new CommandDefinition("SET work_mem TO '16MB'", cancellationToken: ct));
        const string columns = """
            SELECT gnaf_pid AS GnafPid, formatted_address AS FormattedAddress, flat_number AS FlatNumber,
                   number_first AS NumberFirst, street_name AS StreetName, street_type AS StreetType,
                   locality_name AS LocalityName, state AS State, postcode AS Postcode,
                   latitude AS Latitude, longitude AS Longitude, search_text AS SearchText,
                   flat_type AS FlatType, flat_number_prefix AS FlatNumberPrefix,
                   flat_number_suffix AS FlatNumberSuffix, level_type AS LevelType,
                   level_number AS LevelNumber, level_number_prefix AS LevelNumberPrefix,
                   level_number_suffix AS LevelNumberSuffix, building_name AS BuildingName
            """;
        var shortQuery = AddressNormalizer.AbbreviateForSearch(normalizedQuery);
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var postcode = tokens.Length > 0 && tokens[^1].Length == 4 && tokens[^1].All(char.IsDigit)
            ? tokens[^1] : null;
        var streetHint = AddressNormalizer.StreetHint(normalizedQuery);
        var houseNumber = AddressNormalizer.HouseNumberHint(normalizedQuery);
        var flatNumber = System.Text.RegularExpressions.Regex.Match(
            SubaddressParser.Parse(normalizedQuery).Number, @"\d+").Value;
        var parameters = new
        {
            query = normalizedQuery,
            shortQuery,
            prefix = normalizedQuery + "%",
            shortPrefix = shortQuery + "%",
            streetHint,
            streetPrefix = streetHint + "%",
            houseNumber,
            flatNumber,
            postcode,
            limit = Math.Clamp(limit * 4, 20, 100)
        };
        async Task<IReadOnlyList<AddressRecord>> Find(string whereAndOrder)
        {
            var rows = await connection.QueryAsync<AddressRecord>(new CommandDefinition(
                $"{columns} FROM api.address_search {whereAndOrder} LIMIT @limit",
                parameters, cancellationToken: ct));
            return rows.AsList();
        }
        // Use cheap, selective index paths first. Fuzzy search only runs after
        // exact, prefix, and unambiguous alias lookups find nothing.
        var exact = await Find("WHERE search_text=@query OR search_text=@shortQuery");
        if (exact.Count > 0) return exact;
        var prefix = await Find("WHERE search_text LIKE @prefix OR search_text LIKE @shortPrefix ORDER BY length(search_text)");
        if (prefix.Count > 0) return prefix;
        var aliases = await FindAliasCandidatesAsync(connection, columns, normalizedQuery, limit, ct);
        if (aliases.Count > 0) return aliases;
        // Constrain trigram scans by street, number and postcode when possible;
        // national-table-wide fuzzy scans are expensive and less precise.
        const string rank = "ORDER BY greatest(similarity(search_text,@query),similarity(search_text,@shortQuery)) DESC";
        if (streetHint is not null)
        {
            var filter = (postcode is null ? "" : " AND postcode=@postcode") +
                (houseNumber is null ? "" : " AND number_first=@houseNumber");
            if (flatNumber.Length > 0 && houseNumber is not null)
            {
                var exactFlat = await Find("WHERE street_name LIKE @streetPrefix AND number_first=@houseNumber AND flat_number=@flatNumber" +
                    (postcode is null ? " " : " AND postcode=@postcode ") + rank);
                if (exactFlat.Count > 0) return exactFlat;
            }
            var streetPrefix = await Find($"WHERE street_name LIKE @streetPrefix{filter} {rank}");
            if (streetPrefix.Count > 0) return streetPrefix;
            var streetFuzzy = await Find($"WHERE street_name % @streetHint{filter} {rank}");
            if (streetFuzzy.Count > 0) return streetFuzzy;
        }
        if (postcode is not null)
            return await Find($"WHERE postcode=@postcode" +
                (houseNumber is null ? " " : " AND number_first=@houseNumber ") + rank);
        return [];
    }

    private static async Task<IReadOnlyList<AddressRecord>> FindAliasCandidatesAsync(
        NpgsqlConnection connection, string columns, string query, int limit, CancellationToken ct)
    {
        var state = AddressNormalizer.StateHint(query);
        var locality = AddressNormalizer.LocalityHint(query);
        var street = AddressNormalizer.StreetHint(query);
        if (state is null || locality is null) return [];
        var variants = new List<AliasQuery> { new(query) };
        // Only a one-to-one locality alias can be expanded. Ambiguous aliases
        // would invent certainty and may point to the wrong address.
        var localities = await connection.QueryAsync<LocalityAliasRow>(new CommandDefinition("""
            SELECT alias_name AS AliasName,min(canonical_name) AS CanonicalName
            FROM platform.locality_alias WHERE source_version=(
                SELECT source_version FROM platform.dataset_version WHERE status='ACTIVE'
                AND dataset_name='G-NAF')
            AND alias_name=@locality AND state=@state
            GROUP BY alias_name HAVING count(*)=1
            """, new { locality, state }, cancellationToken: ct));
        foreach (var row in localities)
        {
            var mapped = ReplacePhrase(query, row.AliasName + " " + state, row.CanonicalName + " " + state);
            if (mapped != query)
                variants.Add(new(mapped, CanonicalLocality: row.CanonicalName, AliasLocality: row.AliasName));
        }
        if (street is not null)
        {
            foreach (var variant in variants.ToArray())
            {
                var canonicalLocality = variant.CanonicalLocality ?? locality;
                // A street alias also needs a unique canonical street within
                // its locality and state before it is eligible for matching.
                var streets = await connection.QueryAsync<StreetAliasRow>(new CommandDefinition("""
                    SELECT s.alias_name AS AliasName,s.alias_type AS AliasType,
                           s.canonical_name AS CanonicalName,s.canonical_type AS CanonicalType,
                           s.locality_name AS LocalityName
                    FROM platform.street_alias s WHERE s.source_version=(
                        SELECT source_version FROM platform.dataset_version WHERE status='ACTIVE'
                        AND dataset_name='G-NAF')
                    AND s.alias_name=@street AND s.state=@state AND s.locality_name=@canonicalLocality
                    AND NOT EXISTS (
                        SELECT 1 FROM platform.street_alias other
                        WHERE other.source_version=s.source_version AND other.alias_name=s.alias_name
                          AND other.alias_type=s.alias_type AND other.state=s.state
                          AND other.locality_name=s.locality_name
                          AND (other.canonical_name,other.canonical_type)<>(s.canonical_name,s.canonical_type)
                    ) LIMIT 8
                    """, new { street, state, canonicalLocality }, cancellationToken: ct));
                foreach (var row in streets)
                {
                    var aliasStreet = AddressNormalizer.Normalize(row.AliasName + " " + row.AliasType);
                    var canonicalStreet = AddressNormalizer.Normalize(row.CanonicalName + " " + row.CanonicalType);
                    var mapped = ReplacePhrase(variant.Query, aliasStreet, canonicalStreet);
                    if (mapped != variant.Query)
                        variants.Add(variant with { Query = mapped, CanonicalStreet = canonicalStreet, AliasStreet = aliasStreet });
                }
            }
        }
        var results = new List<AddressRecord>();
        foreach (var variant in variants.Skip(1).DistinctBy(x => x.Query).Take(8))
        {
            var abbreviated = AddressNormalizer.AbbreviateForSearch(variant.Query);
            var rows = await connection.QueryAsync<AddressRecord>(new CommandDefinition($"""
                {columns} FROM api.address_search
                WHERE search_text=@canonical OR search_text=@abbreviated
                   OR search_text LIKE @prefix OR search_text LIKE @shortPrefix
                ORDER BY CASE WHEN search_text=@canonical OR search_text=@abbreviated THEN 0 ELSE 1 END,
                         length(search_text)
                LIMIT @limit
                """, new
            {
                canonical = variant.Query,
                abbreviated,
                prefix = variant.Query + "%",
                shortPrefix = abbreviated + "%",
                limit = Math.Clamp(limit * 4, 20, 100)
            }, cancellationToken: ct));
            foreach (var row in rows)
            {
                // Score against the user's alias spelling, but return the
                // canonical serving row and G-NAF ID unchanged.
                var matchText = AddressNormalizer.Normalize(row.SearchText);
                if (variant.CanonicalStreet is not null)
                    matchText = ReplacePhrase(matchText, variant.CanonicalStreet, variant.AliasStreet!);
                if (variant.CanonicalLocality is not null)
                    matchText = ReplacePhrase(matchText, variant.CanonicalLocality + " " + state,
                        variant.AliasLocality + " " + state);
                results.Add(row with { MatchText = matchText });
            }
        }
        return results;
    }

    private static string ReplacePhrase(string text, string from, string to) =>
        (" " + text + " ").Replace(" " + from + " ", " " + to + " ", StringComparison.Ordinal).Trim();

    /// <summary>Returns the active source version used by API response metadata.</summary>
    public async Task<string?> ActiveVersionAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT source_version FROM platform.dataset_version WHERE status = 'ACTIVE'", cancellationToken: ct));
    }

    /// <summary>Returns the active dataset identity, including synthetic fixtures in tests.</summary>
    public async Task<DatasetMetadata?> ActiveDatasetAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<DatasetMetadata>(new CommandDefinition(
            "SELECT dataset_name AS Name, source_version AS Version FROM platform.dataset_version WHERE status='ACTIVE'", cancellationToken: ct));
    }
}

/// <summary>Compact release state used for lifecycle and cache maintenance.</summary>
public sealed record DatasetVersion(string SourceVersion, string SchemaName, string Status, DateTime? PromotedAt);
/// <summary>Release state and quality/storage figures shown to operators.</summary>
public sealed record DatasetDetail(string SourceVersion, string DatasetName, string Status, string Scope,
    long? AddressCount, long? TableBytes, long? ArchiveBytes, DateTime? RetiredAt,
    DateTime? ValidatedAt, DateTime? PromotedAt, double? GeocodeCoverage,
    string? ErrorMessage);

/// <summary>Owns release state and transactional changes to the stable API serving view.</summary>
public sealed class DatasetLifecycle(Database database)
{
    // Keep this explicit list stable and ordered: CREATE OR REPLACE VIEW cannot
    // remove or reorder exposed columns while readers depend on the view.
    private const string ServingColumns = "gnaf_pid,formatted_address,flat_number,number_first,street_name,street_type,locality_name,state,postcode,latitude,longitude,search_text,flat_type,flat_number_prefix,flat_number_suffix,level_type,level_number,level_number_prefix,level_number_suffix,building_name";
    private sealed record ReleaseSchema(string SourceVersion, string SchemaName);

    /// <summary>Removes import-only tables from completed national releases while retaining serving tables and aliases.</summary>
    public async Task<int> PruneCompletedRawAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
        var dropped = await PruneCompletedRawAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return dropped;
    }

    private static async Task<int> PruneCompletedRawAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        var releases = (await connection.QueryAsync<ReleaseSchema>(new CommandDefinition("""
            SELECT source_version AS SourceVersion, schema_name AS SchemaName
            FROM platform.dataset_version
            WHERE dataset_name='G-NAF' AND scope='ALL' AND status IN ('ACTIVE','RETIRED')
            FOR UPDATE
            """, transaction: transaction, cancellationToken: ct))).ToArray();
        var dropped = 0;
        foreach (var release in releases)
        {
            var expected = "gnaf_" + release.SourceVersion.Replace('-', '_').ToLowerInvariant();
            if (release.SchemaName != expected || !Regex.IsMatch(expected, "^[a-z0-9_]+$"))
                throw new InvalidOperationException("Unsafe G-NAF schema name in release metadata");
            var tables = await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT tablename FROM pg_tables WHERE schemaname=@schema AND left(tablename,4)='raw_'",
                new { schema = release.SchemaName }, transaction, cancellationToken: ct));
            foreach (var table in tables)
            {
                if (!Regex.IsMatch(table, "^raw_[a-z0-9_]+$"))
                    throw new InvalidOperationException("Unsafe raw table name in release schema");
                await connection.ExecuteAsync(new CommandDefinition(
                    $"DROP TABLE {release.SchemaName}.{table}", transaction: transaction,
                    cancellationToken: ct, commandTimeout: 0));
                dropped++;
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE platform.dataset_version SET raw_pruned_at=now(),updated_at=now() WHERE source_version=@version AND raw_pruned_at IS NULL",
                new { version = release.SourceVersion }, transaction, cancellationToken: ct));
        }
        return dropped;
    }

    private static async Task PrepareServingTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string schema, CancellationToken ct)
    {
        // Older locally retained releases predate structured subaddress fields. PostgreSQL
        // permits adding constant-default columns without rewriting the national table.
        var sql = $"""
            ALTER TABLE {schema}.address_search
                ADD COLUMN IF NOT EXISTS flat_type text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS flat_number_prefix text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS flat_number_suffix text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS level_type text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS level_number text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS level_number_prefix text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS level_number_suffix text NOT NULL DEFAULT '',
                ADD COLUMN IF NOT EXISTS building_name text NOT NULL DEFAULT ''
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct));
    }

    /// <summary>Lists installed releases, including retained and unpromoted candidates.</summary>
    public async Task<IReadOnlyList<DatasetVersion>> ListAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<DatasetVersion>(new CommandDefinition(
            "SELECT source_version AS SourceVersion, schema_name AS SchemaName, status AS Status, promoted_at AS PromotedAt FROM platform.dataset_version ORDER BY created_at DESC",
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Lists release counts, geocoding coverage, and relation sizes for the portal.</summary>
    public async Task<IReadOnlyList<DatasetDetail>> ListDetailsAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<DatasetDetail>(new CommandDefinition("""
            SELECT source_version AS SourceVersion, dataset_name AS DatasetName,
                   status AS Status, scope AS Scope,
                   (quality_report->>'Addresses')::bigint AS AddressCount,
                   pg_total_relation_size(to_regclass(schema_name || '.address_search')) AS TableBytes,
                   file_size AS ArchiveBytes, retired_at AS RetiredAt,
                   validated_at AS ValidatedAt, promoted_at AS PromotedAt,
                   (quality_report->>'GeocodeCoverage')::double precision AS GeocodeCoverage,
                   error_message AS ErrorMessage
            FROM platform.dataset_version ORDER BY created_at DESC
            """, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Returns the total size of this PostgreSQL database on one node.</summary>
    public async Task<long> DatabaseBytesAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT pg_database_size(current_database())", cancellationToken: ct));
    }

    /// <summary>Creates a synthetic READY release for disposable tests, without switching the live view.</summary>
    public async Task SeedAsync(string version, IReadOnlyList<AddressRecord> addresses, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[a-zA-Z0-9_-]{1,40}$"))
            throw new ArgumentException("Version must contain only letters, digits, underscore or hyphen", nameof(version));
        var schema = "gnaf_" + version.Replace('-', '_').ToLowerInvariant();
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The lock also serializes promotion and rollback across all instances.
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(714845933)", connection, transaction))
            await command.ExecuteNonQueryAsync(ct);
        var existing = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM platform.dataset_version WHERE source_version = @version", new { version }, transaction, cancellationToken: ct));
        if (existing is not null) throw new InvalidOperationException($"Release {version} already exists ({existing})");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO platform.dataset_version(id,dataset_name,source_version,schema_name,status,scope) VALUES (@id,'SYNTHETIC',@version,@schema,'CREATING_SCHEMA','SAMPLE')",
            new { id = Guid.NewGuid(), version, schema }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE SCHEMA {schema}", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE TABLE {schema}.address_search (LIKE platform.empty_address_search INCLUDING ALL)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='LOADING',updated_at=now() WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        await using (var writer = await connection.BeginBinaryImportAsync($"COPY {schema}.address_search (gnaf_pid,formatted_address,flat_number,number_first,street_name,street_type,locality_name,state,postcode,latitude,longitude,search_text,flat_type,flat_number_prefix,flat_number_suffix,level_type,level_number,level_number_prefix,level_number_suffix,building_name) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var row in addresses)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(row.GnafPid, ct);
                await writer.WriteAsync(row.FormattedAddress, ct);
                await writer.WriteAsync(row.FlatNumber, ct);
                await writer.WriteAsync(row.NumberFirst, ct);
                await writer.WriteAsync(row.StreetName, ct);
                await writer.WriteAsync(row.StreetType, ct);
                await writer.WriteAsync(row.LocalityName, ct);
                await writer.WriteAsync(row.State, ct);
                await writer.WriteAsync(row.Postcode, ct);
                if (row.Latitude is double latitude) await writer.WriteAsync(latitude, ct);
                else await writer.WriteNullAsync(ct);
                if (row.Longitude is double longitude) await writer.WriteAsync(longitude, ct);
                else await writer.WriteNullAsync(ct);
                await writer.WriteAsync(row.SearchText, ct);
                await writer.WriteAsync(row.FlatType, ct);
                await writer.WriteAsync(row.FlatNumberPrefix, ct);
                await writer.WriteAsync(row.FlatNumberSuffix, ct);
                await writer.WriteAsync(row.LevelType, ct);
                await writer.WriteAsync(row.LevelNumber, ct);
                await writer.WriteAsync(row.LevelNumberPrefix, ct);
                await writer.WriteAsync(row.LevelNumberSuffix, ct);
                await writer.WriteAsync(row.BuildingName, ct);
            }
            await writer.CompleteAsync(ct);
        }
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='BUILDING_INDEXES',loaded_at=now(),updated_at=now() WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_trgm ON {schema}.address_search USING gin (search_text gin_trgm_ops)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_prefix ON {schema}.address_search (search_text text_pattern_ops)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_postcode ON {schema}.address_search (postcode)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_number_first ON {schema}.address_search (number_first)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_street_prefix ON {schema}.address_search (street_name text_pattern_ops)", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition($"CREATE INDEX address_search_street_trgm ON {schema}.address_search USING gin (street_name gin_trgm_ops)", transaction: transaction, cancellationToken: ct));
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {schema}.address_search", transaction: transaction, cancellationToken: ct));
        if (count < 1) throw new InvalidOperationException("Candidate has no addresses");
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='READY',validated_at=now(),updated_at=now() WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>Atomically switches the stable API view to a validated candidate and retains one rollback release.</summary>
    public async Task PromoteAsync(string version, CancellationToken ct)
    {
        // The advisory lock serializes release switches with rollback and raw
        // pruning. The view change and release-state updates commit together,
        // so API requests observe either the old release or the new release.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
        var runningJobs = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.bulk_job WHERE status='RUNNING'", transaction: transaction, cancellationToken: ct));
        if (runningJobs > 0) throw new InvalidOperationException("Wait for running bulk jobs before switching datasets");
        var candidate = await connection.QuerySingleOrDefaultAsync<DatasetVersion>(new CommandDefinition(
            "SELECT source_version AS SourceVersion, schema_name AS SchemaName, status AS Status, promoted_at AS PromotedAt FROM platform.dataset_version WHERE source_version=@version FOR UPDATE",
            new { version }, transaction, cancellationToken: ct));
        if (candidate is null || candidate.Status != "READY") throw new InvalidOperationException("Candidate is not READY");
        await PrepareServingTableAsync(connection, transaction, candidate.SchemaName, ct);
        await connection.ExecuteAsync(new CommandDefinition($"CREATE OR REPLACE VIEW api.address_search AS SELECT {ServingColumns} FROM {candidate.SchemaName}.address_search",
            transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='RETIRED',retired_at=now(),updated_at=now() WHERE status='ACTIVE'",
            transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='ACTIVE',promoted_at=now(),updated_at=now() WHERE source_version=@version",
            new { version }, transaction, cancellationToken: ct));
        await PruneCompletedRawAsync(connection, transaction, ct);
        var obsolete = (await connection.QueryAsync<ReleaseSchema>(new CommandDefinition("""
            SELECT source_version AS SourceVersion, schema_name AS SchemaName
            FROM platform.dataset_version
            WHERE dataset_name='G-NAF' AND scope='ALL' AND status='RETIRED'
            ORDER BY retired_at DESC, created_at DESC OFFSET 1
            FOR UPDATE
            """, transaction: transaction, cancellationToken: ct))).ToArray();
        foreach (var release in obsolete)
        {
            var expected = "gnaf_" + release.SourceVersion.Replace('-', '_').ToLowerInvariant();
            if (release.SchemaName != expected || !Regex.IsMatch(expected, "^[a-z0-9_]+$"))
                throw new InvalidOperationException("Unsafe G-NAF schema name in release metadata");
            await connection.ExecuteAsync(new CommandDefinition($"DROP SCHEMA {release.SchemaName} CASCADE",
                transaction: transaction, cancellationToken: ct, commandTimeout: 0));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM platform.street_alias WHERE source_version=@version; DELETE FROM platform.locality_alias WHERE source_version=@version",
                new { version = release.SourceVersion }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM platform.dataset_version WHERE source_version=@version",
                new { version = release.SourceVersion }, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>Atomically restores the most recently retired national release as the serving view.</summary>
    public async Task RollbackAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)", transaction: transaction, cancellationToken: ct));
        var runningJobs = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.bulk_job WHERE status='RUNNING'", transaction: transaction, cancellationToken: ct));
        if (runningJobs > 0) throw new InvalidOperationException("Wait for running bulk jobs before switching datasets");
        var prior = await connection.QuerySingleOrDefaultAsync<DatasetVersion>(new CommandDefinition(
            """
            SELECT d.source_version AS SourceVersion, d.schema_name AS SchemaName,
                   d.status AS Status, d.promoted_at AS PromotedAt
            FROM platform.dataset_version d
            JOIN platform.dataset_version a ON a.status='ACTIVE'
                AND d.dataset_name=a.dataset_name AND d.scope=a.scope
            WHERE d.status='RETIRED' ORDER BY d.retired_at DESC LIMIT 1 FOR UPDATE OF d
            """,
            transaction: transaction, cancellationToken: ct));
        if (prior is null) throw new InvalidOperationException("No retained release to roll back to");
        await PrepareServingTableAsync(connection, transaction, prior.SchemaName, ct);
        await connection.ExecuteAsync(new CommandDefinition($"CREATE OR REPLACE VIEW api.address_search AS SELECT {ServingColumns} FROM {prior.SchemaName}.address_search",
            transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='RETIRED',retired_at=now(),updated_at=now() WHERE status='ACTIVE'",
            transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE platform.dataset_version SET status='ACTIVE',promoted_at=now(),retired_at=NULL,updated_at=now() WHERE source_version=@version",
            new { version = prior.SourceVersion }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>Removes one retained national release without changing the live API view.</summary>
    public async Task DeleteRetiredNationalAsync(string version, CancellationToken ct)
    {
        // Serialize with promotion and rollback, then recheck the target inside
        // the transaction. A stale dashboard must never delete a live release.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)",
            transaction: transaction, cancellationToken: ct));
        var preparing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.maintenance_job WHERE status IN ('QUEUED','RUNNING')",
            transaction: transaction, cancellationToken: ct));
        if (preparing > 0) throw new InvalidOperationException("Wait for release preparation to finish before deleting retained data");
        var release = await connection.QuerySingleOrDefaultAsync<DatasetVersion>(new CommandDefinition("""
            SELECT source_version AS SourceVersion, schema_name AS SchemaName,
                   status AS Status, promoted_at AS PromotedAt
            FROM platform.dataset_version
            WHERE source_version=@version AND dataset_name='G-NAF' AND scope='ALL' AND status='RETIRED'
            FOR UPDATE
            """, new { version }, transaction, cancellationToken: ct));
        if (release is null) throw new InvalidOperationException("Only a previous national release can be deleted");
        var expected = "gnaf_" + release.SourceVersion.Replace('-', '_').ToLowerInvariant();
        if (release.SchemaName != expected || !Regex.IsMatch(expected, "^[a-z0-9_]+$"))
            throw new InvalidOperationException("Unsafe G-NAF schema name in release metadata");
        // Even if release metadata were corrupted, refuse to cascade through
        // the stable API view. Its dependency is independent of the status row.
        var servesApi = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM pg_depend dependency
                JOIN pg_rewrite rule ON rule.oid=dependency.objid
                WHERE rule.ev_class='api.address_search'::regclass
                  AND dependency.refobjid=to_regclass(@schema || '.address_search')
            )
            """, new { schema = release.SchemaName }, transaction, cancellationToken: ct));
        if (servesApi) throw new InvalidOperationException("The selected release still serves the API");
        await connection.ExecuteAsync(new CommandDefinition($"DROP SCHEMA {release.SchemaName} CASCADE",
            transaction: transaction, cancellationToken: ct, commandTimeout: 0));
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM platform.street_alias WHERE source_version=@version;
            DELETE FROM platform.locality_alias WHERE source_version=@version;
            DELETE FROM platform.dataset_version WHERE source_version=@version
            """, new { version }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }
}
