using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  SummaryValidator – AI shrnutí nahrazuje veřejně původní popis inzerátu.
//  Nesmí do něj proklouznout kontakt makléře (telefon, e-mail, web),
//  prázdný výstup ani model, který místo shrnutí přepsal celý popis.
// ─────────────────────────────────────────────────────────────────
public class SummaryValidatorTests
{
    private const string NormalSummary =
        "Rodinný dům 4+1 o užitné ploše 106 m² na pozemku 718 m² ve Znojmě, části Oblekovice. " +
        "Jde o přízemní nepodsklepenou stavbu se sedlovou střechou, stáří přibližně 60 let, " +
        "průběžně udržovanou a vhodnou k modernizaci. K domu patří hospodářská stavba a nevyužívaná studna. " +
        "Objekt je napojen na elektřinu, vodovod, kanalizaci a plyn, vytápění zajišťuje plynový kotel. " +
        "Cena 5 500 000 Kč vychází ze znaleckého posudku.";

    [Fact]
    public void NormalText_IsAccepted()
    {
        Assert.True(SummaryValidator.IsValid(NormalSummary, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("Cena 125 000 000 Kč za celý areál.")]
    [InlineData("Pozemek má výměru 1 250 000 m2 a je rovinatý.")]
    [InlineData("Dům byl postaven v roce 1985 a rekonstruován v roce 2015.")]
    [InlineData("Byt 2+kk, 54 m², 3. patro, cena 4 500 000 Kč.")]
    public void AmountsAndYears_AreNotMistakenForPhoneNumbers(string text)
    {
        Assert.True(SummaryValidator.IsValid(text, out var reason), reason);
    }

    [Theory]
    [InlineData("Dům 4+1 ve Znojmě. Více informací na tel. +420 777 123 456.")]
    [InlineData("Dům 4+1 ve Znojmě. Volejte 777123456.")]
    [InlineData("Dům 4+1 ve Znojmě. Kontakt: 777 123 456")]
    [InlineData("Dům 4+1 ve Znojmě. Kontakt 00420 777-123-456.")]
    public void PhoneNumber_IsRejected(string text)
    {
        Assert.False(SummaryValidator.IsValid(text, out var reason));
        Assert.Equal("contains phone number", reason);
    }

    [Theory]
    [InlineData("Dům 4+1 ve Znojmě. Detail na https://www.example.cz/inzerat/123")]
    [InlineData("Dům 4+1 ve Znojmě. Detail na http://example.com")]
    [InlineData("Dům 4+1 ve Znojmě. Více na www.realitka.cz")]
    [InlineData("Dům 4+1 ve Znojmě. Více na realitka.cz/nabidka")]
    public void Url_IsRejected(string text)
    {
        Assert.False(SummaryValidator.IsValid(text, out var reason));
        Assert.Equal("contains URL", reason);
    }

    [Fact]
    public void Email_IsRejected()
    {
        Assert.False(SummaryValidator.IsValid("Dům 4+1 ve Znojmě. Pište na makler@realitka.cz.", out var reason));
        Assert.Equal("contains e-mail", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_IsRejected(string? text)
    {
        Assert.False(SummaryValidator.IsValid(text, out var reason));
        Assert.Equal("empty", reason);
    }

    [Fact]
    public void TooLong_IsRejected()
    {
        var text = string.Concat(Enumerable.Repeat("Dům se zahradou. ", 60));
        Assert.True(text.Length > SummaryValidator.MaxLength);

        Assert.False(SummaryValidator.IsValid(text, out var reason));
        Assert.StartsWith("too long", reason);
    }

    [Fact]
    public void ExactlyMaxLength_IsAccepted()
    {
        var text = new string('a', SummaryValidator.MaxLength);

        Assert.True(SummaryValidator.IsValid(text));
    }
}
