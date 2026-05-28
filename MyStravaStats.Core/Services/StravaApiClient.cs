using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;

namespace MyStravaStats.Core.Services;

public sealed class StravaApiClient(
    HttpClient httpClient,
    IOptions<StravaOptions> stravaOptions)
{
    private readonly StravaOptions _options = stravaOptions.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<StravaAuthSession> ExchangeAuthorizationCodeAsync(
        string code,
        string? acceptedScope,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var tokenResponse = await ExchangeTokenAsync(
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code"
            },
            cancellationToken);

        return MapAuthSession(tokenResponse, fallbackSession: null, acceptedScope);
    }

    public async Task<StravaAuthRefreshResult> EnsureFreshAccessTokenAsync(
        StravaAuthSession authSession,
        CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(authSession.ExpiresAtUnixTimeSeconds);
        if (expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return new StravaAuthRefreshResult
            {
                AuthSession = authSession,
                WasRefreshed = false
            };
        }

        return new StravaAuthRefreshResult
        {
            AuthSession = await RefreshTokenAsync(authSession, cancellationToken),
            WasRefreshed = true
        };
    }

    public async Task<StravaAuthSession> RefreshTokenAsync(
        StravaAuthSession authSession,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var tokenResponse = await ExchangeTokenAsync(
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = authSession.RefreshToken
            },
            cancellationToken);

        return MapAuthSession(tokenResponse, authSession, authSession.AcceptedScope);
    }

    public async Task<List<StravaActivity>> GetActivitiesAsync(
        string accessToken,
        DateTimeOffset yearStartUtc,
        CancellationToken cancellationToken)
    {
        var activities = new List<StravaActivity>();
        const int pageSize = 200;
        var after = yearStartUtc.ToUnixTimeSeconds();

        for (var page = 1; ; page++)
        {
            // https://developers.strava.com/docs/reference/#api-Activities-getLoggedInAthleteActivities
            var pageActivities = await GetFromApiAsync<List<StravaActivity>>(
                $"https://www.strava.com/api/v3/athlete/activities?after={after}&page={page}&per_page={pageSize}",
                accessToken,
                cancellationToken) ?? new List<StravaActivity>();

            if (pageActivities.Count == 0)
            {
                break;
            }

            activities.AddRange(pageActivities.Where(activity => activity.AverageSpeed <= 35));

            if (pageActivities.Count < pageSize)
            {
                break;
            }
        }

        return activities;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetGearNamesAsync(
        string accessToken,
        IEnumerable<StravaActivity> activities,
        CancellationToken cancellationToken)
    {
        var gearIds = activities
            .Select(activity => activity.GearId)
            .Where(gearId => !string.IsNullOrWhiteSpace(gearId))
            .Select(gearId => gearId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var gearNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var gearId in gearIds)
        {
            try
            {
                var gear = await GetFromApiAsync<StravaGear>(
                    $"https://www.strava.com/api/v3/gear/{gearId}",
                    accessToken,
                    cancellationToken);

                gearNames[gearId] = string.IsNullOrWhiteSpace(gear?.Name)
                    ? gearId
                    : $"{gear.Name} ({gearId})";
            }
            catch
            {
                gearNames[gearId] = gearId;
            }
        }

        return gearNames;
    }

    private async Task<StravaTokenResponse> ExchangeTokenAsync(
        IReadOnlyDictionary<string, string?> formValues,
        CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(
            formValues
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.strava.com/oauth/token")
        {
            Content = content
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new StravaApiException(
                $"Strava token request failed with status {(int)response.StatusCode}: {errorBody}",
                response.StatusCode);
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<StravaTokenResponse>(cancellationToken: cancellationToken);
        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("Strava returned an empty token response.");
        }

        return tokenResponse;
    }

    private async Task<T?> GetFromApiAsync<T>(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new StravaApiException(
                $"Strava API request failed with status {(int)response.StatusCode}: {errorBody}",
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static StravaAuthSession MapAuthSession(
        StravaTokenResponse tokenResponse,
        StravaAuthSession? fallbackSession,
        string? acceptedScope)
    {
        return new StravaAuthSession
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(tokenResponse.RefreshToken)
                ? fallbackSession?.RefreshToken ?? string.Empty
                : tokenResponse.RefreshToken,
            ExpiresAtUnixTimeSeconds = tokenResponse.ExpiresAt,
            AthleteId = tokenResponse.Athlete?.Id ?? fallbackSession?.AthleteId,
            AthleteFirstName = tokenResponse.Athlete?.FirstName ?? fallbackSession?.AthleteFirstName,
            AthleteLastName = tokenResponse.Athlete?.LastName ?? fallbackSession?.AthleteLastName,
            AcceptedScope = acceptedScope ?? fallbackSession?.AcceptedScope
        };
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Strava credentials are missing. Set STRAVA_CLIENT_ID and STRAVA_CLIENT_SECRET.");
        }
    }
}
