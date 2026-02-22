namespace RealEstate.Api.Contracts.Scraping;

public sealed class ScrapeTriggerDto
{
    // Prázdné = scrapni všechny aktivní zdroje
    public List<string>? SourceCodes { get; set; } // např. ["REMAX", "MMR"]
    public bool FullRescan { get; set; } = false; // true = ignoruj cache, jinak jen nové/změněné
    
    /// <summary>
    /// Profil pro REMAX scraping (volitelný).
    /// Pokud není specifikován, použije se default (okres Znojmo).
    /// Umožňuje configurovat region, okres, město, filtry.
    /// </summary>
    public RemaxScrapingProfileDto? RemaxProfile { get; set; }
}
