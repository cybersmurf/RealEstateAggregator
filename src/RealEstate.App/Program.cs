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

/// <summary>Veřejná base URL API pro sestavení URL fotek v prohlížeči.</summary>
public sealed record PhotosBaseUrl(string Value)
{
    /// <summary>
    /// Převede stored_url (relativní /uploads/... nebo absolutní http://...) na použitelnou URL.
    /// Pokud je konfigurace ApiPublicUrl omylem localhost, použije se browser origin.
    /// </summary>
    public string Resolve(string? storedUrl, string? fallbackOriginalUrl = null, string? browserBaseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(storedUrl))
            return fallbackOriginalUrl ?? string.Empty;

        var effectiveBase = GetEffectiveBaseUrl(browserBaseUrl);

        // Relativní cesta /uploads/... -> absolutní URL přes bezpečnou base
        if (storedUrl.StartsWith('/'))
            return effectiveBase is null ? storedUrl : effectiveBase.TrimEnd('/') + storedUrl;

        // Legacy absolutní localhost URL -> přepiš na bezpečnou base
        if (Uri.TryCreate(storedUrl, UriKind.Absolute, out var storedUri) && IsLocalHost(storedUri.Host))
        {
            return effectiveBase is null ? storedUrl : effectiveBase.TrimEnd('/') + storedUri.PathAndQuery;
        }

        return storedUrl;
    }

    private string? GetEffectiveBaseUrl(string? browserBaseUrl)
    {
        var configured = Value?.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri))
        {
            // Pokud konfigurace ukazuje na localhost, v produkčním browseru použij aktuální origin.
            if (IsLocalHost(configuredUri.Host) &&
                Uri.TryCreate(browserBaseUrl, UriKind.Absolute, out var browserUri) &&
                !IsLocalHost(browserUri.Host))
            {
                return browserUri.GetLeftPart(UriPartial.Authority);
            }

            return configuredUri.GetLeftPart(UriPartial.Authority);
        }

        if (Uri.TryCreate(browserBaseUrl, UriKind.Absolute, out var fallbackBrowserUri))
            return fallbackBrowserUri.GetLeftPart(UriPartial.Authority);

        return null;
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
