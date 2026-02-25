using System.Reflection;
using System.Text.Json;
using RealEstate.Api.Services;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  IsNewBuild – detekce novostavby z condition / description
// ─────────────────────────────────────────────────────────────────
public class IsNewBuildTests
{
    [Theory]
    // pozitivní – description
    [InlineData(null,           "Krásná novostavba RD v klidné lokalitě",   true)]
    [InlineData(null,           "Rodinný dům ve výstavbě, dokončení 2024",  true)]
    [InlineData(null,           "Dům pod klíč, ihned k nastěhování",        true)]
    [InlineData(null,           "Developerský projekt bytových domů",       true)]
    [InlineData(null,           "Nabízen dokončení 2025, energie A",        true)]
    // pozitivní – condition
    [InlineData("Nový",         null,                                        true)]
    [InlineData("Nová",         null,                                        true)]
    [InlineData("nový",         null,                                        true)]  // lowercase
    // negativní – existující nemovitosti
    [InlineData(null,           null,                                        false)]
    [InlineData("",             "",                                          false)]
    [InlineData("Dobrý",        "Rodinný dům 5+1, po rekonstrukci střechy", false)]
    [InlineData("Starý",        "Dům z roku 1975, celková rekonstrukce",    false)]
    [InlineData("K rekonstrukci", "Velký pozemek, ideální lokalita",        false)]
    public void IsNewBuild_DetectsNewBuildCorrectly(string? condition, string? description, bool expected)
    {
        Assert.Equal(expected, ListingExportContentBuilder.IsNewBuild(condition, description));
    }

    [Fact]
    public void IsNewBuild_KeywordNovostavbIsSubstring_ReturnsTrue()
    {
        // "novostavba", "novostavbu", "novostavby" – všechny mají prefix "novostavb"
        Assert.True(ListingExportContentBuilder.IsNewBuild(null, "Prodej novostavby 4+kk"));
        Assert.True(ListingExportContentBuilder.IsNewBuild(null, "Novostavbu RD předáváme 6/2025"));
    }

    [Fact]
    public void IsNewBuild_VeVystavbe_ReturnsTrue()
    {
        // "ve výstavbě" = "ve výstavb" + "ě"
        Assert.True(ListingExportContentBuilder.IsNewBuild(null, "Dům ve výstavbě – kolaudace Q3/2025"));
    }
}

// ─────────────────────────────────────────────────────────────────
//  SanitizeName – filesystem-safe názvy pro cloud upload
// ─────────────────────────────────────────────────────────────────
public class SanitizeNameTests
{
    [Theory]
    [InlineData("Rodinný dům Znojmo",   "Rodinný dům Znojmo")]       // normální název beze změny
    [InlineData("path/to/file",         "path_to_file")]              // lomítko → podtržítko
    [InlineData("C:\\folder\\file",     "C__folder_file")]            // zpětné lomítko → _
    [InlineData("name:value",           "name_value")]                // dvojtečka → _
    [InlineData("file*name",            "file_name")]                 // hvězdička → _
    [InlineData("file?query",           "file_query")]                // otazník → _
    [InlineData("say \"hello\"",        "say _hello_")]               // uvozovky → _
    [InlineData("<tag>",                "_tag_")]                     // < a > → _
    [InlineData("pipe|char",            "pipe_char")]                 // svislítko → _
    [InlineData("all:bad*char?name\"x\"<a>|b", "all_bad_char_name_x__a__b")]  // kombinace
    public void SanitizeName_ReplacesIllegalChars(string input, string expected)
    {
        Assert.Equal(expected, ListingExportContentBuilder.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_TruncatesTo100Chars()
    {
        var longName = new string('a', 150);
        var result = ListingExportContentBuilder.SanitizeName(longName);
        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeName_ExactlyAtLimit_NotTruncated()
    {
        var name = new string('a', 100);
        Assert.Equal(100, ListingExportContentBuilder.SanitizeName(name).Length);
    }

    [Theory]
    [InlineData("  trimmed  ", "trimmed")]          // leading/trailing mezery jsou trimovány
    [InlineData("",             "")]                // prázdný → prázdný
    public void SanitizeName_TrimsWhitespace(string input, string expected)
    {
        Assert.Equal(expected, ListingExportContentBuilder.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_UnicodePreserved()
    {
        // Czech diacritics musí zůstat nedotčeny
        const string name = "Šumavský dům – útulné bydlení";
        var result = ListingExportContentBuilder.SanitizeName(name);
        Assert.Contains("Šumavský", result);
        Assert.Contains("útulné", result);
    }
}

// ─────────────────────────────────────────────────────────────────
//  BuildDataJson – JSON výstup pro AI analýzu
// ─────────────────────────────────────────────────────────────────
public class BuildDataJsonTests
{
    private static Listing MakeListing(Action<Listing>? configure = null)
    {
        var listing = new Listing
        {
            Id          = Guid.NewGuid(),
            SourceCode  = "TEST",
            SourceName  = "Test Source",
            Url         = "https://test.cz/inzerat/1",
            Title       = "Rodinný dům 4+1 Znojmo",
            Description = "Pěkný dům ve Znojmě",
            LocationText = "Znojmo, okres Znojmo",
            Municipality = "Znojmo",
            District     = "Znojmo",
            Region       = "Jihomoravský kraj",
            PropertyType = PropertyType.House,
            OfferType    = OfferType.Sale,
            Price        = 5_000_000m,
            AreaBuiltUp  = 150.0,
            AreaLand     = 600.0,
            Rooms        = 4,
            FirstSeenAt  = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(listing);
        return listing;
    }

    [Fact]
    public void BuildDataJson_ContainsAllTopLevelFields()
    {
        var listing = MakeListing();
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Povinné klíče musejí existovat
        foreach (var key in new[]
        {
            "id", "title", "property_type", "offer_type", "price",
            "location_text", "source_name", "source_code", "url",
            "description", "first_seen_at", "photos_count", "photo_urls",
            "age_category"
        })
        {
            Assert.True(root.TryGetProperty(key, out _), $"JSON postrádá klíč '{key}'");
        }
    }

    [Fact]
    public void BuildDataJson_PropertyTypeAndOfferType_AreEnglish()
    {
        var listing = MakeListing(l =>
        {
            l.PropertyType = PropertyType.Apartment;
            l.OfferType    = OfferType.Rent;
        });
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Apartment", doc.RootElement.GetProperty("property_type").GetString());
        Assert.Equal("Rent",      doc.RootElement.GetProperty("offer_type").GetString());
    }

    [Fact]
    public void BuildDataJson_AuctionOfferType_SerializedCorrectly()
    {
        var listing = MakeListing(l => l.OfferType = OfferType.Auction);
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Auction", doc.RootElement.GetProperty("offer_type").GetString());
    }

    [Fact]
    public void BuildDataJson_ExistingHome_AgeCategoryIsExisting()
    {
        var listing = MakeListing(l =>
        {
            l.Condition   = "Dobrý";
            l.Description = "Dům po rekonstrukci, vše hotovo";
        });
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("existing", doc.RootElement.GetProperty("age_category").GetString());
    }

    [Fact]
    public void BuildDataJson_NewBuild_AgeCategoryIsNewBuild()
    {
        var listing = MakeListing(l =>
        {
            l.Condition   = "Nový";
            l.Description = "Novostavba – kolaudace Q4/2025";
        });
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("new_build", doc.RootElement.GetProperty("age_category").GetString());
    }

    [Fact]
    public void BuildDataJson_NullPrice_SerializedAsNull()
    {
        var listing = MakeListing(l => l.Price = null);
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("price").ValueKind);
    }

    [Fact]
    public void BuildDataJson_IsValidJson()
    {
        var listing = MakeListing();
        var json = ListingExportContentBuilder.BuildDataJson(listing);
        Assert.True(json.Length > 0);
        // Nesmí vrhat při parsování
        var ex = Record.Exception(() => JsonDocument.Parse(json));
        Assert.Null(ex);
    }
}

// ─────────────────────────────────────────────────────────────────
//  BuildPhotoLinksInlineSection – private helper (via reflection)
// ─────────────────────────────────────────────────────────────────
public class BuildPhotoLinksInlineSectionTests
{
    private static string Invoke(IReadOnlyList<PhotoLink>? photos)
    {
        var method = typeof(ListingExportContentBuilder).GetMethod(
            "BuildPhotoLinksInlineSection",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [photos])!;
    }

    [Fact]
    public void NullOrEmptyPhotos_ReturnsEmpty()
    {
        Assert.Equal("", Invoke(null));
        Assert.Equal("", Invoke([]));
    }

    [Fact]
    public void FewPhotos_AllIncluded_NoTruncationMessage()
    {
        var photos = Enumerable.Range(1, 5)
            .Select(i => new PhotoLink($"foto_{i:00}.jpg", $"https://cdn.test/{i}.jpg"))
            .ToList();

        var result = Invoke(photos);

        Assert.Contains("foto_01.jpg", result);
        Assert.Contains("foto_05.jpg", result);
        Assert.DoesNotContain("a dalších", result);
    }

    [Fact]
    public void MoreThan10Photos_TruncatedWithMessage()
    {
        var photos = Enumerable.Range(1, 15)
            .Select(i => new PhotoLink($"foto_{i:00}.jpg", $"https://cdn.test/{i}.jpg"))
            .ToList();

        var result = Invoke(photos);

        // Zobrazíme max 10
        Assert.Contains("foto_01.jpg", result);
        Assert.Contains("foto_10.jpg", result);
        Assert.DoesNotContain("foto_11.jpg", result);  // 11. fotka nesmí být v inline sekci
        Assert.Contains("a dalších 5 fotek", result);
    }

    [Fact]
    public void ExactlyTenPhotos_NoTruncationMessage()
    {
        var photos = Enumerable.Range(1, 10)
            .Select(i => new PhotoLink($"foto_{i:00}.jpg", $"https://cdn.test/{i}.jpg"))
            .ToList();

        var result = Invoke(photos);
        Assert.DoesNotContain("a dalších", result);
    }

    [Fact]
    public void PhotoLink_OriginalSourceUrl_UsedForAiInsteadOfDirectUrl()
    {
        // Pokud je OriginalSourceUrl nastaveno, systém ho preferuje (pro AI nástroje)
        var photos = new List<PhotoLink>
        {
            new("foto_01.jpg", "https://onedrive.com/direct-url", "https://sreality.cz/original-url")
        };

        var result = Invoke(photos);

        Assert.Contains("https://sreality.cz/original-url", result);
        Assert.DoesNotContain("https://onedrive.com/direct-url", result);
    }

    [Fact]
    public void PhotoLink_NoOriginalUrl_FallsBackToDirectUrl()
    {
        var photos = new List<PhotoLink>
        {
            new("foto_01.jpg", "https://onedrive.com/direct-url")
            // OriginalSourceUrl vynechán – default ""
        };

        var result = Invoke(photos);
        Assert.Contains("https://onedrive.com/direct-url", result);
    }
}

// ─────────────────────────────────────────────────────────────────
//  PageGuard – skip kalkulace pro stránkování (Math.Max guard)
// ─────────────────────────────────────────────────────────────────
public class PageGuardTests
{
    // Replikuje logiku z ListingService.SearchAsync:
    // var skip = Math.Max(0, (filter.Page - 1) * filter.PageSize);
    private static int CalculateSkip(int page, int pageSize)
        => Math.Max(0, (page - 1) * pageSize);

    [Theory]
    [InlineData(0, 50,  0)]   // Page=0 → skip 0 (guard zabrání zápornému skoku)
    [InlineData(1, 50,  0)]   // Page=1 → první strana, skip=0
    [InlineData(2, 50, 50)]   // Page=2 → přeskočit 50
    [InlineData(3, 50, 100)]  // Page=3 → přeskočit 100
    [InlineData(2, 25, 25)]   // Page=2, pageSize=25
    [InlineData(10, 10, 90)]  // Page=10, pageSize=10
    public void SkipCalculation_IsCorrect(int page, int pageSize, int expectedSkip)
    {
        Assert.Equal(expectedSkip, CalculateSkip(page, pageSize));
    }

    [Fact]
    public void NegativePage_ClampedToZero()
    {
        // Extrémní případ: negativní stránka nesmí generovat záporný offset
        Assert.Equal(0, CalculateSkip(-1, 50));
        Assert.Equal(0, CalculateSkip(-100, 50));
    }
}
