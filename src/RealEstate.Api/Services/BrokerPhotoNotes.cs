using System.Text.RegularExpressions;

namespace RealEstate.Api.Services;

/// <summary>
/// Čte FOTKY_OD_MAKLERE.md, který do podsložky Fotky_od_maklere ukládá Claude Desktop při třídění
/// fotek od makléře: pár řádků hlavičky (**Zdroj:** …), tabulka „Složka | Počet | Co je na fotkách“
/// a poznámky pod ní. Formát je volný, proto se bere jen to, co se najde.
/// </summary>
public static partial class BrokerPhotoNotes
{
    public sealed record Parsed(string? Source, string? Notes, IReadOnlyDictionary<string, string> Descriptions);

    public static Parsed Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new Parsed(null, null, new Dictionary<string, string>());

        string? source = null;
        var notes = new List<string>();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columnOfText = -1;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var sourceMatch = SourceLine().Match(line);
            if (sourceMatch.Success) { source = sourceMatch.Groups[1].Value.Trim(); continue; }

            if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':' or ' '))) continue; // oddělovač
                if (columnOfText < 0)
                {
                    // hlavička: sloupec s popisem je ten, který není „Složka“ ani „Počet“
                    var idx = cells.FindIndex(c => !c.StartsWith("Slož", StringComparison.OrdinalIgnoreCase)
                                                 && !c.StartsWith("Poč", StringComparison.OrdinalIgnoreCase));
                    columnOfText = idx < 0 ? cells.Count - 1 : idx;
                    continue;
                }
                if (cells.Count > columnOfText && cells[0].Length > 0)
                    descriptions[Unescape(cells[0])] = Unescape(cells[columnOfText]);
                continue;
            }

            if (!line.StartsWith("**", StringComparison.Ordinal)) notes.Add(Unescape(line));
        }

        return new Parsed(source, notes.Count > 0 ? string.Join("\n", notes) : null, descriptions);
    }

    /// <summary>„05_Kotel“ → „Kotel“, „08_Kulna_a_dilna“ → „Kulna a dilna“.</summary>
    public static string Label(string folder) =>
        FolderPrefix().Replace(folder, "").Replace('_', ' ').Trim();

    private static string Unescape(string s) => s.Replace("\\_", "_").Replace("\\*", "*").Replace("\\&", "&").Replace("\\<", "<").Replace("\\>", ">").Trim();

    [GeneratedRegex(@"^\*\*Zdroj:\*\*\s*(.+)$")]
    private static partial Regex SourceLine();

    [GeneratedRegex(@"^\d+[_\-\s]*")]
    private static partial Regex FolderPrefix();
}
