using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Auth;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Security;

namespace RealEstate.Api.Services.Auth;

public interface IAuthService
{
    Task<(AuthResponseDto? Result, string? Error)> RegisterAsync(RegisterRequestDto request, CancellationToken ct);
    Task<(AuthResponseDto? Result, string? Error)> LoginAsync(LoginRequestDto request, CancellationToken ct);
    Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct);
    Task<UserProfileDto?> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request, CancellationToken ct);
    Task<string?> ChangePasswordAsync(Guid userId, ChangePasswordRequestDto request, CancellationToken ct);
    Task<List<ApiKeyDto>> ListApiKeysAsync(Guid userId, CancellationToken ct);
    Task<ApiKeyCreatedDto?> CreateApiKeyAsync(Guid userId, CreateApiKeyRequestDto request, bool isAdmin, CancellationToken ct);
    Task<bool> RevokeApiKeyAsync(Guid userId, Guid keyId, CancellationToken ct);
}

public sealed class AuthService(
    RealEstateDbContext db,
    AuthTokenService tokens,
    ILogger<AuthService> logger) : IAuthService
{
    private const int MinPasswordLength = 8;
    private const int MaxApiKeysPerUser = 5;

    public async Task<(AuthResponseDto? Result, string? Error)> RegisterAsync(RegisterRequestDto request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null)
            return (null, "Zadejte platný e-mail.");
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < MinPasswordLength)
            return (null, $"Heslo musí mít alespoň {MinPasswordLength} znaků.");

        var exists = await db.Users.AnyAsync(u => u.Email == email, ct);
        if (exists)
            return (null, "Účet s tímto e-mailem už existuje. Přihlaste se.");

        var user = new User
        {
            Email = email,
            PasswordHash = PasswordHashing.Hash(request.Password),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            Plan = UserPlans.Free,
            LastLoginAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Registered user {Email}", email);
        return (Issue(user), null);
    }

    public async Task<(AuthResponseDto? Result, string? Error)> LoginAsync(LoginRequestDto request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        const string invalid = "Nesprávný e-mail nebo heslo.";
        if (email is null)
            return (null, invalid);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !user.IsActive || !PasswordHashing.Verify(request.Password, user.PasswordHash))
            return (null, invalid);

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (Issue(user), null);
    }

    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : MapProfile(user);
    }

    public async Task<UserProfileDto?> UpdateProfileAsync(Guid userId, UpdateProfileRequestDto request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        user.TelegramChatId = string.IsNullOrWhiteSpace(request.TelegramChatId) ? null : request.TelegramChatId.Trim();
        await db.SaveChangesAsync(ct);
        return MapProfile(user);
    }

    public async Task<string?> ChangePasswordAsync(Guid userId, ChangePasswordRequestDto request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return "Uživatel nenalezen.";
        if (user.PasswordHash is not null && !PasswordHashing.Verify(request.CurrentPassword, user.PasswordHash))
            return "Současné heslo nesouhlasí.";
        if (string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < MinPasswordLength)
            return $"Nové heslo musí mít alespoň {MinPasswordLength} znaků.";

        user.PasswordHash = PasswordHashing.Hash(request.NewPassword);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<List<ApiKeyDto>> ListApiKeysAsync(Guid userId, CancellationToken ct)
    {
        var keys = await db.ApiKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return keys.Select(MapApiKey).ToList();
    }

    public async Task<ApiKeyCreatedDto?> CreateApiKeyAsync(Guid userId, CreateApiKeyRequestDto request, bool isAdmin, CancellationToken ct)
    {
        var count = await db.ApiKeys.CountAsync(k => k.UserId == userId && k.IsActive, ct);
        if (count >= MaxApiKeysPerUser && !isAdmin)
            return null;

        var plain = ApiKeyHashing.Generate();
        var key = new ApiKey
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "API klíč" : request.Name.Trim(),
            KeyHash = ApiKeyHashing.Hash(plain),
            KeyPrefix = ApiKeyHashing.DisplayPrefix(plain),
            // Kvótu si zvedne jen admin – zákazník dostane výchozí
            DailyQuota = isAdmin && request.DailyQuota is > 0 ? request.DailyQuota.Value : 1_000,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreatedDto(MapApiKey(key), plain);
    }

    public async Task<bool> RevokeApiKeyAsync(Guid userId, Guid keyId, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, ct);
        if (key is null)
            return false;
        key.IsActive = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private AuthResponseDto Issue(User user)
    {
        var (token, expires) = tokens.Issue(user.Id);
        return new AuthResponseDto(token, expires, MapProfile(user));
    }

    public static UserProfileDto MapProfile(User u) => new(
        u.Id, u.Email, u.DisplayName, u.Plan, u.PlanValidUntil, u.IsAdmin, u.TelegramChatId,
        u.StripeSubscriptionId is not null, u.CreatedAt);

    private static ApiKeyDto MapApiKey(ApiKey k) => new(
        k.Id, k.Name, k.KeyPrefix, k.DailyQuota,
        k.QuotaDay == DateOnly.FromDateTime(DateTime.UtcNow) ? k.UsedToday : 0,
        k.TotalRequests, k.LastUsedAt, k.IsActive, k.CreatedAt);

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var trimmed = email.Trim().ToLowerInvariant();
        return MailAddress.TryCreate(trimmed, out _) ? trimmed : null;
    }
}
