using System.Text.RegularExpressions;

namespace RealEstate.Api.Services;

/// <summary>
/// Kontrola AI shrnutí před uložením. Shrnutí nahrazuje veřejně původní popis inzerátu,
/// takže nesmí obsahovat kontakty (telefon, URL) ani být prázdné či nesmyslně dlouhé.
/// </summary>
public static partial class SummaryValidator
{
    public const int MaxLength = 900;

    // 9 číslic s volitelnou předvolbou (+420 / 00420) a libovolnými mezerami/pomlčkami/tečkami mezi trojicemi.
    // Vyloučeny částky ("125 000 000 Kč") – za číslem nesmí následovat měna ani jednotka plochy.
    [GeneratedRegex(@"(?<![\d,.])(?:\+\s?\d{1,3}|00\d{1,3})?[\s\-.]*\d{3}[\s\-.]*\d{3}[\s\-.]*\d{3}(?!\d|\s*(?:Kč|CZK|,-|m²|m2))", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"https?://|www\.|\S+\.(?:cz|com|sk|eu|net|org)(?:/|\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    /// <summary>Vrátí true, pokud je shrnutí použitelné; jinak false + důvod (pro log).</summary>
    public static bool IsValid(string? summary, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            reason = "empty";
            return false;
        }

        var text = summary.Trim();

        if (text.Length > MaxLength)
        {
            reason = $"too long ({text.Length} > {MaxLength})";
            return false;
        }

        if (PhoneRegex().IsMatch(text))
        {
            reason = "contains phone number";
            return false;
        }

        // E-mail dřív než URL – doména v adrese by jinak spadla pod "contains URL"
        if (EmailRegex().IsMatch(text))
        {
            reason = "contains e-mail";
            return false;
        }

        if (UrlRegex().IsMatch(text))
        {
            reason = "contains URL";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool IsValid(string? summary) => IsValid(summary, out _);
}
