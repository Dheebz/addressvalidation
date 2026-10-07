using Dapper;

namespace Address.Data;

/// <summary>Minimal operator action history without tokens or request bodies.</summary>
public sealed record AdminEvent(Guid Id, DateTime OccurredAt, string Actor, string Action,
    string Outcome, string? SourceVersion, string RequestId, string Detail);

/// <summary>Persists the outcome of operator actions, separate from transient HTTP logs.</summary>
public sealed class AdminEvents(Database database)
{
    /// <summary>Returns the latest 20 actions for the portal timeline.</summary>
    public async Task<IReadOnlyList<AdminEvent>> RecentAsync(CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AdminEvent>(new CommandDefinition("""
            SELECT id AS Id, occurred_at AS OccurredAt, actor AS Actor, action AS Action,
                   outcome AS Outcome, source_version AS SourceVersion,
                   request_id AS RequestId, detail AS Detail
            FROM platform.admin_event ORDER BY occurred_at DESC LIMIT 20
            """, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Records an outcome with request correlation for later diagnosis.</summary>
    public async Task RecordAsync(string actor, string action, string outcome, string? version,
        string requestId, string detail, CancellationToken ct)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO platform.admin_event
                (id,actor,action,outcome,source_version,request_id,detail)
            VALUES (@id,@actor,@action,@outcome,@version,@requestId,@detail)
            """, new { id = Guid.NewGuid(), actor, action, outcome, version, requestId, detail },
            cancellationToken: ct));
    }
}
