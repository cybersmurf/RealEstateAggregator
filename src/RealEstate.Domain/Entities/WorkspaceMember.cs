namespace RealEstate.Domain.Entities;

/// <summary>
/// Člen společného prostoru: účet s tímto e-mailem sdílí stavy, poznámky, fotky z prohlídek a analýzy
/// vlastníka. Váže se na e-mail, ne na Id účtu – člena jde přizvat dřív, než se zaregistruje.
/// </summary>
public class WorkspaceMember
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Vlastník prostoru – jeho stavy a poznámky člen vidí.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Normalizovaný (malými písmeny) e-mail člena. Jeden e-mail smí být jen v jednom prostoru.</summary>
    public string Email { get; set; } = null!;

    /// <summary><see cref="WorkspaceRoles.Reader"/> nebo <see cref="WorkspaceRoles.Writer"/>.</summary>
    public string Role { get; set; } = WorkspaceRoles.Reader;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Owner { get; set; } = null!;
}

public static class WorkspaceRoles
{
    /// <summary>Vidí stavy, poznámky, fotky z prohlídek a analýzy; nic nemění.</summary>
    public const string Reader = "reader";

    /// <summary>Navíc mění stavy a píše poznámky.</summary>
    public const string Writer = "writer";

    public static bool IsValid(string? role) => role is Reader or Writer;
}
