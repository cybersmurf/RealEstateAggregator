using RealEstate.Api.Services.Auth;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Helpers;

/// <summary>
/// Endpoint filtry nad <see cref="ICurrentUser"/>. Fungují na jednotlivých handlerech
/// i na celé skupině (MapGroup). Admin projde vždy.
/// </summary>
public static class AuthorizationFilters
{
    public static TBuilder RequireAuth<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
            if (!user.IsAuthenticated)
                return Unauthorized("Pro tuto akci je nutné přihlášení.");
            return await next(ctx);
        });
        return builder;
    }

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
            if (!user.IsAuthenticated)
                return Unauthorized("Pro tuto akci je nutné přihlášení.");
            if (!user.IsAdmin)
                return Forbidden("Tato akce je dostupná jen správci.");
            return await next(ctx);
        });
        return builder;
    }

    /// <param name="plan"><see cref="UserPlans.Hledac"/> nebo <see cref="UserPlans.Profi"/>.</param>
    public static TBuilder RequirePlan<TBuilder>(this TBuilder builder, string plan) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
            if (!user.IsAuthenticated)
                return Unauthorized("Pro tuto funkci je nutné přihlášení.");
            if (!user.HasPlan(plan))
                return Results.Problem(
                    title: "Plan required",
                    detail: $"Tato funkce vyžaduje tarif „{PlanLabel(plan)}“. Aktuální tarif: {PlanLabel(user.Plan)}.",
                    statusCode: StatusCodes.Status402PaymentRequired,
                    extensions: new Dictionary<string, object?> { ["requiredPlan"] = plan, ["currentPlan"] = user.Plan });
            return await next(ctx);
        });
        return builder;
    }

    public static string PlanLabel(string? plan) => plan switch
    {
        UserPlans.Profi => "Profi",
        UserPlans.Hledac => "Hledač",
        _ => "Zdarma",
    };

    private static IResult Unauthorized(string detail) =>
        Results.Problem(title: "Unauthorized", detail: detail, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(string detail) =>
        Results.Problem(title: "Forbidden", detail: detail, statusCode: StatusCodes.Status403Forbidden);
}
