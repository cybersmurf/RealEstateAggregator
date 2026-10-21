namespace RealEstate.Domain.Entities;

/// <summary>Prohlídka domu: termín, makléř, checklist „co ověřit na místě" (jsonb [{text, done}]), poznámka po prohlídce.</summary>
public sealed class HouseVisit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string? AgentName { get; set; }
    public string? AgentPhone { get; set; }
    public string? Address { get; set; }
    /// <summary>JSON pole {text, done}.</summary>
    public string Checklist { get; set; } = "[]";
    public string? Note { get; set; }
    /// <summary>proposed | planned | done | cancelled</summary>
    public string Status { get; set; } = "planned";
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
}
