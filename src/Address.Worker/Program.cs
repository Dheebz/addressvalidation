using System.Text;
using System.Text.Json;
using Address.Core;
using Address.Data;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new Database(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string is required")));
builder.Services.AddSingleton<IAddressRepository, PostgresAddressRepository>();
builder.Services.AddSingleton<AddressService>();
builder.Services.AddSingleton<BulkJobRepository>();
builder.Services.AddSingleton<BulkRetentionSettings>();
builder.Services.AddSingleton<IObjectStorage>(_ => new BlobObjectStorage(builder.Configuration["Storage:ConnectionString"]
    ?? throw new InvalidOperationException("Storage connection string is required")));
builder.Services.AddHostedService<QueueWorker>();
await builder.Build().RunAsync();

/// <summary>Streams bulk input rows through the same matching service used by synchronous requests.</summary>
public sealed class QueueWorker(ILogger<QueueWorker> logger, Database database, BulkJobRepository jobs,
    IObjectStorage storage, AddressService addresses, IAddressRepository repository,
    BulkRetentionSettings retentionSettings) : BackgroundService
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private DateTime nextCleanupUtc = DateTime.MinValue;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The periodic sweep shares the worker loop but is isolated from job
        // processing: a cleanup failure is logged and retried next minute.
        await database.MigrateAsync(stoppingToken);
        logger.LogInformation("Bulk worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextCleanupUtc)
            {
                nextCleanupUtc = DateTime.UtcNow.AddMinutes(1);
                try { await CleanupExpiredAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Bulk retention sweep failed; retrying in one minute"); }
            }
            var job = await jobs.AcquireAsync(stoppingToken);
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk job {JobId} failed", job.Id);
                await jobs.FailAsync(job.Id, ex.Message, stoppingToken);
            }
        }
    }

    private async Task CleanupExpiredAsync(CancellationToken ct)
    {
        var hours = await retentionSettings.GetHoursAsync(ct);
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        var abandoned = await jobs.FailAbandonedAsync(cutoff, ct);
        if (abandoned > 0)
            logger.LogWarning("Expired {Count} abandoned bulk jobs", abandoned);
        var expired = await jobs.ExpiredAsync(cutoff, 100, ct);
        foreach (var job in expired)
        {
            try
            {
                // Blob names are derived from the job ID, including an output left by
                // a failed worker before it recorded output_blob_uri. Deletion is
                // idempotent, so a restart or second worker can retry safely.
                await storage.DeleteIfExistsAsync(job.InputBlobUri, ct);
                await storage.DeleteIfExistsAsync($"output/{job.Id:N}.ndjson", ct);
                await jobs.DeleteExpiredAsync(job.Id, cutoff, ct);
                logger.LogInformation("Expired bulk job {JobId} and its blobs", job.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not expire bulk job {JobId}; will retry", job.Id); }
        }
    }

    private async Task ProcessAsync(BulkJob job, CancellationToken ct)
    {
        // Release promotion waits for running bulk jobs, so this version remains stable
        // for every output row without another metadata query per address.
        var dataset = await repository.ActiveDatasetAsync(ct);
        var outputName = $"output/{job.Id:N}.ndjson";
        await using var source = await storage.OpenReadAsync(job.InputBlobUri, ct);
        await using var destination = await storage.OpenWriteAsync(outputName, ct);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 65536, leaveOpen: true);
        long processed = 0, failed = 0;
        // Process one NDJSON line at a time. Invalid rows become row-level
        // results rather than failing the entire uploaded job.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            processed++;
            object result;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Expected a JSON object");
                var field = job.Operation == "ID" ? "id" : "address";
                if (!document.RootElement.TryGetProperty(field, out var value) ||
                    value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    throw new InvalidDataException($"Expected a non-empty {field} string");
                var input = value.GetString()!;
                if (input.Length > 500) throw new InvalidDataException("Input exceeds 500 characters");
                string? reference = null;
                if (job.Operation == "SUGGEST" && document.RootElement.TryGetProperty("reference", out var sourceReference))
                {
                    if (sourceReference.ValueKind != JsonValueKind.String || sourceReference.GetString() is not { Length: <= 200 } parsed)
                        throw new InvalidDataException("reference must be a string of at most 200 characters");
                    reference = parsed;
                }
                result = await ProcessRowAsync(job, processed, input, reference, dataset, ct);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            {
                failed++;
                result = new { row = processed, status = "INVALID_INPUT", error = ex.Message };
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(result, OutputJson).AsMemory(), ct);
            // Persist progress in batches, while still writing one NDJSON
            // result per input line (including invalid lines).
            if (processed % 10 == 0)
            {
                await writer.FlushAsync(ct);
                await jobs.ProgressAsync(job.Id, processed, failed, ct);
            }
        }
        await writer.FlushAsync(ct);
        await writer.DisposeAsync();
        await destination.DisposeAsync();
        // Mark completion only after the output stream is fully flushed and
        // closed, so clients never download a partial result as completed.
        await jobs.CompleteAsync(job.Id, outputName, processed, failed, ct);
        logger.LogInformation("Bulk job {JobId} completed: {Processed} rows, {Failed} invalid", job.Id, processed, failed);
    }

    private async Task<object> ProcessRowAsync(BulkJob job, long row, string input,
        string? reference, DatasetMetadata? dataset, CancellationToken ct)
    {
        switch (job.Operation)
        {
            case "SUGGEST":
                // A score threshold filters review candidates; it never turns
                // a suggestion into an asserted, validated address match.
                var suggestions = await addresses.SuggestAsync(input, job.SuggestionLimit ?? 5, ct);
                var candidates = suggestions.Where(item => item.Score >= (job.MinScore ?? 0))
                    .Select(item => new
                    {
                        id = item.Id,
                        formattedAddress = item.Label,
                        score = item.Score,
                        components = AddressComponents.From(item.Address),
                        location = new { latitude = item.Address.Latitude, longitude = item.Address.Longitude }
                    }).ToArray();
                return new
                {
                    row,
                    reference,
                    input,
                    status = candidates.Length == 0 ? "NO_SUGGESTIONS" : "SUGGESTED",
                    suggestions = candidates,
                    datasetVersion = dataset?.Version,
                    postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED"
                };
            case "ID":
                var found = await addresses.GetByIdAsync(input, ct);
                return new
                {
                    row,
                    status = found is null ? "NOT_FOUND" : "FOUND",
                    id = found?.GnafPid,
                    formattedAddress = found?.FormattedAddress,
                    components = found is null ? null : AddressComponents.From(found),
                    location = found is null ? null : new { latitude = found.Latitude, longitude = found.Longitude },
                    source = new { dataset = dataset?.Name, version = dataset?.Version },
                    postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED"
                };
            case "MATCH":
                var match = await addresses.MatchAsync(input, ct);
                var candidate = match.MatchType == "SUBADDRESS_UNCONFIRMED" ? null : match.Address;
                return new
                {
                    row,
                    status = match.MatchType,
                    matchType = match.MatchType,
                    id = candidate?.GnafPid,
                    formattedAddress = candidate?.FormattedAddress,
                    components = candidate is null ? null : AddressComponents.From(candidate),
                    location = candidate is null ? null : new { latitude = candidate.Latitude, longitude = candidate.Longitude },
                    confidence = match.Confidence,
                    datasetVersion = dataset?.Version,
                    postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED"
                };
            case "VALIDATE":
                var validation = await addresses.ValidateAsync(input, ct);
                return new
                {
                    row,
                    status = validation.Status,
                    valid = validation.Valid,
                    id = validation.Address?.GnafPid,
                    formattedAddress = validation.Address?.FormattedAddress,
                    components = validation.Address is null ? null : AddressComponents.From(validation.Address),
                    location = validation.Address is null ? null : new { latitude = validation.Address.Latitude, longitude = validation.Address.Longitude },
                    confidence = validation.Confidence,
                    datasetVersion = dataset?.Version,
                    postalDeliveryStatus = validation.PostalDeliveryStatus
                };
            case "FORMAT":
                var formatted = await addresses.FormatAsync(input, ct);
                return new
                {
                    row,
                    status = formatted.Matched ? "GNAF_FORMATTED" : "NORMALISED",
                    matched = formatted.Matched,
                    matchType = formatted.MatchType,
                    formattedAddress = formatted.Formatted,
                    id = formatted.GnafPid,
                    components = formatted.Address is null ? null : AddressComponents.From(formatted.Address),
                    location = formatted.Address is null ? null : new { latitude = formatted.Address.Latitude, longitude = formatted.Address.Longitude },
                    confidence = formatted.Confidence,
                    datasetVersion = dataset?.Version,
                    postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED"
                };
            default:
                throw new InvalidOperationException($"Unsupported operation {job.Operation}");
        }
    }
}
