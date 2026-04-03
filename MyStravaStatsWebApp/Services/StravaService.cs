using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MyStravaStatsWebApp.Models;
using MyStravaStatsWebApp.Options;

namespace MyStravaStatsWebApp.Services;

public sealed class StravaService(
    HttpClient httpClient,
    IOptions<StravaOptions> stravaOptions,
    IHttpContextAccessor httpContextAccessor,
    StravaSessionStore sessionStore,
    StatsBlobStorageService statsBlobStorageService,
    ILogger<StravaService> logger)
{
    private static readonly Regex WordBreakRegex = new("(?<!^)([A-Z])", RegexOptions.Compiled);
    private readonly StravaOptions _options = stravaOptions.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId) &&
        !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public string BuildAuthorizationUrl(HttpContext httpContext)
    {
        EnsureConfigured();

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessionStore.SaveState(httpContext, state);

        return QueryHelpers.AddQueryString(
            "https://www.strava.com/oauth/authorize",
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = BuildCallbackUrl(httpContext),
                ["approval_prompt"] = "auto",
                ["scope"] = "read,activity:read_all",
                ["state"] = state
            });
    }

    public async Task HandleCallbackAsync(HttpContext httpContext, string code, string state, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var expectedState = sessionStore.GetState(httpContext);
        sessionStore.ClearState(httpContext);

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Strava did not return an authorization code.");
        }

        if (string.IsNullOrWhiteSpace(state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Strava authorization state validation failed.");
        }

        var tokenResponse = await ExchangeTokenAsync(
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["code"] = code,
                ["grant_type"] = "authorization_code"
            },
            cancellationToken);

        sessionStore.SaveAuth(httpContext, MapAuthSession(tokenResponse));
    }

    public void Logout(HttpContext httpContext)
    {
        sessionStore.ClearAll(httpContext);
    }

    public async Task<StravaDashboardState> GetDashboardStateAsync(string? requestErrorMessage, CancellationToken cancellationToken)
    {
        var currentYear = DateTime.UtcNow.Year;

        if (!IsConfigured)
        {
            return new StravaDashboardState
            {
                IsConfigured = false,
                Year = currentYear,
                ErrorMessage = "Strava credentials are missing. Set STRAVA_CLIENT_ID and STRAVA_CLIENT_SECRET."
            };
        }

        if (!string.IsNullOrWhiteSpace(requestErrorMessage))
        {
            return new StravaDashboardState
            {
                IsConfigured = true,
                Year = currentYear,
                ErrorMessage = requestErrorMessage
            };
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return new StravaDashboardState
            {
                IsConfigured = true,
                Year = currentYear,
                ErrorMessage = "The current HTTP context is unavailable for the Strava dashboard."
            };
        }

        var authSession = sessionStore.GetAuth(httpContext);
        if (authSession is null)
        {
            return new StravaDashboardState
            {
                IsConfigured = true,
                Year = currentYear,
                IsAuthenticated = false
            };
        }

        try
        {
            authSession = await EnsureFreshAccessTokenAsync(httpContext, authSession, cancellationToken);

            var yearStartUtc = new DateTimeOffset(currentYear, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var activities = await GetActivitiesAsync(authSession.AccessToken, yearStartUtc, cancellationToken);
            activities = activities
                .Where(activity => GetActivityDate(activity).Year == currentYear)
                .ToList();

            var gearNames = await GetGearNamesAsync(authSession.AccessToken, activities, cancellationToken);

            var dashboardState = new StravaDashboardState
            {
                IsConfigured = true,
                IsAuthenticated = true,
                Year = currentYear,
                AthleteId = authSession.AthleteId,
                AthleteName = string.IsNullOrWhiteSpace(authSession.AthleteDisplayName)
                    ? "Authorized athlete"
                    : authSession.AthleteDisplayName,
                ActivityCount = activities.Count,
                OverallTotals = new DashboardTotals
                {
                    DistanceMeters = activities.Sum(activity => activity.Distance),
                    MovingTimeSeconds = activities.Sum(activity => activity.MovingTime),
                    ElevationGainMeters = activities.Sum(activity => activity.TotalElevationGain)
                },
                ActivityTypeTable = BuildTable(activities, activity => HumanizeActivityType(GetActivityType(activity))),
                GearTable = BuildTable(activities, activity => ResolveGearDisplay(activity.GearId, gearNames))
            };

            await TryUploadDashboardStateAsync(dashboardState, cancellationToken);

            return dashboardState;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to load Strava dashboard data.");

            return new StravaDashboardState
            {
                IsConfigured = true,
                IsAuthenticated = true,
                Year = currentYear,
                AthleteId = authSession.AthleteId,
                AthleteName = string.IsNullOrWhiteSpace(authSession.AthleteDisplayName)
                    ? "Authorized athlete"
                    : authSession.AthleteDisplayName,
                ErrorMessage = $"Unable to load Strava data: {ex.Message}"
            };
        }
    }

    private async Task<StravaAuthSession> EnsureFreshAccessTokenAsync(HttpContext httpContext, StravaAuthSession authSession, CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(authSession.ExpiresAtUnixTimeSeconds);
        if (expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return authSession;
        }

        var tokenResponse = await ExchangeTokenAsync(
            new Dictionary<string, string?>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = authSession.RefreshToken
            },
            cancellationToken);

        var refreshedSession = MapAuthSession(tokenResponse);
        sessionStore.SaveAuth(httpContext, refreshedSession);

        return refreshedSession;
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
            throw new InvalidOperationException($"Strava token request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<StravaTokenResponse>(cancellationToken: cancellationToken);
        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("Strava returned an empty token response.");
        }

        return tokenResponse;
    }

    private async Task<List<StravaActivity>> GetActivitiesAsync(string accessToken, DateTimeOffset yearStartUtc, CancellationToken cancellationToken)
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

            // Romet się nie liczy hehe
            activities.AddRange(pageActivities.Where(activity => activity.AverageSpeed <= 35));

            if (pageActivities.Count < pageSize)
            {
                break;
            }
        }

        return activities;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetGearNamesAsync(
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to resolve Strava gear name for {GearId}.", gearId);
                gearNames[gearId] = gearId;
            }
        }

        return gearNames;
    }

    private async Task<T?> GetFromApiAsync<T>(string url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Strava API request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    private static MonthlyStatsTable BuildTable(
        IEnumerable<StravaActivity> activities,
        Func<StravaActivity, string> groupSelector)
    {
        var groupedActivities = activities
            .Select(activity => new
            {
                Activity = activity,
                GroupName = groupSelector(activity),
                Month = GetActivityDate(activity).Month
            })
            .ToArray();

        var groupNames = groupedActivities
            .Select(entry => entry.GroupName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var rows = groupedActivities
            .GroupBy(entry => entry.Month)
            .OrderBy(group => group.Key)
            .Select(monthGroup =>
            {
                var totalsByGroup = monthGroup
                    .GroupBy(entry => entry.GroupName, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => new DashboardTotals
                        {
                            DistanceMeters = group.Sum(entry => entry.Activity.Distance),
                            MovingTimeSeconds = group.Sum(entry => entry.Activity.MovingTime),
                            ElevationGainMeters = group.Sum(entry => entry.Activity.TotalElevationGain)
                        },
                        StringComparer.Ordinal);

                var completeTotalsByGroup = groupNames.ToDictionary(
                    groupName => groupName,
                    groupName => totalsByGroup.TryGetValue(groupName, out var totals)
                        ? totals
                        : new DashboardTotals(),
                    StringComparer.Ordinal);

                return new MonthlyStatsTableRow
                {
                    Month = monthGroup.Key,
                    GroupTotals = completeTotalsByGroup
                };
            })
            .ToArray();

        var totalsByGroup = groupNames.ToDictionary(
            groupName => groupName,
            groupName => new DashboardTotals
            {
                DistanceMeters = groupedActivities
                    .Where(entry => string.Equals(entry.GroupName, groupName, StringComparison.Ordinal))
                    .Sum(entry => entry.Activity.Distance),
                MovingTimeSeconds = groupedActivities
                    .Where(entry => string.Equals(entry.GroupName, groupName, StringComparison.Ordinal))
                    .Sum(entry => entry.Activity.MovingTime),
                ElevationGainMeters = groupedActivities
                    .Where(entry => string.Equals(entry.GroupName, groupName, StringComparison.Ordinal))
                    .Sum(entry => entry.Activity.TotalElevationGain)
            },
            StringComparer.Ordinal);

        return new MonthlyStatsTable
        {
            GroupNames = groupNames,
            TotalsByGroup = totalsByGroup,
            Rows = rows
        };
    }

    private static string GetActivityType(StravaActivity activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.SportType))
        {
            return activity.SportType;
        }

        return string.IsNullOrWhiteSpace(activity.Type)
            ? "Other"
            : activity.Type;
    }

    private static DateTimeOffset GetActivityDate(StravaActivity activity)
    {
        return activity.StartDateLocal ?? activity.StartDate;
    }

    private static string ResolveGearDisplay(string? gearId, IReadOnlyDictionary<string, string> gearNames)
    {
        if (string.IsNullOrWhiteSpace(gearId))
        {
            return "No gear";
        }

        return gearNames.TryGetValue(gearId, out var gearName)
            ? gearName
            : gearId;
    }

    private static StravaAuthSession MapAuthSession(StravaTokenResponse tokenResponse)
    {
        return new StravaAuthSession
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAtUnixTimeSeconds = tokenResponse.ExpiresAt,
            AthleteId = tokenResponse.Athlete.Id,
            AthleteFirstName = tokenResponse.Athlete.FirstName,
            AthleteLastName = tokenResponse.Athlete.LastName
        };
    }

    private async Task TryUploadDashboardStateAsync(StravaDashboardState dashboardState, CancellationToken cancellationToken)
    {
        if (!statsBlobStorageService.IsConfigured)
        {
            logger.LogWarning(
                "Azure stats blob storage is not configured. Set AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING to enable athlete stats export.");
            return;
        }

        if (dashboardState.AthleteId is null)
        {
            logger.LogWarning("Skipping athlete stats export because the athlete id is unavailable.");
            return;
        }

        try
        {
            await statsBlobStorageService.UploadDashboardStateAsync(dashboardState, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to upload dashboard stats for athlete {AthleteId}.", dashboardState.AthleteId);
        }
    }

    private static string HumanizeActivityType(string value)
    {
        var normalized = value.Replace('_', ' ').Trim();
        return WordBreakRegex.Replace(normalized, " $1");
    }

    private static string BuildCallbackUrl(HttpContext httpContext)
    {
        return UriHelper.BuildAbsolute(
            httpContext.Request.Scheme,
            httpContext.Request.Host,
            httpContext.Request.PathBase,
            "/strava/callback");
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Strava credentials are missing. Set STRAVA_CLIENT_ID and STRAVA_CLIENT_SECRET.");
        }
    }
}
