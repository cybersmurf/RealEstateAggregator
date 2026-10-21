namespace RealEstate.Domain.Entities;

/// <summary>
/// Hlas jedné osoby k domu („Jet se podívat / Možná / Ne"). Váže se na inzerát, při čtení se bere
/// za celou skupinu duplicit – hlas se neztratí, když makléř dům stáhne a vloží znovu.
/// </summary>
public sealed class HouseVote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>go | maybe | no</summary>
    public string Value { get; set; } = "maybe";
    public string? Note { get; set; }
    /// <summary>Cena domu v okamžiku hlasu – po změně ceny se u hlasu ukáže „hlasováno před změnou ceny".</summary>
    public decimal? PriceAtVote { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
    public User User { get; set; } = null!;
}
