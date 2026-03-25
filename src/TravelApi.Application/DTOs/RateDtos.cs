namespace TravelApi.Application.DTOs;

public class RateListItemDto
{
    public Guid PublicId { get; set; }
    public string ServiceType { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PriceUnit { get; set; }
    public decimal NetCost { get; set; }
    public decimal Tax { get; set; }
    public decimal SalePrice { get; set; }
    public decimal Commission { get; set; }
    public string? Currency { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; }
    public string? InternalNotes { get; set; }
    public string? Airline { get; set; }
    public string? AirlineCode { get; set; }
    public string? Origin { get; set; }
    public string? Destination { get; set; }
    public string? CabinClass { get; set; }
    public string? BaggageIncluded { get; set; }
    public string? HotelName { get; set; }
    public string? City { get; set; }
    public int? StarRating { get; set; }
    public string? RoomType { get; set; }
    public string? RoomCategory { get; set; }
    public string? RoomFeatures { get; set; }
    public string? MealPlan { get; set; }
    public string? HotelPriceType { get; set; }
    public int ChildrenPayPercent { get; set; }
    public int ChildMaxAge { get; set; }
    public string? PickupLocation { get; set; }
    public string? DropoffLocation { get; set; }
    public string? VehicleType { get; set; }
    public int? MaxPassengers { get; set; }
    public bool IsRoundTrip { get; set; }
    public bool IncludesFlight { get; set; }
    public bool IncludesHotel { get; set; }
    public bool IncludesTransfer { get; set; }
    public bool IncludesExcursions { get; set; }
    public bool IncludesInsurance { get; set; }
    public int? DurationDays { get; set; }
    public string? Itinerary { get; set; }
    public Guid? SupplierPublicId { get; set; }
    public string? SupplierName { get; set; }
}

public class HotelRateGroupDto
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
    public IReadOnlyList<RateListItemDto> Items { get; set; } = Array.Empty<RateListItemDto>();
}

public class RateSearchItemDto
{
    public Guid PublicId { get; set; }
    public string ServiceType { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PriceUnit { get; set; }
    public decimal NetCost { get; set; }
    public decimal Tax { get; set; }
    public decimal SalePrice { get; set; }
    public string? Currency { get; set; }
    public Guid? SupplierPublicId { get; set; }
    public string? SupplierName { get; set; }
    public DateTime? ValidTo { get; set; }
    public string? Airline { get; set; }
    public string? Origin { get; set; }
    public string? Destination { get; set; }
    public string? CabinClass { get; set; }
    public string? HotelName { get; set; }
    public string? City { get; set; }
    public int? StarRating { get; set; }
    public string? RoomType { get; set; }
    public string? RoomCategory { get; set; }
    public string? RoomFeatures { get; set; }
    public string? MealPlan { get; set; }
    public string? VehicleType { get; set; }
    public bool IsRoundTrip { get; set; }
    public int? DurationDays { get; set; }
}

public class RateSummaryDto
{
    public int TotalCount { get; set; }
    public int AereoCount { get; set; }
    public int TrasladoCount { get; set; }
    public int PaqueteCount { get; set; }
    public int HotelGroupCount { get; set; }
    public int HotelRateCount { get; set; }
    public int ExpiredCount { get; set; }
}
