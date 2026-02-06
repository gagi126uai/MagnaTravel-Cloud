using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly AppDbContext _db;

    public RatesController(AppDbContext db) => _db = db;

    /// <summary>
    /// Listar tarifario con filtros opcionales
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? supplierId, 
        [FromQuery] string? serviceType,
        [FromQuery] bool activeOnly = false,
        CancellationToken ct = default)
    {
        var query = _db.Rates.Include(r => r.Supplier).AsQueryable();
        
        if (supplierId.HasValue)
            query = query.Where(r => r.SupplierId == supplierId.Value);
        
        if (!string.IsNullOrEmpty(serviceType))
            query = query.Where(r => r.ServiceType == serviceType);
        
        if (activeOnly)
            query = query.Where(r => r.IsActive && (r.ValidTo == null || r.ValidTo >= DateTime.UtcNow));
        
        var rates = await query
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
                SupplierId = r.SupplierId,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null
            })
            .ToListAsync(ct);
        
        return Ok(rates);
    }

    /// <summary>
    /// Obtener tarifa por ID con todos los campos
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.Include(r => r.Supplier)
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
                SupplierId = r.SupplierId,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null
            })
            .FirstOrDefaultAsync(ct);
        
        if (rate == null) return NotFound();
        return Ok(rate);
    }

    /// <summary>
    /// Buscar tarifa para autocompletar al crear servicio
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] int? supplierId,
        [FromQuery] string? serviceType,
        [FromQuery] string? query,
        CancellationToken ct)
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
        
        var rates = await q
            .Take(30)
            .Select(r => new {
                r.Id, r.ServiceType, r.ProductName, r.Description, r.PriceUnit,
                r.NetCost, r.Tax, r.SalePrice, r.Currency,
                SupplierId = r.SupplierId,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null,
                // Datos resumidos por tipo
                r.Airline, r.Origin, r.Destination, r.CabinClass,
                r.HotelName, r.City, r.StarRating, r.RoomType, r.MealPlan,
                r.VehicleType, r.IsRoundTrip,
                r.DurationDays
            })
            .ToListAsync(ct);
        
        return Ok(rates);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] RateDto req, CancellationToken ct)
    {
        var rate = new Rate
        {
            SupplierId = req.SupplierId,
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
        return Ok(rate);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] RateDto req, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return NotFound();

        rate.SupplierId = req.SupplierId;
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
        return Ok(rate);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return NotFound();

        _db.Rates.Remove(rate);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

/// <summary>
/// DTO para crear/actualizar tarifa con todos los campos profesionales
/// </summary>
public record RateDto(
    int? SupplierId,
    string ServiceType,
    string ProductName,
    string? Description,
    string? PriceUnit,
    decimal NetCost,
    decimal Tax,
    decimal SalePrice,
    string? Currency,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    string? InternalNotes,
    bool IsActive = true,
    // Aéreo
    string? Airline = null,
    string? AirlineCode = null,
    string? Origin = null,
    string? Destination = null,
    string? CabinClass = null,
    string? BaggageIncluded = null,
    // Hotel
    string? HotelName = null,
    string? City = null,
    int? StarRating = null,
    string? RoomType = null,
    string? RoomCategory = null,
    string? RoomFeatures = null,
    string? MealPlan = null,
    string? HotelPriceType = "base_doble", // por_persona, base_doble
    int ChildrenPayPercent = 0, // 0-100%
    int ChildMaxAge = 12,
    // Traslado
    string? PickupLocation = null,
    string? DropoffLocation = null,
    string? VehicleType = null,
    int? MaxPassengers = null,
    bool IsRoundTrip = false,
    // Paquete
    bool IncludesFlight = false,
    bool IncludesHotel = false,
    bool IncludesTransfer = false,
    bool IncludesExcursions = false,
    bool IncludesInsurance = false,
    int? DurationDays = null,
    string? Itinerary = null
);
