using RealEstate.Api.Services.Auth;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// Společný prostor: člen pracuje se stavy vlastníka; čtenář je nemění; záznamy z prohlídek vidí
// jen správce a členové prostoru správce.
public class WorkspaceAccessTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static CurrentUser Member(string role, bool ownerIsAdmin = true, bool isAdmin = false)
    {
        var user = new CurrentUser { UserId = Guid.NewGuid(), Email = "jirka@example.com", IsAdmin = isAdmin };
        user.ApplyWorkspace(new WorkspaceAccess(Owner, role, ownerIsAdmin));
        return user;
    }

    [Fact]
    public void Reader_SeesOwnersStates_ButCannotWrite()
    {
        var reader = Member(WorkspaceRoles.Reader);

        Assert.Equal(Owner, reader.EffectiveUserId);
        Assert.False(reader.CanWriteWorkspace);
        Assert.True(reader.CanSeeInspectionRecords);
    }

    [Fact]
    public void Writer_SharesOwnersStates_AndCanWrite()
    {
        var writer = Member(WorkspaceRoles.Writer);

        Assert.Equal(Owner, writer.EffectiveUserId);
        Assert.True(writer.CanWriteWorkspace);
    }

    [Fact]
    public void UserWithoutWorkspace_UsesOwnStates_AndSeesNoInspectionRecords()
    {
        var id = Guid.NewGuid();
        var user = new CurrentUser { UserId = id, Email = "nekdo@example.com" };

        Assert.Equal(id, user.EffectiveUserId);
        Assert.True(user.CanWriteWorkspace);
        Assert.False(user.CanSeeInspectionRecords);
    }

    [Fact]
    public void MemberOfNonAdminWorkspace_DoesNotSeeInspectionRecords()
    {
        // Fotky z prohlídek a analýzy patří vlastníkovi aplikace – prostor běžného účtu k nim nepustí
        Assert.False(Member(WorkspaceRoles.Reader, ownerIsAdmin: false).CanSeeInspectionRecords);
    }

    [Fact]
    public void Anonymous_HasNoStates_AndCannotWrite()
    {
        var anonymous = new CurrentUser();

        Assert.Equal(Guid.Empty, anonymous.EffectiveUserId);
        Assert.False(anonymous.CanWriteWorkspace);
        Assert.False(anonymous.CanSeeInspectionRecords);
    }

    [Theory]
    [InlineData("reader", true)]
    [InlineData("writer", true)]
    [InlineData("admin", false)]
    [InlineData(null, false)]
    public void Roles_AreValidated(string? role, bool expected)
        => Assert.Equal(expected, WorkspaceRoles.IsValid(role));
}
