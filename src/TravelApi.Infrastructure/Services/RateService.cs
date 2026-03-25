using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class RateService : IRateService
{
    private readonly AppDbContext _db;

    public RateService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResponse<RateListItemDto>> GetAllAsync(RateListQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var ratesQuery = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        ratesQuery = ApplyRateOrdering(ratesQuery, query);

        return await ProjectRateListItems(ratesQuery)
            .ToPagedResponseAsync(query, ct);
    }

    public async Task<PagedResponse<RateGroupDto>> GetGroupsAsync(RateGroupsQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var ratesQuery = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var groupedQuery = ratesQuery
            .Select(rate => new
            {
                rate.ServiceType,
                GroupName = rate.ServiceType == "Hotel"
                    ? (rate.HotelName ?? "Hotel sin nombre")
                    : rate.ProductName,
                Subtitle = rate.ServiceType == "Hotel"
                    ? rate.City
                    : null,
                StarRating = rate.ServiceType == "Hotel"
                    ? rate.StarRating
                    : null,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.SalePrice,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .GroupBy(rate => new
            {
                rate.ServiceType,
                rate.GroupName,
                rate.Subtitle,
                rate.StarRating,
                rate.SupplierPublicId,
                rate.SupplierName
            })
            .Select(group => new RateGroupSummary
            {
                ServiceType = group.Key.ServiceType,
                GroupName = group.Key.GroupName,
                Subtitle = group.Key.Subtitle,
                StarRating = group.Key.StarRating,
                SupplierPublicId = group.Key.SupplierPublicId,
                SupplierName = group.Key.SupplierName,
                FromPrice = group.Min(item => item.SalePrice),
                HasExpiredRates = group.Any(item => item.IsExpired),
                ItemCount = group.Count()
            });

        groupedQuery = ApplyRateGroupOrdering(groupedQuery, query);

        var totalCount = await groupedQuery.CountAsync(ct);
        var safePage = query.GetNormalizedPage();
        var safePageSize = query.GetNormalizedPageSize();

        var groups = await groupedQuery
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            group.GroupKey = BuildRateGroupKey(
                group.ServiceType,
                group.GroupName,
                group.Subtitle,
                group.SupplierName,
                group.StarRating);
        }

        if (groups.Count == 0)
        {
            return PagedResponse<RateGroupDto>.Create(Array.Empty<RateGroupDto>(), safePage, safePageSize, totalCount);
        }

        var selectedHotelNames = groups
            .Where(group => group.ServiceType == "Hotel")
            .Select(group => group.GroupName)
            .Distinct()
            .ToList();

        var selectedProductNames = groups
            .Where(group => group.ServiceType != "Hotel")
            .Select(group => group.GroupName)
            .Distinct()
            .ToList();

        var selectedGroupKeys = groups
            .Select(group => group.GroupKey)
            .ToHashSet(StringComparer.Ordinal);

        var groupItems = await ProjectRateListItems(ratesQuery)
            .Where(rate =>
                (rate.ServiceType == "Hotel" && selectedHotelNames.Contains(rate.HotelName ?? "Hotel sin nombre")) ||
                (rate.ServiceType != "Hotel" && selectedProductNames.Contains(rate.ProductName)))
            .ToListAsync(ct);

        var itemsByGroup = groupItems
            .GroupBy(BuildRateGroupKey)
            .Where(group => selectedGroupKeys.Contains(group.Key))
            .ToDictionary(
                group => group.Key,
                group => OrderRateGroupItems(group).ToList() as IReadOnlyList<RateListItemDto>,
                StringComparer.Ordinal);

        var pageItems = groups
            .Select(group => new RateGroupDto
            {
                GroupKey = group.GroupKey,
                ServiceType = group.ServiceType,
                GroupName = group.GroupName,
                Subtitle = group.Subtitle,
                StarRating = group.StarRating,
                SupplierPublicId = group.SupplierPublicId,
                SupplierName = group.SupplierName,
                FromPrice = group.FromPrice,
                HasExpiredRates = group.HasExpiredRates,
                ItemCount = group.ItemCount,
                Items = itemsByGroup.TryGetValue(group.GroupKey, out var items)
                    ? items
                    : Array.Empty<RateListItemDto>()
            })
            .ToList();

        return PagedResponse<RateGroupDto>.Create(pageItems, safePage, safePageSize, totalCount);
    }

    public async Task<PagedResponse<HotelRateGroupDto>> GetHotelGroupsAsync(HotelRateGroupsQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var hotelRatesQuery = BuildFilteredRatesQuery(supplierId, "Hotel", query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var groupedQuery = hotelRatesQuery
            .Select(rate => new
            {
                HotelName = rate.HotelName ?? "Hotel sin nombre",
                rate.City,
                rate.StarRating,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.SalePrice,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .GroupBy(rate => new
            {
                rate.HotelName,
                rate.City,
                rate.StarRating,
                rate.SupplierPublicId,
                rate.SupplierName
            })
            .Select(group => new HotelRateGroupSummary
            {
                HotelName = group.Key.HotelName,
                City = group.Key.City,
                StarRating = group.Key.StarRating,
                SupplierPublicId = group.Key.SupplierPublicId,
                SupplierName = group.Key.SupplierName,
                FromPrice = group.Min(item => item.SalePrice),
                HasExpiredRates = group.Any(item => item.IsExpired),
                RoomCount = group.Count()
            });

        groupedQuery = ApplyHotelGroupOrdering(groupedQuery, query);

        var totalCount = await groupedQuery.CountAsync(ct);
        var safePage = query.GetNormalizedPage();
        var safePageSize = query.GetNormalizedPageSize();

        var groups = await groupedQuery
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            group.GroupKey = BuildHotelGroupKey(group.HotelName, group.City, group.SupplierName, group.StarRating);
        }

        if (!groups.Any())
        {
            return PagedResponse<HotelRateGroupDto>.Create(Array.Empty<HotelRateGroupDto>(), safePage, safePageSize, totalCount);
        }

        var selectedHotelNames = groups.Select(group => group.HotelName).Distinct().ToList();
        var hotelItems = await ProjectRateListItems(hotelRatesQuery)
            .Where(rate => selectedHotelNames.Contains(rate.HotelName ?? "Hotel sin nombre"))
            .OrderBy(rate => rate.HotelName)
            .ThenBy(rate => rate.RoomType)
            .ThenBy(rate => rate.RoomCategory)
            .ThenBy(rate => rate.SalePrice)
            .ToListAsync(ct);

        var itemsByGroup = hotelItems
            .GroupBy(rate => BuildHotelGroupKey(rate.HotelName, rate.City, rate.SupplierName, rate.StarRating))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RateListItemDto>)group.ToList());

        var pageItems = groups
            .Select(group => new HotelRateGroupDto
            {
                GroupKey = group.GroupKey,
                HotelName = group.HotelName,
                City = group.City,
                StarRating = group.StarRating,
                SupplierPublicId = group.SupplierPublicId,
                SupplierName = group.SupplierName,
                FromPrice = group.FromPrice,
                HasExpiredRates = group.HasExpiredRates,
                RoomCount = group.RoomCount,
                Items = itemsByGroup.TryGetValue(group.GroupKey, out var groupItems)
                    ? groupItems
                    : Array.Empty<RateListItemDto>()
            })
            .ToList();

        return PagedResponse<HotelRateGroupDto>.Create(pageItems, safePage, safePageSize, totalCount);
    }

    public async Task<RateSummaryDto> GetSummaryAsync(RateSummaryQuery query, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(query.SupplierId, ct);
        var filteredRates = BuildFilteredRatesQuery(supplierId, query.ServiceType, query.ActiveOnly, query.Search);
        var now = DateTime.UtcNow;

        var rates = await filteredRates
            .Select(rate => new
            {
                rate.ServiceType,
                rate.HotelName,
                rate.City,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                rate.StarRating,
                IsExpired = rate.ValidTo.HasValue && rate.ValidTo.Value < now
            })
            .ToListAsync(ct);

        return new RateSummaryDto
        {
            TotalCount = rates.Count,
            AereoCount = rates.Count(rate => rate.ServiceType == "Aereo"),
            TrasladoCount = rates.Count(rate => rate.ServiceType == "Traslado"),
            PaqueteCount = rates.Count(rate => rate.ServiceType == "Paquete"),
            HotelGroupCount = rates
                .Where(rate => rate.ServiceType == "Hotel")
                .Select(rate => BuildHotelGroupKey(rate.HotelName, rate.City, rate.SupplierName, rate.StarRating))
                .Distinct()
                .Count(),
            HotelRateCount = rates.Count(rate => rate.ServiceType == "Hotel"),
            ExpiredCount = rates.Count(rate => rate.IsExpired)
        };
    }

    public async Task<RateListItemDto?> GetByIdAsync(int id, CancellationToken ct)
    {
        return await ProjectRateListItems(_db.Rates.AsNoTracking().Where(rate => rate.Id == id))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<RateListItemDto?> GetByPublicIdAsync(string publicId, CancellationToken ct)
    {
        if (!Guid.TryParse(publicId, out var parsedPublicId))
        {
            return null;
        }

        return await ProjectRateListItems(_db.Rates.AsNoTracking().Where(rate => rate.PublicId == parsedPublicId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<RateSearchItemDto>> SearchAsync(int? supplierId, string? serviceType, string? query, CancellationToken ct)
    {
        var ratesQuery = BuildFilteredRatesQuery(supplierId, serviceType, activeOnly: true, query);

        return await ratesQuery
            .OrderBy(rate => rate.ProductName)
            .ThenBy(rate => rate.HotelName)
            .Select(rate => new RateSearchItemDto
            {
                PublicId = rate.PublicId,
                ServiceType = rate.ServiceType,
                ProductName = rate.ProductName,
                Description = rate.Description,
                PriceUnit = rate.PriceUnit,
                NetCost = rate.NetCost,
                Tax = rate.Tax,
                SalePrice = rate.SalePrice,
                Currency = rate.Currency,
                SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
                SupplierName = rate.Supplier != null ? rate.Supplier.Name : null,
                ValidTo = rate.ValidTo,
                Airline = rate.Airline,
                Origin = rate.Origin,
                Destination = rate.Destination,
                CabinClass = rate.CabinClass,
                HotelName = rate.HotelName,
                City = rate.City,
                StarRating = rate.StarRating,
                RoomType = rate.RoomType,
                RoomCategory = rate.RoomCategory,
                RoomFeatures = rate.RoomFeatures,
                MealPlan = rate.MealPlan,
                VehicleType = rate.VehicleType,
                IsRoundTrip = rate.IsRoundTrip,
                DurationDays = rate.DurationDays
            })
            .Take(30)
            .ToListAsync(ct);
    }

    public async Task<RateListItemDto> CreateAsync(RateDto request, CancellationToken ct)
    {
        var supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);

        var rate = new Rate
        {
            SupplierId = supplierId,
            ServiceType = request.ServiceType,
            ProductName = request.ProductName,
            Description = request.Description,
            PriceUnit = request.PriceUnit ?? "servicio",
            NetCost = request.NetCost,
            Tax = request.Tax,
            SalePrice = request.SalePrice,
            Commission = request.SalePrice - request.NetCost - request.Tax,
            Currency = request.Currency ?? "USD",
            ValidFrom = request.ValidFrom,
            ValidTo = request.ValidTo,
            InternalNotes = request.InternalNotes,
            IsActive = request.IsActive,
            Airline = request.Airline,
            AirlineCode = request.AirlineCode,
            Origin = request.Origin,
            Destination = request.Destination,
            CabinClass = request.CabinClass,
            BaggageIncluded = request.BaggageIncluded,
            HotelName = request.HotelName,
            City = request.City,
            StarRating = request.StarRating,
            RoomType = request.RoomType,
            RoomCategory = request.RoomCategory,
            RoomFeatures = request.RoomFeatures,
            MealPlan = request.MealPlan,
            HotelPriceType = request.HotelPriceType ?? "base_doble",
            ChildrenPayPercent = request.ChildrenPayPercent,
            ChildMaxAge = request.ChildMaxAge,
            PickupLocation = request.PickupLocation,
            DropoffLocation = request.DropoffLocation,
            VehicleType = request.VehicleType,
            MaxPassengers = request.MaxPassengers,
            IsRoundTrip = request.IsRoundTrip,
            IncludesFlight = request.IncludesFlight,
            IncludesHotel = request.IncludesHotel,
            IncludesTransfer = request.IncludesTransfer,
            IncludesExcursions = request.IncludesExcursions,
            IncludesInsurance = request.IncludesInsurance,
            DurationDays = request.DurationDays,
            Itinerary = request.Itinerary
        };

        _db.Rates.Add(rate);
        await _db.SaveChangesAsync(ct);

        return await GetByIdAsync(rate.Id, ct)
            ?? throw new InvalidOperationException("No se pudo cargar la tarifa creada.");
    }

    public async Task<RateListItemDto?> UpdateAsync(int id, RateDto request, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return null;
        }

        var supplierId = await ResolveOptionalSupplierIdAsync(request.SupplierId, ct);

        rate.SupplierId = supplierId;
        rate.ServiceType = request.ServiceType;
        rate.ProductName = request.ProductName;
        rate.Description = request.Description;
        rate.PriceUnit = request.PriceUnit ?? "servicio";
        rate.NetCost = request.NetCost;
        rate.Tax = request.Tax;
        rate.SalePrice = request.SalePrice;
        rate.Commission = request.SalePrice - request.NetCost - request.Tax;
        rate.Currency = request.Currency ?? "USD";
        rate.ValidFrom = request.ValidFrom;
        rate.ValidTo = request.ValidTo;
        rate.InternalNotes = request.InternalNotes;
        rate.IsActive = request.IsActive;
        rate.UpdatedAt = DateTime.UtcNow;
        rate.Airline = request.Airline;
        rate.AirlineCode = request.AirlineCode;
        rate.Origin = request.Origin;
        rate.Destination = request.Destination;
        rate.CabinClass = request.CabinClass;
        rate.BaggageIncluded = request.BaggageIncluded;
        rate.HotelName = request.HotelName;
        rate.City = request.City;
        rate.StarRating = request.StarRating;
        rate.RoomType = request.RoomType;
        rate.RoomCategory = request.RoomCategory;
        rate.RoomFeatures = request.RoomFeatures;
        rate.MealPlan = request.MealPlan;
        rate.HotelPriceType = request.HotelPriceType ?? "base_doble";
        rate.ChildrenPayPercent = request.ChildrenPayPercent;
        rate.ChildMaxAge = request.ChildMaxAge;
        rate.PickupLocation = request.PickupLocation;
        rate.DropoffLocation = request.DropoffLocation;
        rate.VehicleType = request.VehicleType;
        rate.MaxPassengers = request.MaxPassengers;
        rate.IsRoundTrip = request.IsRoundTrip;
        rate.IncludesFlight = request.IncludesFlight;
        rate.IncludesHotel = request.IncludesHotel;
        rate.IncludesTransfer = request.IncludesTransfer;
        rate.IncludesExcursions = request.IncludesExcursions;
        rate.IncludesInsurance = request.IncludesInsurance;
        rate.DurationDays = request.DurationDays;
        rate.Itinerary = request.Itinerary;

        await _db.SaveChangesAsync(ct);

        return await GetByIdAsync(rate.Id, ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (rate == null)
        {
            return false;
        }

        _db.Rates.Remove(rate);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<int?> ResolveOptionalSupplierIdAsync(string? supplierPublicId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicId))
        {
            return null;
        }

        var supplierId = await _db.Suppliers
            .AsNoTracking()
            .ResolveInternalIdAsync(supplierPublicId, ct);

        if (!supplierId.HasValue)
        {
            throw new ArgumentException("Proveedor no encontrado.");
        }

        return supplierId.Value;
    }

    private IQueryable<Rate> BuildFilteredRatesQuery(
        int? supplierId,
        string? serviceType,
        bool activeOnly,
        string? search)
    {
        var query = _db.Rates
            .AsNoTracking()
            .AsQueryable();

        if (supplierId.HasValue)
        {
            query = query.Where(rate => rate.SupplierId == supplierId.Value);
        }

        if (!string.IsNullOrWhiteSpace(serviceType))
        {
            query = query.Where(rate => rate.ServiceType == serviceType);
        }

        if (activeOnly)
        {
            var now = DateTime.UtcNow;
            query = query.Where(rate => rate.IsActive && (!rate.ValidTo.HasValue || rate.ValidTo.Value >= now));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(rate =>
                rate.ProductName.ToLower().Contains(normalized) ||
                (rate.Description != null && rate.Description.ToLower().Contains(normalized)) ||
                (rate.HotelName != null && rate.HotelName.ToLower().Contains(normalized)) ||
                (rate.City != null && rate.City.ToLower().Contains(normalized)) ||
                (rate.Airline != null && rate.Airline.ToLower().Contains(normalized)) ||
                (rate.Origin != null && rate.Origin.ToLower().Contains(normalized)) ||
                (rate.Destination != null && rate.Destination.ToLower().Contains(normalized)) ||
                (rate.Supplier != null && rate.Supplier.Name.ToLower().Contains(normalized)));
        }

        return query;
    }

    private static IQueryable<Rate> ApplyRateOrdering(IQueryable<Rate> query, RateListQuery request)
    {
        var sortBy = (request.SortBy ?? "productName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "suppliername" => desc
                ? query.OrderByDescending(rate => rate.Supplier != null ? rate.Supplier.Name : string.Empty)
                    .ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.Supplier != null ? rate.Supplier.Name : string.Empty)
                    .ThenBy(rate => rate.ProductName),
            "validto" => desc
                ? query.OrderByDescending(rate => rate.ValidTo).ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.ValidTo).ThenBy(rate => rate.ProductName),
            "saleprice" => desc
                ? query.OrderByDescending(rate => rate.SalePrice).ThenByDescending(rate => rate.ProductName)
                : query.OrderBy(rate => rate.SalePrice).ThenBy(rate => rate.ProductName),
            _ => desc
                ? query.OrderByDescending(rate => rate.ProductName).ThenByDescending(rate => rate.ValidTo)
                : query.OrderBy(rate => rate.ProductName).ThenBy(rate => rate.ValidTo)
        };
    }

    private static IQueryable<HotelRateGroupSummary> ApplyHotelGroupOrdering(
        IQueryable<HotelRateGroupSummary> query,
        HotelRateGroupsQuery request)
    {
        var sortBy = (request.SortBy ?? "hotelName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "city" => desc
                ? query.OrderByDescending(group => group.City).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.City).ThenBy(group => group.HotelName),
            "validto" => desc
                ? query.OrderByDescending(group => group.HasExpiredRates).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.HasExpiredRates).ThenBy(group => group.HotelName),
            "fromprice" => desc
                ? query.OrderByDescending(group => group.FromPrice).ThenByDescending(group => group.HotelName)
                : query.OrderBy(group => group.FromPrice).ThenBy(group => group.HotelName),
            _ => desc
                ? query.OrderByDescending(group => group.HotelName).ThenByDescending(group => group.City)
                : query.OrderBy(group => group.HotelName).ThenBy(group => group.City)
        };
    }

    private static IQueryable<RateGroupSummary> ApplyRateGroupOrdering(
        IQueryable<RateGroupSummary> query,
        RateGroupsQuery request)
    {
        var sortBy = (request.SortBy ?? "groupName").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "servicetype" => desc
                ? query.OrderByDescending(group => group.ServiceType).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.ServiceType).ThenBy(group => group.GroupName),
            "suppliername" => desc
                ? query.OrderByDescending(group => group.SupplierName).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.SupplierName).ThenBy(group => group.GroupName),
            "fromprice" => desc
                ? query.OrderByDescending(group => group.FromPrice).ThenByDescending(group => group.GroupName)
                : query.OrderBy(group => group.FromPrice).ThenBy(group => group.GroupName),
            _ => desc
                ? query.OrderByDescending(group => group.GroupName).ThenByDescending(group => group.ServiceType)
                : query.OrderBy(group => group.GroupName).ThenBy(group => group.ServiceType)
        };
    }

    private static IQueryable<RateListItemDto> ProjectRateListItems(IQueryable<Rate> query)
    {
        return query.Select(rate => new RateListItemDto
        {
            PublicId = rate.PublicId,
            ServiceType = rate.ServiceType,
            ProductName = rate.ProductName,
            Description = rate.Description,
            PriceUnit = rate.PriceUnit,
            NetCost = rate.NetCost,
            Tax = rate.Tax,
            SalePrice = rate.SalePrice,
            Commission = rate.Commission,
            Currency = rate.Currency,
            ValidFrom = rate.ValidFrom,
            ValidTo = rate.ValidTo,
            IsActive = rate.IsActive,
            InternalNotes = rate.InternalNotes,
            Airline = rate.Airline,
            AirlineCode = rate.AirlineCode,
            Origin = rate.Origin,
            Destination = rate.Destination,
            CabinClass = rate.CabinClass,
            BaggageIncluded = rate.BaggageIncluded,
            HotelName = rate.HotelName,
            City = rate.City,
            StarRating = rate.StarRating,
            RoomType = rate.RoomType,
            RoomCategory = rate.RoomCategory,
            RoomFeatures = rate.RoomFeatures,
            MealPlan = rate.MealPlan,
            HotelPriceType = rate.HotelPriceType,
            ChildrenPayPercent = rate.ChildrenPayPercent,
            ChildMaxAge = rate.ChildMaxAge,
            PickupLocation = rate.PickupLocation,
            DropoffLocation = rate.DropoffLocation,
            VehicleType = rate.VehicleType,
            MaxPassengers = rate.MaxPassengers,
            IsRoundTrip = rate.IsRoundTrip,
            IncludesFlight = rate.IncludesFlight,
            IncludesHotel = rate.IncludesHotel,
            IncludesTransfer = rate.IncludesTransfer,
            IncludesExcursions = rate.IncludesExcursions,
            IncludesInsurance = rate.IncludesInsurance,
            DurationDays = rate.DurationDays,
            Itinerary = rate.Itinerary,
            SupplierPublicId = rate.Supplier != null ? (Guid?)rate.Supplier.PublicId : null,
            SupplierName = rate.Supplier != null ? rate.Supplier.Name : null
        });
    }

    private static string BuildHotelGroupKey(string? hotelName, string? city, string? supplierName, int? starRating)
    {
        return $"{hotelName ?? string.Empty}|{city ?? string.Empty}|{supplierName ?? string.Empty}|{starRating?.ToString() ?? string.Empty}";
    }

    private static string BuildRateGroupKey(RateListItemDto item)
    {
        var groupName = item.ServiceType == "Hotel"
            ? (item.HotelName ?? "Hotel sin nombre")
            : item.ProductName;
        var subtitle = item.ServiceType == "Hotel" ? item.City : null;
        var starRating = item.ServiceType == "Hotel" ? item.StarRating : null;

        return BuildRateGroupKey(item.ServiceType, groupName, subtitle, item.SupplierName, starRating);
    }

    private static string BuildRateGroupKey(
        string serviceType,
        string? groupName,
        string? subtitle,
        string? supplierName,
        int? starRating)
    {
        return string.Join(
            "|",
            serviceType ?? string.Empty,
            groupName ?? string.Empty,
            subtitle ?? string.Empty,
            supplierName ?? string.Empty,
            starRating?.ToString() ?? string.Empty);
    }

    private static IEnumerable<RateListItemDto> OrderRateGroupItems(IEnumerable<RateListItemDto> items)
    {
        return items
            .OrderBy(item => item.ServiceType == "Hotel" ? item.RoomType ?? string.Empty : item.ProductName)
            .ThenBy(item => item.RoomCategory ?? string.Empty)
            .ThenBy(item => item.Airline ?? string.Empty)
            .ThenBy(item => item.VehicleType ?? string.Empty)
            .ThenBy(item => item.DurationDays ?? 0)
            .ThenBy(item => item.SalePrice)
            .ThenBy(item => item.ValidTo);
    }

    private sealed class HotelRateGroupSummary
    {
        public string GroupKey { get; set; } = string.Empty;
        public string HotelName { get; set; } = string.Empty;
        public string? City { get; set; }
        public int? StarRating { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }
        public decimal FromPrice { get; set; }
        public bool HasExpiredRates { get; set; }
        public int RoomCount { get; set; }
    }

    private sealed class RateGroupSummary
    {
        public string GroupKey { get; set; } = string.Empty;
        public string ServiceType { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public int? StarRating { get; set; }
        public Guid? SupplierPublicId { get; set; }
        public string? SupplierName { get; set; }
        public decimal FromPrice { get; set; }
        public bool HasExpiredRates { get; set; }
        public int ItemCount { get; set; }
    }
}
