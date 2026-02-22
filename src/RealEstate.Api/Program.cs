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

// Playwright scraping (alternative to Python scraper)
builder.Services.AddPlaywrightScraping();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapScrapingPlaywrightEndpoints();
app.MapListingEndpoints();
app.MapSourceEndpoints();
app.MapAnalysisEndpoints();
app.MapScrapingEndpoints();

app.Run();
