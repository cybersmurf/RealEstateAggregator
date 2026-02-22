using Microsoft.EntityFrameworkCore;
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


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<RealEstateDbContext>();
    await dbContext.Database.MigrateAsync();
    await DbInitializer.SeedAsync(dbContext);
}
else
{
    app.UseHttpsRedirection();
}

// Enable static files for local storage serving
app.UseStaticFiles();

app.MapListingEndpoints();
app.MapSourceEndpoints();
app.MapAnalysisEndpoints();
app.MapScrapingEndpoints();

app.Run();
