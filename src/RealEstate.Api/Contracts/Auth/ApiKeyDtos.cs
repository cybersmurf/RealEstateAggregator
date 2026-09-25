namespace RealEstate.Api.Contracts.Auth;

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string KeyPrefix,
    int DailyQuota,
    int UsedToday,
    long TotalRequests,
    DateTime? LastUsedAt,
    bool IsActive,
    DateTime CreatedAt);

public sealed record CreateApiKeyRequestDto(string Name, int? DailyQuota);

/// <param name="Key">Plný klíč – zobrazí se jen jednou, ukládá se pouze hash.</param>
public sealed record ApiKeyCreatedDto(ApiKeyDto Info, string Key);
