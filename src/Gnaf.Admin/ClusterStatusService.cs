using System.Text.Json;
using System.Net.Sockets;
using Npgsql;

namespace Gnaf.Admin;

/// <summary>Direct observation of one PostgreSQL node, not a routed connection.</summary>
public sealed record DatabaseNodeStatus(string Name, string Host, string Role, bool Online,
    bool Streaming, long? ReplayLagBytes);

/// <summary>Result of calling one API replica's readiness endpoint directly.</summary>
public sealed record ApiReplicaStatus(string Name, string Host, bool Ready);

/// <summary>Operator-facing assessment of database failover readiness.</summary>
public sealed record ClusterStatus(IReadOnlyList<DatabaseNodeStatus> Databases,
    int CoordinatorsOnline, int CoordinatorsTotal, string State, string Headline, string Detail)
{
    /// <summary>True only when all local conditions for automatic database takeover are healthy.</summary>
    public bool AutomaticFailoverReady => State == "healthy";
}

/// <summary>Samples each local replica directly to explain failover readiness to operators.</summary>
public sealed class ClusterStatusService(IConfiguration configuration, HttpClient http)
{
    private readonly string[] hosts = (new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is required"))
        .Host ?? throw new InvalidOperationException("Postgres hosts are required"))
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private readonly string[] coordinators = (configuration["Cluster:Coordinators"] ?? "etcd-1,etcd-2,etcd-3")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private readonly string[] apiHosts = (configuration["Api:ReplicaHosts"] ?? "address-api,address-api-b")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Checks every configured API replica without passing through the gateway.</summary>
    public async Task<IReadOnlyList<ApiReplicaStatus>> CheckApiReplicasAsync(CancellationToken ct)
    {
        var checks = apiHosts.Select((host, index) => CheckApiReplicaAsync(host, index, ct)).ToArray();
        return await Task.WhenAll(checks);
    }

    private async Task<ApiReplicaStatus> CheckApiReplicaAsync(string host, int index, CancellationToken ct)
    {
        var name = index switch { 0 => "API A", 1 => "API B", _ => $"API {index + 1}" };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync($"http://{host}:8080/health/ready", timeout.Token);
            return new(name, host, response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is HttpRequestException or TimeoutException or OperationCanceledException))
        {
            return new(name, host, false);
        }
    }

    /// <summary>Combines direct database roles, replication state, and coordinator quorum.</summary>
    public async Task<ClusterStatus> CheckAsync(CancellationToken ct)
    {
        // Probe nodes and quorum members concurrently so an offline member
        // does not make the entire dashboard wait for serial timeouts.
        var nodeTasks = hosts.Select((host, index) => CheckNodeAsync(host, index, ct)).ToArray();
        var coordinatorTasks = coordinators.Select(host => CheckCoordinatorAsync(host, ct)).ToArray();
        await Task.WhenAll(nodeTasks.Cast<Task>().Concat(coordinatorTasks));
        var nodes = nodeTasks.Select(task => task.Result).ToArray();
        var coordinatorsOnline = coordinatorTasks.Count(task => task.Result);
        var primary = nodes.Where(node => node.Role == "primary").ToArray();
        var standby = nodes.Where(node => node.Role == "standby").ToArray();
        var quorum = coordinatorsOnline >= coordinators.Length / 2 + 1;

        // A healthy primary alone is not sufficient for automatic takeover:
        // streaming, standby availability, and etcd quorum must all agree.
        if (primary.Length > 1)
            return new(nodes, coordinatorsOnline, coordinators.Length, "critical", "Database conflict detected",
                "More than one database reports primary. Ask for technical support immediately.");
        if (nodes.All(node => !node.Online))
            return new(nodes, coordinatorsOnline, coordinators.Length, "unavailable", "Databases unavailable",
                "Neither database is responding. Address lookups are unavailable until a primary returns.");
        if (primary.Length == 0)
            return new(nodes, coordinatorsOnline, coordinators.Length, "failover", "Database takeover in progress",
                "Address lookups may pause while a database becomes primary.");
        if (hosts.Length == 1)
            return new(nodes, coordinatorsOnline, coordinators.Length, "single", "One database configured",
                "A second database is needed for automatic takeover.");
        if (!quorum)
            return new(nodes, coordinatorsOnline, coordinators.Length, "degraded", "Failover coordination needs attention",
                $"Only {coordinatorsOnline} of {coordinators.Length} coordinators are online. Address lookups may still work, but automatic takeover is not ready.");
        if (standby.Length != 1 || nodes.Any(node => !node.Online))
            return new(nodes, coordinatorsOnline, coordinators.Length, "degraded", "One database is serving",
                "The standby is unavailable. Address lookups work, but there is no ready database to take over.");
        if (!primary[0].Streaming)
            return new(nodes, coordinatorsOnline, coordinators.Length, "degraded", "Database copy is not current",
                "The standby is online but is not streaming updates from the primary.");
        if (primary[0].ReplayLagBytes is > 1_048_576)
            return new(nodes, coordinatorsOnline, coordinators.Length, "degraded", "Database copy is catching up",
                "The standby is more than 1 MiB behind the primary.");
        return new(nodes, coordinatorsOnline, coordinators.Length, "healthy", "Automatic takeover ready",
            "Both databases are online, the standby is receiving updates, and coordinator quorum is healthy.");
    }

    private async Task<DatabaseNodeStatus> CheckNodeAsync(string host, int index, CancellationToken ct)
    {
        var name = index switch { 0 => "Database A", 1 => "Database B", _ => $"Database {index + 1}" };
        try
        {
            // Connect to this host directly with pooling disabled. A pooled
            // multi-host connection would mask which physical node answered.
            var settings = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Postgres connection string is required"))
            {
                Host = host,
                Pooling = false,
                Timeout = 2,
                CommandTimeout = 2
            };
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await connection.OpenAsync(timeout.Token);
            await using var roleCommand = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
            var isStandby = (bool)(await roleCommand.ExecuteScalarAsync(timeout.Token) ?? true);
            if (isStandby) return new(name, host, "standby", true, false, null);

            await using var replication = new NpgsqlCommand("""
                SELECT state, pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)::bigint
                FROM pg_stat_replication WHERE application_name = @standby
                """, connection);
            replication.Parameters.AddWithValue("standby", hosts.FirstOrDefault(other => other != host) ?? "");
            await using var reader = await replication.ExecuteReaderAsync(timeout.Token);
            if (!await reader.ReadAsync(timeout.Token))
                return new(name, host, "primary", true, false, null);
            return new(name, host, "primary", true,
                reader.GetString(0) == "streaming", reader.IsDBNull(1) ? null : reader.GetInt64(1));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is NpgsqlException or SocketException or TimeoutException or OperationCanceledException))
        {
            return new(name, host, "offline", false, false, null);
        }
    }

    private async Task<bool> CheckCoordinatorAsync(string host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync($"http://{host}:2379/health", timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var value = body.RootElement.GetProperty("health");
            return value.ValueKind == JsonValueKind.True ||
                   value.ValueKind == JsonValueKind.String && value.GetString() == "true";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is HttpRequestException or JsonException or KeyNotFoundException or OperationCanceledException))
        {
            return false;
        }
    }
}
