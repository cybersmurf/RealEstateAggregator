namespace RealEstate.Api.Contracts.Scraping;

public sealed class ScrapeTriggerDto
{
    // Prázdné = scrapni všechny aktivní zdroje
    public List<string>? SourceCodes { get; set; } // např. ["REMAX", "MMR"]
    public bool FullRescan { get; set; } = false; // true = ignoruj cache, jinak jen nové/změněné
    
    /// <summary>
    /// Volitelná URL pro REMAX scraping.
    /// Příklady:
    /// - https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/
    /// - https://www.remax-czech.cz/reality/byty/prodej/hlavni-mesto-praha/
    /// Pokud není specifikován, použije se default URL.
    /// </summary>
    public string? SearchUrl { get; set; }
}
