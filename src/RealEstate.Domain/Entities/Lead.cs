namespace RealEstate.Domain.Entities;

/// <summary>
/// Poptávka (lead) z detailu inzerátu – dnes hypoteční kalkulačka / partner.
/// Ukládáme lokálně a volitelně přeposíláme e-mailem partnerovi.
/// </summary>
public sealed class Lead
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ListingId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>"mortgage" | "contact"</summary>
    public string Kind { get; set; } = "mortgage";
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? Phone { get; set; }
    public string? Message { get; set; }
    public decimal? PropertyPrice { get; set; }
    public decimal? LoanAmount { get; set; }
    public int? LoanYears { get; set; }
    public string? Source { get; set; }
    public bool Consent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ForwardedAt { get; set; }
}
