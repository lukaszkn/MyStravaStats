using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;

namespace MyStravaStatsWebApp.Services;

public sealed class StravaService(
    IOptions<StravaOptions> stravaOptions,
    IHttpContextAccessor httpContextAccessor,
    StravaSessionStore sessionStore,
    StravaApiClient stravaApiClient,
    StravaStatsService stravaStatsService,
    StatsBlobStorageService statsBlobStorageService,
    IAutoSyncUserStore autoSyncUserStore,
    ILogger<StravaService> logger)
{
    private readonly StravaOptions _options = stravaOptions.Value;

    public bool IsConfigured => _options.IsConfigured;

    public bool IsAutoSyncConfigured => autoSyncUserStore.IsConfigured;

    public string BuildAuthorizationUrl(HttpContext httpContext, bool enableAutoSync)
    {
        EnsureConfigured();

        if (enableAutoSync && !autoSyncUserStore.IsConfigured)
        {
            throw new InvalidOperationException(
                "Auto sync storage is not configured. Set AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING and AUTO_SYNC_TOKEN_ENCRYPTION_KEY.");
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessionStore.SaveState(httpContext, state);
        sessionStore.SaveAutoSyncRequested(httpContext, enableAutoSync);

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

    public async Task HandleCallbackAsync(
        HttpContext httpContext,
        string code,
        string state,
        string? acceptedScope,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var expectedState = sessionStore.GetState(httpContext);
        var autoSyncRequested = sessionStore.GetAutoSyncRequested(httpContext);
        sessionStore.ClearState(httpContext);
        sessionStore.ClearAutoSyncRequested(httpContext);

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Strava did not return an authorization code.");
        }

        if (string.IsNullOrWhiteSpace(state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Strava authorization state validation failed.");
        }

        var authSession = await stravaApiClient.ExchangeAuthorizationCodeAsync(code, acceptedScope, cancellationToken);
        sessionStore.SaveAuth(httpContext, authSession);

        if (autoSyncRequested)
        {
            await autoSyncUserStore.SaveEnabledUserAsync(authSession, acceptedScope, cancellationToken);
        }
    }

    public void Logout(HttpContext httpContext)
    {
        sessionStore.ClearAll(httpContext);
    }

    public async Task StopAutoSyncAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var authSession = sessionStore.GetAuth(httpContext);
        if (authSession?.AthleteId is null)
        {
            throw new InvalidOperationException("Connect with Strava before changing auto sync settings.");
        }

        await autoSyncUserStore.DeleteUserAsync(authSession.AthleteId.Value, cancellationToken);
    }

    public async Task<StravaDashboardState> GetDashboardStateAsync(
        string? requestErrorMessage,
        CancellationToken cancellationToken)
    {
        var currentYear = DateTime.UtcNow.Year;

        if (!IsConfigured)
        {
            return CreateDashboardState(
                currentYear,
                "Strava credentials are missing. Set STRAVA_CLIENT_ID and STRAVA_CLIENT_SECRET.",
                isConfigured: false);
        }

        if (!string.IsNullOrWhiteSpace(requestErrorMessage))
        {
            return CreateDashboardState(currentYear, requestErrorMessage, isConfigured: true);
        }

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return CreateDashboardState(
                currentYear,
                "The current HTTP context is unavailable for the Strava dashboard.",
                isConfigured: true);
        }

        var authSession = sessionStore.GetAuth(httpContext);
        if (authSession is null)
        {
            return new StravaDashboardState
            {
                IsConfigured = true,
                IsAutoSyncConfigured = autoSyncUserStore.IsConfigured,
                Year = currentYear,
                IsAuthenticated = false
            };
        }

        try
        {
            var refreshResult = await stravaApiClient.EnsureFreshAccessTokenAsync(authSession, cancellationToken);
            authSession = refreshResult.AuthSession;
            if (refreshResult.WasRefreshed)
            {
                sessionStore.SaveAuth(httpContext, authSession);
            }

            var dashboardState = await stravaStatsService.BuildDashboardStateAsync(authSession, currentYear, cancellationToken);
            await ApplyAutoSyncStateAsync(dashboardState, authSession.AthleteId, cancellationToken);
            await TryUploadDashboardStateAsync(dashboardState, cancellationToken);

            return dashboardState;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to load Strava dashboard data.");

            var dashboardState = new StravaDashboardState
            {
                IsConfigured = true,
                IsAuthenticated = true,
                IsAutoSyncConfigured = autoSyncUserStore.IsConfigured,
                Year = currentYear,
                AthleteId = authSession.AthleteId,
                AthleteName = string.IsNullOrWhiteSpace(authSession.AthleteDisplayName)
                    ? "Authorized athlete"
                    : authSession.AthleteDisplayName,
                ErrorMessage = $"Unable to load Strava data: {ex.Message}"
            };

            await ApplyAutoSyncStateAsync(dashboardState, authSession.AthleteId, cancellationToken);
            return dashboardState;
        }
    }

    private async Task ApplyAutoSyncStateAsync(
        StravaDashboardState dashboardState,
        long? athleteId,
        CancellationToken cancellationToken)
    {
        dashboardState.IsAutoSyncConfigured = autoSyncUserStore.IsConfigured;

        if (!autoSyncUserStore.IsConfigured || athleteId is null)
        {
            return;
        }

        var autoSyncRecord = await autoSyncUserStore.GetUserAsync(athleteId.Value, cancellationToken);
        if (autoSyncRecord is null)
        {
            return;
        }

        dashboardState.IsAutoSyncEnabled = autoSyncRecord.IsEnabled;
        dashboardState.AutoSyncRequiresReauthorization = autoSyncRecord.RequiresReauthorization;
        dashboardState.AutoSyncLastSyncedAtUtc = autoSyncRecord.LastSyncedAtUtc;
        dashboardState.AutoSyncLastSyncStatus = autoSyncRecord.LastSyncStatus;
        dashboardState.AutoSyncLastError = autoSyncRecord.LastError;
    }

    private async Task TryUploadDashboardStateAsync(
        StravaDashboardState dashboardState,
        CancellationToken cancellationToken)
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

    private StravaDashboardState CreateDashboardState(
        int year,
        string errorMessage,
        bool isConfigured)
    {
        return new StravaDashboardState
        {
            IsConfigured = isConfigured,
            IsAutoSyncConfigured = autoSyncUserStore.IsConfigured,
            Year = year,
            ErrorMessage = errorMessage
        };
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
