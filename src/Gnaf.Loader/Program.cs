using Address.Core;
using Address.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
// This CLI is for explicit maintenance and test setup. The admin portal owns
// the ordinary prepare/review/activate workflow for nontechnical operators.
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
// Reject synthetic seeding before creating a connection or running migrations.
// Only the disposable test Compose file enables this command.
if (command == "seed" && !string.Equals(configuration["Seed:AllowSynthetic"], "true", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Synthetic seeding is disabled. Use the disposable test stack.");
    Environment.ExitCode = 2;
    return;
}
var connectionString = configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Database=address;Username=address;Password=address";
var database = new Database(connectionString);
await database.MigrateAsync();
var lifecycle = new DatasetLifecycle(database);
var provider = new DataGovAuGnafSourceProvider(new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
    new Uri(configuration["Source:CatalogueUrl"] ?? "https://data.gov.au/data/api/3/action/package_show?id=19432f89-dc3a-4ef3-b943-5326ef1dbecc"));
string? Option(string key) => args.Skip(1).Zip(args.Skip(2)).FirstOrDefault(pair => pair.First == key).Second;
async Task<GnafRelease> FindReleaseAsync(string version)
{
    try { return await provider.FindReleaseAsync(version, default); }
    catch (InvalidDataException)
    {
        // Historic national ZIPs move out of the current CKAN package. An
        // explicit version must resolve to that exact archive, never latest.
        using var previousClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var previous = new DataGovAuGnafSourceProvider(previousClient,
            new Uri(configuration["Source:PreviousCatalogueUrl"] ??
                "https://data.gov.au/data/api/3/action/package_show?id=e1a365fc-52f5-4798-8f0c-ed1d33d43b6d"));
        return await previous.FindReleaseAsync(version, default);
    }
}
switch (command)
{
    case "status":
        foreach (var installedRelease in await lifecycle.ListAsync(default))
            Console.WriteLine($"{installedRelease.SourceVersion}\t{installedRelease.Status}\t{installedRelease.SchemaName}");
        break;
    case "check":
        var checkedVersion = Option("--version");
        var latest = checkedVersion is null ? await provider.CheckLatestAsync(default) : await FindReleaseAsync(checkedVersion);
        var active = (await lifecycle.ListAsync(default)).FirstOrDefault(x => x.Status == "ACTIVE");
        Console.WriteLine($"Current: {active?.SourceVersion ?? "none"}\nAvailable: {latest.Version}\nSize: {latest.Size?.ToString() ?? "unknown"} bytes\nSource: {latest.Url}");
        break;
    case "upgrade":
        // Import only creates a READY candidate; promotion remains a separate
        // reviewed action to preserve the active API data during preparation.
        var archive = Option("--archive");
        var requestedVersion = Option("--version");
        var release = archive is null
            ? requestedVersion is null ? await provider.CheckLatestAsync(default) : await FindReleaseAsync(requestedVersion)
            : null;
        var releaseVersion = requestedVersion ?? release?.Version ?? throw new ArgumentException("--version is required with --archive");
        var state = Option("--state") ?? "ALL";
        var maxDrop = double.Parse(configuration["Quality:MaxCountDropFraction"] ?? "0.10", System.Globalization.CultureInfo.InvariantCulture);
        var minGeocode = double.Parse(configuration["Quality:MinGeocodeCoverage"] ?? "0.90", System.Globalization.CultureInfo.InvariantCulture);
        GnafSourceMetadata? metadata = null;
        if (archive is null)
        {
            (archive, metadata) = await provider.DownloadAsync(release!, configuration["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"), default);
        }
        Console.WriteLine($"Importing {releaseVersion} ({state}) from {archive}");
        var report = await new GnafArchiveImporter(database).ImportAsync(releaseVersion, archive, state, maxDrop, minGeocode, metadata);
        Console.WriteLine($"Candidate READY: {report.Addresses:N0} addresses, {report.GeocodeCoverage:P2} geocoded. Promote explicitly after review.");
        break;
    case "preflight":
        var preflightArchive = Option("--archive") ?? throw new ArgumentException("--archive is required");
        var preflightVersion = Option("--version") ?? throw new ArgumentException("--version is required");
        var preflightScope = Option("--state") ?? "ALL";
        var checkedSchema = await new GnafArchiveImporter(database).PreflightAsync(preflightVersion, preflightArchive, preflightScope);
        Console.WriteLine($"Archive preflight passed for {checkedSchema.Version}: {checkedSchema.Headers.Count} PSV table types; " +
            (checkedSchema.BaselineVersion is null ? "no active header manifest available" : $"matches active {checkedSchema.BaselineVersion} headers"));
        break;
    case "seed":
        var version = args.ElementAtOrDefault(1) ?? "sample_1";
        var addresses = version == "sample_2"
            ? new[] { New("SAMPLE-4", "12 King Street, Adelaide SA 5000", "", "12", "KING", "STREET", "ADELAIDE", "SA", "5000", -34.9285, 138.6007) }
            : new[]
            {
                New("SAMPLE-1", "Unit 4, 10 Collins Street, Melbourne VIC 3000", "4", "10", "COLLINS", "STREET", "MELBOURNE", "VIC", "3000", -37.8136, 144.9631),
                New("SAMPLE-2", "100 George Street, Sydney NSW 2000", "", "100", "GEORGE", "STREET", "SYDNEY", "NSW", "2000", -33.8688, 151.2093),
                New("SAMPLE-3", "24 Smith Street, Brisbane QLD 4000", "", "24", "SMITH", "STREET", "BRISBANE", "QLD", "4000", -27.4698, 153.0251)
            };
        await lifecycle.SeedAsync(version, addresses, default);
        Console.WriteLine($"Candidate {version} READY. Run promote {version} when ready.");
        break;
    case "promote":
        await lifecycle.PromoteAsync(args.ElementAtOrDefault(1) ?? throw new ArgumentException("Version required"), default);
        try
        {
            GnafCacheMaintenance.RemoveObsolete(configuration["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"),
                await lifecycle.ListAsync(default));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Release switched, but archive cleanup failed: {ex.Message}");
        }
        Console.WriteLine("Promotion complete");
        break;
    case "prune-cache":
        Console.WriteLine($"Removed {GnafCacheMaintenance.RemoveObsolete(
            configuration["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"),
            await lifecycle.ListAsync(default))} completed or obsolete source files");
        break;
    case "rebuild":
        var rebuiltVersion = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Version required");
        var rebuiltReport = await new GnafArchiveImporter(database).RebuildCandidateAsync(rebuiltVersion);
        Console.WriteLine($"Candidate {rebuiltVersion} READY: {rebuiltReport.Addresses:N0} addresses, {rebuiltReport.GeocodeCoverage:P2} geocoded.");
        break;
    case "upgrade-active-serving":
        await new GnafArchiveImporter(database).UpgradeActiveServingAsync();
        break;
    case "enrich-aliases":
        // Prefer the installed release's recorded URL/checksum so alias maps
        // cannot silently come from a different source archive.
        var aliasVersion = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Version required");
        var archiveOption = Array.IndexOf(args, "--archive");
        var aliasArchive = archiveOption >= 0 ? args.ElementAtOrDefault(archiveOption + 1) : null;
        var removeDownloadedArchive = aliasArchive is null;
        if (aliasArchive is null)
        {
            GnafRelease? source = null;
            await using (var sourceConnection = await database.DataSource.OpenConnectionAsync())
            await using (var sourceCommand = new NpgsqlCommand("""
                SELECT source_url,source_filename,file_size FROM platform.dataset_version
                WHERE source_version=@version AND dataset_name='G-NAF'
                """, sourceConnection))
            {
                sourceCommand.Parameters.AddWithValue("version", aliasVersion);
                await using var sourceReader = await sourceCommand.ExecuteReaderAsync();
                if (!await sourceReader.ReadAsync()) throw new InvalidOperationException("Installed release not found");
                if (!sourceReader.IsDBNull(0))
                {
                    var sourceUrl = new Uri(sourceReader.GetString(0));
                    if (sourceUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Source URL must use HTTPS");
                    var filename = sourceReader.IsDBNull(1) ? Path.GetFileName(sourceUrl.AbsolutePath) : sourceReader.GetString(1);
                    long? size = sourceReader.IsDBNull(2) ? null : sourceReader.GetInt64(2);
                    source = new GnafRelease(aliasVersion, sourceUrl, filename, size, null, null);
                }
            }
            source ??= await FindReleaseAsync(aliasVersion);
            (aliasArchive, _) = await provider.DownloadAsync(source,
                configuration["Source:CacheDirectory"] ?? Path.Combine(Path.GetTempPath(), "gnaf-cache"), default);
        }
        try
        {
            var aliases = await new GnafArchiveImporter(database).EnrichAliasesAsync(aliasVersion, aliasArchive);
            Console.WriteLine($"Alias dictionaries for {aliasVersion}: {aliases.Streets:N0} streets, {aliases.Localities:N0} localities");
        }
        finally
        {
            if (removeDownloadedArchive && aliasArchive is not null) File.Delete(aliasArchive);
        }
        break;
    case "rollback":
        // Rollback switches the stable view; it does not restore a database
        // backup or recover from full-host loss.
        await lifecycle.RollbackAsync(default);
        Console.WriteLine("Rollback complete");
        break;
    case "prune-completed-raw":
        Console.WriteLine($"Removed {await lifecycle.PruneCompletedRawAsync(default)} redundant raw tables from completed national releases");
        break;
    default:
        throw new ArgumentException("Commands: status, check [--version VERSION], preflight --archive PATH --version VERSION [--state ACT], upgrade [--archive PATH --version VERSION] [--state ACT], rebuild <version>, upgrade-active-serving, enrich-aliases <version> [--archive PATH], seed [version], promote <version>, rollback, prune-completed-raw, prune-cache");
}

static AddressRecord New(string id, string formatted, string flat, string number, string street, string type,
    string locality, string state, string postcode, double latitude, double longitude) =>
    new(id, formatted, flat, number, street, type, locality, state, postcode, latitude, longitude,
        AddressNormalizer.Normalize(formatted));
