using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class AutoSyncTokenProtectorTests
{
    [Fact]
    public void ProtectAndUnprotectRoundTripsPayload()
    {
        var protector = CreateProtector(keySeed: 1);
        var payload = new AutoSyncAuthSessionPayload
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresAtUnixTimeSeconds = 4_102_444_800,
            AthleteId = 123,
            AthleteFirstName = "Ada",
            AthleteLastName = "Lovelace",
            AcceptedScope = "read,activity:read_all"
        };

        var protectedValue = protector.Protect(payload);
        var unprotected = protector.Unprotect<AutoSyncAuthSessionPayload>(protectedValue);

        Assert.NotEqual("access-token", protectedValue);
        Assert.Equal(payload.AccessToken, unprotected.AccessToken);
        Assert.Equal(payload.RefreshToken, unprotected.RefreshToken);
        Assert.Equal(payload.ExpiresAtUnixTimeSeconds, unprotected.ExpiresAtUnixTimeSeconds);
        Assert.Equal(payload.AthleteId, unprotected.AthleteId);
        Assert.Equal(payload.AcceptedScope, unprotected.AcceptedScope);
    }

    [Fact]
    public void UnprotectWithWrongKeyThrows()
    {
        var protectedValue = CreateProtector(keySeed: 1).Protect(new AutoSyncAuthSessionPayload
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresAtUnixTimeSeconds = 4_102_444_800
        });

        var wrongProtector = CreateProtector(keySeed: 2);

        Assert.Throws<InvalidOperationException>(() =>
            wrongProtector.Unprotect<AutoSyncAuthSessionPayload>(protectedValue));
    }

    private static AutoSyncTokenProtector CreateProtector(byte keySeed)
    {
        var key = Enumerable.Repeat(keySeed, 32).ToArray();
        return new AutoSyncTokenProtector(Options.Create(new AutoSyncOptions
        {
            TokenEncryptionKey = Convert.ToBase64String(key)
        }));
    }
}
