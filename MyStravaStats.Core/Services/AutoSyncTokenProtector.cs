using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Options;

namespace MyStravaStats.Core.Services;

public sealed class AutoSyncTokenProtector
{
    private const int NonceSizeInBytes = 12;
    private const int TagSizeInBytes = 16;
    private const string ProtectedValuePrefix = "v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly byte[]? _key;

    public AutoSyncTokenProtector(IOptions<AutoSyncOptions> options)
    {
        _key = TryDecodeKey(options.Value.TokenEncryptionKey);
    }

    public bool IsConfigured => _key is not null;

    public string Protect<TValue>(TValue value)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("Auto sync token encryption is not configured. Set AUTO_SYNC_TOKEN_ENCRYPTION_KEY.");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeInBytes];

        using var aes = new AesGcm(_key, TagSizeInBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return string.Join(
            ".",
            ProtectedValuePrefix,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
    }

    public TValue Unprotect<TValue>(string protectedValue)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("Auto sync token encryption is not configured. Set AUTO_SYNC_TOKEN_ENCRYPTION_KEY.");
        }

        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || !string.Equals(parts[0], ProtectedValuePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Auto sync token payload has an unsupported format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var ciphertext = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSizeInBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Auto sync token payload could not be decrypted with the configured key.", ex);
        }

        return JsonSerializer.Deserialize<TValue>(plaintext, JsonOptions)
            ?? throw new InvalidOperationException("Auto sync token payload was empty after decryption.");
    }

    private static byte[]? TryDecodeKey(string? configuredKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return null;
        }

        var trimmedKey = configuredKey.Trim();

        byte[] decodedKey;
        if (TryDecodeHex(trimmedKey, out var hexKey))
        {
            decodedKey = hexKey;
        }
        else
        {
            try
            {
                decodedKey = Convert.FromBase64String(trimmedKey);
            }
            catch (FormatException)
            {
                decodedKey = Encoding.UTF8.GetBytes(trimmedKey);
            }
        }

        return decodedKey.Length is 16 or 24 or 32
            ? decodedKey
            : throw new InvalidOperationException("AUTO_SYNC_TOKEN_ENCRYPTION_KEY must be a 16, 24, or 32 byte key encoded as base64, hex, or raw text.");
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (value.Length % 2 != 0 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        bytes = new byte[value.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(
                value.AsSpan(index * 2, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
        }

        return true;
    }
}
