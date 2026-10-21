using RealEstate.Api.Contracts.Overview;
using RealEstate.Api.Services;
using RealEstate.Api.Services.Overview;

namespace RealEstate.Tests;

public class DedupeNotesTests
{
    [Fact]
    public void Opakovana_veta_se_necha_jen_jednou()
    {
        var line = "7. 10. 2026: srovnání stavu podle pořadí 6. 10. (v2) – sledovat / druhá vlna.";
        var result = ListingService.DedupeNotes($"{line}\n{line}\nKrátké\nKrátké");
        Assert.Equal($"{line}\nKrátké\nKrátké", result);
    }

    [Fact]
    public void Prazdna_poznamka_zustane()
    {
        Assert.Null(ListingService.DedupeNotes(null));
        Assert.Equal("", ListingService.DedupeNotes(""));
    }
}

public class ApplyStructuredTests
{
    [Fact]
    public void Strukturovane_hodnoceni_naplni_kartu()
    {
        var card = new HouseCardDto { Match = "fits", HouseType = "bungalov" };
        OverviewService.ApplyStructured(card, """
            {"shoda":"částečně","typ":"patrový, samostatně stojící","obyvatelne_podlazi":"jde udělat",
             "verdikt":"Dobrý dům, ale u silnice.","varovani":["u hlavní silnice","kotel 20+ let"],
             "plusy":["garáž"],"minusy":["malá zahrada"],"overit_na_miste":["provoz na silnici"],
             "varianta":"A","trenazer":"garáž","zahrada_m2":420,"topeni":"plyn"}
            """);
        Assert.Equal("partial", card.Match);
        Assert.Equal("patrový, samostatně stojící", card.HouseType);
        Assert.Equal("jde udělat", card.HabitableFloor);
        Assert.Equal("Dobrý dům, ale u silnice.", card.Verdict);
        Assert.Equal(["u hlavní silnice", "kotel 20+ let"], card.Warnings);
        Assert.Equal(["provoz na silnici"], card.VerifyOnSite);
        Assert.Equal("A", card.Variant);
        Assert.Equal(420, card.GardenM2);
        Assert.Equal("plyn", card.Heating);
    }

    [Fact]
    public void Neplatny_json_kartu_nezmeni()
    {
        var card = new HouseCardDto { Match = "fits" };
        OverviewService.ApplyStructured(card, "{not json");
        OverviewService.ApplyStructured(card, null);
        Assert.Equal("fits", card.Match);
        Assert.Null(card.Verdict);
    }
}
