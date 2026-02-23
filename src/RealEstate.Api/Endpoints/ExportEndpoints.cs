using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/listings");

        group.MapPost("/{id:guid}/export-drive", ExportToDrive)
            .WithName("ExportListingToDrive")
            .WithTags("Export");

        return app;
    }

    private static async Task<IResult> ExportToDrive(
        Guid id,
        [FromServices] IGoogleDriveExportService exportService,
        CancellationToken ct)
    {
        try
        {
            var result = await exportService.ExportListingToDriveAsync(id, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Chyba při exportu na Google Drive",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
