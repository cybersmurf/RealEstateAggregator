namespace RealEstate.Api.Contracts.Auth;

public sealed record RegisterRequestDto(string Email, string Password, string? DisplayName);

public sealed record LoginRequestDto(string Email, string Password);

/// <param name="Token">Bearer token pro API (HMAC, platnost dle ExpiresAt).</param>
public sealed record AuthResponseDto(string Token, DateTime ExpiresAt, UserProfileDto User);

public sealed record UserProfileDto(
    Guid Id,
    string Email,
    string? DisplayName,
    string Plan,
    DateTime? PlanValidUntil,
    bool IsAdmin,
    string? TelegramChatId,
    bool HasStripeSubscription,
    DateTime CreatedAt);

public sealed record UpdateProfileRequestDto(string? DisplayName, string? TelegramChatId);

public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

/// <summary>Popis tarifu pro stránku ceníku – co je zahrnuto a za kolik.</summary>
public sealed record PlanInfoDto(
    string Code,
    string Name,
    decimal? MonthlyPriceCzk,
    decimal? YearlyPriceCzk,
    IReadOnlyList<string> Features,
    bool Purchasable);
