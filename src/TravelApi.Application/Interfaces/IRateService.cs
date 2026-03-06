

namespace TravelApi.Application.Interfaces;

public interface IRateService
{
    Task<IEnumerable<object>> GetAllAsync(int? supplierId, string? serviceType, bool activeOnly, CancellationToken ct);
    Task<object?> GetByIdAsync(int id, CancellationToken ct);
    Task<IEnumerable<object>> SearchAsync(int? supplierId, string? serviceType, string? query, CancellationToken ct);
    Task<object> CreateAsync(RateDto request, CancellationToken ct);
    Task<object?> UpdateAsync(int id, RateDto request, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}

// Moving RateDto from controllers namespace to application layer
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
