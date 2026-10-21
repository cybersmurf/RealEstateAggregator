namespace RealEstate.Domain.Entities;

/// <summary>Denní záznam pro záložku „Dnes": souhrnná věta a položky (JSON seznam {type, listingId?, title, text, link}). Jeden na den, přepisuje se.</summary>
public sealed class DailyDigest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public DateOnly DigestDate { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string Summary { get; set; } = "";
    public string Items { get; set; } = "[]";
}
