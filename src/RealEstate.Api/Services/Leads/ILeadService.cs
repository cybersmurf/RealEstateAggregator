using RealEstate.Api.Contracts.Leads;

namespace RealEstate.Api.Services.Leads;

/// <summary>Poptávky (leady) z hypoteční kalkulačky – uložení + best-effort přeposlání e-mailem.</summary>
public interface ILeadService
{
    /// <returns>Uložený lead, nebo chybová hláška (validace) – nikdy obojí.</returns>
    Task<(LeadDto? Lead, string? Error)> CreateAsync(LeadCreateDto request, Guid? userId, CancellationToken ct);

    /// <summary>Přehled pro admina, nejnovější první, s titulkem inzerátu.</summary>
    Task<List<LeadDto>> ListAsync(int take, CancellationToken ct);
}
