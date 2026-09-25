namespace RealEstate.Api.Contracts.Listings;

/// <summary>
/// Skupina inzerátů téže nemovitosti napříč zdroji: primární záznam + všechny jeho duplikáty
/// (aktivní i stažené). Slouží pro kartu „Stejná nemovitost v dalších zdrojích“.
/// </summary>
public sealed record DuplicateGroupDto(
    Guid PrimaryId,
    DateTime OldestFirstSeenAt,
    decimal? MinPrice,
    decimal? MaxPrice,
    int SourceCount,
    IReadOnlyList<DuplicateGroupItemDto> Items);

public sealed record DuplicateGroupItemDto(
    Guid Id,
    string SourceCode,
    string SourceName,
    string Url,
    string Title,
    decimal? Price,
    DateTime FirstSeenAt,
    DateTime? LastSeenAt,
    bool IsActive,
    DateTime? DeactivatedAt,
    bool IsPrimary,
    bool IsCurrent,
    string? ThumbnailUrl,
    int PhotoCount);
