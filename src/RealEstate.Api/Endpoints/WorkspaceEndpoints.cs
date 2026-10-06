using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Endpoints;

public sealed record WorkspaceMemberDto(Guid Id, string Email, string Role, bool Registered, DateTime? LastLoginAt, DateTime CreatedAt);

public sealed record AddWorkspaceMemberRequestDto(string Email, string? Role);

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        // Společný prostor zatím spravuje jen správce: fotky z prohlídek a analýzy nejsou vedené po
        // uživatelích, takže sdílet je smí jen ten, komu patří.
        var group = app.MapGroup("/api/workspace/members")
            .WithTags("Workspace")
            .RequireAdmin();

        group.MapGet("", List).WithName("ListWorkspaceMembers");
        group.MapPost("", Add).WithName("AddWorkspaceMember");
        group.MapDelete("/{id:guid}", Remove).WithName("RemoveWorkspaceMember");

        return app;
    }

    private static async Task<IResult> List(
        [FromServices] RealEstateDbContext db,
        [FromServices] ICurrentUser user,
        CancellationToken ct)
    {
        var ownerId = user.EffectiveUserId;
        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.OwnerId == ownerId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id, m.Email, m.Role, m.CreatedAt,
                Account = db.Users.Where(u => u.Email == m.Email).Select(u => new { u.LastLoginAt }).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(members
            .Select(m => new WorkspaceMemberDto(m.Id, m.Email, m.Role, m.Account is not null, m.Account?.LastLoginAt, m.CreatedAt))
            .ToList());
    }

    private static async Task<IResult> Add(
        [FromBody] AddWorkspaceMemberRequestDto request,
        [FromServices] RealEstateDbContext db,
        [FromServices] ICurrentUser user,
        CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(request.Email);
        if (email is null || !email.Contains('@'))
            return Results.Problem(title: "Neplatný e-mail", statusCode: StatusCodes.Status400BadRequest);

        var role = string.IsNullOrWhiteSpace(request.Role) ? WorkspaceRoles.Reader : request.Role.Trim().ToLowerInvariant();
        if (!WorkspaceRoles.IsValid(role))
            return Results.Problem(title: "Neplatná role", detail: "Role musí být reader nebo writer.",
                statusCode: StatusCodes.Status400BadRequest);

        var ownerId = user.EffectiveUserId;
        if (string.Equals(email, AuthService.NormalizeEmail(user.Email), StringComparison.Ordinal))
            return Results.Problem(title: "Sebe přidat nelze", statusCode: StatusCodes.Status400BadRequest);

        var existing = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.Email == email, ct);
        if (existing is not null && existing.OwnerId != ownerId)
            return Results.Problem(title: "E-mail už patří do jiného společného prostoru",
                statusCode: StatusCodes.Status409Conflict);

        if (existing is null)
        {
            existing = new WorkspaceMember { OwnerId = ownerId, Email = email, Role = role };
            db.WorkspaceMembers.Add(existing);
        }
        else
        {
            existing.Role = role;   // opakované přidání = změna role
        }
        await db.SaveChangesAsync(ct);

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.LastLoginAt })
            .FirstOrDefaultAsync(ct);
        return Results.Ok(new WorkspaceMemberDto(existing.Id, existing.Email, existing.Role,
            account is not null, account?.LastLoginAt, existing.CreatedAt));
    }

    private static async Task<IResult> Remove(
        Guid id,
        [FromServices] RealEstateDbContext db,
        [FromServices] ICurrentUser user,
        CancellationToken ct)
    {
        var ownerId = user.EffectiveUserId;
        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(m => m.Id == id && m.OwnerId == ownerId, ct);
        if (member is null) return Results.NotFound();

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
