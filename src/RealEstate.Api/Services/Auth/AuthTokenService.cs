using System.Security.Cryptography;
using System.Text;

namespace RealEstate.Api.Services.Auth;

/// <summary>
/// Podepsané bearer tokeny bez externí závislosti: <c>base64url(payload).base64url(HMAC-SHA256)</c>,
/// payload = <c>{userId}|{expUnix}</c>. Tajemství: AUTH_SECRET, jinak odvozené z API_KEY –
/// s výchozím dev klíčem tedy funguje lokálně bez konfigurace.
/// </summary>
public sealed class AuthTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public AuthTokenService(IConfiguration config)
    {
        var secret = Environment.GetEnvironmentVariable("AUTH_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "dev-key-change-me";
            secret = "auth:" + apiKey;
        }
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        var days = config.GetValue<int?>("Auth:TokenLifetimeDays") ?? 30;
        _lifetime = TimeSpan.FromDays(Math.Clamp(days, 1, 365));
    }

    public TimeSpan Lifetime => _lifetime;

    public (string Token, DateTime ExpiresAt) Issue(Guid userId)
    {
        var expires = DateTime.UtcNow.Add(_lifetime);
        var payload = $"{userId:N}|{new DateTimeOffset(expires).ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var sig = HMACSHA256.HashData(_key, payloadBytes);
        return ($"{Base64Url(payloadBytes)}.{Base64Url(sig)}", expires);
    }

    public Guid? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return null;

        byte[] payloadBytes, sig;
        try
        {
            payloadBytes = FromBase64Url(token[..dot]);
            sig = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, sig))
            return null;

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var parts = payload.Split('|');
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out var userId)
            || !long.TryParse(parts[1], out var expUnix))
            return null;

        if (DateTimeOffset.FromUnixTimeSeconds(expUnix) < DateTimeOffset.UtcNow)
            return null;

        return userId;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
