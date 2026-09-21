using System.Text.RegularExpressions;

namespace RealEstate.Api.Services;

/// <summary>
/// Potvrzuje příznak damage_detected z vision modelu jen tehdy, když ho model
/// něčím doložil: štítkem poškození, kategorií "damage", nebo zmínkou v popisu.
///
/// Reálný případ (LEXAMO, RD 4+kk Dyje): tři fotky dvora a leteckého snímku dostaly
/// damage_detected=true se štítky ["renovation_needed","brick_walls","wooden_beams"],
/// přestože popis téhož modelu říkal „good condition, no visible defects".
/// Samotné renovation_needed není poškození – je to odhad stavu, ne viditelná vada.
///
/// Popis se počítá, protože slovník štítků nepokrývá vše (oprýskaná omítka, vlhkost):
/// na produkci mělo 51 fotek poškození jen v popisu a byly to pravdivé nálezy.
/// </summary>
public static partial class PhotoDamageValidator
{
    private static readonly HashSet<string> DamageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mold", "water_damage", "crack", "broken_windows", "damaged_roof",
    };

    /// <summary>„no visible damage", „without any signs of cracks" – popření, ne nález.</summary>
    [GeneratedRegex(@"\b(no|without|free of)\b[^.;]*", RegexOptions.IgnoreCase)]
    private static partial Regex NegatedClause();

    [GeneratedRegex(
        @"\b(crack\w*|mou?ld\w*|peel\w*|stain\w*|damp\w*|rot(ten|ting)?|damage\w*|dilapidated|crumbl\w*|deteriorat\w*|broken|leak\w*|rust\w*|disrepair|poor condition)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageWord();

    public static bool IsConfirmed(
        bool damageFlag, IEnumerable<string>? labels, string? category, string? description)
    {
        if (!damageFlag) return false;

        if (string.Equals(category?.Trim(), "damage", StringComparison.OrdinalIgnoreCase))
            return true;

        if (labels is not null && labels.Any(l => DamageLabels.Contains(l.Trim())))
            return true;

        return !string.IsNullOrWhiteSpace(description)
            && DamageWord().IsMatch(NegatedClause().Replace(description, " "));
    }
}
