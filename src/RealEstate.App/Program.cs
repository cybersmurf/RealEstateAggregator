using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Services;
using RealEstate.App.Components;
using RealEstate.App.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add MudBlazor services
builder.Services.AddMudServices();

// Sdílené UI services
builder.Services.AddSingleton<RealEstate.App.Services.SourceLogoProvider>();

// ─── Účty: cookie přihlášení, stav do komponent, politika Admin ───────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = false;   // platnost kopíruje bearer token z API
        options.Cookie.Name = "realestate.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireClaim(ApiAuthHandler.AdminClaim, "true"));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5001";
var scrapingApiKey = builder.Configuration["ScrapingApiKey"] ?? "dev-key-change-me";

// Pojmenovaný klient bez identity – používají ho form-post endpointy /account/* (login, registrace)
builder.Services.AddHttpClient("RealEstateApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(10); // velký multipart upload fotek z prohlídky
});

// Veřejná URL API pro sestavení absoluntích URL fotek v prohlížeči
// V Dockeru: ApiPublicUrl=${PUBLIC_API_URL:-http://localhost:5001}
builder.Services.AddSingleton<PhotosBaseUrl>(_ =>
    new PhotosBaseUrl(
        builder.Configuration["ApiPublicUrl"] ?? "http://localhost:5001",
        builder.Configuration.GetValue<bool?>("Photos:PreferOriginal") ?? true));

// HttpClient pro komponenty: per okruh, s ApiAuthHandler (Bearer přihlášeného uživatele).
// Sdílený SocketsHttpHandler drží connection pool – nový HttpClient na okruh nevyčerpá sockety.
var sharedHandler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};
builder.Services.AddScoped(sp =>
{
    var handler = new ApiAuthHandler(sp.GetRequiredService<AuthenticationStateProvider>(), scrapingApiKey)
    {
        InnerHandler = sharedHandler,
    };
    return new HttpClient(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(apiBaseUrl),
        Timeout = TimeSpan.FromMinutes(10),
    };
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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Stažené fotky inzerátů (/uploads/listings/…) jsou dílem zdrojů – slouží jen klasifikaci,
// veřejně je nešíříme. UI zobrazuje původní URL zdroje. Fotky z prohlídek (/uploads/…/my_photos) zůstávají.
var serveStoredListingPhotos = builder.Configuration.GetValue<bool?>("Photos:ServeStoredListingPhotos") ?? false;
if (!serveStoredListingPhotos)
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/uploads/listings", StringComparison.OrdinalIgnoreCase)
            && path.Value!.Contains("/photos/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    });
}

app.UseStaticFiles(); // Serves runtime-uploaded files from wwwroot (e.g. /uploads/)
app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapSitemap();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Veřejná base URL API pro sestavení URL fotek v prohlížeči.</summary>
/// <param name="PreferOriginal">
/// true = zobrazovat fotky přímo ze zdrojového portálu (odkaz), ne ze stažených kopií.
/// Stažené soubory slouží jen ke klasifikaci a po ní se mažou; veřejně je nešíříme.
/// </param>
public sealed record PhotosBaseUrl(string Value, bool PreferOriginal = true)
{
    /// <summary>
    /// Převede stored_url (relativní /uploads/... nebo absolutní http://...) na použitelnou URL.
    /// Při <see cref="PreferOriginal"/> vrací původní URL ze zdroje, kdykoli je k dispozici.
    /// Pokud je konfigurace ApiPublicUrl omylem localhost, použije se browser origin.
    /// </summary>
    public string Resolve(string? storedUrl, string? fallbackOriginalUrl = null, string? browserBaseUrl = null)
    {
        if (PreferOriginal && !string.IsNullOrWhiteSpace(fallbackOriginalUrl))
            return fallbackOriginalUrl;

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
