namespace MyStravaStats.Core.Options;

public sealed class StravaOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
