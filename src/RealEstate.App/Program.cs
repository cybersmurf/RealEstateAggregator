using RealEstate.App.Components;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add MudBlazor services
builder.Services.AddMudServices();

// Sdílené UI services
builder.Services.AddSingleton<RealEstate.App.Services.SourceLogoProvider>();

// Add HttpClient for API communication
builder.Services.AddHttpClient("RealEstateApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5001");
    client.Timeout = TimeSpan.FromMinutes(10); // velký multipart upload fotek z prohlídky
    var scrapingApiKey = builder.Configuration["ScrapingApiKey"] ?? "dev-key-change-me";
    client.DefaultRequestHeaders.Add("X-Api-Key", scrapingApiKey);
});

// Veřejná URL API pro sestavení absoluntích URL fotek v prohlížeči
// V Dockeru: ApiPublicUrl=${PUBLIC_API_URL:-http://localhost:5001}
builder.Services.AddSingleton<PhotosBaseUrl>(_ =>
    new PhotosBaseUrl(builder.Configuration["ApiPublicUrl"] ?? "http://localhost:5001"));

// Register HttpClient as singleton for DI
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return factory.CreateClient("RealEstateApi");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.UseStaticFiles(); // Serves runtime-uploaded files from wwwroot (e.g. /uploads/)
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Veřejná base URL API pro sestavení absolutních URL fotek v prohlížeči.</summary>
public sealed record PhotosBaseUrl(string Value)
{
    /// <summary>Převede stored_url (relativní /uploads/... nebo absolutní http://...) na plnou URL.</summary>
    public string Resolve(string? storedUrl, string? fallbackOriginalUrl = null)
    {
        if (string.IsNullOrWhiteSpace(storedUrl))
            return fallbackOriginalUrl ?? string.Empty;
        // Relativní cesta → prefix veřejnou URL API
        if (storedUrl.StartsWith('/'))
            return Value.TrimEnd('/') + storedUrl;
        // Absolutní URL (legacy záznamy s localhost:5001) → přepíš base
        if (storedUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
            storedUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            var path = new Uri(storedUrl).PathAndQuery;
            return Value.TrimEnd('/') + path;
        }
        return storedUrl;
    }
}
