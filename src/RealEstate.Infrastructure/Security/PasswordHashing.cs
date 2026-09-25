using System.Security.Cryptography;

namespace RealEstate.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256 hashování hesel bez závislosti na ASP.NET Identity.
/// Formát: <c>pbkdf2$&lt;iterace&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</c> – iterace jsou v řetězci,
/// takže je lze později zvýšit bez rozbití starých hashů.
/// </summary>
public static class PasswordHashing
{
    private const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
            return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
