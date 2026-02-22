namespace RealEstate.Api.Contracts.Scraping;

/// <summary>
/// Specifikace pro REMAX scraping profilu.
/// Umožňuje configurovat scraping pro libovolný region/okres/město s filtry.
/// </summary>
public sealed class RemaxScrapingProfileDto
{
    /// <summary>Jméno profilu (pro referenci a logging)</summary>
    public string Name { get; set; } = "Default";
    
    /// <summary>
    /// Přímá URL pro vyhledávání.
    /// Příklady:
    /// - Okres Znojmo: https://www.remax-czech.cz/reality/vyhledavani/?hledani=2&price_to=7500000&regions%5B116%5D%5B3713%5D=on&types%5B6%5D=on
    /// - Město Znojmo: https://www.remax-czech.cz/reality/vyhledavani/?desc_text=Znojmo&hledani=1&price_to=7500000&types%5B6%5D=on
    /// 
    /// Pokud je specifikovaná direktní URL, ostatní parametry jsou ignorovány!
    /// </summary>
    public string? DirectUrl { get; set; }
    
    /// <summary>Region ID (např. Jihomoravský kraj = 116)</summary>
    public int? RegionId { get; set; }
    
    /// <summary>
    /// District/Okres ID (např. Znojmo = 3713).
    /// Musí být ve struktuře regions[RegionId][DistrictId]
    /// </summary>
    public int? DistrictId { get; set; }
    
    /// <summary>Město/municipality (textově, např. "Znojmo")</summary>
    public string? CityName { get; set; }
    
    /// <summary>Typ nemovitosti bitmask (types[6]=domy/vily, types[1]=byty, atd.)</summary>
    public int PropertyTypeMask { get; set; } = 6; // Domy a vily (default)
    
    /// <summary>Maximální cena v Kč</summary>
    public long? PriceMax { get; set; } = 7_500_000;
    
    /// <summary>Minimální cena v Kč</summary>
    public long? PriceMin { get; set; }
    
    /// <summary>Hledaný text (např. "Znojmo")</summary>
    public string? SearchText { get; set; }
    
    /// <summary>
    /// Typ vyhledávání pro REMAX:
    /// 1 = fulltext (desc_text parameter)
    /// 2 = region-based (&hledani=2)
    /// </summary>
    public int SearchType { get; set; } = 2;
    
    /// <summary>Nabídnutá (offer type): "Sale" nebo "Rent"</summary>
    public string OfferType { get; set; } = "Sale";
    
    /// <summary>Maximální počet stránek k procházení (0 = všechny)</summary>
    public int MaxPages { get; set; } = 5;
}
