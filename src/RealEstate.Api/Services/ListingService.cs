using System.Text;
using LinqKit;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Repositories;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing real estate listings with EF Core IQueryable filtering and PredicateBuilder.
/// </summary>
public class ListingService : IListingService
{
    private readonly IListingRepository _repository;
    private readonly RealEstateDbContext _dbContext;
    private static readonly Guid DefaultUserId = new("00000000-0000-0000-0000-000000000001");

    public ListingService(IListingRepository repository, RealEstateDbContext dbContext)
    {
        _repository = repository;
        _dbContext = dbContext;
    }

    public async Task<PagedResultDto<ListingSummaryDto>> SearchAsync(
        ListingFilterDto filter,
        CancellationToken cancellationToken)
    {
        var query = _repository.Query(); // IQueryable<Listing> s AsExpandable()

        // 1) Stavíme predikát s AND kombinací filtrů přes LinqKit PredicateBuilder
        var predicate = BuildBasePredicate(filter);

        // 2) Přidáme fulltext (OR nad klíčovými slovy)
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var searchPredicate = BuildSearchPredicate(filter.SearchText);
            predicate = predicate.And(searchPredicate);
        }

        // 3) Expandujeme LinqKit predikát
        query = query.Where(predicate);

        // 4) Counting před stránkováním
        var totalCount = await query.CountAsync(cancellationToken);

        // 4) Sorting – řazení dle filtru, .ThenBy(Id) zajišťuje deterministické pořadí
        //    Nullable sloupce: OrderBy(x == null) zajišťuje NULLS LAST (false=0 < true=1)
        query = filter.SortBy switch
        {
            "price"    => filter.SortDescending
                            ? query.OrderBy(x => x.Price == null).ThenByDescending(x => x.Price).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.Price == null).ThenBy(x => x.Price).ThenBy(x => x.Id),
            "area"     => filter.SortDescending
                            ? query.OrderBy(x => x.AreaBuiltUp == null).ThenByDescending(x => x.AreaBuiltUp).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.AreaBuiltUp == null).ThenBy(x => x.AreaBuiltUp).ThenBy(x => x.Id),
            "land"     => filter.SortDescending
                            ? query.OrderBy(x => x.AreaLand == null).ThenByDescending(x => x.AreaLand).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.AreaLand == null).ThenBy(x => x.AreaLand).ThenBy(x => x.Id),
            "date"     => filter.SortDescending
                            ? query.OrderByDescending(x => x.FirstSeenAt).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.FirstSeenAt).ThenBy(x => x.Id),
            "title"    => filter.SortDescending
                            ? query.OrderByDescending(x => x.Title).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.Title).ThenBy(x => x.Id),
            "location" => filter.SortDescending
                            ? query.OrderByDescending(x => x.LocationText).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.LocationText).ThenBy(x => x.Id),
            _          => query.OrderByDescending(x => x.FirstSeenAt)
                               .ThenBy(x => x.Price)
                               .ThenBy(x => x.Id),
        };

        // 5) Paging
        var skip = Math.Max(0, (filter.Page - 1) * filter.PageSize); // guard pro případ Page=0 z race condition
        var entities = await query
            .Skip(skip)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        // 6) Projekce do DTO
        var items = entities.Select(MapToSummaryDto).ToList();

        return new PagedResultDto<ListingSummaryDto>
        {
            Items = items,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<ListingDetailDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await _repository.GetByIdAsync(id, cancellationToken);
        if (entity is null)
            return null;

        var userState = entity.UserStates.FirstOrDefault(s => s.UserId == DefaultUserId);

        return new ListingDetailDto
        {
            Id = entity.Id,
            SourceName = entity.Source.Name,
            SourceCode = entity.Source.Code,
            SourceUrl = entity.Url,
            Title = entity.Title,
            Description = entity.Description ?? string.Empty,
            LocationText = entity.LocationText ?? string.Empty,
            Region = entity.Region,
            District = entity.District,
            Municipality = entity.Municipality,
            PropertyType = entity.PropertyType.ToString(),
            OfferType = entity.OfferType.ToString(),
            Price = entity.Price,
            PriceNote = entity.PriceNote,
            AreaBuiltUp = (double?)entity.AreaBuiltUp,
            AreaLand = (double?)entity.AreaLand,
            Rooms = entity.Rooms,
            Disposition = entity.Disposition,
            HasKitchen = entity.HasKitchen,
            ConstructionType = entity.ConstructionType?.ToString(),
            Condition = entity.Condition?.ToString(),
            FirstSeenAt = entity.FirstSeenAt,
            CreatedAtSource = entity.CreatedAtSource,
            UpdatedAtSource = entity.UpdatedAtSource,
            IsActive = entity.IsActive,
            LastSeenAt = entity.LastSeenAt,
            Photos = entity.Photos
                .OrderBy(p => p.Order)
                .Select(p => new ListingPhotoDto
                {
                    Id = p.Id,
                    OriginalUrl = p.OriginalUrl,
                    StoredUrl = p.StoredUrl,   // null when not yet stored locally
                    Order = p.Order
                })
                .ToList(),
            UserState = userState is not null
                ? new ListingUserStateDto
                {
                    Status = userState.Status.ToString(),
                    Notes = userState.Notes,
                    LastUpdated = userState.LastUpdated
                }
                : new ListingUserStateDto(),
            DriveFolderUrl = entity.DriveFolderId is not null
                ? $"https://drive.google.com/drive/folders/{entity.DriveFolderId}"
                : null,
            DriveInspectionFolderUrl = entity.DriveInspectionFolderId is not null
                ? $"https://drive.google.com/drive/folders/{entity.DriveInspectionFolderId}"
                : null,
            HasOneDriveExport = entity.OneDriveFolderId is not null
        };
    }

    public async Task<ListingUserStateDto?> UpdateUserStateAsync(
        Guid listingId,
        ListingUserStateUpdateDto request,
        CancellationToken cancellationToken)
    {
        var listing = await _repository.GetByIdAsync(listingId, cancellationToken);
        if (listing is null)
        {
            return null;
        }

        var normalizedStatus = NormalizeStatus(request.Status);
        var userState = await _dbContext.UserListingStates
            .FirstOrDefaultAsync(
                s => s.ListingId == listingId && s.UserId == DefaultUserId,
                cancellationToken);

        if (userState is null)
        {
            userState = new UserListingState
            {
                Id = Guid.NewGuid(),
                ListingId = listingId,
                UserId = DefaultUserId,
                Status = normalizedStatus,
                Notes = request.Notes,
                LastUpdated = DateTime.UtcNow
            };
            _dbContext.UserListingStates.Add(userState);
        }
        else
        {
            userState.Status = normalizedStatus;
            userState.Notes = request.Notes;
            userState.LastUpdated = DateTime.UtcNow;
            _dbContext.UserListingStates.Update(userState);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ListingUserStateDto
        {
            Status = userState.Status,
            Notes = userState.Notes,
            LastUpdated = userState.LastUpdated
        };
    }

    public async Task<ListingStatsDto> GetStatsAsync(CancellationToken cancellationToken)
    {
        var countsBySource = await _dbContext.Listings
            .Where(l => l.IsActive)
            .GroupBy(l => new { l.Source.Code, l.Source.Name })
            .Select(g => new SourceCountDto
            {
                SourceCode = g.Key.Code,
                SourceName = g.Key.Name,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        return new ListingStatsDto
        {
            TotalCount = countsBySource.Sum(x => x.Count),
            ActiveSourceCount = countsBySource.Count,
            CountsBySource = countsBySource
        };
    }

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "New";
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "New",
            "Liked",
            "Disliked",
            "ToVisit",
            "Visited"
        };

        return allowed.Contains(status) ? status : "New";
    }

    /// <summary>
    /// Staví základní predikát s AND kombinací filtrů pomocí PredicateBuilder.
    /// </summary>
    private static ExpressionStarter<Listing> BuildBasePredicate(ListingFilterDto filter)
    {
        // true = začínáme s "vše je povoleno" (identita AND)
        var predicate = PredicateBuilder.New<Listing>(true);

        // Source codes
        if (filter.SourceCodes is { Count: > 0 })
        {
            predicate = predicate.And(x => filter.SourceCodes.Contains(x.Source.Code));
        }

        // Location
        if (!string.IsNullOrWhiteSpace(filter.Region))
        {
            predicate = predicate.And(x => x.Region == filter.Region);
        }

        if (!string.IsNullOrWhiteSpace(filter.District))
        {
            predicate = predicate.And(x => x.District == filter.District);
        }

        if (!string.IsNullOrWhiteSpace(filter.Municipality))
        {
            predicate = predicate.And(x => x.Municipality == filter.Municipality);
        }

        // Price
        if (filter.PriceMin is not null)
        {
            predicate = predicate.And(x => x.Price >= filter.PriceMin);
        }

        if (filter.PriceMax is not null)
        {
            predicate = predicate.And(x => x.Price <= filter.PriceMax);
        }

        // Area Built Up
        if (filter.AreaBuiltUpMin is not null)
        {
            var min = (double)filter.AreaBuiltUpMin.Value;
            predicate = predicate.And(x => x.AreaBuiltUp >= min);
        }

        if (filter.AreaBuiltUpMax is not null)
        {
            var max = (double)filter.AreaBuiltUpMax.Value;
            predicate = predicate.And(x => x.AreaBuiltUp <= max);
        }

        // Area Land
        if (filter.AreaLandMin is not null)
        {
            var min = (double)filter.AreaLandMin.Value;
            predicate = predicate.And(x => x.AreaLand >= min);
        }

        if (filter.AreaLandMax is not null)
        {
            var max = (double)filter.AreaLandMax.Value;
            predicate = predicate.And(x => x.AreaLand <= max);
        }

        // Property Type
        if (!string.IsNullOrWhiteSpace(filter.PropertyType))
        {
            if (Enum.TryParse<PropertyType>(filter.PropertyType, ignoreCase: true, out var propertyType))
            {
                predicate = predicate.And(x => x.PropertyType == propertyType);
            }
        }

        // OfferType
        if (!string.IsNullOrWhiteSpace(filter.OfferType))
        {
            if (Enum.TryParse<OfferType>(filter.OfferType, ignoreCase: true, out var offerTypeEnum))
            {
                var capturedOfferType = offerTypeEnum; // capture in local variable for lambda
                predicate = predicate.And(x => x.OfferType == capturedOfferType);
            }
        }

        // Disposition (dispozice bytu: "1+1", "2+kk", atd.)
        if (!string.IsNullOrWhiteSpace(filter.Disposition))
        {
            var dispositionLower = filter.Disposition.ToLower();
            predicate = predicate.And(x => x.Disposition != null && x.Disposition.ToLower() == dispositionLower);
        }

        // Only New Since
        if (filter.OnlyNewSince is not null)
        {
            predicate = predicate.And(x => x.FirstSeenAt >= filter.OnlyNewSince);
        }

        // User Status - přes navigační vlastnost UserStates
        if (!string.IsNullOrWhiteSpace(filter.UserStatus))
        {
            predicate = predicate.And(x =>
                x.UserStates.Any(s => s.UserId == DefaultUserId && s.Status == filter.UserStatus));
        }

        // Jen aktivní inzeráty
        predicate = predicate.And(x => x.IsActive);

        return predicate;
    }

    /// <summary>
    /// Staví fulltext predikát pomocí precomputed tsvector sloupce s GIN indexem (search_tsv).
    /// Využívá PostgreSQL plainto_tsquery pro multi-keyword OR vyhledávání.
    /// </summary>
    private static ExpressionStarter<Listing> BuildSearchPredicate(string searchText)
    {
        var trimmed = searchText.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return PredicateBuilder.New<Listing>(true);
        }

        // plainto_tsquery("simple", "byt znojmo") generuje 'byt' & 'znojmo'
        // Wrapper přes shadow property search_tsv (GENERATED ALWAYS, má GIN index)
        var predicate = PredicateBuilder.New<Listing>(false);
        predicate = predicate.Or(x =>
            EF.Property<NpgsqlTsVector>(x, "SearchTsv")
                .Matches(EF.Functions.PlainToTsQuery("simple", trimmed)));

        return predicate;
    }

    private static ListingSummaryDto MapToSummaryDto(Listing entity)
    {
        var userState = entity.UserStates.FirstOrDefault(s => s.UserId == DefaultUserId);

        return new ListingSummaryDto
        {
            Id = entity.Id,
            SourceName = entity.Source.Name,
            SourceCode = entity.Source.Code,
            Title = entity.Title,
            LocationText = entity.LocationText,
            Region = entity.Region,
            District = entity.District,
            Municipality = entity.Municipality,
            PropertyType = entity.PropertyType.ToString(),
            OfferType = entity.OfferType.ToString(),
            Disposition = entity.Disposition,
            Price = entity.Price,
            PriceNote = entity.PriceNote,
            AreaBuiltUp = (double?)entity.AreaBuiltUp,
            AreaLand = (double?)entity.AreaLand,
            FirstSeenAt = entity.FirstSeenAt,
            UpdatedAtSource = entity.UpdatedAtSource,
            IsActive = entity.IsActive,
            ThumbnailUrl = entity.Photos
                .OrderBy(p => p.Order)
                .Select(p => p.StoredUrl ?? p.OriginalUrl)
                .FirstOrDefault(),
            UserStatus = userState?.Status.ToString() ?? "New",
            HasNotes = !string.IsNullOrWhiteSpace(userState?.Notes),
            UserStatusLastUpdated = userState?.LastUpdated
        };
    }

    // =========================================================================
    // CSV Export
    // =========================================================================

    public async Task<byte[]> ExportCsvAsync(ListingFilterDto filter, CancellationToken cancellationToken)
    {
        // Využijeme stávající SearchAsync (maxPageSize interně omezeno na 5000)
        filter.Page = 1;
        filter.PageSize = 5000;
        var result = await SearchAsync(filter, cancellationToken);

        var sb = new StringBuilder();

        // BOM pro Excel (UTF-8 s BOM = spрávné české znaky v Excelu bez import wizardu)
        sb.Append('\uFEFF');

        // Hlavička
        sb.AppendLine("ID;Zdroj;Název;Typ;Nabídka;Dispozice;Cena (Kč);Užitná plocha (m2);Pozemek (m2);Lokalita;Kraj;Okres;Obec;Datum nalezení;Můj stav");

        foreach (var l in result.Items)
        {
            sb.Append(CsvField(l.Id.ToString())).Append(';');
            sb.Append(CsvField(l.SourceName)).Append(';');
            sb.Append(CsvField(l.Title)).Append(';');
            sb.Append(CsvField(TranslatePropertyType(l.PropertyType))).Append(';');
            sb.Append(CsvField(TranslateOfferType(l.OfferType))).Append(';');
            sb.Append(CsvField(l.Disposition ?? "")).Append(';');
            sb.Append(l.Price.HasValue ? l.Price.Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "").Append(';');
            sb.Append(l.AreaBuiltUp.HasValue ? l.AreaBuiltUp.Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "").Append(';');
            sb.Append(l.AreaLand.HasValue ? l.AreaLand.Value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "").Append(';');
            sb.Append(CsvField(l.LocationText)).Append(';');
            sb.Append(CsvField(l.Region ?? "")).Append(';');
            sb.Append(CsvField(l.District ?? "")).Append(';');
            sb.Append(CsvField(l.Municipality ?? "")).Append(';');
            sb.Append(l.FirstSeenAt.ToString("yyyy-MM-dd")).Append(';');
            sb.AppendLine(CsvField(TranslateUserStatus(l.UserStatus)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Obal pole do uvozovek pokud obsahuje středník, nový řádek, nebo uvozovky
        if (value.Contains(';') || value.Contains('\n') || value.Contains('"'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private static string TranslatePropertyType(string? t) => t switch
    {
        "House"      => "Dům",
        "Apartment"  => "Byt",
        "Land"       => "Pozemek",
        "Cottage"    => "Chata/chalupa",
        "Commercial" => "Komercí",
        "Industrial" => "Průmyslový",
        "Garage"     => "Garáž",
        _            => t ?? "-"
    };

    private static string TranslateOfferType(string? t) => t switch
    {
        "Sale"    => "Prodej",
        "Rent"    => "Pronájem",
        "Auction" => "Dražba",
        _         => t ?? "-"
    };

    private static string TranslateUserStatus(string? s) => s switch
    {
        "Liked"    => "Zajímavé",
        "Disliked" => "Nezajímavé",
        "ToVisit"  => "K návštěvě",
        "Visited"  => "Navštíveno",
        _          => "Nové"
    };

    // =========================================================================
    // My Listings (user-tagged)
    // =========================================================================

    // Pořadí skupin v UI (nižší číslo = výše)
    private static readonly Dictionary<string, int> StatusOrder = new()
    {
        { "ToVisit",   0 },
        { "Liked",     1 },
        { "Visited",   2 },
        { "Disliked",  3 },
    };

    public async Task<MyListingsSummaryDto> GetMyListingsAsync(CancellationToken cancellationToken)
    {
        // Inzeráty kde uživatel explicitně nastavil stav (!=New)
        var taggedStatuses = new[] { "Liked", "Disliked", "ToVisit", "Visited" };

        var listings = await _repository.Query()
            .Where(l => l.UserStates.Any(s =>
                s.UserId == DefaultUserId && taggedStatuses.Contains(s.Status)))
            .OrderByDescending(l =>
                l.UserStates
                    .Where(s => s.UserId == DefaultUserId)
                    .Select(s => s.LastUpdated)
                    .FirstOrDefault())
            .ThenBy(l => l.Title)
            .ToListAsync(cancellationToken);

        var dtos = listings.Select(MapToSummaryDto).ToList();

        // Skupiny dle stavu
        var groups = dtos
            .GroupBy(d => d.UserStatus)
            .Where(g => taggedStatuses.Contains(g.Key))
            .OrderBy(g => StatusOrder.GetValueOrDefault(g.Key, 99))
            .Select(g => new UserListingsGroupDto
            {
                Status = g.Key,
                StatusLabel = TranslateUserStatus(g.Key),
                Count = g.Count(),
                Listings = g.ToList()
            })
            .ToList();

        var countsByStatus = groups.ToDictionary(g => g.Status, g => g.Count);

        return new MyListingsSummaryDto
        {
            CountsByStatus = countsByStatus,
            Groups = groups
        };
    }
}
