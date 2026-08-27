using System.Globalization;
using System.Text;

namespace RealEstate.Api.Services;

/// <summary>
/// Zahazuje SmartTags, pro které není opora ve zdrojovém textu inzerátu.
///
/// Model si tagy vymýšlí – reálný případ: 60 let starý dům „vhodný k modernizaci"
/// dostal tagy ["cihlový dům","novostavba","terasa","zahrada","kolaudovaný"],
/// z nichž ani jeden v popisu nebyl. Na produkci neslo 114 aktivních inzerátů
/// vymyšlený tag „novostavba". Prompt se opravil, ale LLM zůstává nedeterministické,
/// takže tahle kontrola je poslední pojistka před zápisem do DB.
/// </summary>
public static class SmartTagValidator
{
    /// <summary>Délka kmene pro porovnání – pokrývá české skloňování (rekonstrukce/rekonstruovaný).</summary>
    private const int StemLength = 5;

    /// <summary>Nejkratší slovo, které ještě nese věcné tvrzení.</summary>
    private const int MinContentWordLength = 4;

    /// <summary>
    /// Slova, která sama o sobě nic netvrdí. Bez nich by tag „cihlový dům" prošel
    /// jen kvůli slovu „dům" kdekoliv v popisu.
    /// </summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "dum", "domu", "domek", "byt", "bytu", "nemovitost", "nemovitosti",
        "prodej", "prodam", "pronajem", "objekt", "stavba", "budova",
        "cena", "plocha", "pozemek", "pozemku", "metr", "velky", "maly",
    };

    /// <summary>
    /// Vrátí jen tagy podložené textem. Pořadí zachovává, duplicity odstraňuje.
    /// </summary>
    public static List<string> Filter(IEnumerable<string> tags, string? title, string? description)
    {
        var haystack = BuildWordSet($"{title} {description}");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var trimmed = tag.Trim();
            if (!seen.Add(trimmed)) continue;
            if (IsSupported(trimmed, haystack)) result.Add(trimmed);
        }

        return result;
    }

    /// <summary>
    /// Kmeny, které tag potvrzují navíc k jeho vlastnímu.
    /// Česká předpona umí kmen schovat: „podsklepený" znamená, že sklep JE,
    /// ale slovo nezačíná na „sklep". Negace („nepodsklepený") se sem záměrně nedostane,
    /// protože porovnáváme začátek slova.
    /// </summary>
    private static readonly Dictionary<string, string[]> TagAliases = new(StringComparer.Ordinal)
    {
        ["sklep"] = ["podsklep"],
        ["podkr"] = ["pudni", "vestav"],
        ["vytah"] = ["osobni vytah"],
        ["parko"] = ["stani", "garaz"],
    };

    /// <summary>
    /// Tag je podložený, pokud některé jeho věcné slovo tvoří začátek slova ve zdrojovém textu.
    /// Porovnání po slovech (ne substringem) je záměrné: „nepodsklepený" nesmí potvrdit tag „sklep".
    /// </summary>
    public static bool IsSupported(string tag, IReadOnlySet<string> sourceWords)
    {
        var tagWords = Tokenize(tag);
        if (tagWords.Count == 0) return false;

        var contentWords = tagWords.Where(w => w.Length >= MinContentWordLength && !GenericWords.Contains(w)).ToList();

        // Tag složený jen z obecných/krátkých slov („byt", „garáž") – vyžaduj přesnou shodu slova
        if (contentWords.Count == 0)
            return tagWords.Any(sourceWords.Contains);

        foreach (var word in contentWords)
        {
            var stem = word[..Math.Min(StemLength, word.Length)];
            if (sourceWords.Any(source => source.StartsWith(stem, StringComparison.Ordinal)))
                return true;

            if (TagAliases.TryGetValue(stem, out var aliases)
                && aliases.Any(alias => sourceWords.Any(source => source.StartsWith(alias, StringComparison.Ordinal))))
                return true;
        }

        return false;
    }

    /// <summary>Rozloží text na množinu slov bez diakritiky, malými písmeny.</summary>
    public static HashSet<string> BuildWordSet(string? text)
        => [.. Tokenize(text)];

    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var normalized = RemoveDiacritics(text).ToLowerInvariant();
        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
