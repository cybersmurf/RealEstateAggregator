namespace RealEstate.Domain.Entities;

/// <summary>
/// Porovnání fotek z inzerátu s fotkami z prohlídky pro jednu kategorii místnosti (kuchyň, exteriér…).
/// Nálezy (retuš, širokoúhlý záběr, vada mimo záběr…) jsou strukturované, aby šly sčítat napříč domy.
/// </summary>
public class ListingPhotoComparison
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Inzerát, u kterého jsou fotky z prohlídky.</summary>
    public Guid ListingId { get; set; }

    /// <summary>Kategorie fotek (exterior, kitchen, bathroom…).</summary>
    public string Category { get; set; } = null!;

    public int ListingPhotoCount { get; set; }
    public int InspectionPhotoCount { get; set; }

    /// <summary>Shrnutí rozdílů v češtině (1–2 věty).</summary>
    public string? Summary { get; set; }

    /// <summary>Pole nálezů jako JSON: [{type, severity, description, listingPhoto, inspectionPhoto}].</summary>
    public string Findings { get; set; } = "[]";

    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
}
