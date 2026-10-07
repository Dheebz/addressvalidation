using Azure.Storage.Blobs;
using Dapper;
using Npgsql;

namespace Address.Data;

/// <summary>Durable queue metadata; input and result bodies live in object storage.</summary>
public sealed record BulkJob(Guid Id, string Operation, string Status, string InputBlobUri,
    string? OutputBlobUri, long? TotalRecords, long ProcessedRecords, long FailedRecords,
    string? ErrorMessage, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt,
    double? MinScore, int? SuggestionLimit, string? RequestedBy, string? AccessTokenHash);

/// <summary>Bounds how long completed bulk results and their metadata are kept.</summary>
public static class BulkRetention
{
    /// <summary>Default retention for a fresh installation.</summary>
    public const int DefaultHours = 24;
    /// <summary>Limits values accepted from the administrator portal.</summary>
    public static int ValidateHours(int hours) => hours is >= 1 and <= 168 ? hours :
        throw new ArgumentOutOfRangeException(nameof(hours), "Bulk retention must be 1 to 168 hours");
}

/// <summary>Reads and updates the retention policy shared by all running services.</summary>
public sealed class BulkRetentionSettings(Database database)
{
    /// <summary>Returns the current database-backed retention period.</summary>
    public async Task<int> GetHoursAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT retention_hours FROM platform.bulk_retention_settings WHERE singleton=true",
            cancellationToken: ct));
    }

    /// <summary>Applies a new period immediately to existing and future jobs.</summary>
    public async Task SetHoursAsync(int hours, string actor, CancellationToken ct)
    {
        BulkRetention.ValidateHours(hours);
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_retention_settings
            SET retention_hours=@hours, updated_at=now(), updated_by=@actor
            WHERE singleton=true
            """, new { hours, actor }, cancellationToken: ct));
    }
}

/// <summary>Coordinates bulk workers through durable PostgreSQL job states.</summary>
public sealed class BulkJobRepository(Database database)
{
    private const string Columns = """
        id AS Id, operation AS Operation, status AS Status, input_blob_uri AS InputBlobUri,
        output_blob_uri AS OutputBlobUri, total_records AS TotalRecords,
        processed_records AS ProcessedRecords, failed_records AS FailedRecords,
        error_message AS ErrorMessage, created_at AS CreatedAt,
        started_at AS StartedAt, completed_at AS CompletedAt,
        suggest_min_score AS MinScore, suggest_limit AS SuggestionLimit,
        requested_by AS RequestedBy, access_token_hash AS AccessTokenHash
        """;

    /// <summary>Queues a job after its input blob has been stored.</summary>
    public async Task CreateAsync(Guid id, string operation, string inputUri, CancellationToken ct,
        double? minScore = null, int? suggestionLimit = null, string? requestedBy = null,
        string? accessTokenHash = null)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO platform.bulk_job(id,operation,status,input_blob_uri,suggest_min_score,suggest_limit,requested_by,access_token_hash)
            VALUES (@id,@operation,'QUEUED',@inputUri,@minScore,@suggestionLimit,@requestedBy,@accessTokenHash)
            """, new { id, operation, inputUri, minScore, suggestionLimit, requestedBy, accessTokenHash }, cancellationToken: ct));
    }

    /// <summary>Reads progress and terminal status for one job; the API checks caller access.</summary>
    public async Task<BulkJob?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<BulkJob>(new CommandDefinition(
            $"SELECT {Columns} FROM platform.bulk_job WHERE id=@id", new { id }, cancellationToken: ct));
    }

    /// <summary>Requeues stale work and atomically claims one available job.</summary>
    public async Task<BulkJob?> AcquireAsync(CancellationToken ct)
    {
        // Claim and stale-job recovery share a transaction so two workers
        // cannot process the same queued job after a worker restart.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845933)",
            transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_job SET status='QUEUED',started_at=NULL
            WHERE status='RUNNING' AND heartbeat_at < now() - interval '5 minutes'
            """, transaction: transaction, cancellationToken: ct));
        // A stale job starts again from its immutable input blob. The row lock
        // prevents two new claimants from selecting the same queued job.
        var job = await connection.QuerySingleOrDefaultAsync<BulkJob>(new CommandDefinition($"""
            SELECT {Columns} FROM platform.bulk_job WHERE status='QUEUED'
            ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """, transaction: transaction, cancellationToken: ct));
        if (job is not null)
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE platform.bulk_job SET status='RUNNING',started_at=now(),heartbeat_at=now()
                WHERE id=@id
                """, new { job.Id }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return job;
    }

    /// <summary>Updates counters and heartbeat while a worker still owns the job.</summary>
    public async Task ProgressAsync(Guid id, long processed, long failed, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_job SET processed_records=@processed,failed_records=@failed,
                heartbeat_at=now() WHERE id=@id AND status='RUNNING'
            """, new { id, processed, failed }, cancellationToken: ct));
    }

    /// <summary>Publishes the completed output blob location after the stream is closed.</summary>
    public async Task CompleteAsync(Guid id, string outputUri, long processed, long failed, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_job SET status='COMPLETED',output_blob_uri=@outputUri,
                total_records=@processed,processed_records=@processed,failed_records=@failed,
                completed_at=now(),heartbeat_at=now() WHERE id=@id AND status='RUNNING'
            """, new { id, outputUri, processed, failed }, cancellationToken: ct));
    }

    /// <summary>Stores a bounded diagnostic message for a failed running job.</summary>
    public async Task FailAsync(Guid id, string error, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_job SET status='FAILED',error_message=@error,completed_at=now(),
                heartbeat_at=now() WHERE id=@id AND status='RUNNING'
            """, new { id, error = error[..Math.Min(error.Length, 2000)] }, cancellationToken: ct));
    }

    /// <summary>Stops work abandoned beyond retention without interrupting an active worker.</summary>
    public async Task<int> FailAbandonedAsync(DateTime cutoff, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.bulk_job
            SET status='FAILED', completed_at=created_at,
                error_message='Job expired before completion'
            WHERE created_at < @cutoff AND
              (status='QUEUED' OR
               (status='RUNNING' AND heartbeat_at < now() - interval '5 minutes'))
            """, new { cutoff }, cancellationToken: ct));
    }

    /// <summary>Selects only terminal jobs past their configured retention period.</summary>
    public async Task<IReadOnlyList<BulkJob>> ExpiredAsync(DateTime cutoff, int limit, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BulkJob>(new CommandDefinition($"""
            SELECT {Columns} FROM platform.bulk_job
            WHERE status IN ('COMPLETED','FAILED') AND completed_at < @cutoff
            ORDER BY completed_at LIMIT @limit
            """, new { cutoff, limit }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Deletes metadata only if the job is still terminal and expired.</summary>
    public async Task DeleteExpiredAsync(Guid id, DateTime cutoff, CancellationToken ct)
    {
        // Recheck terminal state and cutoff during deletion in case another
        // process changed the job after the retention sweep selected it.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM platform.bulk_job WHERE id=@id
            AND status IN ('COMPLETED','FAILED') AND completed_at < @cutoff
            """, new { id, cutoff }, cancellationToken: ct));
    }
}

/// <summary>Blob operations needed by both HTTP intake and the background worker.</summary>
public interface IObjectStorage
{
    /// <summary>Uploads one immutable input object before the job is queued.</summary>
    Task UploadAsync(string name, Stream content, CancellationToken ct);
    /// <summary>Streams an existing input or completed result.</summary>
    Task<Stream> OpenReadAsync(string name, CancellationToken ct);
    /// <summary>Opens a result stream that the worker will close before completion.</summary>
    Task<Stream> OpenWriteAsync(string name, CancellationToken ct);
    /// <summary>Removes an object if present, allowing cleanup retries.</summary>
    Task DeleteIfExistsAsync(string name, CancellationToken ct);
}

/// <summary>Azure Blob compatible storage implementation, backed by Azurite locally.</summary>
public sealed class BlobObjectStorage(string connectionString) : IObjectStorage
{
    private readonly BlobContainerClient container = new(connectionString, "bulk-jobs");

    /// <summary>Stores a new input object; duplicate names are rejected.</summary>
    public async Task UploadAsync(string name, Stream content, CancellationToken ct)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        await container.GetBlobClient(name).UploadAsync(content, overwrite: false, cancellationToken: ct);
    }

    /// <summary>Opens an existing input or output object for streaming reads.</summary>
    public async Task<Stream> OpenReadAsync(string name, CancellationToken ct) =>
        await container.GetBlobClient(name).OpenReadAsync(cancellationToken: ct);

    /// <summary>Creates or replaces an output object for a worker retry.</summary>
    public async Task<Stream> OpenWriteAsync(string name, CancellationToken ct)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return await container.GetBlobClient(name).OpenWriteAsync(overwrite: true, cancellationToken: ct);
    }

    /// <summary>Idempotently removes a result or input object during cleanup.</summary>
    public async Task DeleteIfExistsAsync(string name, CancellationToken ct) =>
        await container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: ct);
}
