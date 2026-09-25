namespace RealEstate.Api.Services;

/// <summary>
/// Maže lokální kopie fotek inzerátů (listing_photos), aby fotky zdrojů nezůstávaly na veřejném webu.
/// Fotky z prohlídky (user_listing_photos) se nikdy nemažou.
/// </summary>
public interface IPhotoPurgeService
{
    /// <summary>
    /// Smaže uložené soubory a vynuluje stored_url pro dávku fotek.
    /// onlyClassified=true bere jen už klasifikované; olderThanDays&gt;0 jen starší záznamy.
    /// </summary>
    Task<PhotoPurgeResultDto> PurgeStoredAsync(bool onlyClassified, int olderThanDays, int batchSize, CancellationToken ct);
}

public record PhotoPurgeResultDto(
    int Processed,
    int Deleted,
    int Failed,
    int Remaining);
