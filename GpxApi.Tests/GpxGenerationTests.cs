using System.Text.Json;
using GpxApi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpxApi.Tests;

public class GpxGenerationTests
{
    private static StravaActivitySyncService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var provider = new ServiceCollection().BuildServiceProvider();
        return new StravaActivitySyncService(
            new FakeHttpClientFactory(_ => throw new InvalidOperationException("HTTP nie powinien być wołany")),
            NullLogger<StravaActivitySyncService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config);
    }

    [Fact]
    public void GenerateGpxFromStreams_TworzyPunktyZWysokosciaCzasemITetnem()
    {
        var service = CreateService();
        var activity = new ActivityRecord
        {
            Name = "Test <bieg>",
            StartDate = new DateTime(2026, 9, 8, 5, 0, 0, DateTimeKind.Utc)
        };
        var streams = JsonDocument.Parse("""
        {
            "latlng":    { "data": [[52.0, 21.0], [52.001, 21.001], [52.002, 21.002]] },
            "time":      { "data": [0, 10, 20] },
            "altitude":  { "data": [100.0, 101.5, 103.0] },
            "heartrate": { "data": [120, 130, 140] }
        }
        """).RootElement;

        var gpx = service.GenerateGpxFromStreams(activity, streams, activityJson: null);

        Assert.Equal(3, CountOccurrences(gpx, "<trkpt "));
        Assert.Contains("<ele>101.5</ele>", gpx);
        Assert.Contains("<time>2026-09-08T05:00:10Z</time>", gpx);
        Assert.Contains("<gpxtpx:hr>130</gpxtpx:hr>", gpx);
        Assert.Contains("Test &lt;bieg&gt;", gpx); // nazwa musi być escapowana w XML
    }

    [Fact]
    public void GenerateGpxFromStreams_BezStreamow_ZwracaPustyTrack()
    {
        var service = CreateService();
        var activity = new ActivityRecord { Name = "Pusta" };
        var streams = JsonDocument.Parse("{}").RootElement;

        var gpx = service.GenerateGpxFromStreams(activity, streams, activityJson: null);

        Assert.DoesNotContain("<trkpt", gpx);
        Assert.Contains("<gpx", gpx);
    }

    private static int CountOccurrences(string text, string fragment)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(fragment, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += fragment.Length;
        }
        return count;
    }
}
