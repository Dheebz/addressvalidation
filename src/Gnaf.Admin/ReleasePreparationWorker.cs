using Address.Data;

namespace Gnaf.Admin;

/// <summary>Prepares a release in the background but never activates it without an operator action.</summary>
public sealed class ReleasePreparationWorker(
    MaintenanceJobs jobs, AdminEvents events, GnafArchiveImporter importer, IGnafSourceProvider source,
    IConfiguration configuration, ILogger<ReleasePreparationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A container restart cannot resume an in-flight import at an arbitrary
        // COPY/index stage. Mark it interrupted so an operator can retry cleanly.
        await jobs.MarkInterruptedAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            // The database claim is the coordination point if a second admin
            // replica runs this hosted worker later.
            var job = await jobs.ClaimAsync(stoppingToken);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            try
            {
                var release = new GnafRelease(job.SourceVersion, new Uri(job.SourceUrl),
                    job.SourceFilename, job.SourceSize, null, null);
                var cache = configuration["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache");
                var (path, metadata) = await source.DownloadAsync(release, cache, stoppingToken);
                await jobs.SetStageAsync(job.Id, "CHECKING_SCHEMA", stoppingToken);
                await importer.PreflightAsync(job.SourceVersion, path, "ALL", metadata.Filename, stoppingToken);
                // A historical replay may have completed every import stage
                // under the old forward-upgrade count rule. Recheck that exact
                // archive's built candidate instead of repeating a national COPY.
                if (await importer.TryRevalidateFailedHistoricalAsync(job.SourceVersion,
                    metadata.Checksum, ct: stoppingToken) is not null)
                {
                    await jobs.CompleteAsync(job.Id, stoppingToken);
                    await RecordOutcomeAsync(job.SourceVersion, "SUCCEEDED", "Historical release revalidated and ready for review");
                    continue;
                }
                await jobs.SetStageAsync(job.Id, "IMPORTING", stoppingToken);
                // An interrupted candidate can contain partial raw tables;
                // reset only that candidate before replaying its import.
                await importer.ResetIncompleteCandidateAsync(job.SourceVersion, stoppingToken);
                await importer.ImportAsync(job.SourceVersion, path, "ALL", source: metadata, ct: stoppingToken);
                await jobs.CompleteAsync(job.Id, stoppingToken);
                await RecordOutcomeAsync(job.SourceVersion, "SUCCEEDED", "Release prepared and ready for review");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to prepare G-NAF release {Version}", job.SourceVersion);
                await jobs.FailAsync(job.Id, ex.Message, CancellationToken.None);
                await RecordOutcomeAsync(job.SourceVersion, "FAILED", "Release preparation failed");
            }
        }
    }

    private async Task RecordOutcomeAsync(string version, string outcome, string detail)
    {
        try
        {
            await events.RecordAsync("system", "PREPARE", outcome, version, "job:" + version,
                detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record preparation outcome for {Version}", version);
        }
    }
}
