using System.Net;
using System.Net.Sockets;
using System.Data.Common;
using System.Security.Claims;
using Address.Data;
using Gnaf.Admin;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
var localDevelopment = builder.Environment.IsDevelopment();
// Form tokens are enabled in both modes. Local HTTP needs a non-Secure cookie;
// deployments behind HTTPS use the __Host cookie prefix and Secure policy.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = localDevelopment ? "gnaf-csrf-local" : "__Host-gnaf-csrf";
    options.Cookie.SecurePolicy = localDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
if (!localDevelopment)
{
    // Non-development deployments fail closed unless an identity provider and a
    // persistent key ring have been configured. Compose remains localhost only.
    var authority = builder.Configuration["AdminAuth:Authority"];
    var clientId = builder.Configuration["AdminAuth:ClientId"];
    var clientSecret = builder.Configuration["AdminAuth:ClientSecret"];
    var keysDirectory = builder.Configuration["AdminAuth:KeysDirectory"];
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != "https" ||
        string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) ||
        string.IsNullOrWhiteSpace(keysDirectory))
        throw new InvalidOperationException("Production admin requires HTTPS AdminAuth:Authority, ClientId, ClientSecret, and KeysDirectory");
    builder.Services.AddDataProtection().SetApplicationName("Gnaf.Admin")
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    // OIDC performs interactive sign-in; the local cookie carries the admin
    // session. The role policy prevents any ordinary signed-in user acting.
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    }).AddCookie(options =>
    {
        options.Cookie.Name = "__Host-gnaf-admin";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    }).AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.ResponseType = "code";
        options.SaveTokens = false;
        options.MapInboundClaims = false;
    });
    builder.Services.AddAuthorization(options => options.FallbackPolicy =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(context =>
            context.User.HasClaim("roles", "Address.Admin") ||
            context.User.HasClaim(ClaimTypes.Role, "Address.Admin")).Build());
}
else builder.Services.AddAuthorization();

builder.Services.AddSingleton(new Database(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string is required")));
builder.Services.AddSingleton<DatasetLifecycle>();
builder.Services.AddSingleton<GnafArchiveImporter>();
builder.Services.AddSingleton<MaintenanceJobs>();
builder.Services.AddSingleton<AdminEvents>();
builder.Services.AddSingleton<BulkRetentionSettings>();
builder.Services.AddSingleton<HousekeepingService>();
builder.Services.AddHttpClient<ClusterStatusService>(client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddSingleton<IGnafSourceProvider>(_ => new DataGovAuGnafSourceProvider(
    new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
    new Uri(builder.Configuration["Source:CatalogueUrl"] ??
        "https://data.gov.au/data/api/3/action/package_show?id=19432f89-dc3a-4ef3-b943-5326ef1dbecc"),
    new Uri(builder.Configuration["Source:PreviousCatalogueUrl"] ??
        "https://data.gov.au/data/api/3/action/package_show?id=e1a365fc-52f5-4798-8f0c-ed1d33d43b6d")));
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://address-api:8080");
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHostedService<ReleasePreparationWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<HousekeepingService>());

var app = builder.Build();
// Schema migration finishes before serving requests or starting preparation.
await app.Services.GetRequiredService<Database>().MigrateAsync();
app.UseStaticFiles();
if (!localDevelopment) app.UseAuthentication();
app.UseAuthorization();
// Every operator mutation is a browser form POST. Validate before a handler can
// queue work or switch the serving view, including in local development.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        (context.Request.Path == "/prepare" || context.Request.Path == "/rollback" ||
         context.Request.Path == "/settings/bulk-retention" ||
         context.Request.Path.StartsWithSegments("/promote") ||
         context.Request.Path.StartsWithSegments("/releases")))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid or missing request token.");
            return;
        }
    }
    await next(context);
});

// Liveness and readiness are anonymous for container orchestration. Other
// endpoints inherit the administrator policy outside Development.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (Database database, CancellationToken ct) =>
{
    try
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        return Results.Ok(new { status = "ready", checkedAtUtc = DateTime.UtcNow });
    }
    catch (Exception ex) when (ex is DbException or SocketException or TimeoutException or OperationCanceledException)
    {
        return Results.Json(new { status = "unavailable", checkedAtUtc = DateTime.UtcNow }, statusCode: 503);
    }
}).AllowAnonymous();

// Dashboard reads are independent of release mutations. A failed history read
// should not conceal the current serving release from an operator.
app.MapGet("/", async (HttpContext context, DatasetLifecycle lifecycle, MaintenanceJobs jobs,
    AdminEvents events, BulkRetentionSettings retentionSettings, IAntiforgery antiforgery, ClusterStatusService clusterService,
    HousekeepingService housekeeping, IHttpClientFactory clients, ILoggerFactory logs, CancellationToken ct) =>
{
    var cluster = await clusterService.CheckAsync(ct);
    IReadOnlyList<DatasetDetail> versions = [];
    long? databaseBytes = null;
    int? retentionHours = null;
    MaintenanceJob? job = null;
    IReadOnlyList<AdminEvent> recentEvents = [];
    var dataAvailable = true;
    try
    {
        versions = await lifecycle.ListDetailsAsync(ct);
        databaseBytes = await lifecycle.DatabaseBytesAsync(ct);
        retentionHours = await retentionSettings.GetHoursAsync(ct);
        job = await jobs.LatestAsync(ct);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested &&
        (ex is DbException or SocketException or TimeoutException or OperationCanceledException))
    {
        dataAvailable = false;
        logs.CreateLogger("Dashboard").LogWarning(ex, "Could not read dataset status");
    }
    if (dataAvailable)
    {
        try { recentEvents = await events.RecentAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is DbException or SocketException or TimeoutException or OperationCanceledException))
        {
            // Audit history is useful, but a read failure must not hide an
            // otherwise healthy release dashboard from the operator.
            logs.CreateLogger("Dashboard").LogWarning(ex, "Could not read operator history");
        }
    }
    var apiReady = await CheckApiAsync(clients, ct);
    var apiReplicas = await clusterService.CheckApiReplicasAsync(ct);
    var token = antiforgery.GetAndStoreTokens(context).RequestToken;
    context.Response.Headers.CacheControl = "no-store";
    return Results.Content(AdminPage.Render(versions, job, apiReady, apiReplicas, databaseBytes, cluster, dataAvailable,
        context.Request.Query["notice"].ToString(), context.Request.Query["error"].ToString(), token, recentEvents,
        localDevelopment ? "Local development" : context.User.Identity?.Name ?? "Signed in", retentionHours,
        housekeeping.Snapshot),
        "text/html; charset=utf-8");
});

app.MapGet("/api/system", async (ClusterStatusService clusterService,
    IHttpClientFactory clients, CancellationToken ct) =>
    Results.Ok(new
    {
        checkedAtUtc = DateTime.UtcNow,
        cluster = await clusterService.CheckAsync(ct),
        apiReady = await CheckApiAsync(clients, ct),
        apiReplicas = await clusterService.CheckApiReplicasAsync(ct)
    }));

app.MapGet("/api/events", async (AdminEvents events, CancellationToken ct) =>
    Results.Ok(new { events = await events.RecentAsync(ct) }));

app.MapGet("/api/housekeeping", (HousekeepingService housekeeping) => Results.Ok(housekeeping.Snapshot));

// Catalogue checks are read-only: discovering a release never queues an import.
app.MapGet("/api/releases", async (IGnafSourceProvider source, DatasetLifecycle lifecycle,
    ILoggerFactory logs, CancellationToken ct) =>
{
    try
    {
        var available = await source.ListAvailableAsync(ct);
        var installed = (await lifecycle.ListDetailsAsync(ct))
            .Where(x => x.DatasetName == "G-NAF" && x.Scope == "ALL")
            .ToDictionary(x => x.SourceVersion, x => x.Status, StringComparer.Ordinal);
        return Results.Ok(new
        {
            activeVersion = installed.FirstOrDefault(x => x.Value == "ACTIVE").Key,
            releases = available.Select(release => new
            {
                version = release.Version,
                size = release.Size,
                installedStatus = installed.GetValueOrDefault(release.Version)
            })
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
    {
        logs.CreateLogger("ReleaseSource").LogWarning(ex, "Could not list official G-NAF releases");
        return Results.Json(new { error = "Could not reach the official G-NAF catalogue. The live service is unaffected; try again shortly." }, statusCode: 503);
    }
});

app.MapGet("/api/latest", async (IGnafSourceProvider source, DatasetLifecycle lifecycle,
    ILoggerFactory logs, CancellationToken ct) =>
{
    try
    {
        var release = await source.CheckLatestAsync(ct);
        var installed = (await lifecycle.ListDetailsAsync(ct))
            .FirstOrDefault(x => x.SourceVersion == release.Version);
        return Results.Ok(new
        {
            version = release.Version,
            size = release.Size,
            installedStatus = installed?.Status,
            source = "data.gov.au"
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
    {
        logs.CreateLogger("ReleaseSource").LogWarning(ex, "Could not check the G-NAF catalogue");
        return Results.Json(new { error = "Could not reach the official G-NAF catalogue. The live service is unaffected; try again shortly." }, statusCode: 503);
    }
});

app.MapGet("/api/maintenance", async (MaintenanceJobs jobs, IConfiguration config, CancellationToken ct) =>
{
    var job = await jobs.LatestAsync(ct);
    if (job is null) return Results.Ok(new { job = (object?)null });
    long? downloaded = null;
    if (job.Stage == "DOWNLOADING")
    {
        var cache = config["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache");
        var part = Path.Combine(cache, job.SourceVersion + "_gda2020.zip.part");
        var complete = Path.Combine(cache, job.SourceVersion + "_gda2020.zip");
        if (File.Exists(part)) downloaded = new FileInfo(part).Length;
        else if (File.Exists(complete)) downloaded = new FileInfo(complete).Length;
    }
    var datasetStage = job.Stage == "IMPORTING"
        ? await jobs.DatasetStageAsync(job.SourceVersion, ct) : null;
    return Results.Ok(new
    {
        job = new
        {
            version = job.SourceVersion,
            job.Status,
            job.Stage,
            job.ErrorMessage,
            downloaded,
            total = job.SourceSize,
            datasetStage
        }
    });
});

// POST actions are reached only after authorization and antiforgery middleware.
// Preparation queues background work; activation remains a separate decision.
app.MapPost("/settings/bulk-retention", async (HttpContext context, BulkRetentionSettings settings,
    AdminEvents events, ILoggerFactory logs, CancellationToken ct) =>
{
    var form = await context.Request.ReadFormAsync(ct);
    if (!int.TryParse(form["retentionHours"], out var hours) || hours is < 1 or > 168)
    {
        await RecordEventAsync(events, context, logs, "BULK_RETENTION", "REJECTED", null,
            "Invalid retention period");
        return RedirectError("Choose a bulk data retention period from 1 to 168 hours.");
    }
    var actor = context.User.FindFirst("preferred_username")?.Value ??
        context.User.FindFirst("sub")?.Value ?? "local-operator";
    await settings.SetHoursAsync(hours, actor, ct);
    await RecordEventAsync(events, context, logs, "BULK_RETENTION", "SUCCEEDED", null,
        $"Bulk data retention set to {hours} hours");
    return RedirectNotice($"Bulk uploads and results now expire {hours} hours after completion. Older jobs may be removed by the next cleanup sweep.");
});

app.MapPost("/prepare", async (HttpContext context, IGnafSourceProvider source, MaintenanceJobs jobs,
    AdminEvents events, ILoggerFactory logs, CancellationToken ct) =>
{
    try
    {
        var form = await context.Request.ReadFormAsync(ct);
        var version = form["version"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^20\d{2}_(0[1-9]|1[0-2])$"))
        {
            await RecordEventAsync(events, context, logs, "PREPARE", "REJECTED", null,
                "No valid official release selected");
            return RedirectError("Choose a release from the official catalogue before preparing it.");
        }
        var release = await source.FindReleaseAsync(version, ct);
        await jobs.QueueAsync(release, ct);
        await RecordEventAsync(events, context, logs, "PREPARE", "SUCCEEDED", release.Version,
            "Preparation queued");
        return RedirectNotice($"Preparing {AdminPage.ReleaseName(release.Version)}. The API will keep serving the active release.");
    }
    catch (InvalidOperationException ex)
    {
        await RecordEventAsync(events, context, logs, "PREPARE", "REJECTED", null, ex.Message);
        return RedirectError(ex.Message);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException)
    {
        logs.CreateLogger("ReleaseSource").LogWarning(ex, "Could not queue the selected release");
        await RecordEventAsync(events, context, logs, "PREPARE", "FAILED", null, "Official source unavailable");
        return RedirectError("The selected release was not found in the official catalogue or the source is unavailable. The live release is unchanged.");
    }
});

// Re-read release state at submission time: the page may be stale or another
// operator may have switched releases after it was rendered.
app.MapPost("/promote/{version}", async (string version, HttpContext context, DatasetLifecycle lifecycle,
    AdminEvents events, IConfiguration config, ILoggerFactory logs, CancellationToken ct) =>
{
    var versions = await lifecycle.ListDetailsAsync(ct);
    var active = versions.FirstOrDefault(x => x.Status == "ACTIVE");
    var candidate = versions.FirstOrDefault(x => x.SourceVersion == version && x.Status == "READY" &&
        x.DatasetName == "G-NAF" && x.Scope == "ALL" &&
        (active is null || active.DatasetName == x.DatasetName && active.Scope == x.Scope));
    if (candidate is null)
    {
        if (versions.Any(x => x.SourceVersion == version && x.Status == "ACTIVE" &&
            x.DatasetName == "G-NAF" && x.Scope == "ALL"))
        {
            await RecordEventAsync(events, context, logs, "PROMOTE", "SUCCEEDED", version,
                "Release was already active; no switch needed");
            return RedirectNotice($"{AdminPage.ReleaseName(version)} is already live.");
        }
        await RecordEventAsync(events, context, logs, "PROMOTE", "REJECTED", version, "Release is not ready");
        return RedirectError("That national release is not ready to activate.");
    }
    try
    {
        await lifecycle.PromoteAsync(version, ct);
        await RecordEventAsync(events, context, logs, "PROMOTE", "SUCCEEDED", version, "Release activated");
        try
        {
            GnafCacheMaintenance.RemoveObsolete(config["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"),
                await lifecycle.ListAsync(ct));
        }
        catch (IOException ex)
        {
            logs.CreateLogger("CacheCleanup").LogWarning(ex, "Release switched but archive cleanup failed");
        }
        return RedirectNotice($"{AdminPage.ReleaseName(version)} is now live. The API stayed running during the switch.");
    }
    catch (Exception ex)
    {
        logs.CreateLogger("ReleaseSwitch").LogError(ex, "Could not activate {Version}", version);
        await RecordEventAsync(events, context, logs, "PROMOTE", "FAILED", version, "Release switch failed");
        return RedirectError(SwitchError(ex));
    }
});

app.MapPost("/rollback", async (HttpContext context, DatasetLifecycle lifecycle,
    AdminEvents events, ILoggerFactory logs, CancellationToken ct) =>
{
    try
    {
        await lifecycle.RollbackAsync(ct);
        await RecordEventAsync(events, context, logs, "ROLLBACK", "SUCCEEDED", null, "Previous release restored");
        return RedirectNotice("The previous national release is live again.");
    }
    catch (Exception ex)
    {
        logs.CreateLogger("ReleaseSwitch").LogError(ex, "Could not roll back G-NAF");
        await RecordEventAsync(events, context, logs, "ROLLBACK", "FAILED", null, "Rollback failed");
        return RedirectError(SwitchError(ex));
    }
});

app.MapPost("/releases/{version}/delete", async (string version, HttpContext context, DatasetLifecycle lifecycle,
    AdminEvents events, IConfiguration config, ILoggerFactory logs, CancellationToken ct) =>
{
    var form = await context.Request.ReadFormAsync(ct);
    if (!string.Equals(form["confirmVersion"].ToString(), version, StringComparison.Ordinal))
    {
        await RecordEventAsync(events, context, logs, "DELETE_RELEASE", "REJECTED", version,
            "Release version confirmation did not match");
        return RedirectError("Type the exact release version shown on the page to delete its data.");
    }
    try
    {
        await lifecycle.DeleteRetiredNationalAsync(version, ct);
        await RecordEventAsync(events, context, logs, "DELETE_RELEASE", "SUCCEEDED", version,
            "Previous national release deleted");
        try
        {
            GnafCacheMaintenance.RemoveObsolete(config["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"),
                await lifecycle.ListAsync(ct));
        }
        catch (IOException ex)
        {
            logs.CreateLogger("CacheCleanup").LogWarning(ex, "Release deleted but archive cleanup failed");
        }
        return RedirectNotice($"{AdminPage.ReleaseName(version)} was deleted. The current release remains live; rollback to the deleted release is no longer available.");
    }
    catch (InvalidOperationException ex)
    {
        await RecordEventAsync(events, context, logs, "DELETE_RELEASE", "REJECTED", version, ex.Message);
        return RedirectError(ex.Message.Contains("preparation", StringComparison.OrdinalIgnoreCase)
            ? "Wait for release preparation to finish, then try again."
            : "Only a previous national release can be deleted. Refresh the page and check the live release.");
    }
    catch (Exception ex)
    {
        logs.CreateLogger("ReleaseDeletion").LogError(ex, "Could not delete previous release {Version}", version);
        await RecordEventAsync(events, context, logs, "DELETE_RELEASE", "FAILED", version, "Release deletion failed");
        return RedirectError("Could not delete the previous release. The current release remains live. Check the admin logs or ask for support.");
    }
});

app.Run();

static IResult RedirectNotice(string message) => Results.Redirect("/?notice=" + WebUtility.UrlEncode(message));
static IResult RedirectError(string message) => Results.Redirect("/?error=" + WebUtility.UrlEncode(message));
static async Task RecordEventAsync(AdminEvents events, HttpContext context, ILoggerFactory logs,
    string action, string outcome, string? version, string detail)
{
    // Keep audit payloads small and stable; never persist cookies, tokens or
    // arbitrary exception text from an external source.
    var actor = context.User.FindFirst("preferred_username")?.Value ??
        context.User.FindFirst("sub")?.Value ?? "local-operator";
    try
    {
        await events.RecordAsync(actor, action, outcome, version, context.TraceIdentifier, detail, CancellationToken.None);
        logs.CreateLogger("AdminAudit").LogInformation(
            "Admin action {Action} {Outcome}; version {Version}; actor {Actor}; request {RequestId}",
            action, outcome, version, actor, context.TraceIdentifier);
    }
    catch (Exception ex)
    {
        // A release switch may already have committed. Audit failure must not
        // turn a successful switch into a misleading HTTP failure response.
        logs.CreateLogger("AdminAudit").LogError(ex, "Could not persist admin action {Action} for {RequestId}",
            action, context.TraceIdentifier);
    }
}
static async Task<bool> CheckApiAsync(IHttpClientFactory clients, CancellationToken ct)
{
    try
    {
        using var response = await clients.CreateClient("api").GetAsync("/health/ready", ct);
        return response.IsSuccessStatusCode;
    }
    catch (HttpRequestException) { return false; }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
}
static string SwitchError(Exception ex) => ex.Message.Contains("bulk job", StringComparison.OrdinalIgnoreCase)
    ? "A bulk job is running. Wait for it to finish, then try again. The live release is unchanged."
    : ex.Message.Contains("No retained release", StringComparison.OrdinalIgnoreCase)
        ? "There is no earlier national release available to restore."
        : "The release switch did not complete. The current release remains live. Check the admin logs or ask for support.";
