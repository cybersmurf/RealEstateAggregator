using RealEstate.Api.Contracts.Overview;

namespace RealEstate.Api.Services.Overview;

/// <summary>Stránka /prehled: denní záznam, žebříček kandidátů s hlasováním, prohlídky. Vše za workspace a za skupinu duplicit.</summary>
public interface IOverviewService
{
    Task<DailyDigestDto?> GetLatestDigestAsync(CancellationToken ct);
    Task<DailyDigestDto> SaveDigestAsync(SaveDigestDto dto, CancellationToken ct);

    Task<CandidatesDto> GetCandidatesAsync(CancellationToken ct);
    Task<List<VoteDto>> GetVotesAsync(Guid listingId, CancellationToken ct);
    Task<Dictionary<Guid, List<VoteDto>>> GetAllVotesAsync(CancellationToken ct);
    Task<List<VoteDto>> VoteAsync(Guid listingId, VoteRequestDto request, CancellationToken ct);
    Task SaveRanksAsync(IReadOnlyList<RankItemDto> items, CancellationToken ct);

    Task<List<VisitDto>> GetVisitsAsync(DateTime? from, DateTime? to, CancellationToken ct);
    Task<VisitDto?> GetVisitAsync(Guid id, CancellationToken ct);
    Task<VisitDto> CreateVisitAsync(VisitUpsertDto dto, CancellationToken ct);
    Task<VisitDto?> UpdateVisitAsync(Guid id, VisitUpsertDto dto, CancellationToken ct);
    Task<VisitDto?> CompleteVisitAsync(Guid id, string? note, CancellationToken ct);
    Task<bool> DeleteVisitAsync(Guid id, CancellationToken ct);
}
