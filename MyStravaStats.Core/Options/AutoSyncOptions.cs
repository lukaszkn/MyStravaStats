namespace MyStravaStats.Core.Options;

public sealed class AutoSyncOptions
{
    public string? TokenEncryptionKey { get; set; }

    public string ContainerName { get; set; } = "auto-sync-users";
}
