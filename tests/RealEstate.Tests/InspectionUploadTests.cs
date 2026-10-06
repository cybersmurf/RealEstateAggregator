using RealEstate.Api.Endpoints;

namespace RealEstate.Tests;

// Nahrání fotek z prohlídky dřív smazalo všechny předchozí fotky inzerátu. Nové se přidávají
// a číslování musí pokračovat za tím, co už na disku a v databázi je.
public class InspectionUploadTests
{
    [Fact]
    public void FirstUpload_StartsAtZero()
        => Assert.Equal(0, ExportEndpoints.NextInspectionIndex(0, []));

    [Fact]
    public void NextUpload_ContinuesAfterExistingFiles()
        => Assert.Equal(72, ExportEndpoints.NextInspectionIndex(72,
            ["000_prohlidka_01_IMG_6484.jpeg", "071_prohlidka_72_IMG_6670.jpeg"]));

    [Theory]
    [InlineData(3, "126_prohlidka_127_IMG_7397.jpeg", 127)]   // na disku je víc, než kolik je záznamů
    [InlineData(254, "071_prohlidka_72_IMG_6670.jpeg", 254)]  // záznamů je víc než souborů
    [InlineData(2, "IMG_7151.jpg", 2)]                        // název bez pořadového čísla
    public void NextIndex_IsBeyondRecordsAndFiles(int records, string fileName, int expected)
        => Assert.Equal(expected, ExportEndpoints.NextInspectionIndex(records, [fileName]));
}

public class InspectionThumbnailTests
{
    [Theory]
    [InlineData("https://realestate.sudata.eu/uploads/listings/abc/inspection/012_IMG_7015.JPG",
                "https://realestate.sudata.eu/uploads/listings/abc/inspection/thumbs/012_IMG_7015.jpg")]
    [InlineData("http://localhost:5001/uploads/listings/abc/inspection/000_prohlidka_01_IMG_6651.jpeg",
                "http://localhost:5001/uploads/listings/abc/inspection/thumbs/000_prohlidka_01_IMG_6651.jpg")]
    public void ThumbnailUrl_LivesInThumbsFolderNextToOriginal(string photoUrl, string expected)
        => Assert.Equal(expected, ExportEndpoints.InspectionThumbnailUrl(photoUrl));
}
