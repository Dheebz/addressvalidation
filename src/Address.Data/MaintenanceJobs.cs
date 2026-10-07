using Dapper;

namespace Address.Data;

/// <summary>Durable preparation state shown in the admin portal.</summary>
public sealed record MaintenanceJob(Guid Id, string SourceVersion, string SourceUrl,
    string SourceFilename, long? SourceSize, string Status, string Stage,
    string? ErrorMessage, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt);

/// <summary>Serializes and reports release preparation jobs across admin replicas.</summary>
public sealed class MaintenanceJobs(Database database)
{
    private const string Columns = "id AS Id,source_version AS SourceVersion,source_url AS SourceUrl,source_filename AS SourceFilename,source_size AS SourceSize,status AS Status,stage AS Stage,error_message AS ErrorMessage,created_at AS CreatedAt,started_at AS StartedAt,completed_at AS CompletedAt";

    /// <summary>Returns the most recently queued preparation, including terminal jobs.</summary>
    public async Task<MaintenanceJob?> LatestAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MaintenanceJob>(new CommandDefinition(
            $"SELECT {Columns} FROM platform.maintenance_job ORDER BY created_at DESC LIMIT 1", cancellationToken: ct));
    }

    /// <summary>Returns importer progress for the version currently being prepared.</summary>
    public async Task<string?> DatasetStageAsync(string version, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM platform.dataset_version WHERE source_version=@version",
            new { version }, cancellationToken: ct));
    }

    /// <summary>Queues a release only when no other preparation is open.</summary>
    public async Task<Guid> QueueAsync(GnafRelease release, CancellationToken ct)
    {
        // The lock and the partial unique index enforce one open preparation
        // across admin replicas, even when two operators click simultaneously.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(714845935)", transaction: transaction, cancellationToken: ct));
        var installed = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT status FROM platform.dataset_version WHERE source_version=@version",
            new { version = release.Version }, transaction, cancellationToken: ct));
        if (installed is "READY" or "ACTIVE" or "RETIRED")
            throw new InvalidOperationException($"Release {release.Version} is already installed");
        var open = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM platform.maintenance_job WHERE status IN ('QUEUED','RUNNING')",
            transaction: transaction, cancellationToken: ct));
        if (open > 0) throw new InvalidOperationException("An update is already being prepared");
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO platform.maintenance_job
                (id,source_version,source_url,source_filename,source_size,status,stage)
            VALUES (@id,@version,@url,@filename,@size,'QUEUED','WAITING')
            """, new
        {
            id,
            version = release.Version,
            url = release.Url.ToString(),
            filename = release.Filename,
            size = release.Size
        }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>Marks jobs left RUNNING by a prior admin process as retryable failures.</summary>
    public async Task MarkInterruptedAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.maintenance_job SET status='FAILED',stage='INTERRUPTED',
                error_message='The admin service stopped during preparation. Retry this release to resume the download and rebuild the candidate.',
                completed_at=now() WHERE status='RUNNING'
            """, cancellationToken: ct));
    }

    /// <summary>Claims the oldest queued release for a background worker.</summary>
    public async Task<MaintenanceJob?> ClaimAsync(CancellationToken ct)
    {
        // SKIP LOCKED lets a second worker remain idle instead of claiming the
        // same job while the first worker transitions it to RUNNING.
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MaintenanceJob>(new CommandDefinition($"""
            UPDATE platform.maintenance_job SET status='RUNNING',stage='DOWNLOADING',started_at=now()
            WHERE id=(SELECT id FROM platform.maintenance_job WHERE status='QUEUED'
                      ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING {Columns}
            """, cancellationToken: ct));
    }

    /// <summary>Publishes the current preparation stage for portal progress.</summary>
    public async Task SetStageAsync(Guid id, string stage, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE platform.maintenance_job SET stage=@stage WHERE id=@id AND status='RUNNING'",
            new { id, stage }, cancellationToken: ct));
    }

    /// <summary>Marks preparation complete after the importer reports READY.</summary>
    public async Task CompleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE platform.maintenance_job SET status='COMPLETED',stage='READY',completed_at=now() WHERE id=@id",
            new { id }, cancellationToken: ct));
    }

    /// <summary>Stores a bounded failure message for operator retry guidance.</summary>
    public async Task FailAsync(Guid id, string error, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE platform.maintenance_job SET status='FAILED',stage='FAILED',
                error_message=@error,completed_at=now() WHERE id=@id
            """, new { id, error = error[..Math.Min(error.Length, 2000)] }, cancellationToken: ct));
    }
}
