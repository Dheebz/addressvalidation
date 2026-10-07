using Address.Data;

namespace Gnaf.Admin;

/// <summary>Last completed local storage sweep, displayed separately from live database health.</summary>
public sealed record HousekeepingSnapshot(DateTime? CheckedAtUtc, string State, long? CacheBytes,
    long? AvailableBytes, long? TotalBytes, int RemovedFiles, string Detail);

/// <summary>Reconciles abandoned source archives and checks the local data volume hourly.</summary>
public sealed class HousekeepingService(DatasetLifecycle releases, MaintenanceJobs jobs,
    IConfiguration configuration, ILogger<HousekeepingService> logger) : BackgroundService
{
    private readonly string cacheDirectory = configuration["Source:CacheDirectory"] ??
        Path.Combine(Path.GetTempPath(), "gnaf-cache");
    private HousekeepingSnapshot snapshot = new(null, "pending", null, null, null, 0,
        "The first storage check has not finished yet.");

    public HousekeepingSnapshot Snapshot => Volatile.Read(ref snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var job = await jobs.LatestAsync(ct);
            var preparing = job?.Status is "QUEUED" or "RUNNING";
            var removed = 0;
            if (!preparing)
            {
                // The age guard also protects a loader started outside this portal.
                var installed = await releases.ListAsync(ct);
                removed = GnafCacheMaintenance.RemoveObsolete(cacheDirectory, installed, TimeSpan.FromHours(24));
            }

            var cacheBytes = Directory.Exists(cacheDirectory)
                ? Directory.EnumerateFiles(cacheDirectory).Sum(path => new FileInfo(path).Length) : 0;
            // Ask about the cache mount itself; the container root can live on
            // a different filesystem from the database and cache volumes.
            var drive = new DriveInfo(cacheDirectory);
            var available = drive.AvailableFreeSpace;
            var total = drive.TotalSize;
            var lowDisk = available < 5L * 1024 * 1024 * 1024 || available < total / 10;
            var state = lowDisk ? "attention" : "healthy";
            var detail = lowDisk ? "Local storage is running low. Free space or expand the database volume." :
                preparing ? "Release preparation is running; archive cleanup will resume on the next check." :
                "Local storage checked. Expired source archives are removed automatically.";
            Volatile.Write(ref snapshot, new HousekeepingSnapshot(DateTime.UtcNow, state,
                cacheBytes, available, total, removed, detail));
            if (lowDisk) logger.LogWarning("Local storage low: {AvailableBytes} bytes available", available);
            else if (removed > 0) logger.LogInformation("Removed {Count} obsolete G-NAF cache files", removed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local storage maintenance failed; retrying on the next check");
            Volatile.Write(ref snapshot, new HousekeepingSnapshot(DateTime.UtcNow, "attention", null,
                null, null, 0, "Storage check failed. Review the admin service logs."));
        }
    }
}
