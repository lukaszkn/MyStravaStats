using System.Net;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class StravaApiClientTests
{
    [Fact]
    public async Task EnsureFreshAccessTokenRefreshesAndPreservesAthleteMetadata()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://www.strava.com/oauth/token", request.RequestUri?.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token": "new-access-token",
                      "refresh_token": "rotated-refresh-token",
                      "expires_at": 4102444800
                    }
                    """)
            };
        }));

        var client = new StravaApiClient(httpClient, Options.Create(new StravaOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret"
        }));

        var result = await client.EnsureFreshAccessTokenAsync(new StravaAuthSession
        {
            AccessToken = "old-access-token",
            RefreshToken = "old-refresh-token",
            ExpiresAtUnixTimeSeconds = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            AthleteId = 123,
            AthleteFirstName = "Ada",
            AthleteLastName = "Lovelace",
            AcceptedScope = "read,activity:read_all"
        }, CancellationToken.None);

        Assert.True(result.WasRefreshed);
        Assert.Equal("new-access-token", result.AuthSession.AccessToken);
        Assert.Equal("rotated-refresh-token", result.AuthSession.RefreshToken);
        Assert.Equal(123, result.AuthSession.AthleteId);
        Assert.Equal("Ada", result.AuthSession.AthleteFirstName);
        Assert.Equal("Lovelace", result.AuthSession.AthleteLastName);
        Assert.Equal("read,activity:read_all", result.AuthSession.AcceptedScope);
    }
}
