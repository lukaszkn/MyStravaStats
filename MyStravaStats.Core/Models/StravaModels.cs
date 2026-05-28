using System.Text.Json.Serialization;

namespace MyStravaStats.Core.Models;

public sealed class StravaDashboardState
{
    public bool IsConfigured { get; init; }

    public bool IsAuthenticated { get; init; }

    public bool IsAutoSyncConfigured { get; set; }

    public bool IsAutoSyncEnabled { get; set; }

    public bool AutoSyncRequiresReauthorization { get; set; }

    public DateTimeOffset? AutoSyncLastSyncedAtUtc { get; set; }

    public string? AutoSyncLastSyncStatus { get; set; }

    public string? AutoSyncLastError { get; set; }

    public int Year { get; init; }

    public long? AthleteId { get; init; }

    public string? AthleteName { get; init; }

    public string? ErrorMessage { get; init; }

    public int ActivityCount { get; init; }

    public DashboardTotals OverallTotals { get; init; } = new();

    public MonthlyStatsTable ActivityTypeTable { get; init; } = new();

    public MonthlyStatsTable GearTable { get; init; } = new();
}

public sealed class AthleteStatsBlobDocument
{
    public long AthleteId { get; init; }

    public string? AthleteName { get; init; }

    public int Year { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public StravaDashboardState DashboardState { get; init; } = new();

    public static AthleteStatsBlobDocument FromDashboardState(
        StravaDashboardState dashboardState,
        DateTimeOffset generatedAtUtc)
    {
        if (dashboardState.AthleteId is null)
        {
            throw new InvalidOperationException("Athlete id is required to create athlete-specific stats.");
        }

        return new AthleteStatsBlobDocument
        {
            AthleteId = dashboardState.AthleteId.Value,
            AthleteName = dashboardState.AthleteName,
            Year = dashboardState.Year,
            GeneratedAtUtc = generatedAtUtc,
            DashboardState = dashboardState
        };
    }
}

public sealed class AthleteCompetitionTable
{
    public bool IsConfigured { get; init; }

    public int Year { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<AthleteCompetitionColumn> Athletes { get; init; } = Array.Empty<AthleteCompetitionColumn>();

    public IReadOnlyList<AthleteCompetitionMonthRow> Rows { get; init; } = Array.Empty<AthleteCompetitionMonthRow>();
}

public sealed class AthleteCompetitionColumn
{
    public long AthleteId { get; init; }

    public string AthleteName { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public double YearlyDistanceMeters { get; init; }

    public IReadOnlyDictionary<string, double> YearlyActivityTypeDistancesMeters { get; init; } = new Dictionary<string, double>();

    public int Rank { get; init; }

    public bool IsLeader { get; init; }
}

public sealed class AthleteCompetitionMonthRow
{
    public int Month { get; init; }

    public IReadOnlyDictionary<long, double> DistancesByAthleteId { get; init; } = new Dictionary<long, double>();

    public IReadOnlyList<long> LeadingAthleteIds { get; init; } = Array.Empty<long>();
}

public sealed class DashboardTotals
{
    public double DistanceMeters { get; init; }

    public int MovingTimeSeconds { get; init; }

    public double ElevationGainMeters { get; init; }
}

public sealed class MonthlyStatsTable
{
    public IReadOnlyList<string> GroupNames { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, DashboardTotals> TotalsByGroup { get; init; } = new Dictionary<string, DashboardTotals>();

    public IReadOnlyList<MonthlyStatsTableRow> Rows { get; init; } = Array.Empty<MonthlyStatsTableRow>();
}

public sealed class MonthlyStatsTableRow
{
    public int Month { get; init; }

    public IReadOnlyDictionary<string, DashboardTotals> GroupTotals { get; init; } = new Dictionary<string, DashboardTotals>();
}

public sealed class StravaAuthSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public long ExpiresAtUnixTimeSeconds { get; init; }

    public long? AthleteId { get; init; }

    public string? AthleteFirstName { get; init; }

    public string? AthleteLastName { get; init; }

    public string? AcceptedScope { get; init; }

    public string AthleteDisplayName => string.Join(
        " ",
        new[] { AthleteFirstName, AthleteLastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()));
}

public sealed class StravaAuthRefreshResult
{
    public required StravaAuthSession AuthSession { get; init; }

    public bool WasRefreshed { get; init; }
}

public sealed class AutoSyncUserRecord
{
    public long AthleteId { get; set; }

    public string? AthleteName { get; set; }

    public string? AcceptedScope { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool RequiresReauthorization { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastSyncedAtUtc { get; set; }

    public string? LastSyncStatus { get; set; }

    public string? LastError { get; set; }

    public required string ProtectedAuthSession { get; set; }
}

public sealed class AutoSyncAuthSessionPayload
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public long ExpiresAtUnixTimeSeconds { get; init; }

    public long? AthleteId { get; init; }

    public string? AthleteFirstName { get; init; }

    public string? AthleteLastName { get; init; }

    public string? AcceptedScope { get; init; }
}

public sealed class StravaTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("athlete")]
    public StravaAthleteSummary? Athlete { get; set; }
}

public sealed class StravaAthleteSummary
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("firstname")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastname")]
    public string? LastName { get; set; }
}

public sealed class StravaActivity
{
    [JsonPropertyName("sport_type")]
    public string? SportType { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("average_speed")]
    public double AverageSpeed { get; set; }

    [JsonPropertyName("distance")]
    public double Distance { get; set; }

    [JsonPropertyName("moving_time")]
    public int MovingTime { get; set; }

    [JsonPropertyName("total_elevation_gain")]
    public double TotalElevationGain { get; set; }

    [JsonPropertyName("gear_id")]
    public string? GearId { get; set; }

    [JsonPropertyName("start_date")]
    public DateTimeOffset StartDate { get; set; }

    [JsonPropertyName("start_date_local")]
    public DateTimeOffset? StartDateLocal { get; set; }
}

public sealed class StravaGear
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
