namespace MyStravaStatsWebApp.Services;

public static class AthleteDisplayNameFormatter
{
    public static string GetFirstName(string? athleteName)
    {
        return string.IsNullOrWhiteSpace(athleteName)
            ? string.Empty
            : athleteName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }
}
