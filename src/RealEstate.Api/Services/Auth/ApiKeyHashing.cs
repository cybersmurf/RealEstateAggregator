using System.Security.Cryptography;
using System.Text;

namespace RealEstate.Api.Services.Auth;

public static class ApiKeyHashing
{
    public const string Prefix = "rea_";

    /// <summary>Vygeneruje nový klíč: <c>rea_</c> + 40 znaků base64url (240 bitů entropie).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(30);
        var body = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', 'x').Replace('/', 'y');
        return Prefix + body;
    }

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string DisplayPrefix(string key) => key.Length <= 12 ? key : key[..12];
}
