using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;
using RealEstate.Api.Services.Auth;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Repositories;

namespace RealEstate.Tests;

// „Náš cíl": kritérium „Samostatně stojící" – chybné „detached" obrazového modelu nesmí přebít makléřovo „Řadový".
public class TargetProfileTests
{
    private static Listing House(string? position, string? source, string? listed) => new()
    {
        Id = Guid.NewGuid(),
        PropertyType = PropertyType.House,
        OfferType = OfferType.Sale,
        Price = 8_500_000m,
        AreaBuiltUp = 140,
        Rooms = 5,
        District = "Znojmo",
        AreaLand = 800,
        HousePosition = position,
        HousePositionSource = source,
        HousePositionListed = listed,
    };

    [Theory]
    // Kubšice / Únanov / Břežany před novým určením: model „detached", makléř „Řadový" → makléř
    [InlineData("detached", null, "Řadový", false)]
    [InlineData("detached", null, "V bloku", false)]
    [InlineData("detached", "vision", "Rohový", false)]
    // Damnice: katastr se snímky potvrdil samostatný dům proti makléři
    [InlineData("detached", "cadastre", "Řadový", true)]
    // Štítary / Černín před novým určením: model „semi", makléř „Samostatný" → makléř
    [InlineData("semi_detached", null, "Samostatný", true)]
    // Katastr změřil společnou zeď a jiný zdroj to potvrdil – makléřovo „Samostatný" neplatí
    [InlineData("corner", "cadastre", "Samostatný", false)]
    [InlineData("detached", "broker", "Samostatný", true)]
    [InlineData("terraced", "broker", "Řadový", false)]
    // Kopie domu bez údaje makléře: platí uložená (sladěná) poloha
    [InlineData("detached", "vision", null, true)]
    [InlineData("detached", null, null, true)]
    [InlineData("terraced", "broker", null, false)]
    [InlineData("unknown", "vision", null, false)]
    [InlineData(null, null, "Samostatný", true)]
    [InlineData(null, null, "Řadový", false)]
    [InlineData(null, null, null, false)]
    public void IsDetached_CadastreThenBrokerThenStoredPosition(string? position, string? source, string? listed, bool expected)
    {
        Assert.Equal(expected, TargetProfile.IsDetached(position, source, listed));

        var listing = House(position, source, listed);
        var evaluation = TargetProfile.Evaluate(listing);
        Assert.Equal(expected, !evaluation.Missing.Contains("Samostatně stojící"));
        // SQL výraz počítá totéž co Evaluate
        Assert.Equal(evaluation.Met, TargetProfile.ScoreExpression.Compile()(listing));
    }

    [Fact]
    public void ScoreExpression_TranslatesToSql_WithHousePositionSource()
    {
        var options = new DbContextOptionsBuilder<RealEstateDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        var ctx = new RealEstateDbContext(options);
        var service = new ListingService(new ListingRepository(ctx), ctx, new CurrentUser());

        var sql = service.BuildFilteredQuery(new ListingFilterDto { TargetMinCriteria = 8 }).ToQueryString();

        Assert.Contains("house_position_source", sql);
        Assert.Contains("house_position_listed", sql);
        Assert.Contains("'cadastre'", sql);
        Assert.Contains("'Samostatn%'", sql);
    }
}
