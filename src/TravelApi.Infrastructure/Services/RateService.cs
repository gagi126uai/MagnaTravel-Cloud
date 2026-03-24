using Microsoft.EntityFrameworkCore;
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

    public async Task<IEnumerable<object>> GetAllAsync(int? supplierId, string? serviceType, bool activeOnly, CancellationToken ct)
    {
        var query = _db.Rates.Include(r => r.Supplier).AsQueryable();
        
        if (supplierId.HasValue)
            query = query.Where(r => r.SupplierId == supplierId.Value);
        
        if (!string.IsNullOrEmpty(serviceType))
            query = query.Where(r => r.ServiceType == serviceType);
        
        if (activeOnly)
            query = query.Where(r => r.IsActive && (r.ValidTo == null || r.ValidTo >= DateTime.UtcNow));
        
        return await query
            .OrderBy(r => r.Supplier != null ? r.Supplier.Name : "")
            .ThenBy(r => r.ServiceType)
            .ThenBy(r => r.ProductName)
            .Select(r => new {
                r.Id, r.ServiceType, r.ProductName, r.Description, r.PriceUnit,
                r.NetCost, r.Tax, r.SalePrice, r.Commission, r.Currency,
                r.ValidFrom, r.ValidTo, r.IsActive, r.InternalNotes,
                // Campos dinámicos
                r.Airline, r.AirlineCode, r.Origin, r.Destination, r.CabinClass, r.BaggageIncluded,
                r.HotelName, r.City, r.StarRating, r.RoomType, r.RoomCategory, r.RoomFeatures, r.MealPlan, r.HotelPriceType, r.ChildrenPayPercent, r.ChildMaxAge,
                r.PickupLocation, r.DropoffLocation, r.VehicleType, r.MaxPassengers, r.IsRoundTrip,
                r.IncludesFlight, r.IncludesHotel, r.IncludesTransfer, r.IncludesExcursions, r.IncludesInsurance,
                r.DurationDays, r.Itinerary,
                SupplierPublicId = r.Supplier != null ? (Guid?)r.Supplier.PublicId : null,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null
            })
            .ToListAsync(ct);
    }

    public async Task<object?> GetByIdAsync(int id, CancellationToken ct)
    {
        return await _db.Rates.Include(r => r.Supplier)
            .Where(r => r.Id == id)
            .Select(r => new {
                r.Id, r.ServiceType, r.ProductName, r.Description, r.PriceUnit,
                r.NetCost, r.Tax, r.SalePrice, r.Commission, r.Currency,
                r.ValidFrom, r.ValidTo, r.IsActive, r.InternalNotes,
                r.Airline, r.AirlineCode, r.Origin, r.Destination, r.CabinClass, r.BaggageIncluded,
                r.HotelName, r.City, r.StarRating, r.RoomType, r.MealPlan,
                r.PickupLocation, r.DropoffLocation, r.VehicleType, r.MaxPassengers, r.IsRoundTrip,
                r.IncludesFlight, r.IncludesHotel, r.IncludesTransfer, r.IncludesExcursions, r.IncludesInsurance,
                r.DurationDays, r.Itinerary,
                SupplierPublicId = r.Supplier != null ? (Guid?)r.Supplier.PublicId : null,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IEnumerable<object>> SearchAsync(int? supplierId, string? serviceType, string? query, CancellationToken ct)
    {
        var q = _db.Rates.Include(r => r.Supplier)
            .Where(r => r.IsActive && (r.ValidTo == null || r.ValidTo >= DateTime.UtcNow));
        
        if (supplierId.HasValue)
            q = q.Where(r => r.SupplierId == supplierId.Value);
        
        if (!string.IsNullOrEmpty(serviceType))
            q = q.Where(r => r.ServiceType == serviceType);
        
        if (!string.IsNullOrEmpty(query))
            q = q.Where(r => r.ProductName.Contains(query) || 
                           (r.Description != null && r.Description.Contains(query)) ||
                           (r.HotelName != null && r.HotelName.Contains(query)) ||
                           (r.Airline != null && r.Airline.Contains(query)));
        
        return await q
            .Take(30)
            .Select(r => new {
                r.Id, r.ServiceType, r.ProductName, r.Description, r.PriceUnit,
                r.NetCost, r.Tax, r.SalePrice, r.Currency,
                SupplierPublicId = r.Supplier != null ? (Guid?)r.Supplier.PublicId : null,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null,
                // Datos resumidos por tipo
                r.Airline, r.Origin, r.Destination, r.CabinClass,
                r.HotelName, r.City, r.StarRating, r.RoomType, r.MealPlan,
                r.VehicleType, r.IsRoundTrip,
                r.DurationDays
            })
            .ToListAsync(ct);
    }

    public async Task<object> CreateAsync(RateDto req, CancellationToken ct)
    {
        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(req.SupplierId))
        {
            supplierId = await _db.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(req.SupplierId, ct);

            if (!supplierId.HasValue)
                throw new ArgumentException("Proveedor no encontrado.");
        }

        var rate = new Rate
        {
            SupplierId = supplierId,
            ServiceType = req.ServiceType,
            ProductName = req.ProductName,
            Description = req.Description,
            PriceUnit = req.PriceUnit ?? "servicio",
            NetCost = req.NetCost,
            Tax = req.Tax,
            SalePrice = req.SalePrice,
            Commission = req.SalePrice - req.NetCost - req.Tax,
            Currency = req.Currency ?? "USD",
            ValidFrom = req.ValidFrom,
            ValidTo = req.ValidTo,
            InternalNotes = req.InternalNotes,
            IsActive = true,
            // Campos dinámicos
            Airline = req.Airline,
            AirlineCode = req.AirlineCode,
            Origin = req.Origin,
            Destination = req.Destination,
            CabinClass = req.CabinClass,
            BaggageIncluded = req.BaggageIncluded,
            HotelName = req.HotelName,
            City = req.City,
            StarRating = req.StarRating,
            RoomType = req.RoomType,
            RoomCategory = req.RoomCategory,
            RoomFeatures = req.RoomFeatures,
            MealPlan = req.MealPlan,
            HotelPriceType = req.HotelPriceType ?? "base_doble",
            ChildrenPayPercent = req.ChildrenPayPercent,
            ChildMaxAge = req.ChildMaxAge,
            PickupLocation = req.PickupLocation,
            DropoffLocation = req.DropoffLocation,
            VehicleType = req.VehicleType,
            MaxPassengers = req.MaxPassengers,
            IsRoundTrip = req.IsRoundTrip,
            IncludesFlight = req.IncludesFlight,
            IncludesHotel = req.IncludesHotel,
            IncludesTransfer = req.IncludesTransfer,
            IncludesExcursions = req.IncludesExcursions,
            IncludesInsurance = req.IncludesInsurance,
            DurationDays = req.DurationDays,
            Itinerary = req.Itinerary
        };

        _db.Rates.Add(rate);
        await _db.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<object?> UpdateAsync(int id, RateDto req, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return null;

        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(req.SupplierId))
        {
            supplierId = await _db.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(req.SupplierId, ct);

            if (!supplierId.HasValue)
                throw new ArgumentException("Proveedor no encontrado.");
        }

        rate.SupplierId = supplierId;
        rate.ServiceType = req.ServiceType;
        rate.ProductName = req.ProductName;
        rate.Description = req.Description;
        rate.PriceUnit = req.PriceUnit ?? "servicio";
        rate.NetCost = req.NetCost;
        rate.Tax = req.Tax;
        rate.SalePrice = req.SalePrice;
        rate.Commission = req.SalePrice - req.NetCost - req.Tax;
        rate.Currency = req.Currency ?? "USD";
        rate.ValidFrom = req.ValidFrom;
        rate.ValidTo = req.ValidTo;
        rate.InternalNotes = req.InternalNotes;
        rate.IsActive = req.IsActive;
        rate.UpdatedAt = DateTime.UtcNow;
        // Campos dinámicos
        rate.Airline = req.Airline;
        rate.AirlineCode = req.AirlineCode;
        rate.Origin = req.Origin;
        rate.Destination = req.Destination;
        rate.CabinClass = req.CabinClass;
        rate.BaggageIncluded = req.BaggageIncluded;
        rate.HotelName = req.HotelName;
        rate.City = req.City;
        rate.StarRating = req.StarRating;
        rate.RoomType = req.RoomType;
        rate.RoomCategory = req.RoomCategory;
        rate.RoomFeatures = req.RoomFeatures;
        rate.MealPlan = req.MealPlan;
        rate.HotelPriceType = req.HotelPriceType ?? "base_doble";
        rate.ChildrenPayPercent = req.ChildrenPayPercent;
        rate.ChildMaxAge = req.ChildMaxAge;
        rate.PickupLocation = req.PickupLocation;
        rate.DropoffLocation = req.DropoffLocation;
        rate.VehicleType = req.VehicleType;
        rate.MaxPassengers = req.MaxPassengers;
        rate.IsRoundTrip = req.IsRoundTrip;
        rate.IncludesFlight = req.IncludesFlight;
        rate.IncludesHotel = req.IncludesHotel;
        rate.IncludesTransfer = req.IncludesTransfer;
        rate.IncludesExcursions = req.IncludesExcursions;
        rate.IncludesInsurance = req.IncludesInsurance;
        rate.DurationDays = req.DurationDays;
        rate.Itinerary = req.Itinerary;

        await _db.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return false;

        _db.Rates.Remove(rate);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
