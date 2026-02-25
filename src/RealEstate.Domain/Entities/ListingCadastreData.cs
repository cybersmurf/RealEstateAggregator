namespace RealEstate.Domain.Entities;

/// <summary>
/// Data z katastru nemovitostí (ČÚZK/RUIAN) pro konkrétní inzerát.
/// Zdrojem je RUIAN ArcGIS REST API (vyhledávání adresního místa).
/// Volitelně může být rozšířeno o manuálně zadaná data (LV, břemena).
/// </summary>
public sealed class ListingCadastreData
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }

    // ── RUIAN identifikace ──────────────────────────────────────────────────
    public long? RuianKod { get; set; }          // Kód adresního místa z RUIAN
    public string? ParcelNumber { get; set; }    // Parcelní číslo (manuálně)
    public string? LvNumber { get; set; }        // Číslo listu vlastnictví (manuálně)

    // ── Katastrální data ───────────────────────────────────────────────────
    public int? LandAreaM2 { get; set; }         // Výměra pozemku z katastru
    public string? LandType { get; set; }        // Druh pozemku
    public string? OwnerType { get; set; }       // Fyzická / právnická osoba / stát

    // ── Břemena (JSONB) ────────────────────────────────────────────────────
    /// <summary>
    /// Serialized JSON array: [{"type":"zástavní právo","description":"...","who":"XY Banka"}]
    /// </summary>
    public string? EncumbrancesJson { get; set; }

    // ── Metadata ───────────────────────────────────────────────────────────
    public string AddressSearched { get; set; } = "";
    public string? CadastreUrl { get; set; }     // Přímý link na nahlížení.cuzk.cz
    public string FetchStatus { get; set; } = "pending"; // pending/found/not_found/error
    public string? FetchError { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public string? RawRuianJson { get; set; }    // Surová odpověď z RUIAN

    // ── Navigation property ────────────────────────────────────────────────
    public Listing? Listing { get; set; }
}
