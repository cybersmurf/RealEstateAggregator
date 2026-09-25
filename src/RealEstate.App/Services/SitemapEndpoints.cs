using System.Text;
using System.Xml;
using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Contracts.Market;

namespace RealEstate.App.Services;

/// <summary>
/// /sitemap.xml pro vyhledávače: statické stránky, obce a aktivní inzeráty.
/// Výsledek se cachuje na 6 hodin – API se ptá jen jednou za tu dobu.
/// </summary>
public static class SitemapEndpoints
{
    private static string? _cached;
    private static DateTime _cachedAt;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static IEndpointRouteBuilder MapSitemap(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sitemap.xml", async (HttpContext ctx, IHttpClientFactory factory, IConfiguration config, CancellationToken ct) =>
        {
            if (_cached is null || DateTime.UtcNow - _cachedAt > TimeSpan.FromHours(6))
            {
                await Lock.WaitAsync(ct);
                try
                {
                    if (_cached is null || DateTime.UtcNow - _cachedAt > TimeSpan.FromHours(6))
                    {
                        var baseUrl = (config["PublicBaseUrl"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
                        _cached = await BuildAsync(factory.CreateClient("RealEstateApi"), baseUrl, ct);
                        _cachedAt = DateTime.UtcNow;
                    }
                }
                finally
                {
                    Lock.Release();
                }
            }
            return Results.Content(_cached, "application/xml", Encoding.UTF8);
        });
        return app;
    }

    private static async Task<string> BuildAsync(HttpClient api, string baseUrl, CancellationToken ct)
    {
        var urls = new List<(string Loc, string Freq, string Prio, DateTime? Mod)>
        {
            ($"{baseUrl}/", "daily", "1.0", null),
            ($"{baseUrl}/listings", "hourly", "0.9", null),
            ($"{baseUrl}/lokality", "daily", "0.8", null),
            ($"{baseUrl}/pricing", "monthly", "0.5", null),
            ($"{baseUrl}/terms", "yearly", "0.2", null),
            ($"{baseUrl}/privacy", "yearly", "0.2", null),
        };

        try
        {
            var localities = await api.GetFromJsonAsync<List<LocalityIndexItemDto>>("api/localities?minActive=2", ct) ?? new();
            urls.AddRange(localities.Select(l => ($"{baseUrl}/lokalita/{l.Slug}", "daily", "0.7", (DateTime?)null)));

            for (var page = 1; page <= 25; page++)
            {
                using var resp = await api.PostAsJsonAsync("api/listings/search",
                    new ListingFilterDto { Page = page, PageSize = 200, SortBy = "date", SortDescending = true }, ct);
                if (!resp.IsSuccessStatusCode) break;
                var result = await resp.Content.ReadFromJsonAsync<PagedResultDto<ListingSummaryDto>>(cancellationToken: ct);
                if (result is null || result.Items.Count == 0) break;
                urls.AddRange(result.Items.Select(i => ($"{baseUrl}/listings/{i.Id}", "weekly", "0.5", (DateTime?)(i.UpdatedAtSource ?? i.FirstSeenAt))));
                if (result.Items.Count < 200) break;
            }
        }
        catch
        {
            // API nedostupné – vrátíme aspoň statické stránky
        }

        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(sb, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = Encoding.UTF8 }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var (loc, freq, prio, mod) in urls)
            {
                xml.WriteStartElement("url");
                xml.WriteElementString("loc", loc);
                if (mod is not null) xml.WriteElementString("lastmod", mod.Value.ToString("yyyy-MM-dd"));
                xml.WriteElementString("changefreq", freq);
                xml.WriteElementString("priority", prio);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return sb.ToString();
    }
}
