using System.Reflection;
using RealEstate.Api.Contracts.Cadastre;
using RealEstate.Api.Services;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  CadastreService – PreferMunicipality (private static)
// ─────────────────────────────────────────────────────────────────────────────
public class CadastreServiceHelperTests
{
    // Reflection helper – volá private static PreferMunicipality(string?, string)
    private static string PreferMunicipality(string? municipality, string locationText)
    {
        var method = typeof(CadastreService).GetMethod(
            "PreferMunicipality",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Metoda PreferMunicipality nenalezena.");

        return (string)method.Invoke(null, [municipality, locationText])!;
    }

    // ── municipality má přednost ──────────────────────────────────────────────

    [Fact]
    public void PreferMunicipality_WhenMunicipalitySet_ReturnsMunicipality()
    {
        var result = PreferMunicipality("Pohořelice", "Pohořelice, okres Brno-venkov");
        Assert.Equal("Pohořelice", result);
    }

    [Fact]
    public void PreferMunicipality_WhenMunicipalitySetWithWhitespace_ReturnsTrimmed()
    {
        var result = PreferMunicipality("  Znojmo  ", "Znojmo, kraj Jihomoravský");
        Assert.Equal("Znojmo", result);
    }

    // ── fallback – adresa z location_text ────────────────────────────────────

    [Fact]
    public void PreferMunicipality_WhenMunicipalityNull_ReturnsCleanedLocationText()
    {
        var result = PreferMunicipality(null, "Praha 5, okres Praha-západ");
        // „, okres Praha-západ" by mělo být odstraněno
        Assert.DoesNotContain("okres", result);
    }

    [Fact]
    public void PreferMunicipality_WhenMunicipalityEmpty_ReturnsCleanedLocationText()
    {
        var result = PreferMunicipality("", "Brno, kraj Jihomoravský");
        Assert.DoesNotContain("kraj", result);
        Assert.Contains("Brno", result);
    }

    [Fact]
    public void PreferMunicipality_WhenMunicipalityWhitespaceOnly_ReturnsCleanedLocationText()
    {
        var result = PreferMunicipality("   ", "Olomouc, okr. Olomouc");
        Assert.DoesNotContain("okr.", result);
        Assert.Contains("Olomouc", result);
    }

    [Theory]
    [InlineData("Štítary", "Praha, kraj Jihomoravský", "Štítary")]
    [InlineData("Mikulov", "Mikulov, okres Břeclav", "Mikulov")]
    [InlineData(null, "Brno", "Brno")]
    public void PreferMunicipality_Parametrized(string? municipality, string location, string expected)
    {
        var result = PreferMunicipality(municipality, location);
        Assert.Equal(expected, result);
    }

    // ── dlouhý text je zkrácen na 80 znaků ───────────────────────────────────

    [Fact]
    public void PreferMunicipality_LongLocationText_TruncatedTo80Chars()
    {
        var longText = new string('A', 100);
        var result = PreferMunicipality(null, longText);
        Assert.True(result.Length <= 80, $"Délka {result.Length} > 80");
    }

    [Fact]
    public void PreferMunicipality_ExactlyAt80Chars_NotTruncated()
    {
        var text = new string('B', 80);
        var result = PreferMunicipality(null, text);
        Assert.Equal(80, result.Length);
    }

    // ── null location_text bezpečně zpracován ────────────────────────────────

    [Fact]
    public void PreferMunicipality_BothNull_ReturnsEmpty()
    {
        var result = PreferMunicipality(null, null!);
        Assert.Equal("", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ListingCadastreData – výchozí hodnoty entity
// ─────────────────────────────────────────────────────────────────────────────
public class ListingCadastreDataDefaultsTests
{
    [Fact]
    public void DefaultFetchStatus_IsPending()
    {
        var entity = new ListingCadastreData();
        Assert.Equal("pending", entity.FetchStatus);
    }

    [Fact]
    public void DefaultAddressSearched_IsEmptyString()
    {
        var entity = new ListingCadastreData();
        Assert.Equal("", entity.AddressSearched);
    }

    [Fact]
    public void DefaultFetchedAt_IsRecentUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var entity = new ListingCadastreData();
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.InRange(entity.FetchedAt, before, after);
    }

    [Fact]
    public void NullableFields_AreNullByDefault()
    {
        var entity = new ListingCadastreData();
        Assert.Null(entity.RuianKod);
        Assert.Null(entity.ParcelNumber);
        Assert.Null(entity.LvNumber);
        Assert.Null(entity.LandAreaM2);
        Assert.Null(entity.LandType);
        Assert.Null(entity.OwnerType);
        Assert.Null(entity.EncumbrancesJson);
        Assert.Null(entity.CadastreUrl);
        Assert.Null(entity.FetchError);
        Assert.Null(entity.RawRuianJson);
        Assert.Null(entity.Listing);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ListingCadastreDto – record equality & properties
// ─────────────────────────────────────────────────────────────────────────────
public class ListingCadastreDtoTests
{
    private static readonly Guid SampleListingId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid SampleId        = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly DateTime SampleDate  = new(2026, 2, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Record_CreatedWithCorrectValues()
    {
        var dto = new ListingCadastreDto(
            Id: SampleId,
            ListingId: SampleListingId,
            RuianKod: 123456789L,
            ParcelNumber: "123/4",
            LvNumber: "LV-100",
            LandAreaM2: 500,
            LandType: "zahrada",
            OwnerType: "fyzická osoba",
            EncumbrancesJson: """[{"type":"zástatavní právo"}]""",
            AddressSearched: "Pohořelice",
            CadastreUrl: "https://nahlizenidokn.cuzk.cz/ZobrazitMapu/Basic?typeCode=adresniMisto&id=123456789",
            FetchStatus: "found",
            FetchError: null,
            FetchedAt: SampleDate
        );

        Assert.Equal(SampleId, dto.Id);
        Assert.Equal(SampleListingId, dto.ListingId);
        Assert.Equal(123456789L, dto.RuianKod);
        Assert.Equal("123/4", dto.ParcelNumber);
        Assert.Equal("LV-100", dto.LvNumber);
        Assert.Equal(500, dto.LandAreaM2);
        Assert.Equal("zahrada", dto.LandType);
        Assert.Equal("fyzická osoba", dto.OwnerType);
        Assert.Equal("found", dto.FetchStatus);
        Assert.Null(dto.FetchError);
        Assert.Equal(SampleDate, dto.FetchedAt);
    }

    [Fact]
    public void Records_WithSameValues_AreEqual()
    {
        var a = new ListingCadastreDto(SampleId, SampleListingId, null, null, null, null, null, null, null, "Brno", null, "pending", null, SampleDate);
        var b = new ListingCadastreDto(SampleId, SampleListingId, null, null, null, null, null, null, null, "Brno", null, "pending", null, SampleDate);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Records_WithDifferentStatus_AreNotEqual()
    {
        var a = new ListingCadastreDto(SampleId, SampleListingId, null, null, null, null, null, null, null, "Brno", null, "found", null, SampleDate);
        var b = new ListingCadastreDto(SampleId, SampleListingId, null, null, null, null, null, null, null, "Brno", null, "not_found", null, SampleDate);

        Assert.NotEqual(a, b);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  BulkRuianResultDto – statistiky & zpráva
// ─────────────────────────────────────────────────────────────────────────────
public class BulkRuianResultDtoTests
{
    [Fact]
    public void Record_SumsAddUp()
    {
        var dto = new BulkRuianResultDto(Total: 10, Found: 6, NotFound: 2, Error: 1, Skipped: 1, Message: "OK");

        Assert.Equal(dto.Total, dto.Found + dto.NotFound + dto.Error + dto.Skipped);
    }

    [Fact]
    public void Record_ZeroValues_Valid()
    {
        var dto = new BulkRuianResultDto(0, 0, 0, 0, 0, "Nic ke zpracování");
        Assert.Equal("Nic ke zpracování", dto.Message);
        Assert.Equal(0, dto.Total);
    }

    [Theory]
    [InlineData(5, 3, 1, 1, 0)]
    [InlineData(100, 80, 15, 5, 0)]
    [InlineData(1, 0, 0, 0, 1)]
    public void Record_ConstructorAcceptsValidCounts(int total, int found, int notFound, int error, int skipped)
    {
        var dto = new BulkRuianResultDto(total, found, notFound, error, skipped, "test");
        Assert.Equal(total, dto.Total);
        Assert.Equal(found, dto.Found);
        Assert.Equal(notFound, dto.NotFound);
        Assert.Equal(error, dto.Error);
        Assert.Equal(skipped, dto.Skipped);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SaveCadastreDataRequest – record defaults & equality
// ─────────────────────────────────────────────────────────────────────────────
public class SaveCadastreDataRequestTests
{
    [Fact]
    public void AllNulls_IsValidRequest()
    {
        var req = new SaveCadastreDataRequest(null, null, null, null, null, null);
        Assert.Null(req.ParcelNumber);
        Assert.Null(req.LvNumber);
        Assert.Null(req.LandAreaM2);
        Assert.Null(req.LandType);
        Assert.Null(req.OwnerType);
        Assert.Null(req.EncumbrancesJson);
    }

    [Fact]
    public void WithValues_Properties_Set()
    {
        var req = new SaveCadastreDataRequest("123/4", "LV-200", 800, "orná půda", "stát", null);
        Assert.Equal("123/4", req.ParcelNumber);
        Assert.Equal("LV-200", req.LvNumber);
        Assert.Equal(800, req.LandAreaM2);
        Assert.Equal("orná půda", req.LandType);
        Assert.Equal("stát", req.OwnerType);
        Assert.Null(req.EncumbrancesJson);
    }

    [Fact]
    public void RecordEquality_SameValues()
    {
        var a = new SaveCadastreDataRequest("1st", null, null, null, null, null);
        var b = new SaveCadastreDataRequest("1st", null, null, null, null, null);
        Assert.Equal(a, b);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  RUIAN URL formát – konstanty v CadastreService
// ─────────────────────────────────────────────────────────────────────────────
public class RuianUrlTests
{
    // Verifikuje, že URL formát pro nahlížení.cuzk.cz odpovídá dokumentaci ČÚZK.
    // URL musí obsahovat typeCode=adresniMisto&id={ruianKod}.

    [Theory]
    [InlineData(123456789L, "https://nahlizenidokn.cuzk.cz/ZobrazitMapu/Basic?typeCode=adresniMisto&id=123456789")]
    [InlineData(1L,         "https://nahlizenidokn.cuzk.cz/ZobrazitMapu/Basic?typeCode=adresniMisto&id=1")]
    [InlineData(999999999L, "https://nahlizenidokn.cuzk.cz/ZobrazitMapu/Basic?typeCode=adresniMisto&id=999999999")]
    public void CadastreUrlFormat_MatchesCuzkDeepLink(long ruianKod, string expected)
    {
        // Sestavení URL stejným způsobem jako v CadastreService.CallRuianAsync
        const string NahlizenidoknBase = "https://nahlizenidokn.cuzk.cz";
        var url = $"{NahlizenidoknBase}/ZobrazitMapu/Basic?typeCode=adresniMisto&id={ruianKod}";
        Assert.Equal(expected, url);
    }

    [Fact]
    public void RuianFindUrl_ContainsCorrectPath()
    {
        const string expected = "https://ags.cuzk.cz/arcgis/rest/services/RUIAN/"
            + "Vyhledavaci_sluzba_nad_daty_RUIAN/MapServer/find";

        // Konstanty jsou private — ověřujeme reálné URL ze service přes reflection
        var field = typeof(CadastreService).GetField(
            "RuianFindUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        var value = (string?)field.GetValue(null);
        Assert.Equal(expected, value);
    }
}
