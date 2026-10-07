using Address.Data;
using FluentAssertions;
using Xunit;

namespace Address.Integration.Tests;

/// <summary>Guards managed cache cleanup from deleting retained candidates or unrelated files.</summary>
public sealed class GnafCacheMaintenanceTests
{
    [Fact]
    public void PeriodicSweepLeavesRecentAndInProgressArchivesAlone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnaf-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stale = Path.Combine(directory, "2025_02_gda2020.zip");
            var recent = Path.Combine(directory, "2025_05_gda2020.zip.part");
            var downloading = Path.Combine(directory, "2025_08_gda2020.zip.part");
            foreach (var path in new[] { stale, recent, downloading }) File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(downloading, DateTime.UtcNow.AddDays(-2));
            var releases = new[] { new DatasetVersion("2025_08", "gnaf_2025_08", "DOWNLOADING", null) };

            GnafCacheMaintenance.RemoveObsolete(directory, releases, TimeSpan.FromHours(24)).Should().Be(1);
            File.Exists(stale).Should().BeFalse();
            File.Exists(recent).Should().BeTrue();
            File.Exists(downloading).Should().BeTrue();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RemovesOnlyManagedArchivesOutsideRetainedReleases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnaf-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[]
            {
                "2026_08_gda2020.zip", "2026_08_gda2020.zip.part", "2026_05_raw_tables.dump",
                "2026_03_gda2020.zip", "2026_02_gda2020.zip", "2026_02_gda2020.zip.part",
                "2026_01_gda2020.zip", "notes.txt"
            }) File.WriteAllText(Path.Combine(directory, name), "fixture");
            var retained = new[]
            {
                new DatasetVersion("2026_08", "gnaf_2026_08", "ACTIVE", null),
                new DatasetVersion("2026_05", "gnaf_2026_05", "RETIRED", null),
                new DatasetVersion("2026_03", "gnaf_2026_03", "READY", null),
                new DatasetVersion("2026_01", "gnaf_2026_01", "FAILED", null)
            };
            GnafCacheMaintenance.RemoveObsolete(directory, retained).Should().Be(6);
            Directory.GetFiles(directory).Select(Path.GetFileName).Should().BeEquivalentTo(
                "2026_03_gda2020.zip", "notes.txt");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
