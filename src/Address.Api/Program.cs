using Address.Core;
using Address.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 104857600);
// Development auth is deliberately explicit and environment-gated. A
// non-development process must validate bearer tokens before serving data.
var isDevelopmentAuth = builder.Configuration["Authentication:Mode"] == "Development";
if (isDevelopmentAuth && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Development authentication is forbidden outside Development");
if (!isDevelopmentAuth && string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Authority"]))
    throw new InvalidOperationException("Production authentication requires an Entra authority");

builder.Services.AddSingleton(new Database(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string is required")));
builder.Services.AddSingleton<IAddressRepository, PostgresAddressRepository>();
builder.Services.AddSingleton<AddressService>();
builder.Services.AddSingleton<DatasetLifecycle>();
builder.Services.AddSingleton<BulkJobRepository>();
builder.Services.AddSingleton<BulkRetentionSettings>();
builder.Services.AddSingleton<IObjectStorage>(_ => new BlobObjectStorage(builder.Configuration["Storage:ConnectionString"]
    ?? throw new InvalidOperationException("Storage connection string is required")));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
var concurrentLookups = builder.Configuration.GetValue("ApiConcurrency:PerReplica", 16);
if (concurrentLookups is < 1 or > 256)
    throw new InvalidOperationException("ApiConcurrency:PerReplica must be between 1 and 256");
// Admission is per API process. The gateway spreads requests across replicas;
// each replica independently protects its own database connection capacity.
builder.Services.AddRateLimiter(options =>
{
    // Reject immediately when all lookup slots are occupied. A waiting queue
    // would turn overload into long client timeouts and hold open connections.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/v1/addresses")
            ? RateLimitPartition.GetConcurrencyLimiter("address-lookups", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = concurrentLookups,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })
            : RateLimitPartition.GetNoLimiter("other"));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejection, ct) =>
    {
        var context = rejection.HttpContext;
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = "API_BUSY",
                message = "Address lookups are busy. Retry shortly.",
                correlationId = context.TraceIdentifier
            }
        }, ct);
    };
});
if (!isDevelopmentAuth)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.Audience = builder.Configuration["Authentication:Audience"];
        options.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true };
    });
    builder.Services.AddAuthorization();
}

var app = builder.Build();
await app.Services.GetRequiredService<Database>().MigrateAsync();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSwagger();
// Swagger UI's embedded index otherwise stays cached across local rebuilds.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger"))
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    await next(context);
});
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "AddressDesk API documentation";
    options.InjectStylesheet("/swagger-nav.css?v=3");
    options.InjectJavascript("/swagger-nav.js?v=3");
});
if (!isDevelopmentAuth) { app.UseAuthentication(); app.UseAuthorization(); }
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    // Use one request identifier in success responses, overload replies and
    // datastore failures so callers can correlate logs across API replicas.
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    try
    {
        await next(context);
    }
    catch (NpgsqlException ex) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
    {
        app.Logger.LogWarning(ex, "Address datastore unavailable for {Path}", context.Request.Path);
        context.Response.Clear();
        context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
        context.Response.Headers.RetryAfter = "2";
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = "DATASTORE_UNAVAILABLE",
                message = "Address data is temporarily unavailable. Retry shortly.",
                correlationId = context.TraceIdentifier
            }
        });
    }
});

IResult Error(HttpContext context, string code, string message, int status) =>
    Results.Json(new { error = new { code, message, correlationId = context.TraceIdentifier } }, statusCode: status);

bool HasRole(HttpContext context, string role)
{
    // The local development mode is reachable only when the host environment
    // is Development. Production callers need the corresponding token claim.
    if (isDevelopmentAuth) return true;
    return context.User.Identity?.IsAuthenticated == true &&
        (context.User.HasClaim("roles", role) || context.User.HasClaim("scp", role));
}

string? BulkCaller(HttpContext context) => isDevelopmentAuth ? null :
    context.User.FindFirst("oid")?.Value ?? context.User.FindFirst("sub")?.Value ??
    context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

bool CanReadBulkJob(HttpContext context, BulkJob job)
{
    // The opaque token protects local anonymous use; a production caller must
    // also be the same signed-in identity that submitted the job.
    if (job.AccessTokenHash is not { Length: 64 }) return false;
    if (!isDevelopmentAuth && (BulkCaller(context) is not { } caller || caller != job.RequestedBy)) return false;
    var token = context.Request.Headers["X-Bulk-Job-Token"].ToString();
    if (string.IsNullOrEmpty(token))
        token = context.Request.Cookies[$"bulk-{job.Id:N}"] ?? "";
    if (token.Length != 64) return false;
    var actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(job.AccessTokenHash));
}

const string attribution = "Incorporates or developed using G-NAF © Geoscape Australia, licensed by the Commonwealth of Australia under the Open Geo-coded National Address File (G-NAF) End User Licence Agreement.";

app.MapGet("/health/live", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "live", service = "address-api" });
});
app.MapGet("/health/ready", async (HttpContext context, Database db, CancellationToken ct) =>
{
    // A sample or regional release is usable for tests, but cannot make the
    // national production address service report ready.
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        await using var connection = await db.DataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_version FROM platform.dataset_version WHERE status='ACTIVE' AND dataset_name='G-NAF' AND scope='ALL'";
        var version = await command.ExecuteScalarAsync(ct) as string;
        if (version is null)
            return Results.Json(new { status = "not_ready", checks = new { database = "ready", dataset = "no_active_national_release" } }, statusCode: 503);
        return Results.Ok(new
        {
            status = "ready",
            checks = new { database = "ready", dataset = "active" },
            dataset = new { name = "G-NAF", version }
        });
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    {
        return Results.Json(new { status = "not_ready", checks = new { database = "unavailable", dataset = "unknown" } }, statusCode: 503);
    }
});

app.MapGet("/v1/metadata", async (HttpContext context, IAddressRepository repository,
    BulkRetentionSettings retentionSettings, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    var dataset = await repository.ActiveDatasetAsync(ct);
    var retentionHours = await retentionSettings.GetHoursAsync(ct);
    return Results.Ok(new
    {
        service = "GNAF Address Validation",
        apiVersion = "v1",
        dataset = new { name = dataset?.Name, version = dataset?.Version },
        capabilities = new[] { "ID", "SUGGEST", "MATCH", "VALIDATE", "FORMAT" },
        bulkOperations = new[] { "ID", "MATCH", "VALIDATE", "FORMAT", "SUGGEST" },
        limits = new { suggestionCount = 20, bulkUploadBytes = 104857600, concurrentAddressRequestsPerReplica = concurrentLookups, bulkResultRetentionHours = retentionHours },
        health = new { live = "/health/live", ready = "/health/ready" },
        postalDeliveryValidation = false,
        attribution,
        correlationId = context.TraceIdentifier
    });
});

app.MapGet("/v1/addresses/suggest", async (string? q, int? limit, HttpContext context, AddressService service, IAddressRepository repository, CancellationToken ct) =>
{
    // Suggestions retain the candidate's structured components so clients
    // do not have to parse a display label back into fields.
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    if (string.IsNullOrWhiteSpace(q) || q.Length > 200) return Error(context, "INVALID_REQUEST", "q must be 1 to 200 characters", 400);
    if (limit is < 1 or > 20) return Error(context, "INVALID_REQUEST", "limit must be 1 to 20", 400);
    var suggestions = await service.SuggestAsync(q, limit ?? 10, ct);
    return Results.Ok(new
    {
        suggestions = suggestions.Select(item => new
        {
            item.Id,
            item.Label,
            item.Score,
            components = AddressComponents.From(item.Address)
        }),
        datasetVersion = await repository.ActiveVersionAsync(ct),
        correlationId = context.TraceIdentifier
    });
});

app.MapGet("/v1/addresses/{gnafPid}", async (string gnafPid, HttpContext context, AddressService service, IAddressRepository repository, CancellationToken ct) =>
{
    // IDs are stable within the published G-NAF release; include dataset
    // provenance because the active release can change between requests.
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    if (gnafPid.Length is < 1 or > 80) return Error(context, "INVALID_REQUEST", "Invalid G-NAF PID", 400);
    var address = await service.GetByIdAsync(gnafPid, ct);
    if (address is null) return Error(context, "NOT_FOUND", "G-NAF PID not found", 404);
    var dataset = await repository.ActiveDatasetAsync(ct);
    return Results.Ok(new
    {
        id = address.GnafPid,
        address = new
        {
            formatted = address.FormattedAddress,
            flat = new { type = address.FlatType, number = address.FlatNumberPrefix + address.FlatNumber + address.FlatNumberSuffix },
            level = new { type = address.LevelType, number = address.LevelNumberPrefix + address.LevelNumber + address.LevelNumberSuffix },
            buildingName = address.BuildingName
        },
        components = AddressComponents.From(address),
        location = new { latitude = address.Latitude, longitude = address.Longitude },
        source = new { dataset = dataset?.Name, version = dataset?.Version },
        postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED",
        correlationId = context.TraceIdentifier
    });
});

app.MapPost("/v1/addresses/match", async (AddressInput request, HttpContext context, AddressService service, IAddressRepository repository, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500) return Error(context, "INVALID_REQUEST", "address must be 1 to 500 characters", 400);
    var match = await service.MatchAsync(request.Address, ct);
    return Results.Ok(new
    {
        // Never expose a building-level candidate as a confirmed match when
        // the caller supplied a unit or floor that we could not establish.
        match = match.Address is null || match.MatchType == "SUBADDRESS_UNCONFIRMED" ? null : new
        {
            id = match.Address.GnafPid,
            formattedAddress = match.Address.FormattedAddress,
            flat = new { type = match.Address.FlatType, number = match.Address.FlatNumberPrefix + match.Address.FlatNumber + match.Address.FlatNumberSuffix },
            level = new { type = match.Address.LevelType, number = match.Address.LevelNumberPrefix + match.Address.LevelNumber + match.Address.LevelNumberSuffix },
            components = AddressComponents.From(match.Address),
            location = new { latitude = match.Address.Latitude, longitude = match.Address.Longitude },
            confidence = match.Confidence,
            matchType = match.MatchType
        },
        matchType = match.MatchType,
        datasetVersion = await repository.ActiveVersionAsync(ct),
        postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED",
        correlationId = context.TraceIdentifier
    });
});

app.MapPost("/v1/addresses/validate", async (AddressInput request, HttpContext context, AddressService service, IAddressRepository repository, CancellationToken ct) =>
{
    // A valid address match is a dataset similarity decision. The explicit
    // postal-delivery field prevents clients treating it as delivery proof.
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500) return Error(context, "INVALID_REQUEST", "address must be 1 to 500 characters", 400);
    var result = await service.ValidateAsync(request.Address, ct);
    return Results.Ok(new
    {
        valid = result.Valid,
        validation = new { status = result.Status, level = result.Level, confidence = result.Confidence },
        gnaf = result.Address is null ? null : new { id = result.Address.GnafPid },
        address = result.Address is null ? null : new
        {
            formatted = result.Address.FormattedAddress,
            components = AddressComponents.From(result.Address),
            location = new { latitude = result.Address.Latitude, longitude = result.Address.Longitude }
        },
        postalDelivery = new { status = result.PostalDeliveryStatus },
        datasetVersion = await repository.ActiveVersionAsync(ct),
        correlationId = context.TraceIdentifier
    });
});

app.MapPost("/v1/addresses/format", async (AddressInput request, HttpContext context, AddressService service, IAddressRepository repository, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500) return Error(context, "INVALID_REQUEST", "address must be 1 to 500 characters", 400);
    var formatted = await service.FormatAsync(request.Address, ct);
    return Results.Ok(new
    {
        formatted = formatted.Formatted,
        matched = formatted.Matched,
        confidence = formatted.Confidence,
        matchType = formatted.MatchType,
        gnaf = formatted.GnafPid is null ? null : new { id = formatted.GnafPid },
        components = formatted.Address is null ? null : AddressComponents.From(formatted.Address),
        location = formatted.Address is null ? null : new { latitude = formatted.Address.Latitude, longitude = formatted.Address.Longitude },
        datasetVersion = await repository.ActiveVersionAsync(ct),
        postalDeliveryStatus = "POSTAL_DELIVERY_NOT_VERIFIED",
        correlationId = context.TraceIdentifier
    });
});

app.MapGet("/admin/gnaf/status", async (HttpContext context, DatasetLifecycle lifecycle, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Admin")) return Error(context, "FORBIDDEN", "Address.Admin role required", 403);
    return Results.Ok(new { releases = await lifecycle.ListAsync(ct) });
});

app.MapPost("/v1/bulk", async (string operation, double? minScore, int? limit, HttpContext context, BulkJobRepository jobs,
    IObjectStorage storage, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    operation = operation.ToUpperInvariant();
    if (operation is not ("ID" or "MATCH" or "VALIDATE" or "FORMAT" or "SUGGEST"))
        return Error(context, "INVALID_REQUEST", "operation must be ID, MATCH, VALIDATE, FORMAT or SUGGEST", 400);
    if (operation != "SUGGEST" && (minScore is not null || limit is not null))
        return Error(context, "INVALID_REQUEST", "minScore and limit apply only to SUGGEST", 400);
    if (minScore is { } score && (!double.IsFinite(score) || score is < 0 or > 1))
        return Error(context, "INVALID_REQUEST", "minScore must be between 0 and 1", 400);
    if (limit is < 1 or > 20)
        return Error(context, "INVALID_REQUEST", "limit must be 1 to 20", 400);
    if (context.Request.ContentType?.StartsWith("application/x-ndjson", StringComparison.OrdinalIgnoreCase) != true ||
        context.Request.ContentLength is null or 0 or > 104857600)
        return Error(context, "INVALID_REQUEST", "Upload a non-empty NDJSON file of at most 100 MB", 400);
    var caller = BulkCaller(context);
    if (!isDevelopmentAuth && caller is null)
        return Error(context, "FORBIDDEN", "A stable user identity is required for bulk jobs", 403);
    var id = Guid.NewGuid();
    var name = $"input/{id:N}.ndjson";
    var accessToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    var accessTokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
    // Store input before queueing so a worker can always read a queued job;
    // remove the blob if database registration fails.
    await storage.UploadAsync(name, context.Request.Body, ct);
    try
    {
        await jobs.CreateAsync(id, operation, name, ct,
        operation == "SUGGEST" ? minScore ?? 0 : null,
        operation == "SUGGEST" ? limit ?? 5 : null,
        caller, accessTokenHash);
    }
    catch
    {
        try { await storage.DeleteIfExistsAsync(name, ct); }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Could not remove orphaned upload {JobId}", id); }
        throw;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Cookies.Append($"bulk-{id:N}", accessToken, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = !isDevelopmentAuth || context.Request.IsHttps,
        Path = $"/v1/bulk/{id}"
    });
    return Results.Json(new
    {
        jobId = id,
        accessToken,
        status = "QUEUED",
        statusUrl = $"/v1/bulk/{id}",
        correlationId = context.TraceIdentifier
    }, statusCode: 202);
});

app.MapGet("/v1/bulk/{id:guid}", async (Guid id, HttpContext context, BulkJobRepository jobs,
    BulkRetentionSettings retentionSettings, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    var job = await jobs.GetAsync(id, ct);
    if (job is null || !CanReadBulkJob(context, job)) return Error(context, "NOT_FOUND", "Bulk job not found", 404);
    context.Response.Headers.CacheControl = "no-store";
    var retentionHours = await retentionSettings.GetHoursAsync(ct);
    return Results.Ok(new
    {
        jobId = job.Id,
        job.Operation,
        suggestionOptions = job.Operation == "SUGGEST" ? new { minScore = job.MinScore, limit = job.SuggestionLimit } : null,
        job.Status,
        progress = new { processed = job.ProcessedRecords, total = job.TotalRecords, failed = job.FailedRecords },
        result = job.Status == "COMPLETED" ? new { downloadUrl = $"/v1/bulk/{id}/result" } : null,
        job.ErrorMessage,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
        expiresAt = job.CompletedAt?.AddHours(retentionHours),
        correlationId = context.TraceIdentifier
    });
});

app.MapGet("/v1/bulk/{id:guid}/result", async (Guid id, HttpContext context, BulkJobRepository jobs,
    IObjectStorage storage, CancellationToken ct) =>
{
    if (!HasRole(context, "Address.Read")) return Error(context, "FORBIDDEN", "Address.Read role required", 403);
    var job = await jobs.GetAsync(id, ct);
    if (job is null || !CanReadBulkJob(context, job)) return Error(context, "NOT_FOUND", "Bulk job not found", 404);
    context.Response.Headers.CacheControl = "no-store";
    if (job.Status != "COMPLETED" || job.OutputBlobUri is null)
        return Error(context, "NOT_READY", "Bulk result is not ready", 409);
    // The worker sets COMPLETED only after closing output, so this stream is
    // never intentionally exposed while still being written.
    var stream = await storage.OpenReadAsync(job.OutputBlobUri, ct);
    return Results.Stream(stream, "application/x-ndjson", $"{id:N}.ndjson");
});

app.Run();

/// <summary>Shared JSON request body for match, validate, and format operations.</summary>
public sealed record AddressInput(string Address);
