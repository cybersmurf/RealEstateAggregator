using RealEstate.Api.Contracts.SavedSearches;

namespace RealEstate.Api.Services.SavedSearches;

/// <summary>CRUD uložených hledání aktuálně přihlášeného uživatele (tarif Hledač a výš).</summary>
public interface ISavedSearchService
{
    Task<List<SavedSearchDto>> ListAsync(CancellationToken ct);
    Task<SavedSearchDto?> GetAsync(Guid id, CancellationToken ct);
    Task<(SavedSearchDto? Result, string? Error)> CreateAsync(SavedSearchUpsertDto request, CancellationToken ct);
    /// <summary>(null, null) = hledání neexistuje nebo nepatří uživateli.</summary>
    Task<(SavedSearchDto? Result, string? Error)> UpdateAsync(Guid id, SavedSearchUpsertDto request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<List<SavedSearchNotificationDto>?> ListNotificationsAsync(Guid id, CancellationToken ct);
    /// <summary>Pošle zkušební zprávu do kanálů zapnutých u hledání; null = hledání neexistuje.</summary>
    Task<SavedSearchTestResultDto?> SendTestAsync(Guid id, CancellationToken ct);
}

/// <summary>Vyhodnocení všech aktivních uložených hledání a rozeslání upozornění.</summary>
public interface ISavedSearchNotifier
{
    Task<SavedSearchRunResultDto> RunAllAsync(CancellationToken ct);
}
