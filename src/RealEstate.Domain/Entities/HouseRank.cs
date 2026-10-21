namespace RealEstate.Domain.Entities;

/// <summary>Ruční pořadí kandidáta ve skupině žebříčku (visit / watch / second); sdílené pro celý workspace.</summary>
public sealed class HouseRank
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    /// <summary>Vlastník workspace (stavy inzerátů patří jemu).</summary>
    public Guid OwnerId { get; set; }
    /// <summary>visit | watch | second</summary>
    public string Bucket { get; set; } = "watch";
    public int Position { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
}
