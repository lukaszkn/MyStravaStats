using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class StravaStatsServiceTests
{
    [Fact]
    public async Task BuildDashboardStateKeepsStatsBlobDocumentShape()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.StartsWith("https://www.strava.com/api/v3/athlete/activities", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    [
                      {
                        "sport_type": "Ride",
                        "type": "Ride",
                        "average_speed": 8.2,
                        "distance": 12345.6,
                        "moving_time": 3600,
                        "total_elevation_gain": 250.5,
                        "gear_id": "b1",
                        "start_date": "2026-01-15T08:00:00Z",
                        "start_date_local": "2026-01-15T09:00:00+01:00"
                      },
                      {
                        "sport_type": "Ride",
                        "type": "Ride",
                        "average_speed": 36.0,
                        "distance": 99999,
                        "moving_time": 1,
                        "total_elevation_gain": 1,
                        "start_date": "2026-01-16T08:00:00Z"
                      }
                    ]
                    """);
            }

            if (url == "https://www.strava.com/api/v3/gear/b1")
            {
                return JsonResponse("""{ "id": "b1", "name": "Road Bike" }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var apiClient = new StravaApiClient(httpClient, Options.Create(new StravaOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret"
        }));
        var statsService = new StravaStatsService(apiClient, NullLogger<StravaStatsService>.Instance);

        var dashboardState = await statsService.BuildDashboardStateAsync(new StravaAuthSession
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresAtUnixTimeSeconds = 4_102_444_800,
            AthleteId = 123,
            AthleteFirstName = "Ada",
            AthleteLastName = "Lovelace"
        }, 2026, CancellationToken.None);

        var document = AthleteStatsBlobDocument.FromDashboardState(
            dashboardState,
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(1, dashboardState.ActivityCount);
        Assert.Equal(12345.6, dashboardState.OverallTotals.DistanceMeters);
        Assert.Contains("Road Bike (b1)", dashboardState.GearTable.GroupNames);
        Assert.Contains("\"athleteId\":123", json);
        Assert.Contains("\"dashboardState\"", json);
        Assert.Contains("\"activityCount\":1", json);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }
}
