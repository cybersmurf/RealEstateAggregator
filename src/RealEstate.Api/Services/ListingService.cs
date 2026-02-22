using LinqKit;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Repositories;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing real estate listings with EF Core IQueryable filtering and PredicateBuilder.
/// </summary>
public class ListingService : IListingService
{
    private readonly IListingRepository _repository;

    public ListingService(IListingRepository repository)
    {
        _repository = repository;
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

        // 4) Sorting
        query = query
            .OrderByDescending(x => x.FirstSeenAt)
            .ThenBy(x => x.Price);

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

        var userState = entity.UserStates.FirstOrDefault();

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
                    StoredUrl = p.StoredUrl ?? string.Empty,
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
        // TODO: Implementovat přes UserListingStateRepository
        // Pro MVP jednoduše vrátíme null
        await Task.CompletedTask;
        return null;
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
            predicate = predicate.And(x => x.UserStates.Any(s => s.Status == filter.UserStatus));
        }

        // Jen aktivní inzeráty
        predicate = predicate.And(x => x.IsActive);

        return predicate;
    }

    /// <summary>
    /// Staví fulltext predikát s OR kombinací klíčových slov.
    /// </summary>
    private static ExpressionStarter<Listing> BuildSearchPredicate(string searchText)
    {
        var keywords = searchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLower())
            .ToArray();

        if (keywords.Length == 0)
        {
            // Žádná klíčová slova = povoleno vše
            return PredicateBuilder.New<Listing>(true);
        }

        // false = začínáme s "nic není povoleno" (identita OR)
        var predicate = PredicateBuilder.New<Listing>(false);

        foreach (var keyword in keywords)
        {
            var temp = keyword; // closure workaround
            predicate = predicate.Or(x =>
                x.Title.ToLower().Contains(temp) ||
                (x.Description != null && x.Description.ToLower().Contains(temp)) ||
                (x.LocationText != null && x.LocationText.ToLower().Contains(temp)));
        }

        return predicate;
    }

    private static ListingSummaryDto MapToSummaryDto(Listing entity)
    {
        var userState = entity.UserStates.FirstOrDefault();

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
            UserStatus = userState?.Status.ToString() ?? "New",
            HasNotes = !string.IsNullOrWhiteSpace(userState?.Notes),
            UserStatusLastUpdated = userState?.LastUpdated
        };
    }
}
