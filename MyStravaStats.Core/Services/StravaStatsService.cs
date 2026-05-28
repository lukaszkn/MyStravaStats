using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyStravaStats.Core.Models;

namespace MyStravaStats.Core.Services;

public sealed class StravaStatsService(
    StravaApiClient stravaApiClient,
    ILogger<StravaStatsService> logger)
{
    private static readonly Regex WordBreakRegex = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    public async Task<StravaDashboardState> BuildDashboardStateAsync(
        StravaAuthSession authSession,
        int year,
        CancellationToken cancellationToken)
    {
        var yearStartUtc = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var activities = await stravaApiClient.GetActivitiesAsync(authSession.AccessToken, yearStartUtc, cancellationToken);
        activities = activities
            .Where(activity => GetActivityDate(activity).Year == year)
            .ToList();

        var gearNames = await stravaApiClient.GetGearNamesAsync(authSession.AccessToken, activities, cancellationToken);

        return new StravaDashboardState
        {
            IsConfigured = true,
            IsAuthenticated = true,
            Year = year,
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
    }

    public async Task<StravaDashboardState?> TryBuildDashboardStateAsync(
        StravaAuthSession authSession,
        int year,
        CancellationToken cancellationToken)
    {
        try
        {
            return await BuildDashboardStateAsync(authSession, year, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to load Strava dashboard data for athlete {AthleteId}.", authSession.AthleteId);
            return null;
        }
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

    private static string HumanizeActivityType(string value)
    {
        var normalized = value.Replace('_', ' ').Trim();
        return WordBreakRegex.Replace(normalized, " $1");
    }
}
