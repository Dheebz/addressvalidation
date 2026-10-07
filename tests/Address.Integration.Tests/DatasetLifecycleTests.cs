using Address.Core;
using Address.Data;
using FluentAssertions;
using Npgsql;
using System.IO.Compression;
using Xunit;

namespace Address.Integration.Tests;

/// <summary>Exercises migrations, import quality gates, promotion and rollback in a disposable database.</summary>
public sealed class DatasetLifecycleTests
{
    [Fact]
    public async Task FailedCandidatePreservesActiveAndPromotionAndRollbackSwitchViews()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ADDRESS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnection))
            throw new InvalidOperationException("ADDRESS_TEST_CONNECTION must point to a disposable PostgreSQL instance");
        // A unique database keeps destructive schema/drop assertions isolated
        // from the installed national dataset and from other test runs.
        var databaseName = "address_test_" + Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(Path.GetTempPath(), $"gnaf-fixture-{Guid.NewGuid():N}.zip");
        var invalidFixture = Path.Combine(Path.GetTempPath(), $"gnaf-fixture-{Guid.NewGuid():N}.zip");
        var missingGeocodeFixture = Path.Combine(Path.GetTempPath(), $"gnaf-fixture-{Guid.NewGuid():N}.zip");
        var schemaDriftFixture = Path.Combine(Path.GetTempPath(), $"gnaf-fixture-{Guid.NewGuid():N}.zip");
        var wrongVersionFixture = Path.Combine(Path.GetTempPath(), $"gnaf_2026_05_{Guid.NewGuid():N}.zip");
        var nationalHeaderFixture = Path.Combine(Path.GetTempPath(), $"g-naf_aug26_allstates_gda2020_psv_{Guid.NewGuid():N}.zip");
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = databaseName };
            var database = new Database(testBuilder.ConnectionString);
            await database.MigrateAsync();
            CreateNationalHeaderFixture(nationalHeaderFixture);
            var nationalPreflight = await new GnafArchiveImporter(database)
                .PreflightAsync("2026_08", nationalHeaderFixture, "ALL");
            nationalPreflight.Headers.Should().HaveCount(6);
            nationalPreflight.BaselineVersion.Should().BeNull();
            var retentionSettings = new BulkRetentionSettings(database);
            (await retentionSettings.GetHoursAsync(default)).Should().Be(24);
            await retentionSettings.SetHoursAsync(12, "integration-operator", default);
            (await retentionSettings.GetHoursAsync(default)).Should().Be(12);
            Func<Task> invalidRetention = () => retentionSettings.SetHoursAsync(0, "integration-operator", default);
            await invalidRetention.Should().ThrowAsync<ArgumentOutOfRangeException>();
            await retentionSettings.SetHoursAsync(24, "integration-operator", default);
            var lifecycle = new DatasetLifecycle(database);
            var repository = new PostgresAddressRepository(database);
            var maintenance = new MaintenanceJobs(database);
            var audit = new AdminEvents(database);
            await audit.RecordAsync("integration-operator", "PROMOTE", "REJECTED", "fixture",
                "integration-request", "Release is not ready", default);
            (await audit.RecentAsync(default)).Should().ContainSingle(item =>
                item.Actor == "integration-operator" && item.Action == "PROMOTE" &&
                item.Outcome == "REJECTED" && item.SourceVersion == "fixture" &&
                item.RequestId == "integration-request");
            var a = Address("A", "10 Collins Street Melbourne VIC 3000");
            var b = Address("B", "12 King Street Adelaide SA 5000");

            var source = new GnafRelease("portal_fixture", new Uri("https://example.com/gnaf.zip"),
                "gnaf.zip", 100, null, null);
            var maintenanceId = await maintenance.QueueAsync(source, default);
            (await maintenance.ClaimAsync(default))!.Id.Should().Be(maintenanceId);
            (await maintenance.ClaimAsync(default)).Should().BeNull();
            await maintenance.SetStageAsync(maintenanceId, "IMPORTING", default);
            (await maintenance.LatestAsync(default))!.Stage.Should().Be("IMPORTING");
            await maintenance.MarkInterruptedAsync(default);
            (await maintenance.LatestAsync(default))!.Status.Should().Be("FAILED");
            await maintenance.QueueAsync(source, default);
            var retry = await maintenance.ClaimAsync(default);
            retry.Should().NotBeNull();
            await maintenance.CompleteAsync(retry!.Id, default);
            (await maintenance.LatestAsync(default))!.Status.Should().Be("COMPLETED");
            var failedMaintenanceId = await maintenance.QueueAsync(source, default);
            (await maintenance.ClaimAsync(default))!.Id.Should().Be(failedMaintenanceId);
            await maintenance.FailAsync(failedMaintenanceId, "download failed", default);
            (await maintenance.LatestAsync(default))!.Status.Should().Be("FAILED");
            (await maintenance.LatestAsync(default))!.ErrorMessage.Should().Be("download failed");

            await lifecycle.SeedAsync("release_a", [a], default);
            await lifecycle.PromoteAsync("release_a", default);
            (await repository.GetByIdAsync("A", default)).Should().NotBeNull();
            (await repository.ActiveVersionAsync(default)).Should().Be("release_a");

            Func<Task> failed = () => lifecycle.SeedAsync("bad_release", [], default);
            await failed.Should().ThrowAsync<InvalidOperationException>();
            (await repository.ActiveVersionAsync(default)).Should().Be("release_a");
            (await repository.GetByIdAsync("A", default)).Should().NotBeNull();

            await lifecycle.SeedAsync("release_b", [b], default);
            await MakeLegacyServingTable(database, "gnaf_release_b");
            (await repository.GetByIdAsync("B", default)).Should().BeNull();
            var jobs = new BulkJobRepository(database);
            // Re-running the migration must upgrade an older queue constraint
            // without disturbing existing jobs or the active address release.
            await using (var legacy = await database.DataSource.OpenConnectionAsync())
            await using (var downgrade = new NpgsqlCommand("""
                ALTER TABLE platform.bulk_job DROP CONSTRAINT bulk_job_operation_check;
                ALTER TABLE platform.bulk_job ADD CONSTRAINT bulk_job_operation_check
                    CHECK (operation IN ('ID','MATCH','VALIDATE','FORMAT'));
                ALTER TABLE platform.admin_event DROP CONSTRAINT admin_event_action_check;
                ALTER TABLE platform.admin_event ADD CONSTRAINT admin_event_action_check
                    CHECK (action IN ('PREPARE','PROMOTE','ROLLBACK','BULK_RETENTION'));
                """, legacy))
                await downgrade.ExecuteNonQueryAsync();
            await database.MigrateAsync();
            await audit.RecordAsync("integration-operator", "DELETE_RELEASE", "SUCCEEDED", "release_a",
                "delete-request", "Previous national release deleted", default);
            (await audit.RecentAsync(default)).Should().ContainSingle(item =>
                item.Action == "DELETE_RELEASE" && item.SourceVersion == "release_a" &&
                item.Outcome == "SUCCEEDED" && item.RequestId == "delete-request");
            var suggestJobId = Guid.NewGuid();
            await jobs.CreateAsync(suggestJobId, "SUGGEST", "input/suggest.ndjson", default, 0.7, 8,
                "integration-user", new string('a', 64));
            var suggestJob = await jobs.GetAsync(suggestJobId, default);
            suggestJob!.MinScore.Should().Be(0.7);
            suggestJob.SuggestionLimit.Should().Be(8);
            suggestJob.RequestedBy.Should().Be("integration-user");
            suggestJob.AccessTokenHash.Should().Be(new string('a', 64));
            (await jobs.AcquireAsync(default))!.Id.Should().Be(suggestJobId);
            await jobs.CompleteAsync(suggestJobId, "output/suggest.ndjson", 1, 0, default);
            var jobId = Guid.NewGuid();
            await jobs.CreateAsync(jobId, "VALIDATE", "input/test.ndjson", default);
            (await jobs.AcquireAsync(default))!.Id.Should().Be(jobId);
            Func<Task> promoteDuringJob = () => lifecycle.PromoteAsync("release_b", default);
            await promoteDuringJob.Should().ThrowAsync<InvalidOperationException>();
            await jobs.ProgressAsync(jobId, 2, 1, default);
            await jobs.CompleteAsync(jobId, "output/test.ndjson", 2, 1, default);
            var completed = await jobs.GetAsync(jobId, default);
            completed!.Status.Should().Be("COMPLETED");
            completed.ProcessedRecords.Should().Be(2);
            completed.FailedRecords.Should().Be(1);
            var failedJobId = Guid.NewGuid();
            await jobs.CreateAsync(failedJobId, "MATCH", "input/failed.ndjson", default);
            (await jobs.AcquireAsync(default))!.Id.Should().Be(failedJobId);
            await jobs.FailAsync(failedJobId, "invalid upload", default);
            (await jobs.GetAsync(failedJobId, default))!.Status.Should().Be("FAILED");
            (await jobs.GetAsync(failedJobId, default))!.ErrorMessage.Should().Be("invalid upload");
            var queuedJobId = Guid.NewGuid();
            await jobs.CreateAsync(queuedJobId, "ID", "input/queued.ndjson", default);
            await using (var check = await database.DataSource.OpenConnectionAsync())
            await using (var age = new NpgsqlCommand("UPDATE platform.bulk_job SET completed_at=now()-interval '25 hours' WHERE id = ANY(@ids)", check))
            {
                age.Parameters.AddWithValue("ids", new[] { jobId, failedJobId });
                await age.ExecuteNonQueryAsync();
            }
            var cutoff = DateTime.UtcNow.AddHours(-await retentionSettings.GetHoursAsync(default));
            (await jobs.ExpiredAsync(cutoff, 10, default)).Select(job => job.Id)
                .Should().BeEquivalentTo(new[] { jobId, failedJobId });
            await using (var check = await database.DataSource.OpenConnectionAsync())
            await using (var age = new NpgsqlCommand("UPDATE platform.bulk_job SET created_at=now()-interval '25 hours' WHERE id=@id", check))
            {
                age.Parameters.AddWithValue("id", queuedJobId);
                await age.ExecuteNonQueryAsync();
            }
            (await jobs.FailAbandonedAsync(cutoff, default)).Should().Be(1);
            (await jobs.ExpiredAsync(cutoff, 10, default)).Select(job => job.Id)
                .Should().BeEquivalentTo(new[] { jobId, failedJobId, queuedJobId });
            await jobs.DeleteExpiredAsync(jobId, cutoff, default);
            await jobs.DeleteExpiredAsync(failedJobId, cutoff, default);
            await jobs.DeleteExpiredAsync(queuedJobId, cutoff, default);
            (await jobs.GetAsync(jobId, default)).Should().BeNull();
            (await jobs.GetAsync(failedJobId, default)).Should().BeNull();
            (await jobs.GetAsync(queuedJobId, default)).Should().BeNull();
            await lifecycle.PromoteAsync("release_b", default);
            (await repository.ActiveVersionAsync(default)).Should().Be("release_b");
            (await repository.GetByIdAsync("B", default)).Should().NotBeNull();
            (await repository.GetByIdAsync("A", default)).Should().BeNull();

            await MakeLegacyServingTable(database, "gnaf_release_a");
            await lifecycle.RollbackAsync(default);
            (await repository.ActiveVersionAsync(default)).Should().Be("release_a");
            (await repository.GetByIdAsync("A", default)).Should().NotBeNull();
            (await repository.GetByIdAsync("B", default)).Should().BeNull();

            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("UPDATE platform.dataset_version SET dataset_name='G-NAF',scope='ALL' WHERE source_version IN ('release_a','release_b')", check).ExecuteNonQueryAsync();
            await lifecycle.SeedAsync("release_c", [b], default);
            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("UPDATE platform.dataset_version SET dataset_name='G-NAF',scope='ALL' WHERE source_version='release_c'", check).ExecuteNonQueryAsync();
            await lifecycle.PromoteAsync("release_c", default);
            (await lifecycle.ListAsync(default)).Should().NotContain(x => x.SourceVersion == "release_b");
            await using (var check = await database.DataSource.OpenConnectionAsync())
                (await new NpgsqlCommand("SELECT to_regnamespace('gnaf_release_b') IS NULL", check).ExecuteScalarAsync()).Should().Be(true);
            (await repository.GetByIdAsync("B", default)).Should().NotBeNull();

            // Explicit storage cleanup accepts only a retired full national
            // release. It cannot remove the live view or leave alias rows.
            Func<Task> deleteLive = () => lifecycle.DeleteRetiredNationalAsync("release_c", default);
            await deleteLive.Should().ThrowAsync<InvalidOperationException>();
            Func<Task> deleteAlreadyPruned = () => lifecycle.DeleteRetiredNationalAsync("release_b", default);
            await deleteAlreadyPruned.Should().ThrowAsync<InvalidOperationException>();
            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("""
                    INSERT INTO platform.street_alias VALUES ('release_a','COLLINS','ST','COLLINS','ST','MELBOURNE','VIC');
                    INSERT INTO platform.locality_alias VALUES ('release_a','MELBOURNE','MELBOURNE','VIC');
                    """, check).ExecuteNonQueryAsync();
            await lifecycle.DeleteRetiredNationalAsync("release_a", default);
            (await lifecycle.ListAsync(default)).Should().NotContain(x => x.SourceVersion == "release_a");
            (await repository.ActiveVersionAsync(default)).Should().Be("release_c");
            (await repository.GetByIdAsync("B", default)).Should().NotBeNull();
            await using (var check = await database.DataSource.OpenConnectionAsync())
            {
                (await new NpgsqlCommand("SELECT to_regnamespace('gnaf_release_a') IS NULL", check).ExecuteScalarAsync()).Should().Be(true);
                (await new NpgsqlCommand("SELECT count(*) FROM platform.street_alias WHERE source_version='release_a'", check).ExecuteScalarAsync()).Should().Be(0L);
                (await new NpgsqlCommand("SELECT count(*) FROM platform.locality_alias WHERE source_version='release_a'", check).ExecuteScalarAsync()).Should().Be(0L);
            }
            Func<Task> rollbackAfterDeletion = () => lifecycle.RollbackAsync(default);
            await rollbackAfterDeletion.Should().ThrowAsync<InvalidOperationException>();

            CreateFixture(fixture, includeGeocode: true, includeSecondAddress: true, includeUnsupportedAddress: true);
            var importer = new GnafArchiveImporter(database);
            var report = await importer.ImportAsync("real_fixture", fixture, "ACT");
            report.Addresses.Should().Be(2);
            report.StreetAliases.Should().Be(1);
            report.LocalityAliases.Should().Be(1);
            report.GeocodeCoverage.Should().Be(1);
            await using (var check = await database.DataSource.OpenConnectionAsync())
            {
                var rawCount = await new NpgsqlCommand("SELECT count(*) FROM gnaf_real_fixture.raw_address_detail", check).ExecuteScalarAsync();
                rawCount.Should().Be(3L);
            }
            (await repository.GetByIdAsync("B", default)).Should().NotBeNull();
            await lifecycle.PromoteAsync("real_fixture", default);
            (await repository.GetByIdAsync("GNAF-FIXTURE-1", default)).Should().NotBeNull();
            await using (var check = await database.DataSource.OpenConnectionAsync())
            {
                await new NpgsqlCommand("UPDATE platform.dataset_version SET scope='ALL' WHERE source_version='real_fixture'", check).ExecuteNonQueryAsync();
            }
            (await lifecycle.PruneCompletedRawAsync(default)).Should().BeGreaterThan(0);
            (await lifecycle.PruneCompletedRawAsync(default)).Should().Be(0);
            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("UPDATE platform.dataset_version SET scope='ACT' WHERE source_version='real_fixture'", check).ExecuteNonQueryAsync();
            var rebuiltAliases = await importer.EnrichAliasesAsync("real_fixture", fixture);
            rebuiltAliases.Should().Be((1L, 1L));
            var aliasMatch = await new AddressService(repository).MatchAsync(
                "10 Royal Street Capital ACT 2600", default);
            aliasMatch.Address!.GnafPid.Should().Be("GNAF-FIXTURE-1");
            aliasMatch.MatchType.Should().Be("ALIAS");
            aliasMatch.Confidence.Should().Be(1);
            (await new AddressService(repository).ValidateAsync(
                "11 Royal Street Capital ACT 2600", default)).Valid.Should().BeFalse();
            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("INSERT INTO platform.locality_alias VALUES ('real_fixture','CAPITAL','OTHER','ACT')", check).ExecuteNonQueryAsync();
            (await new AddressService(repository).ValidateAsync(
                "10 Royal Street Capital ACT 2600", default)).Valid.Should().BeFalse();
            await using (var check = await database.DataSource.OpenConnectionAsync())
                await new NpgsqlCommand("DELETE FROM platform.locality_alias WHERE source_version='real_fixture' AND alias_name='CAPITAL' AND canonical_name='OTHER'", check).ExecuteNonQueryAsync();
            await using (var check = await database.DataSource.OpenConnectionAsync())
            {
                var rawTableMissing = await new NpgsqlCommand("SELECT to_regclass('gnaf_real_fixture.raw_address_detail') IS NULL", check).ExecuteScalarAsync();
                rawTableMissing.Should().Be(true);
                await new NpgsqlCommand("UPDATE platform.dataset_version SET scope='ACT' WHERE source_version='real_fixture'", check).ExecuteNonQueryAsync();
            }
            (await repository.GetByIdAsync("GNAF-FIXTURE-RETIRED", default)).Should().BeNull();
            Func<Task> rollbackToUnrelatedSample = () => lifecycle.RollbackAsync(default);
            await rollbackToUnrelatedSample.Should().ThrowAsync<InvalidOperationException>();
            var suite = await repository.GetByIdAsync("GNAF-FIXTURE-2", default);
            suite.Should().NotBeNull();
            suite!.FlatType.Should().Be("SE");
            suite.FlatNumberSuffix.Should().Be("C");
            suite.LevelType.Should().Be("L");
            suite.FormattedAddress.Should().StartWith("SUITE 18C LEVEL 1");
            var suiteMatch = await new AddressService(repository).ValidateAsync(
                "Suite18C Floor 1 12 King St Canberra ACT 2600", default);
            suiteMatch.Valid.Should().BeTrue();
            suiteMatch.Address!.GnafPid.Should().Be("GNAF-FIXTURE-2");

            CreateFixture(schemaDriftFixture, includeGeocode: true, includeSecondAddress: true);
            using (var changed = ZipFile.Open(schemaDriftFixture, ZipArchiveMode.Update))
            {
                const string entryName = "Standard/ACT_ADDRESS_DETAIL_psv.psv";
                var original = changed.GetEntry(entryName)!;
                string content;
                using (var reader = new StreamReader(original.Open())) content = reader.ReadToEnd();
                original.Delete();
                using var writer = new StreamWriter(changed.CreateEntry(entryName).Open());
                writer.Write(content.Replace("CONFIDENCE\n", "CONFIDENCE|NEW_FIELD\n"));
            }
            Func<Task> schemaDrift = () => importer.ImportAsync("real_drift", schemaDriftFixture, "ACT");
            await schemaDrift.Should().ThrowAsync<InvalidDataException>().WithMessage("*Schema drift*");
            (await lifecycle.ListAsync(default)).Should().NotContain(x => x.SourceVersion == "real_drift");
            File.Copy(fixture, wrongVersionFixture);
            Func<Task> wrongVersion = () => importer.ImportAsync("2026_08", wrongVersionFixture, "ACT");
            await wrongVersion.Should().ThrowAsync<InvalidDataException>().WithMessage("*identifies 2026_05*");
            (await lifecycle.ListAsync(default)).Should().NotContain(x => x.SourceVersion == "2026_08");

            CreateFixture(invalidFixture, includeGeocode: true);
            Func<Task> failedQuality = () => importer.ImportAsync("real_bad", invalidFixture, "ACT");
            await failedQuality.Should().ThrowAsync<InvalidDataException>();
            (await repository.ActiveVersionAsync(default)).Should().Be("real_fixture");
            (await repository.GetByIdAsync("GNAF-FIXTURE-1", default)).Should().NotBeNull();
            (await lifecycle.ListAsync(default)).Should().Contain(x => x.SourceVersion == "real_bad" && x.Status == "FAILED");
            await importer.ResetIncompleteCandidateAsync("real_bad");
            (await lifecycle.ListAsync(default)).Should().NotContain(x => x.SourceVersion == "real_bad");
            var retried = await importer.ImportAsync("real_bad", fixture, "ACT");
            retried.Addresses.Should().Be(2);
            (await repository.ActiveVersionAsync(default)).Should().Be("real_fixture");

            CreateFixture(missingGeocodeFixture, includeGeocode: false);
            Func<Task> failedGeocode = () => importer.ImportAsync("real_no_geo", missingGeocodeFixture, "ACT");
            await failedGeocode.Should().ThrowAsync<InvalidDataException>();
            (await repository.ActiveVersionAsync(default)).Should().Be("real_fixture");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            File.Delete(fixture);
            File.Delete(invalidFixture);
            File.Delete(missingGeocodeFixture);
            File.Delete(schemaDriftFixture);
            File.Delete(wrongVersionFixture);
            File.Delete(nationalHeaderFixture);
        }
    }

    [Fact]
    public async Task HistoricalReplaySkipsForwardCountDropAndCanRecheckBuiltCandidate()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ADDRESS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnection))
            throw new InvalidOperationException("ADDRESS_TEST_CONNECTION must point to a disposable PostgreSQL instance");
        var databaseName = "address_test_" + Guid.NewGuid().ToString("N");
        var currentArchive = Path.Combine(Path.GetTempPath(), $"gnaf-current-{Guid.NewGuid():N}.zip");
        var historicalArchive = Path.Combine(Path.GetTempPath(), $"gnaf-historical-{Guid.NewGuid():N}.zip");
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = "postgres" };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = databaseName };
            var database = new Database(testBuilder.ConnectionString);
            await database.MigrateAsync();
            CreateFixture(currentArchive, includeGeocode: true);
            CreateFixture(historicalArchive, includeGeocode: true, includeAliases: false);
            var importer = new GnafArchiveImporter(database);
            var lifecycle = new DatasetLifecycle(database);
            await importer.ImportAsync("2026_08", currentArchive, "ACT");
            await lifecycle.PromoteAsync("2026_08", default);

            var historical = await importer.ImportAsync("2020_05", historicalArchive, "ACT");
            historical.HistoricalReplay.Should().BeTrue();
            historical.StreetAliases.Should().Be(0);
            (await lifecycle.ListAsync(default)).Should().Contain(x => x.SourceVersion == "2020_05" && x.Status == "READY");
            (await lifecycle.ListAsync(default)).Should().Contain(x => x.SourceVersion == "2026_08" && x.Status == "ACTIVE");

            await using (var connection = await database.DataSource.OpenConnectionAsync())
            await using (var markFailed = new NpgsqlCommand("""
                UPDATE platform.dataset_version SET status='FAILED', quality_report=NULL,
                    error_message='street aliases count dropped more than 10.00 % from active release'
                WHERE source_version='2020_05'
                """, connection))
                await markFailed.ExecuteNonQueryAsync();
            string checksum;
            await using (var connection = await database.DataSource.OpenConnectionAsync())
            await using (var readChecksum = new NpgsqlCommand(
                "SELECT checksum FROM platform.dataset_version WHERE source_version='2020_05'", connection))
                checksum = (string)(await readChecksum.ExecuteScalarAsync())!;
            (await importer.TryRevalidateFailedHistoricalAsync("2020_05", "wrong")).Should().BeNull();
            var rechecked = await importer.TryRevalidateFailedHistoricalAsync("2020_05", checksum);
            rechecked.Should().NotBeNull();
            rechecked!.HistoricalReplay.Should().BeTrue();
            (await lifecycle.ListAsync(default)).Should().Contain(x => x.SourceVersion == "2020_05" && x.Status == "READY");

            Func<Task> forwardDrop = () => importer.ImportAsync("2026_11", historicalArchive, "ACT");
            await forwardDrop.Should().ThrowAsync<InvalidDataException>().WithMessage("*count dropped*");
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            File.Delete(currentArchive);
            File.Delete(historicalArchive);
        }
    }

    private static AddressRecord Address(string id, string formatted) =>
        new(id, formatted, "", "10", "COLLINS", "STREET", "MELBOURNE", "VIC", "3000", null, null,
            AddressNormalizer.Normalize(formatted));

    private static async Task MakeLegacyServingTable(Database database, string schema)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            ALTER TABLE {schema}.address_search
                DROP COLUMN flat_type, DROP COLUMN flat_number_prefix,
                DROP COLUMN flat_number_suffix, DROP COLUMN level_type,
                DROP COLUMN level_number, DROP COLUMN level_number_prefix,
                DROP COLUMN level_number_suffix, DROP COLUMN building_name
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void CreateFixture(string path, bool includeGeocode, bool includeSecondAddress = false,
        bool includeUnsupportedAddress = false, bool includeAliases = true)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string data)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(data);
        }
        Add("Authority Code/Authority_Code_STREET_TYPE_AUT_psv.psv", "CODE|NAME\nST|STREET\n");
        Add("Standard/ACT_STATE_psv.psv", "STATE_PID|STATE_ABBREVIATION\nACT1|ACT\n");
        Add("Standard/ACT_LOCALITY_psv.psv", "LOCALITY_PID|LOCALITY_NAME|STATE_PID\nLOC1|CANBERRA|ACT1\n");
        Add("Standard/ACT_STREET_LOCALITY_psv.psv", "STREET_LOCALITY_PID|STREET_NAME|STREET_TYPE_CODE|LOCALITY_PID\nSL1|KING|ST|LOC1\n");
        Add("Standard/ACT_STREET_LOCALITY_ALIAS_psv.psv", "STREET_LOCALITY_ALIAS_PID|STREET_LOCALITY_PID|STREET_NAME|STREET_TYPE_CODE\n" +
            (includeAliases ? "SLA1|SL1|ROYAL|ST\n" : ""));
        Add("Standard/ACT_LOCALITY_ALIAS_psv.psv", "LOCALITY_ALIAS_PID|LOCALITY_PID|NAME|STATE_PID\n" +
            (includeAliases ? "LA1|LOC1|CAPITAL|ACT1\n" : ""));
        Add("Standard/ACT_ADDRESS_DETAIL_psv.psv", "ADDRESS_DETAIL_PID|LOCALITY_PID|STREET_LOCALITY_PID|FLAT_TYPE_CODE|FLAT_NUMBER_PREFIX|FLAT_NUMBER|FLAT_NUMBER_SUFFIX|LEVEL_TYPE_CODE|LEVEL_NUMBER_PREFIX|LEVEL_NUMBER|LEVEL_NUMBER_SUFFIX|BUILDING_NAME|NUMBER_FIRST|NUMBER_LAST|POSTCODE|CONFIDENCE\nGNAF-FIXTURE-1|LOC1|SL1||||||||||10||2600|2\n" +
            (includeSecondAddress ? "GNAF-FIXTURE-2|LOC1|SL1|SE||18|C|L||1|||12||2600|2\n" : "") +
            (includeUnsupportedAddress ? "GNAF-FIXTURE-RETIRED|LOC1|SL1||||||||||14||2600|-1\n" : ""));
        Add("Standard/ACT_ADDRESS_DEFAULT_GEOCODE_psv.psv", "ADDRESS_DETAIL_PID|LATITUDE|LONGITUDE\n" +
            (includeGeocode ? "GNAF-FIXTURE-1|-35.2800|149.1300\n" +
                (includeSecondAddress ? "GNAF-FIXTURE-2|-35.2810|149.1310\n" : "") : ""));
    }

    private static void CreateNationalHeaderFixture(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string header)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.WriteLine(header);
        }
        Add("Authority_Code_STREET_TYPE_AUT_psv.psv", "CODE|NAME");
        foreach (var state in new[] { "ACT", "NSW", "NT", "QLD", "SA", "TAS", "VIC", "WA" })
        {
            Add($"{state}_STATE_psv.psv", "STATE_PID|STATE_ABBREVIATION");
            Add($"{state}_LOCALITY_psv.psv", "LOCALITY_PID|LOCALITY_NAME|STATE_PID");
            Add($"{state}_STREET_LOCALITY_psv.psv", "STREET_LOCALITY_PID|STREET_NAME|STREET_TYPE_CODE|LOCALITY_PID");
            Add($"{state}_ADDRESS_DEFAULT_GEOCODE_psv.psv", "ADDRESS_DETAIL_PID|LATITUDE|LONGITUDE");
            Add($"{state}_ADDRESS_DETAIL_psv.psv", "ADDRESS_DETAIL_PID|LOCALITY_PID|STREET_LOCALITY_PID|FLAT_TYPE_CODE|FLAT_NUMBER_PREFIX|FLAT_NUMBER|FLAT_NUMBER_SUFFIX|LEVEL_TYPE_CODE|LEVEL_NUMBER_PREFIX|LEVEL_NUMBER|LEVEL_NUMBER_SUFFIX|BUILDING_NAME|NUMBER_FIRST|NUMBER_LAST|POSTCODE|CONFIDENCE");
        }
    }
}
