using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;

namespace RealEstate.Infrastructure;

public static class DbInitializer
{
    public static async Task SeedAsync(RealEstateDbContext dbContext, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Sources.AnyAsync(cancellationToken))
        {
            return;
        }

        var sources = new List<Source>
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
        };

        dbContext.Sources.AddRange(sources);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
