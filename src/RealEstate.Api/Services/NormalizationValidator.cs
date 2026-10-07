using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RealEstate.Api.Services;

/// <summary>
/// Poslední pojistka nad <c>ai_normalized_data</c>: model dostává jen titulek a popis, přesto
/// doplňoval, co v textu není. Audit 7. 10. 2026 na produkci: <c>energy_class</c> bez zmínky
/// o třídě nebo PENB u 1 079 z 2 190 inzerátů (často „G" – Sreality ji ukazuje jako výchozí
/// bez průkazu), <c>has_elevator=true</c> bez slova výtah u 691 z 2 036, tepelné čerpadlo bez
/// „čerpadl" u 319 z 805. Hodnota bez opory v textu se vrací na null; <c>false</c> se nechává,
/// protože „není uvedeno" a „nemá" model rozlišuje sám.
/// </summary>
public static partial class NormalizationValidator
{
    /// <summary>Kmeny (bez diakritiky, malá písmena), které musí text obsahovat, aby <c>true</c> platilo.</summary>
    private static readonly Dictionary<string, string[]> BooleanStems = new(StringComparer.Ordinal)
    {
        ["has_pool"]     = ["bazen"],
        ["has_garage"]   = ["garaz"],
        ["has_elevator"] = ["vytah"],
        ["has_terrace"]  = ["teras"],
        ["has_balcony"]  = ["balkon", "lodzi"],
        ["has_basement"] = ["sklep", "suteren"],
        ["has_garden"]   = ["zahrad"],
        ["has_sauna"]    = ["saun"],
    };

    private static readonly Dictionary<string, string[]> HeatingStems = new(StringComparer.Ordinal)
    {
        ["heat_pump"]  = ["cerpadl"],
        ["gas"]        = ["plyn"],
        ["electric"]   = ["elektr", "primotop", "akumulac"],
        ["solid_fuel"] = ["tuh", "uhl", "drev", "pelet", "kotel", "krb", "kamn"],
        ["district"]   = ["dalkov", "czt", "teplarn", "centraln", "ustredn"],
    };

    /// <summary>„energetická třída B", „PENB: C", „štítek D" – písmeno do 60 znaků za klíčovým slovem.</summary>
    [GeneratedRegex(@"(energetick|penb|prukaz|trid[ay]|stitek)[^.;]{0,60}?\b([a-g])\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnergyClassMention();

    /// <summary>
    /// Vrátí JSON jen s hodnotami, které mají oporu v titulku a popisu; v <paramref name="dropped"/>
    /// jsou názvy vynulovaných polí (pro log). Nevalidní JSON vrací beze změny.
    /// </summary>
    public static string Clean(string json, string? title, string? description, out List<string> dropped)
    {
        dropped = [];
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return json;
        }
        if (obj is null) return json;

        var text = Fold($"{title} {description}");

        foreach (var (key, stems) in BooleanStems)
        {
            if (obj[key] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag
                && !stems.Any(text.Contains))
            {
                obj[key] = null;
                dropped.Add(key);
            }
        }

        if (obj["heating_type"] is JsonValue heating && heating.TryGetValue<string>(out var heatingType)
            && heatingType is not null
            && HeatingStems.TryGetValue(heatingType, out var heatingStems)
            && !heatingStems.Any(text.Contains))
        {
            obj["heating_type"] = null;
            dropped.Add("heating_type");
        }

        if (obj["energy_class"] is JsonValue energy && energy.TryGetValue<string>(out var energyClass)
            && !string.IsNullOrWhiteSpace(energyClass))
        {
            var letter = energyClass.Trim().ToLowerInvariant();
            var supported = letter.Length == 1
                && EnergyClassMention().Matches(text).Any(m => m.Groups[2].Value == letter);
            if (!supported)
            {
                obj["energy_class"] = null;
                dropped.Add("energy_class");
            }
        }

        if (obj["year_built"] is JsonValue year && year.TryGetValue<int>(out var yearBuilt)
            && (yearBuilt < 1700 || yearBuilt > DateTime.UtcNow.Year + 3
                || !text.Contains(yearBuilt.ToString(CultureInfo.InvariantCulture))))
        {
            obj["year_built"] = null;
            dropped.Add("year_built");
        }

        return dropped.Count == 0 ? json : obj.ToJsonString();
    }

    /// <summary>Malá písmena bez diakritiky – stejný převod jako v SQL migraci nad existujícími řádky.</summary>
    internal static string Fold(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
