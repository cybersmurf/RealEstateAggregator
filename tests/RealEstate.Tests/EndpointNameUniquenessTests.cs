using System.Text.RegularExpressions;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Unikátnost .WithName() napříč všemi minimal-API endpointy.
//
//  Proč to existuje: 25. 8. 2026 přibyl POST /api/listings/detect-duplicates
//  se jménem "DetectDuplicates", které už měl POST /api/ollama/detect-duplicates.
//  ASP.NET staví routovací tabulku až při prvním requestu, takže se to
//  neprojevilo při startu ani v žádném z tehdejších 173 testů — jen tím,
//  že produkce začala na KAŽDÝ request vracet 500:
//    InvalidOperationException: Duplicate endpoint name 'DetectDuplicates' …
//
//  Test čte zdrojáky, ne sestavenou aplikaci. Je to hrubší nástroj, zato
//  nepotřebuje hostovat celý WebApplicationFactory kvůli jedné invariantě.
// ─────────────────────────────────────────────────────────────────
public class EndpointNameUniquenessTests
{
    private static readonly Regex WithNameCall =
        new(@"\.WithName\(""(?<name>[^""]+)""\)", RegexOptions.Compiled);

    private static string EndpointsRoot()
    {
        // Z bin/Debug/netX.0 zpět do kořene repa
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "RealEstate.Api")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "RealEstate.Api");
    }

    [Fact]
    public void EndpointNames_AreGloballyUnique()
    {
        var root = EndpointsRoot();
        var occurrences = new List<(string Name, string File, int Line)>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = WithNameCall.Match(lines[i]);
                if (match.Success)
                    occurrences.Add((match.Groups["name"].Value, Path.GetFileName(file), i + 1));
            }
        }

        Assert.NotEmpty(occurrences);

        var duplicates = occurrences
            .GroupBy(o => o.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' → " + string.Join(", ", g.Select(o => $"{o.File}:{o.Line}")))
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Jména endpointů musí být globálně unikátní, jinak routing shodí každý request na 500:\n  "
                + string.Join("\n  ", duplicates));
    }
}
