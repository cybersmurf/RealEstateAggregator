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

        // 1) Stavíme predikát s AND kombinací filtrů
        var predicate = BuildBasePredicate(filter);

        // 2) Přidáme fulltext (OR nad klíčovými slovy)
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var searchPredicate = BuildSearchPredicate(filter.SearchText);
            predicate = predicate.And(searchPredicate);
        }

        query = query.Where(predicate);

        // 3) Counting před stránkováním
        var totalCount = await query.CountAsync(cancellationToken);

        // 4) Sorting – řazení dle filtru, .ThenBy(Id) zajišťuje deterministické pořadí
        query = filter.SortBy switch
        {
            "price"    => filter.SortDescending
                            ? query.OrderByDescending(x => x.Price).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.Price).ThenBy(x => x.Id),
            "area"     => filter.SortDescending
                            ? query.OrderByDescending(x => x.AreaBuiltUp).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.AreaBuiltUp).ThenBy(x => x.Id),
            "land"     => filter.SortDescending
                            ? query.OrderByDescending(x => x.AreaLand).ThenBy(x => x.Id)
                            : query.OrderBy(x => x.AreaLand).ThenBy(x => x.Id),
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
        var skip = (filter.Page - 1) * filter.PageSize;
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
                : new ListingUserStateDto()
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

        // Offer Type
        if (!string.IsNullOrWhiteSpace(filter.OfferType))
        {
            if (Enum.TryParse<OfferType>(filter.OfferType, ignoreCase: true, out var offerType))
            {
                predicate = predicate.And(x => x.OfferType == offerType);
            }
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
}
