using RealEstate.Api;
using RealEstate.Api.Endpoints;
using RealEstate.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddEndpointsApiExplorer()
    .AddSwaggerGen();

// Custom services
builder.Services.AddRealEstateDb(builder.Configuration);
builder.Services.AddRealEstateServices();
builder.Services.AddStorageService(builder.Configuration);

// Playwright scraping (alternative to Python scraper)
builder.Services.AddPlaywrightScraping();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

// Enable static files for local storage serving
app.UseStaticFiles();

app.MapScrapingPlaywrightEndpoints();
app.MapListingEndpoints();
app.MapSourceEndpoints();
app.MapAnalysisEndpoints();
app.MapScrapingEndpoints();

app.Run();
