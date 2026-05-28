using Microsoft.Extensions.Logging;
using MyStravaStats.Core.Models;

namespace MyStravaStats.Core.Services;

public sealed class StravaAutoSyncService(
    IAutoSyncUserStore autoSyncUserStore,
    StravaApiClient stravaApiClient,
    StravaStatsService stravaStatsService,
    StatsBlobStorageService statsBlobStorageService,
    ILogger<StravaAutoSyncService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!stravaApiClient.IsConfigured)
        {
            logger.LogWarning("Skipping auto sync because Strava credentials are not configured.");
            return;
        }

        if (!statsBlobStorageService.IsConfigured)
        {
            logger.LogWarning("Skipping auto sync because Azure stats blob storage is not configured.");
            return;
        }

        if (!autoSyncUserStore.IsConfigured)
        {
            logger.LogWarning("Skipping auto sync because auto-sync storage or token encryption is not configured.");
            return;
        }

        var currentYear = DateTime.UtcNow.Year;
        var records = await autoSyncUserStore.GetEnabledUsersAsync(cancellationToken);

        logger.LogInformation("Starting Strava auto sync for {UserCount} opted-in users.", records.Count);

        foreach (var record in records)
        {
            await SyncOneUserAsync(record, currentYear, cancellationToken);
        }

        logger.LogInformation("Finished Strava auto sync for {UserCount} opted-in users.", records.Count);
    }

    private async Task SyncOneUserAsync(
        AutoSyncUserRecord record,
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            var authSession = autoSyncUserStore.UnprotectAuthSession(record);
            var refreshResult = await stravaApiClient.EnsureFreshAccessTokenAsync(authSession, cancellationToken);
            var dashboardState = await stravaStatsService.BuildDashboardStateAsync(refreshResult.AuthSession, year, cancellationToken);

            await statsBlobStorageService.UploadDashboardStateAsync(dashboardState, cancellationToken);
            await autoSyncUserStore.MarkSyncSuccessAsync(record, refreshResult.AuthSession, cancellationToken);

            logger.LogInformation(
                "Auto-synced athlete {AthleteId}; refreshed access token: {WasRefreshed}.",
                record.AthleteId,
                refreshResult.WasRefreshed);
        }
        catch (StravaApiException ex) when (ex.RequiresReauthorization)
        {
            logger.LogWarning(ex, "Auto sync for athlete {AthleteId} requires reauthorization.", record.AthleteId);
            await autoSyncUserStore.MarkSyncFailureAsync(record, ex.Message, requiresReauthorization: true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto sync failed for athlete {AthleteId}.", record.AthleteId);
            await autoSyncUserStore.MarkSyncFailureAsync(record, ex.Message, requiresReauthorization: false, cancellationToken);
        }
    }
}
