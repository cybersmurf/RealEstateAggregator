using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure;

public static class DbInitializer
{
    public static async Task SeedAsync(RealEstateDbContext dbContext, CancellationToken cancellationToken = default)
    {
        // Upsert logika: přidá chybějící sources, stávající nevymaže
        var existingCodes = await dbContext.Sources
            .Select(s => s.Code)
            .ToHashSetAsync(cancellationToken);

        var allSources = new List<Source>
        {
            new()
            {
                Code = "REMAX",
                Name = "RE/MAX Czech Republic",
                BaseUrl = "https://www.remax-czech.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "MMR",
                Name = "M&M Reality",
                BaseUrl = "https://www.mmreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "PRODEJMETO",
                Name = "Prodejme.to",
                BaseUrl = "https://www.prodejme.to",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "ZNOJMOREALITY",
                Name = "Znojmo Reality",
                BaseUrl = "https://www.znojmoreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "SREALITY",
                Name = "Sreality",
                BaseUrl = "https://www.sreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "NEMZNOJMO",
                Name = "Nemovitosti Znojmo",
                BaseUrl = "https://www.nemovitostiznojmo.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "HVREALITY",
                Name = "Horák & Vetchý reality",
                BaseUrl = "https://hvreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "PREMIAREALITY",
                Name = "PREMIA Reality s.r.o.",
                BaseUrl = "https://www.premiareality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "DELUXREALITY",
                Name = "DeluXreality Znojmo",
                BaseUrl = "https://deluxreality.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "LEXAMO",
                Name = "Lexamo Reality",
                BaseUrl = "https://www.lexamo.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
            new()
            {
                Code = "CENTURY21",
                Name = "CENTURY 21 Czech Republic",
                BaseUrl = "https://www.century21.cz",
                IsActive = true,
                SupportsUrlScrape = true,
                SupportsListScrape = true,
                ScraperType = "Python",
            },
        };

        var newSources = allSources.Where(s => !existingCodes.Contains(s.Code)).ToList();
        if (newSources.Count > 0)
        {
            dbContext.Sources.AddRange(newSources);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
