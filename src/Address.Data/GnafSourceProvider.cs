using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Address.Data;

/// <summary>Catalogue entry for one official GDA2020 G-NAF archive.</summary>
public sealed record GnafRelease(string Version, Uri Url, string Filename, long? Size,
    string? ETag, DateTime? LastModified);

/// <summary>Separates release discovery and download from the database import.</summary>
public interface IGnafSourceProvider
{
    /// <summary>Finds the latest eligible official release.</summary>
    Task<GnafRelease> CheckLatestAsync(CancellationToken ct);
    /// <summary>Lists current and archived national releases available from the official catalogue.</summary>
    Task<IReadOnlyList<GnafRelease>> ListAvailableAsync(CancellationToken ct);
    /// <summary>Resolves an exact official version, including one in the archive catalogue.</summary>
    Task<GnafRelease> FindReleaseAsync(string version, CancellationToken ct);
    /// <summary>Downloads or reuses a local archive and returns its provenance.</summary>
    Task<(string Path, GnafSourceMetadata Metadata)> DownloadAsync(GnafRelease release, string cacheDirectory, CancellationToken ct);
}

/// <summary>Reads the public data.gov.au CKAN catalogue and selects the GDA2020 PSV ZIP.</summary>
public sealed class DataGovAuGnafSourceProvider(HttpClient client, Uri catalogueUrl,
    Uri? previousCatalogueUrl = null) : IGnafSourceProvider
{
    // CKAN resource names have used both short and full month names. Capture
    // either form, then map its first three letters to the release month.
    private static readonly Regex VersionPattern = new(@"\b(JAN(?:UARY)?|FEB(?:RUARY)?|MAR(?:CH)?|APR(?:IL)?|MAY|JUN(?:E)?|JUL(?:Y)?|AUG(?:UST)?|SEP(?:T(?:EMBER)?)?|OCT(?:OBER)?|NOV(?:EMBER)?|DEC(?:EMBER)?)\s+(20\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>Chooses the newest version after filtering catalogue resources to GDA2020 ZIPs.</summary>
    public async Task<GnafRelease> CheckLatestAsync(CancellationToken ct)
        => (await ReadCatalogueAsync(catalogueUrl, ct)).OrderByDescending(x => x.Version, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("No GDA2020 G-NAF ZIP resource found in the catalogue");

    /// <summary>Combines current and archived official packages, preferring the current entry for duplicates.</summary>
    public async Task<IReadOnlyList<GnafRelease>> ListAvailableAsync(CancellationToken ct)
    {
        var current = await ReadCatalogueAsync(catalogueUrl, ct);
        List<GnafRelease> previous = previousCatalogueUrl is null ? [] : await ReadCatalogueAsync(previousCatalogueUrl, ct);
        return current.Concat(previous).GroupBy(x => x.Version, StringComparer.Ordinal)
            .Select(group => group.First()).OrderByDescending(x => x.Version, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Finds an exact version; a missing or malformed version cannot fall back to latest.</summary>
    public async Task<GnafRelease> FindReleaseAsync(string version, CancellationToken ct)
    {
        var release = (await ReadCatalogueAsync(catalogueUrl, ct)).FirstOrDefault(x => x.Version == version);
        if (release is null && previousCatalogueUrl is not null)
            release = (await ReadCatalogueAsync(previousCatalogueUrl, ct)).FirstOrDefault(x => x.Version == version);
        return release ?? throw new InvalidDataException($"GDA2020 G-NAF ZIP for {version} not found in the catalogue");
    }

    private async Task<List<GnafRelease>> ReadCatalogueAsync(Uri catalogue, CancellationToken ct)
    {
        using var response = await client.GetAsync(catalogue, ct);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct);
        var root = document.RootElement;
        if (!root.GetProperty("success").GetBoolean()) throw new InvalidDataException("Catalogue API reported failure");
        var resources = root.GetProperty("result").GetProperty("resources").EnumerateArray();
        var releases = new List<GnafRelease>();
        foreach (var resource in resources)
        {
            var name = resource.GetProperty("name").GetString() ?? "";
            var format = resource.GetProperty("format").GetString() ?? "";
            if (!format.Equals("ZIP", StringComparison.OrdinalIgnoreCase) || !name.Contains("GDA2020", StringComparison.OrdinalIgnoreCase)) continue;
            var match = VersionPattern.Match(name);
            if (!match.Success) continue;
            var month = Array.IndexOf(Months, match.Groups[1].Value[..3].ToUpperInvariant()) + 1;
            var version = $"{match.Groups[2].Value}_{month:00}";
            var url = new Uri(resource.GetProperty("url").GetString() ?? throw new InvalidDataException("Missing ZIP URL"));
            if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("G-NAF ZIP URL must use HTTPS");
            var filename = Path.GetFileName(url.AbsolutePath);
            long? size = resource.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number ? sizeValue.GetInt64() : null;
            DateTime? modified = resource.TryGetProperty("last_modified", out var modifiedValue) && modifiedValue.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(modifiedValue.GetString(), out var parsed) ? parsed.ToUniversalTime() : null;
            releases.Add(new GnafRelease(version, url, filename, size, null, modified));
        }
        return releases;
    }

    /// <summary>Resumes an archive download and computes a SHA-256 digest before import.</summary>
    public async Task<(string Path, GnafSourceMetadata Metadata)> DownloadAsync(GnafRelease release, string cacheDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, release.Version + "_gda2020.zip");
        if (File.Exists(path) && (release.Size is null || new FileInfo(path).Length == release.Size))
            return (path, await MetadataAsync(path, release, ct));
        // A .part file is never treated as a complete source archive. A retry
        // resumes from its length when the server honours the byte range.
        var temporary = path + ".part";
        if (release.Size is long expected && File.Exists(temporary) && new FileInfo(temporary).Length > expected)
            File.Delete(temporary);
        string? etag = release.ETag;
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
            if (release.Size is long knownSize && offset == knownSize)
            {
                File.Move(temporary, path, true);
                return (path, await MetadataAsync(path, release with { ETag = etag }, ct));
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, release.Url);
                if (offset > 0)
                {
                    // If-Range prevents appending bytes from a newer upstream
                    // archive to the previous partial download.
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    if (etag is not null) request.Headers.TryAddWithoutValidation("If-Range", etag);
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset)
                    throw new InvalidDataException("G-NAF server returned an unexpected byte range");
                if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidDataException("G-NAF server does not support a resumable response");
                // A normal 200 response restarts from byte zero; only a valid
                // 206 response can be appended to the existing .part file.
                var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                etag = response.Headers.ETag?.Tag ?? etag;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temporary, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct);
                var length = new FileInfo(temporary).Length;
                if (release.Size is long size && length != size)
                    throw new IOException($"Download incomplete: {length:N0} of {size:N0} bytes");
                File.Move(temporary, path, true);
                return (path, await MetadataAsync(path, release with { ETag = etag }, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 10)), ct);
            }
        }
        throw new IOException("G-NAF download did not complete after 200 attempts; partial file retained for retry");
    }

    private static async Task<GnafSourceMetadata> MetadataAsync(string path, GnafRelease release, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new GnafSourceMetadata(release.Url.ToString(), release.Filename, release.ETag,
            release.LastModified, Convert.ToHexStringLower(hash), stream.Length);
    }
}
