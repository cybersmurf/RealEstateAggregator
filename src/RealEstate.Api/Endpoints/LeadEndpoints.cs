using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Leads;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Api.Services.Leads;

namespace RealEstate.Api.Endpoints;

public static class LeadEndpoints
{
    public static IEndpointRouteBuilder MapLeadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leads").WithTags("Leads");

        // Anonymní – poptávku může poslat i nepřihlášený návštěvník; brzdí ji rate limit podle IP
        group.MapPost("", Create)
            .WithName("CreateLead")
            .WithSummary("Uloží poptávku (hypotéka) a přepošle ji partnerovi.")
            .RequireRateLimiting("leads");

        group.MapGet("", List)
            .WithName("AdminListLeads")
            .WithSummary("Přehled poptávek pro správce.")
            .RequireAdmin();

        return app;
    }

    private static async Task<IResult> Create(
        [FromBody] LeadCreateDto request, [FromServices] ILeadService leads, [FromServices] ICurrentUser current, CancellationToken ct)
    {
        var (lead, error) = await leads.CreateAsync(request, current.UserId, ct);
        return error is null
            ? Results.Ok(lead)
            : Results.Problem(title: "Invalid lead", detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> List([FromServices] ILeadService leads, int take = 200, CancellationToken ct = default)
        => Results.Ok(await leads.ListAsync(take, ct));
}
