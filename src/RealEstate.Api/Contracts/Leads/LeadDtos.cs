namespace RealEstate.Api.Contracts.Leads;

/// <summary>Poptávka z detailu inzerátu (hypoteční kalkulačka). Consent je povinný.</summary>
public sealed record LeadCreateDto(
    Guid? ListingId,
    string Kind,
    string Name,
    string Email,
    string? Phone,
    string? Message,
    decimal? PropertyPrice,
    decimal? LoanAmount,
    int? LoanYears,
    string? Source,
    bool Consent);

public sealed record LeadDto(
    Guid Id,
    DateTime CreatedAt,
    string Kind,
    string Name,
    string Email,
    string? Phone,
    Guid? ListingId,
    string? ListingTitle,
    decimal? PropertyPrice,
    decimal? LoanAmount,
    int? LoanYears,
    bool Forwarded);
