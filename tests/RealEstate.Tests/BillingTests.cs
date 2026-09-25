using System.Text.Json;
using RealEstate.Api.Contracts.Leads;
using RealEstate.Api.Services.Billing;
using RealEstate.Api.Services.Leads;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Stripe webhook – ověření podpisu a čisté parsery. Bez HTTP a bez DB:
//  podpis se v testu počítá stejným HMAC jako na straně Stripe.
// ─────────────────────────────────────────────────────────────────
public class BillingTests
{
    private const string Secret = "whsec_test_secret_123";
    private const string Payload = """{"id":"evt_1","type":"checkout.session.completed","data":{"object":{}}}""";
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static string Header(long timestamp, string? signature = null) =>
        $"t={timestamp},v1={signature ?? StripeBillingService.ComputeSignature(Payload, timestamp, Secret)}";

    [Fact]
    public void VerifySignature_ValidHeader_ReturnsTrue()
    {
        var ok = StripeBillingService.VerifySignature(Payload, Header(Now.ToUnixTimeSeconds()), Secret, Now, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void VerifySignature_MultipleV1_AcceptsAnyMatching()
    {
        var ts = Now.ToUnixTimeSeconds();
        var header = $"t={ts},v1={new string('0', 64)},v1={StripeBillingService.ComputeSignature(Payload, ts, Secret)}";

        Assert.True(StripeBillingService.VerifySignature(Payload, header, Secret, Now, out _));
    }

    [Fact]
    public void VerifySignature_WrongSecret_ReturnsFalse()
    {
        var header = Header(Now.ToUnixTimeSeconds(), StripeBillingService.ComputeSignature(Payload, Now.ToUnixTimeSeconds(), "other"));

        var ok = StripeBillingService.VerifySignature(Payload, header, Secret, Now, out var error);

        Assert.False(ok);
        Assert.Equal("Podpis nesouhlasí.", error);
    }

    [Fact]
    public void VerifySignature_TamperedPayload_ReturnsFalse()
    {
        var header = Header(Now.ToUnixTimeSeconds());

        Assert.False(StripeBillingService.VerifySignature(Payload + " ", header, Secret, Now, out _));
    }

    [Theory]
    [InlineData(6 * 60)]
    [InlineData(-6 * 60)]
    [InlineData(24 * 3600)]
    public void VerifySignature_TimestampOutsideTolerance_ReturnsFalse(int offsetSeconds)
    {
        var ts = Now.AddSeconds(-offsetSeconds).ToUnixTimeSeconds();

        var ok = StripeBillingService.VerifySignature(Payload, Header(ts), Secret, Now, out var error);

        Assert.False(ok);
        Assert.Contains("toleranci", error);
    }

    [Fact]
    public void VerifySignature_TimestampWithinTolerance_ReturnsTrue()
    {
        var ts = Now.AddMinutes(-4).ToUnixTimeSeconds();

        Assert.True(StripeBillingService.VerifySignature(Payload, Header(ts), Secret, Now, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=00")]
    [InlineData("t=1700000000")]
    public void VerifySignature_MalformedHeader_ReturnsFalse(string header)
    {
        var ok = StripeBillingService.VerifySignature(Payload, header, Secret, Now, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseSubscription_LegacyShape_ReadsPeriodEndFromRoot()
    {
        const string json = """
            {
              "id": "sub_123",
              "customer": "cus_abc",
              "status": "active",
              "current_period_end": 1790000000,
              "metadata": { "plan": "profi", "user_id": "6f1d2c3b-0000-0000-0000-000000000001" },
              "items": { "data": [ { "price": { "id": "price_profi_m" } } ] }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var info = StripeBillingService.ParseSubscription(doc.RootElement);

        Assert.Equal("sub_123", info.Id);
        Assert.Equal("cus_abc", info.CustomerId);
        Assert.Equal("active", info.Status);
        Assert.Equal("price_profi_m", info.PriceId);
        Assert.Equal("profi", info.MetadataPlan);
        Assert.Equal("6f1d2c3b-0000-0000-0000-000000000001", info.MetadataUserId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), info.CurrentPeriodEnd);
    }

    [Fact]
    public void ParseSubscription_NewApiShape_ReadsPeriodEndFromItemAndExpandedCustomer()
    {
        const string json = """
            {
              "id": "sub_456",
              "customer": { "id": "cus_expanded" },
              "status": "trialing",
              "items": { "data": [ { "price": "price_hledac_y", "current_period_end": 1800000000 } ] }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var info = StripeBillingService.ParseSubscription(doc.RootElement);

        Assert.Equal("cus_expanded", info.CustomerId);
        Assert.Equal("price_hledac_y", info.PriceId);
        Assert.Null(info.MetadataPlan);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), info.CurrentPeriodEnd);
    }

    [Fact]
    public void ParsePlanFromPrice_MapsKnownIdsAndIgnoresUnknown()
    {
        var map = new Dictionary<string, string> { ["price_h_m"] = "hledac", ["price_p_y"] = "profi" };

        Assert.Equal("hledac", StripeBillingService.ParsePlanFromPrice("price_h_m", map));
        Assert.Equal("profi", StripeBillingService.ParsePlanFromPrice("price_p_y", map));
        Assert.Null(StripeBillingService.ParsePlanFromPrice("price_unknown", map));
        Assert.Null(StripeBillingService.ParsePlanFromPrice(null, map));
    }

    [Theory]
    [InlineData("hledac", "month", "Stripe:PriceHledacMonthly")]
    [InlineData("hledac", "year", "Stripe:PriceHledacYearly")]
    [InlineData("profi", "month", "Stripe:PriceProfiMonthly")]
    [InlineData("profi", "year", "Stripe:PriceProfiYearly")]
    public void PriceConfigKey_MatchesDockerComposeKeys(string plan, string interval, string expected)
    {
        Assert.Equal(expected, StripeBillingService.PriceConfigKey(plan, interval));
    }
}

public class LeadValidationTests
{
    private static LeadCreateDto Valid(
        string name = "Jan Novák",
        string email = "jan@example.com",
        bool consent = true,
        string kind = "mortgage",
        int? years = 30) =>
        new(null, kind, name, email, "+420 777 123 456", "Zájem o hypotéku", 5_500_000m, 4_400_000m, years, "listing-detail", consent);

    [Fact]
    public void Validate_ValidLead_ReturnsNull()
    {
        Assert.Null(LeadService.Validate(Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingName_ReturnsError(string name)
    {
        Assert.Equal("Zadejte jméno.", LeadService.Validate(Valid(name: name)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("jan@")]
    [InlineData("@example.com")]
    public void Validate_InvalidEmail_ReturnsError(string email)
    {
        Assert.Equal("Zadejte platný e-mail.", LeadService.Validate(Valid(email: email)));
    }

    [Fact]
    public void Validate_MissingConsent_ReturnsError()
    {
        var error = LeadService.Validate(Valid(consent: false));

        Assert.NotNull(error);
        Assert.Contains("souhlasu", error);
    }

    [Fact]
    public void Validate_UnknownKind_ReturnsError()
    {
        Assert.Equal("Neznámý typ poptávky.", LeadService.Validate(Valid(kind: "spam")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public void Validate_LoanYearsOutOfRange_ReturnsError(int years)
    {
        Assert.Equal("Splatnost musí být 1–40 let.", LeadService.Validate(Valid(years: years)));
    }
}
