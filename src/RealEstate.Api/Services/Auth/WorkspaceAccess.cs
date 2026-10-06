using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Auth;

/// <summary>Členství přihlášeného účtu ve společném prostoru jiného účtu.</summary>
public sealed record WorkspaceAccess(Guid OwnerId, string Role, bool OwnerIsAdmin)
{
    /// <summary>
    /// Najde prostor, do kterého e-mail účtu patří. Vlastník sám sebe jako člena nemá; členství
    /// v prostoru neaktivního vlastníka neplatí.
    /// </summary>
    public static async Task<WorkspaceAccess?> ResolveAsync(RealEstateDbContext db, User user, CancellationToken ct)
    {
        var email = AuthService.NormalizeEmail(user.Email);
        if (email is null) return null;

        var membership = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.Email == email && m.OwnerId != user.Id && m.Owner.IsActive)
            .Select(m => new { m.OwnerId, m.Role, m.Owner.IsAdmin })
            .FirstOrDefaultAsync(ct);

        return membership is null
            ? null
            : new WorkspaceAccess(membership.OwnerId, membership.Role, membership.IsAdmin);
    }
}
