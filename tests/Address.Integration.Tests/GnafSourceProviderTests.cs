using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Address.Data;
using Xunit;

namespace Address.Integration.Tests;

/// <summary>Uses fake HTTP responses to verify catalogue filtering and resumable downloads.</summary>
public sealed class GnafSourceProviderTests
{
    [Fact]
    public async Task CatalogueSelectsLatestOfficialGda2020Zip()
    {
        const string catalogue = """
            {"success":true,"result":{"resources":[
              {"name":"G-NAF May 2026 GDA2020","format":"ZIP","url":"https://example.test/may.zip","size":100},
              {"name":"G-NAF August 2026 GDA2020","format":"ZIP","url":"https://example.test/aug.zip","size":200},
              {"name":"G-NAF September 2026 GDA94","format":"ZIP","url":"https://example.test/other.zip","size":300}
            ]}}
            """;
        using var client = new HttpClient(new CatalogueHandler(catalogue));
        var provider = new DataGovAuGnafSourceProvider(client, new Uri("https://example.test/catalogue"));

        var release = await provider.CheckLatestAsync(default);

        release.Version.Should().Be("2026_08");
        release.Url.Should().Be(new Uri("https://example.test/aug.zip"));
        release.Size.Should().Be(200);
        (await provider.FindReleaseAsync("2026_05", default)).Url
            .Should().Be(new Uri("https://example.test/may.zip"));
    }

    [Fact]
    public async Task ArchivedNovemberReleaseCanBeSelectedWithoutChangingLatest()
    {
        const string current = """
            {"success":true,"result":{"resources":[
              {"name":"AUG 2026 - Geoscape G-NAF - GDA2020","format":"ZIP","url":"https://example.test/aug.zip","size":200}
            ]}}
            """;
        const string previous = """
            {"success":true,"result":{"resources":[
              {"name":"NOV 2025 - Geoscape G-NAF - GDA94","format":"ZIP","url":"https://example.test/wrong-datum.zip","size":100},
              {"name":"AUG 2026 - Geoscape G-NAF - GDA2020","format":"ZIP","url":"https://example.test/archive-aug.zip","size":200},
              {"name":"NOV 2025 - Geoscape G-NAF - GDA2020","format":"ZIP","url":"https://example.test/archive-nov.zip","size":101},
              {"name":"MAY 2025 - Geoscape G-NAF - GDA2020","format":"ZIP","url":"https://example.test/archive-may.zip","size":99}
            ]}}
            """;
        using var client = new HttpClient(new MappedCatalogueHandler(current, previous));
        var provider = new DataGovAuGnafSourceProvider(client, new Uri("https://example.test/current"),
            new Uri("https://example.test/previous"));

        (await provider.CheckLatestAsync(default)).Version.Should().Be("2026_08");
        var available = await provider.ListAvailableAsync(default);
        available.Select(x => x.Version).Should().Equal("2026_08", "2025_11", "2025_05");
        (await provider.FindReleaseAsync("2025_11", default)).Url
            .Should().Be(new Uri("https://example.test/archive-nov.zip"));
        (await provider.FindReleaseAsync("2026_08", default)).Url
            .Should().Be(new Uri("https://example.test/aug.zip"));
        await FluentActions.Invoking(() => provider.FindReleaseAsync("2025_12", default))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("{\"success\":false,\"result\":{\"resources\":[]}}")]
    [InlineData("{\"success\":true,\"result\":{\"resources\":[{\"name\":\"G-NAF May 2026 GDA94\",\"format\":\"ZIP\",\"url\":\"https://example.test/old.zip\"}]}}")]
    [InlineData("{\"success\":true,\"result\":{\"resources\":[{\"name\":\"G-NAF May 2026 GDA2020\",\"format\":\"ZIP\",\"url\":\"http://example.test/insecure.zip\"}]}}")]
    public async Task CatalogueRejectsFailedMissingOrInsecureRelease(string catalogue)
    {
        using var client = new HttpClient(new CatalogueHandler(catalogue));
        var provider = new DataGovAuGnafSourceProvider(client, new Uri("https://example.test/catalogue"));

        await FluentActions.Invoking(() => provider.CheckLatestAsync(default))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task PartialDownloadResumesFromStoredOffset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gnaf-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new RangeHandler());
            var provider = new DataGovAuGnafSourceProvider(client, new Uri("https://example.test/catalogue"));
            var release = new GnafRelease("2026_08", new Uri("https://example.test/gnaf.zip"), "gnaf.zip", 9, null, null);
            var (path, metadata) = await provider.DownloadAsync(release, directory, default);
            (await File.ReadAllTextAsync(path)).Should().Be("ABCDEFGHI");
            metadata.FileSize.Should().Be(9);
            File.Exists(path + ".part").Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private int calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1)
            {
                request.Headers.Range.Should().BeNull();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes("ABCD"))
                });
            }
            request.Headers.Range?.Ranges.Single().From.Should().Be(4);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("EFGHI"))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, 8, 9);
            return Task.FromResult(response);
        }
    }

    private sealed class CatalogueHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().Be(new Uri("https://example.test/catalogue"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }

    private sealed class MappedCatalogueHandler(string current, string previous) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath switch
            {
                "/current" => current,
                "/previous" => previous,
                _ => throw new InvalidOperationException("Unexpected catalogue URL")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
