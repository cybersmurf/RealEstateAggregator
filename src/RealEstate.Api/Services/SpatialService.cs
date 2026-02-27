using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Spatial;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Prostorové operace s PostGIS:
/// – geokódování (Nominatim) 
/// – stavba koridoru (OSRM route + ST_Buffer v EPSG:5514)
/// – vyhledávání v oblasti (ST_Intersects)
/// – správa uložených oblastí
/// </summary>
public sealed class SpatialService(
    RealEstateDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<SpatialService> logger) : ISpatialService
{
    // -------------------------------------------------------------------------
    // Konfigurace externích API
    // -------------------------------------------------------------------------
    private const string NominatimBaseUrl = "https://nominatim.openstreetmap.org";
    private const string OsrmBaseUrl = "http://router.project-osrm.org";
    private const string UserAgent = "RealEstateAggregator/1.0 (spatial search, CZ)";

    // ═══════════════════════════════════════════════════════════════════════════
    // GEOCODING
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<(double Lat, double Lon)?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        // Nejdřív zkusit parsovat jako "lat,lon"
        var parts = address.Trim().Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            return (lat, lon);
        }

        try
        {
            var client = httpClientFactory.CreateClient("Nominatim");
            var url = $"{NominatimBaseUrl}/search?q={Uri.EscapeDataString(address)}&countrycodes=cz&format=json&limit=1&accept-language=cs";
            
            var results = await client.GetFromJsonAsync<NominatimResult[]>(url, ct);
            if (results is { Length: > 0 })
            {
                return (results[0].Lat, results[0].Lon);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Geocoding selhal pro '{Address}'", address);
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CORRIDOR BUILDER
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<CorridorResultDto> BuildCorridorAsync(CorridorRequestDto request, CancellationToken ct = default)
    {
        // 1. Geokóduj start a end
        var startCoords = await GeocodeAsync(request.Start, ct)
            ?? throw new InvalidOperationException($"Nelze geokódovat start: '{request.Start}'");
        var endCoords = await GeocodeAsync(request.End, ct)
            ?? throw new InvalidOperationException($"Nelze geokódovat end: '{request.End}'");

        // 2. Získej trasu přes OSRM nebo použij přímou linii
        string lineWkt;
        if (request.UseRoute)
        {
            lineWkt = await GetRouteLineStringAsync(startCoords, endCoords, ct)
                      ?? BuildStraightLineWkt(startCoords, endCoords);
        }
        else
        {
            lineWkt = BuildStraightLineWkt(startCoords, endCoords);
        }

        // 3. Postav WKT polygon koridoru pomocí PostGIS:
        //    – transformuj do EPSG:5514 (S-JTSK, metrický CRS pro ČR)
        //    – ST_Buffer(geom, buffer_meters)
        //    – transformuj zpět do WGS84 (EPSG:4326)
        var (polygonWkt, listingCount) = await BuildCorridorAndCountAsync(lineWkt, request.BufferMeters, ct);

        // 4. Volitelně ulož oblast
        Guid? savedAreaId = null;
        if (!string.IsNullOrEmpty(request.SaveAsName))
        {
            var area = await SaveAreaAsync(
                request.SaveAsName, polygonWkt, "corridor",
                request.Start, request.End, request.BufferMeters, ct);
            savedAreaId = area.Id;
        }

        return new CorridorResultDto(
            polygonWkt,
            startCoords.Lat, startCoords.Lon,
            endCoords.Lat, endCoords.Lon,
            request.BufferMeters,
            listingCount,
            savedAreaId);
    }

    public async Task<CorridorResultDto> BuildCorridorFromLineStringAsync(
        string lineStringWkt,
        int bufferMeters,
        double startLat, double startLon,
        double endLat, double endLon,
        string? saveName,
        CancellationToken ct = default)
    {
        var (polygonWkt, listingCount) = await BuildCorridorAndCountAsync(lineStringWkt, bufferMeters, ct);

        Guid? savedAreaId = null;
        if (!string.IsNullOrEmpty(saveName))
        {
            var area = await SaveAreaAsync(
                saveName, polygonWkt, "gpx-corridor",
                null, null, bufferMeters, ct);
            savedAreaId = area.Id;
        }

        return new CorridorResultDto(
            polygonWkt,
            startLat, startLon,
            endLat, endLon,
            bufferMeters,
            listingCount,
            savedAreaId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SPATIAL SEARCH
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<ListingMapPointDto>> SearchInAreaAsync(
        SpatialSearchRequestDto request, CancellationToken ct = default)
    {
        string? whereClause;
        object[] parameters;

        if (!string.IsNullOrEmpty(request.PolygonWkt))
        {
            // Hledej pomocí WKT polygonu – $1 = pozicový Npgsql parametr
            whereClause = """
                l.is_active = true
                AND l.location_point IS NOT NULL
                AND ST_Intersects(l.location_point, ST_GeomFromText($1, 4326))
                """;
            parameters = [request.PolygonWkt];
        }
        else if (request.BboxMinLat.HasValue && request.BboxMinLon.HasValue
              && request.BboxMaxLat.HasValue && request.BboxMaxLon.HasValue)
        {
            // Bounding box search – $1..$4 = pozicové Npgsql parametry
            whereClause = """
                l.is_active = true
                AND l.location_point IS NOT NULL
                AND l.location_point && ST_MakeEnvelope($1, $2, $3, $4, 4326)
                """;
            parameters = [request.BboxMinLon.Value, request.BboxMinLat.Value,
                          request.BboxMaxLon.Value, request.BboxMaxLat.Value];
        }
        else
        {
            throw new ArgumentException("Musíš zadat buď PolygonWkt nebo Bbox.");
        }

        return await ExecuteMapPointsQueryAsync(whereClause, parameters, request, ct);
    }

    public async Task<IReadOnlyList<ListingMapPointDto>> GetAllMapPointsAsync(
        string? propertyType = null,
        string? offerType = null,
        decimal? priceMax = null,
        CancellationToken ct = default)
    {
        var request = new SpatialSearchRequestDto(null, null, null, null, null,
            propertyType, offerType, null, priceMax, 1, 2000);

        var sql = """
            SELECT l.id, l.title, l.price, l.location_text,
                   l.latitude, l.longitude,
                   l.property_type, l.offer_type,
                   p.original_url AS main_photo_url,
                   l.source_code
            FROM re_realestate.listings l
            LEFT JOIN re_realestate.listing_photos p
                ON p.listing_id = l.id AND p.order_index = 0
            WHERE l.is_active = true
              AND l.location_point IS NOT NULL
            """;

        var extraWhere = BuildExtraFilters(request, out var extraParams, 0);
        if (!string.IsNullOrEmpty(extraWhere))
            sql += "\n AND " + extraWhere;

        sql += "\n            LIMIT 2000";

        return await RawQueryMapPoints(sql, extraParams, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SAVED AREAS
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<SpatialAreaDto> SaveAreaAsync(string name, string polygonWkt, string areaType,
        string? startCity, string? endCity, int? bufferMeters, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO re_realestate.spatial_areas
                (id, name, area_type, geom, start_city, end_city, buffer_m, created_at, updated_at)
            VALUES ({0}, {1}, {2}, ST_GeomFromText({3}, 4326), {4}, {5}, {6}, {7}, {7})
            """,
            [id, name, areaType, polygonWkt, startCity!, endCity!, bufferMeters!, now],
            ct);

        return new SpatialAreaDto(id, name, null, areaType, startCity, endCity, bufferMeters, true, now);
    }

    public async Task<IReadOnlyList<SpatialAreaDto>> GetSavedAreasAsync(CancellationToken ct = default)
    {
        var areas = await db.SpatialAreas
            .Where(a => a.IsActive)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new SpatialAreaDto(
                a.Id, a.Name, a.Description, a.AreaType,
                a.StartCity, a.EndCity, a.BufferMeters, a.IsActive, a.CreatedAt))
            .ToListAsync(ct);

        return areas;
    }

    public async Task<object> GetGeocodeStatsAsync(CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COUNT(*) AS total,
                    COUNT(*) FILTER (WHERE latitude IS NOT NULL) AS with_coords,
                    COUNT(*) FILTER (WHERE latitude IS NULL AND is_active = true) AS active_without_coords,
                    COUNT(*) FILTER (WHERE geocode_source = 'scraper') AS from_scraper,
                    COUNT(*) FILTER (WHERE geocode_source = 'nominatim') AS from_nominatim
                FROM re_realestate.listings
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new
                {
                    total = (int)(long)reader.GetValue(0),
                    withCoords = (int)(long)reader.GetValue(1),
                    activeWithoutCoords = (int)(long)reader.GetValue(2),
                    fromScraper = (int)(long)reader.GetValue(3),
                    fromNominatim = (int)(long)reader.GetValue(4),
                };
            }

            return new { total = 0, withCoords = 0, activeWithoutCoords = 0, fromScraper = 0, fromNominatim = 0 };
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BULK GEOCODING
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<BulkGeocodeResultDto> BulkGeocodeListingsAsync(int batchSize = 50, CancellationToken ct = default)
    {
        // 1. Načti dávku inzerátů bez GPS přes EF Core LINQ
        var listings = await db.Listings
            .Where(l => l.Latitude == null && l.IsActive
                     && l.LocationText != null && l.LocationText != "")
            .OrderByDescending(l => l.FirstSeenAt)
            .Take(batchSize)
            .Select(l => new { l.Id, l.LocationText })
            .ToListAsync(ct);

        if (listings.Count == 0)
        {
            var remainingZero = await db.Listings.CountAsync(l => l.Latitude == null && l.IsActive, ct);
            return new BulkGeocodeResultDto(0, 0, 0, remainingZero, 0);
        }

        var client = httpClientFactory.CreateClient("Nominatim");
        int succeeded = 0, failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var item in listings)
        {
            ct.ThrowIfCancellationRequested();

            var query = ExtractCityFromLocationText(item.LocationText!);
            if (string.IsNullOrWhiteSpace(query))
            {
                failed++;
                continue;
            }

            try
            {
                var url = $"{NominatimBaseUrl}/search"
                        + $"?q={Uri.EscapeDataString(query)}&countrycodes=cz&format=json&limit=1&accept-language=cs";

                var results = await client.GetFromJsonAsync<NominatimResult[]>(url, ct);

                if (results is { Length: > 0 })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE re_realestate.listings
                        SET latitude = {0}, longitude = {1},
                            geocode_source = 'nominatim', geocoded_at = now()
                        WHERE id = {2}
                        """,
                        [results[0].Lat, results[0].Lon, item.Id], ct);

                    succeeded++;
                    logger.LogDebug("Geocoded [{Query}] → {Lat},{Lon}", query, results[0].Lat, results[0].Lon);
                }
                else
                {
                    failed++;
                    logger.LogDebug("Geocoding nenalezl výsledek pro: '{Query}'", query);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "Geocoding selhal pro listing {Id} / '{Query}'", item.Id, query);
            }

            // Nominatim ToS: max 1 request/second
            await Task.Delay(1100, ct);
        }

        sw.Stop();
        var avgMs = listings.Count > 0 ? (int)(sw.ElapsedMilliseconds / listings.Count) : 0;
        var remaining = await db.Listings.CountAsync(l => l.Latitude == null && l.IsActive, ct);

        logger.LogInformation(
            "Bulk geocoding dokončen: batch={Batch}, succeeded={Succeeded}, failed={Failed}, remaining={Remaining}",
            listings.Count, succeeded, failed, remaining);

        return new BulkGeocodeResultDto(listings.Count, succeeded, failed, remaining, avgMs);
    }

    /// <summary>
    /// Extrahuje vhodný geocoding dotaz z location_text inzerátu.
    /// "Štítary" → "Štítary", "Pohořelice, Jihomoravský kraj" → "Pohořelice",
    /// "Praha 10-Vršovice" → "Praha 10-Vršovice"
    /// </summary>
    private static string ExtractCityFromLocationText(string locationText)
    {
        if (string.IsNullOrWhiteSpace(locationText)) return "";

        var parts = locationText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return locationText.Trim();

        // Heuristika: pokud první část vypadá jako "ulice 28" (číslo na konci), vezmi druhou část
        var first = parts[0].Trim();
        if (parts.Length > 1 && System.Text.RegularExpressions.Regex.IsMatch(first, @"\s+\d+\s*$"))
            return parts[1].Trim();

        return first;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Zavolá OSRM a vrátí WKT LINESTRING trasy, nebo null při selhání.</summary>
    private async Task<string?> GetRouteLineStringAsync(
        (double Lat, double Lon) start, (double Lat, double Lon) end, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("Osrm");
            // OSRM: lon,lat pořadí!
            var url = $"{OsrmBaseUrl}/route/v1/driving/"
                    + $"{start.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                    + $"{start.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)};"
                    + $"{end.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
                    + $"{end.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    + "?geometries=geojson&overview=full";

            var osrmResponse = await client.GetFromJsonAsync<OsrmResponse>(url, ct);
            var coords = osrmResponse?.Routes?.FirstOrDefault()?.Geometry?.Coordinates;

            if (coords is not { Count: > 1 })
            {
                logger.LogWarning("OSRM vrátil prázdnou trasu, použiju přímou linii");
                return null;
            }

            // Konverze GeoJSON coordinates ([lon, lat]) na WKT LINESTRING (lon lat, ...)
            var wktPoints = string.Join(", ", coords.Select(c =>
                $"{c[0].ToString(System.Globalization.CultureInfo.InvariantCulture)} "
              + $"{c[1].ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

            return $"LINESTRING({wktPoints})";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OSRM selhal, použiju přímou linii");
            return null;
        }
    }

    /// <summary>Přímočará linie jako WKT LINESTRING.</summary>
    private static string BuildStraightLineWkt((double Lat, double Lon) start, (double Lat, double Lon) end)
    {
        var s = System.Globalization.CultureInfo.InvariantCulture;
        return $"LINESTRING({start.Lon.ToString(s)} {start.Lat.ToString(s)}, "
             + $"{end.Lon.ToString(s)} {end.Lat.ToString(s)})";
    }

    /// <summary>
    /// PostGIS SQL: transformuj LINESTRING do EPSG:5514, udělej buffer, transformuj zpět.
    /// Vrátí WKT polygon + počet inzerátů uvnitř.
    /// </summary>
    private async Task<(string PolygonWkt, int Count)> BuildCorridorAndCountAsync(
        string lineWkt, int bufferMeters, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH corridor AS (
                    SELECT ST_Transform(
                        ST_Buffer(
                            ST_Transform(ST_GeomFromText($1, 4326), 5514),
                            $2
                        ),
                        4326
                    ) AS geom
                )
                SELECT
                    ST_AsText(c.geom) AS polygon_wkt,
                    COUNT(l.id) AS listing_count
                FROM corridor c
                LEFT JOIN re_realestate.listings l
                    ON l.is_active = true
                   AND l.location_point IS NOT NULL
                   AND ST_Intersects(l.location_point, c.geom)
                GROUP BY c.geom
                """;

            // Pozicové parametry $1, $2 – Npgsql mapuje dle pořadí, ParameterName nenastavujeme
            var p1 = cmd.CreateParameter(); p1.Value = lineWkt;
            var p2 = cmd.CreateParameter(); p2.Value = bufferMeters;
            cmd.Parameters.Add(p1);
            cmd.Parameters.Add(p2);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var wkt = reader.GetString(0);
                var count = (int)(long)reader.GetValue(1);
                return (wkt, count);
            }

            return ("POLYGON EMPTY", 0);
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    private async Task<IReadOnlyList<ListingMapPointDto>> ExecuteMapPointsQueryAsync(
        string whereClause, object[] whereParams, SpatialSearchRequestDto request, CancellationToken ct)
    {
        var extraWhere = BuildExtraFilters(request, out var extraParams, whereParams.Length);
        var allParams = whereParams.Concat(extraParams).ToArray();

        var sql = $"""
            SELECT l.id, l.title, l.price, l.location_text,
                   l.latitude, l.longitude,
                   l.property_type, l.offer_type,
                   p.original_url AS main_photo_url,
                   l.source_code
            FROM re_realestate.listings l
            LEFT JOIN re_realestate.listing_photos p
                ON p.listing_id = l.id AND p.order_index = 0
            WHERE {whereClause}
            {(string.IsNullOrEmpty(extraWhere) ? "" : "AND " + extraWhere)}
            LIMIT {request.PageSize}
            OFFSET {(request.Page - 1) * request.PageSize}
            """;

        return await RawQueryMapPoints(sql, allParams, ct);
    }

    private static string BuildExtraFilters(SpatialSearchRequestDto req, out object[] extraParams, int paramOffset)
    {
        var conditions = new List<string>();
        var parms = new List<object>();
        int idx = paramOffset + 1;

        if (!string.IsNullOrEmpty(req.PropertyType))
        {
            conditions.Add($"l.property_type = ${idx++}");
            parms.Add(req.PropertyType);
        }
        if (!string.IsNullOrEmpty(req.OfferType))
        {
            conditions.Add($"l.offer_type = ${idx++}");
            parms.Add(req.OfferType);
        }
        if (req.PriceMin.HasValue)
        {
            conditions.Add($"l.price >= ${idx++}");
            parms.Add(req.PriceMin.Value);
        }
        if (req.PriceMax.HasValue)
        {
            conditions.Add($"l.price <= ${idx++}");
            parms.Add(req.PriceMax.Value);
        }

        extraParams = [.. parms];
        return conditions.Count > 0 ? string.Join(" AND ", conditions) : "";
    }

    private async Task<IReadOnlyList<ListingMapPointDto>> RawQueryMapPoints(
        string sql, object[] sqlParams, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            for (int i = 0; i < sqlParams.Length; i++)
            {
                var p = cmd.CreateParameter();
                // Pozicové parametry $1, $2, ... – Npgsql mapuje dle pořadí přidání, NE dle ParameterName
                p.Value = sqlParams[i] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            var results = new List<ListingMapPointDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                results.Add(new ListingMapPointDto(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),  // main_photo_url (nullable – LEFT JOIN)
                    reader.GetString(9)                                 // source_code (NOT NULL)
                ));
            }

            return results;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DESERIALIZATION HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed record NominatimResult(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("display_name")] string DisplayName);

    private sealed record OsrmResponse(
        [property: JsonPropertyName("routes")] List<OsrmRoute>? Routes);

    private sealed record OsrmRoute(
        [property: JsonPropertyName("geometry")] OsrmGeometry? Geometry);

    private sealed record OsrmGeometry(
        [property: JsonPropertyName("coordinates")] List<List<double>>? Coordinates);
}
