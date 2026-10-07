using Address.Data;
using FluentAssertions;
using Gnaf.Admin;
using Xunit;

namespace Address.Integration.Tests;

/// <summary>Checks release-control eligibility, form-token rendering, and HTML escaping.</summary>
public sealed class AdminPageTests
{
    [Fact]
    public void StorageMaintenanceStatusIsVisibleToOperator()
    {
        var html = AdminPage.Render([], null, true, [], null,
            new ClusterStatus([], 0, 0, "unavailable", "Unavailable", "Unavailable"),
            true, "", "", housekeeping: new HousekeepingSnapshot(DateTime.UtcNow,
                "attention", 0, 1024, 10240, 2, "Local storage is running low."));

        html.Should().Contain("Storage needs attention");
        html.Should().Contain("Local storage is running low.");
        html.Should().Contain("Files removed in last sweep: 2");
    }

    [Fact]
    public void OnlyFullNationalReadyReleaseCanBeActivated()
    {
        var html = Render([
            Release("2026_08", "G-NAF", "ALL", "ACTIVE"),
            Release("sample_1", "SYNTHETIC", "SAMPLE", "READY")
        ]);

        html.Should().Contain("Other local datasets (1)");
        html.Should().NotContain("/promote/sample_1");
        html.Should().Contain("No prepared national release is waiting for review");
    }

    [Fact]
    public void NationalReadyReleaseAppearsForReview()
    {
        var html = Render([
            Release("2026_08", "G-NAF", "ALL", "ACTIVE"),
            Release("2026_11", "G-NAF", "ALL", "READY")
        ]);

        html.Should().Contain("/promote/2026_11");
        html.Should().Contain("Activate November 2026");
    }

    [Fact]
    public void HistoricalReleaseExplainsCountComparisonAndRetry()
    {
        var now = DateTime.UtcNow;
        var failedJob = new MaintenanceJob(Guid.NewGuid(), "2020_05", "https://example.com/archive.zip",
            "archive.zip", null, "FAILED", "FAILED",
            "address aliases count dropped more than 10.00 % from active release", now, now, now);
        var versions = new[] { Release("2026_08", "G-NAF", "ALL", "ACTIVE") };
        var failedHtml = AdminPage.Render(versions, failedJob, true, [], null,
            new ClusterStatus([], 0, 0, "unavailable", "Unavailable", "Unavailable"), true, "", "");
        failedHtml.Should().Contain("rechecked from its imported data");

        var readyHtml = Render([versions[0], Release("2020_05", "G-NAF", "ALL", "READY")]);
        readyHtml.Should().Contain("Historical replay: a lower count");
        readyHtml.Should().Contain("Activate May 2020");
    }

    [Fact]
    public void ReleasePickerComesBeforeInfrastructureAndSubmitsExactVersion()
    {
        var html = Render([Release("2026_08", "G-NAF", "ALL", "ACTIVE")]);

        html.Should().Contain("Refresh official releases");
        html.Should().Contain("id=\"check-latest\" type=\"button\" disabled>Check latest");
        html.Should().Contain("id=\"release-select\"");
        html.Should().Contain("name=\"version\" id=\"prepare-version\"");
        html.IndexOf("Choose and prepare a release", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("Database and failover status", StringComparison.Ordinal));
    }

    [Fact]
    public void OperatorMessagesAreHtmlEncoded()
    {
        var html = Render([], notice: "<script>notice</script>", error: "<script>error</script>");

        html.Should().NotContain("<script>notice</script>");
        html.Should().NotContain("<script>error</script>");
        html.Should().Contain("&lt;script&gt;notice&lt;/script&gt;");
        html.Should().Contain("&lt;script&gt;error&lt;/script&gt;");
    }

    [Fact]
    public void ReleaseFormsContainEncodedAntiforgeryToken()
    {
        var html = AdminPage.Render([
                Release("2026_08", "G-NAF", "ALL", "ACTIVE"),
                Release("2026_11", "G-NAF", "ALL", "READY"),
                Release("2026_05", "G-NAF", "ALL", "RETIRED")
            ], null, true, [], null,
            new ClusterStatus([], 0, 0, "unavailable", "Unavailable", "Unavailable"),
            true, "", "", "token'<> ");

        html.Should().Contain("name='__RequestVerificationToken' value='token&#39;&lt;&gt; '");
        html.Split("name='__RequestVerificationToken'").Length.Should().Be(6);
        html.Should().Contain("action=\"/settings/bulk-retention\"");
        html.Should().Contain("action=\"/releases/2026_05/delete\"");
        html.Should().Contain("<option value='24' selected>24 hours</option>");
    }

    [Fact]
    public void OnlyPreviousNationalReleaseHasDeletionControl()
    {
        var html = Render([
            Release("2026_08", "G-NAF", "ALL", "ACTIVE"),
            Release("2026_05", "G-NAF", "ALL", "RETIRED"),
            Release("sample_1", "SYNTHETIC", "SAMPLE", "RETIRED")
        ]);

        html.Should().Contain("Delete May 2026 data");
        html.Should().Contain("name=\"confirmVersion\"");
        html.Should().Contain("/releases/2026_05/delete");
        html.Should().NotContain("/releases/2026_08/delete");
        html.Should().NotContain("/releases/sample_1/delete");
    }

    [Fact]
    public void CompletedPreparationShowsLiveOnceActivated()
    {
        var now = DateTime.UtcNow;
        var job = new MaintenanceJob(Guid.NewGuid(), "2026_08", "https://example.com/archive.zip",
            "archive.zip", null, "COMPLETED", "READY", null, now, now, now);
        var html = AdminPage.Render([
                Release("2026_08", "G-NAF", "ALL", "ACTIVE"),
                Release("2026_05", "G-NAF", "ALL", "RETIRED")
            ], job, true, [], null,
            new ClusterStatus([], 0, 0, "unavailable", "Unavailable", "Unavailable"),
            true, "", "");

        html.Should().Contain("This release is live.");
        html.Should().NotContain("Review the release below before activating it.");
    }

    [Fact]
    public void AuditHistoryEncodesActorAndRequestId()
    {
        var html = AdminPage.Render([], null, true, [], null,
            new ClusterStatus([], 0, 0, "unavailable", "Unavailable", "Unavailable"),
            true, "", "", "token", [new AdminEvent(Guid.NewGuid(), DateTime.UtcNow,
                "<script>actor</script>", "PROMOTE", "SUCCEEDED", "2026_08", "<request>", "Release activated")]);

        html.Should().Contain("&lt;script&gt;actor&lt;/script&gt;");
        html.Should().Contain("&lt;request&gt;");
        html.Should().NotContain("<script>actor</script>");
    }

    private static DatasetDetail Release(string version, string name, string scope, string status) =>
        new(version, name, status, scope, 15_000_000, 5_000_000_000, 1_700_000_000,
            null, DateTime.UtcNow, null, 1.0, null);

    private static string Render(IReadOnlyList<DatasetDetail> releases, string notice = "", string error = "") =>
        AdminPage.Render(releases, null, true,
            [new ApiReplicaStatus("API A", "address-api", true), new ApiReplicaStatus("API B", "address-api-b", true)],
            12_000_000_000,
            new ClusterStatus([new DatabaseNodeStatus("Database A", "postgres", "primary", true, true, 0),
                new DatabaseNodeStatus("Database B", "postgres-b", "standby", true, false, null)],
                3, 3, "healthy", "Automatic takeover ready", "Both databases are online"),
            true, notice, error);
}
