using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using RealEstate.Export.Services;
using RealEstate.Infrastructure;

// ============================================================================
// DI Setup
// ============================================================================

var services = new ServiceCollection();

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var connString = config.GetConnectionString("RealEstate")
    ?? "Host=localhost;Port=5432;Database=realestate_dev;Username=postgres;Password=dev";

services.AddDbContext<RealEstateDbContext>(options =>
{
    options.UseNpgsql(connString, npgsqlOptions =>
    {
        npgsqlOptions.UseVector();
    });
});

services.AddScoped<IExportService, MarkdownExporter>();

var serviceProvider = services.BuildServiceProvider();

// ============================================================================
// CLI Entry Point
// ============================================================================

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (cmdArgs.Length == 0)
{
    PrintUsage();
    return;
}

var command = cmdArgs[0];

try
{
    switch (command)
    {
        case "export-listing":
            await ExportListing(cmdArgs, serviceProvider);
            break;

        case "export-batch":
            await ExportBatch(cmdArgs, serviceProvider);
            break;

        case "help":
            PrintUsage();
            break;

        default:
            Console.WriteLine($"❌ Neznámý příkaz: {command}");
            PrintUsage();
            Environment.Exit(1);
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ Chyba: {ex.Message}");
    Environment.Exit(1);
}

// ============================================================================
// export-listing Command
// ============================================================================

async Task ExportListing(string[] cmdArgs, IServiceProvider sp)
{
    var id = ParseArgumentValue(cmdArgs, "--id", "-i");
    var format = ParseArgumentValue(cmdArgs, "--format", "-f") ?? "markdown";
    var output = ParseArgumentValue(cmdArgs, "--output", "-o") ?? "./exports";

    if (string.IsNullOrEmpty(id))
    {
        Console.Error.WriteLine("❌ Chybí parametr --id");
        Console.WriteLine("Příklad: dotnet run -- export-listing --id 12345678-1234-1234-1234-123456789012");
        Environment.Exit(1);
    }

    if (!Guid.TryParse(id, out var listingId))
    {
        Console.Error.WriteLine($"❌ Neplatné ID: {id}");
        Environment.Exit(1);
    }

    using var scope = sp.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
    var exporter = scope.ServiceProvider.GetRequiredService<IExportService>();

    var listing = await dbContext.Listings
        .Include(l => l.Photos)
        .Include(l => l.Source)
        .FirstOrDefaultAsync(l => l.Id == listingId);

    if (listing == null)
    {
        Console.Error.WriteLine($"❌ Inzerát se ID {listingId} nenalezen");
        Environment.Exit(1);
    }

    try
    {
        Directory.CreateDirectory(output);
        
        var exportFormat = Enum.Parse<ExportFormat>(format, ignoreCase: true);
        var fileName = $"{listing.Title?.Replace(" ", "_") ?? listingId.ToString()[..8]}.{GetExtension(exportFormat)}";
        var filePath = Path.Combine(output, fileName);

        var content = await exporter.ExportListingAsync(listing, exportFormat);

        await File.WriteAllTextAsync(filePath, content);
        Console.WriteLine($"✅ Exportováno: {filePath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"❌ Chyba při exportu: {ex.Message}");
        Environment.Exit(1);
    }
}

// ============================================================================
// export-batch Command
// ============================================================================

async Task ExportBatch(string[] cmdArgs, IServiceProvider sp)
{
    var region = ParseArgumentValue(cmdArgs, "--region", "-r");
    var priceMaxStr = ParseArgumentValue(cmdArgs, "--price-max", "-p");
    var isActiveStr = ParseArgumentValue(cmdArgs, "--active", "-a");
    var limitStr = ParseArgumentValue(cmdArgs, "--limit", "-l") ?? "10";
    var format = ParseArgumentValue(cmdArgs, "--format", "-f") ?? "markdown";
    var output = ParseArgumentValue(cmdArgs, "--output", "-o") ?? "./exports";

    decimal? priceMax = null;
    if (!string.IsNullOrEmpty(priceMaxStr) && decimal.TryParse(priceMaxStr, out var price))
    {
        priceMax = price;
    }

    bool? isActive = null;
    if (!string.IsNullOrEmpty(isActiveStr) && bool.TryParse(isActiveStr, out var active))
    {
        isActive = active;
    }

    if (!int.TryParse(limitStr, out var limit))
    {
        limit = 10;
    }

    using var scope = sp.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
    var exporter = scope.ServiceProvider.GetRequiredService<IExportService>();

    var query = dbContext.Listings
        .Include(l => l.Photos)
        .Include(l => l.Source)
        .AsQueryable();

    if (!string.IsNullOrEmpty(region))
    {
        query = query.Where(l => l.Region!.Contains(region));
    }

    if (priceMax.HasValue)
    {
        query = query.Where(l => l.Price <= priceMax);
    }

    if (isActive.HasValue)
    {
        query = query.Where(l => l.IsActive == isActive);
    }

    var listings = await query
        .OrderByDescending(l => l.FirstSeenAt)
        .Take(limit)
        .ToListAsync();

    if (listings.Count == 0)
    {
        Console.WriteLine("⚠️  Žádné inzeráty nesplňují kritéria");
        return;
    }

    try
    {
        Directory.CreateDirectory(output);

        var exportFormat = Enum.Parse<ExportFormat>(format, ignoreCase: true);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var fileName = $"export_batch_{timestamp}.{GetExtension(exportFormat)}";
        var filePath = Path.Combine(output, fileName);

        var content = await exporter.ExportListingsAsync(listings, exportFormat);

        await File.WriteAllTextAsync(filePath, content);
        Console.WriteLine($"✅ Exportováno {listings.Count} inzerátů: {filePath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"❌ Chyba při exportu: {ex.Message}");
        Environment.Exit(1);
    }
}

// ============================================================================
// Helpers
// ============================================================================

string? ParseArgumentValue(string[] args, params string[] aliases)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (aliases.Contains(args[i]) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }
    return null;
}

string GetExtension(ExportFormat format) => format switch
{
    ExportFormat.Markdown => "md",
    ExportFormat.Json => "json",
    ExportFormat.Html => "html",
    _ => "txt"
};

void PrintUsage()
{
    Console.WriteLine(@"
RealEstate Export CLI
====================

Příkazy:

  export-listing
    Exportuj jeden inzerát
    
    Parametry:
      --id, -i <GUID>              ID inzerátu (povinný)
      --format, -f <format>        Formát: markdown, json, html (výchozí: markdown)
      --output, -o <path>          Výstupní složka (výchozí: ./exports)
    
    Příklad:
      dotnet run -- export-listing --id 12345678-1234-1234-1234-123456789012
      dotnet run -- export-listing --id 12345678-1234-1234-1234-123456789012 --format json --output ./my_exports

  export-batch
    Exportuj více inzerátů s filtry
    
    Parametry:
      --region, -r <region>        Filtr dle regionu
      --price-max, -p <cena>       Maximální cena
      --active, -a <true|false>    Filtr dle aktivního stavu
      --limit, -l <počet>          Maximální počet inzerátů (výchozí: 10)
      --format, -f <format>        Formát: markdown, json, html (výchozí: markdown)
      --output, -o <path>          Výstupní složka (výchozí: ./exports)
    
    Příklady:
      dotnet run -- export-batch --limit 5
      dotnet run -- export-batch --region ""Jihomoravský kraj"" --price-max 5000000
      dotnet run -- export-batch --active true --format json

  help
    Zobraz tuto nápovědu
");
}

