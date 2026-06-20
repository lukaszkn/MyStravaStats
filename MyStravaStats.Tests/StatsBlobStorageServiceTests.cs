using MyStravaStats.Core.Models;
using MyStravaStats.Core.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class StatsBlobStorageServiceTests
{
    [Theory]
    [InlineData("123.json", true)]
    [InlineData("0.json", false)]
    [InlineData("athlete.json", false)]
    [InlineData("123.txt", false)]
    [InlineData("trends/2026/123.json", false)]
    public void IsDashboardSnapshotBlobNameOnlyAllowsTopLevelAthleteJson(string blobName, bool expected)
    {
        Assert.Equal(expected, StatsBlobStorageService.IsDashboardSnapshotBlobName(blobName));
    }

    [Fact]
    public void GetTrendColorIsStableForAthleteId()
    {
        var color = StatsBlobStorageService.GetTrendColor(123);

        Assert.Equal("#fd7e14", color);
        Assert.Equal(color, StatsBlobStorageService.GetTrendColor(123));
    }

    [Fact]
    public void TrendDocumentUsesDashboardTotalForLatestPoint()
    {
        var document = AthleteTrendBlobDocument.FromDashboardState(
            new StravaDashboardState
            {
                Year = 2026,
                AthleteId = 123,
                AthleteName = "Lukasz K",
                OverallTotals = new DashboardTotals
                {
                    DistanceMeters = 834_500d
                },
                TrendPoints =
                [
                    new AthleteTrendPoint
                    {
                        RecordedAt = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.FromHours(2)),
                        TotalKilometers = 829.7d
                    }
                ]
            },
            new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero));

        var latestPoint = Assert.Single(document.Points);
        Assert.Equal(834.5d, latestPoint.TotalKilometers, 3);
    }
}
