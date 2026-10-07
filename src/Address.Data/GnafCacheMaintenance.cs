using System.Text.RegularExpressions;

namespace Address.Data;

/// <summary>Deletes only recognised source-cache files after release data is retained in PostgreSQL.</summary>
public static class GnafCacheMaintenance
{
    private static readonly Regex ManagedArchive = new(
        @"^(\d{4}_\d{2})_(?:gda2020\.zip(?:\.part)?|raw_tables\.dump)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Removes managed archives no longer needed for a candidate retry.</summary>
    public static int RemoveObsolete(string cacheDirectory, IReadOnlyList<DatasetVersion> releases,
        TimeSpan? minimumAge = null)
    {
        if (!Directory.Exists(cacheDirectory)) return 0;
        // Keep source files only while a candidate may need an import retry or
        // rebuild. Active and retired releases keep their serving tables; their
        // official ZIP can be downloaded again if a rebuild is ever required.
        var retained = releases.Where(r => r.Status is "READY" or "CREATING_SCHEMA" or "DOWNLOADING" or
            "CHECKING_SCHEMA" or "LOADING" or "TRANSFORMING" or "BUILDING_INDEXES" or "VALIDATING")
            .Select(r => r.SourceVersion).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        // Ignore every filename outside this exact managed pattern so an
        // operator's unrelated files in the volume are not removed.
        foreach (var path in Directory.EnumerateFiles(cacheDirectory))
        {
            var match = ManagedArchive.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            // Periodic sweeps leave recent files alone in case a separate loader
            // is still writing them. Release-switch cleanup retains its immediate behavior.
            if (minimumAge is { } age && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - age) continue;
            var completeZip = path.EndsWith(".zip.part", StringComparison.Ordinal) &&
                File.Exists(path[..^5]);
            if (retained.Contains(match.Groups[1].Value) && !completeZip) continue;
            File.Delete(path);
            removed++;
        }
        return removed;
    }
}
