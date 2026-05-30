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

public class RateGroupDto
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

// ===========================================================================
// Pieza C "tarifario que se llena solo" (2026-05-30): deteccion de duplicados.
//
// Antes de guardar una "tarifa aprendida" (un servicio cargado a mano que el
// usuario quiere reusar como tarifa), el sistema busca tarifas ya existentes
// parecidas y se las muestra para que el usuario decida. Estos DTOs son el
// contrato del endpoint POST /api/rates/duplicate-check.
// ===========================================================================

/// <summary>
/// Lo que el frontend manda para preguntar "¿ya existe una tarifa parecida?".
/// <see cref="SupplierId"/> acepta el PublicId (Guid en string) o el id legacy;
/// el resto de los campos identifican el servicio segun su tipo.
/// </summary>
public class RateDuplicateCheckRequest
{
    /// <summary>Tipo de servicio: "Hotel", "Aereo", "Traslado", "Paquete", "Asistencia", etc.</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>PublicId del proveedor (Guid en string) o id legacy. Puede venir vacio.</summary>
    public string? SupplierId { get; set; }

    /// <summary>
    /// Nombre principal a comparar: para Hotel es el nombre del hotel, para el
    /// resto es el nombre del producto. La busqueda difusa corre sobre este campo.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Componentes adicionales de la huella exacta segun el tipo de servicio.</summary>
    public RateDuplicateFingerprint Fingerprint { get; set; } = new();
}

/// <summary>
/// Campos que, combinados con SupplierId y nombre, forman la "huella exacta" de
/// una tarifa. No todos aplican a todos los tipos (ej. RoomType es de Hotel).
/// </summary>
public class RateDuplicateFingerprint
{
    // Hotel
    public string? RoomType { get; set; }
    public string? MealPlan { get; set; }
    public string? RoomCategory { get; set; }
    // Vuelo
    public string? Origin { get; set; }
    public string? Destination { get; set; }
    public string? Airline { get; set; }
    // Traslado
    public string? PickupLocation { get; set; }
    public string? DropoffLocation { get; set; }
    public string? VehicleType { get; set; }
    public bool IsRoundTrip { get; set; }
}

/// <summary>
/// Respuesta del chequeo de duplicados. <see cref="ExactMatch"/> es la tarifa
/// identica (misma huella) si existe, o null. <see cref="FuzzyMatches"/> son
/// tarifas con nombre PARECIDO (no identico) ordenadas de mas a menos similar.
/// </summary>
public class RateDuplicateCheckResponse
{
    public RateDuplicateExactDto? ExactMatch { get; set; }
    public IReadOnlyList<RateDuplicateFuzzyDto> FuzzyMatches { get; set; } = Array.Empty<RateDuplicateFuzzyDto>();
}

/// <summary>Tarifa identica encontrada (misma huella exacta).</summary>
public class RateDuplicateExactDto
{
    public Guid PublicId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? HotelName { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    public string? Currency { get; set; }
}

/// <summary>
/// Tarifa con nombre parecido. <see cref="Score"/> va de 0 a 1 (1 = identico),
/// calculado por similarity() de pg_trgm. En el fallback ILIKE (sin extension)
/// el score se reporta como 0 porque no hay medida real de similitud.
/// </summary>
public class RateDuplicateFuzzyDto
{
    public Guid PublicId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? HotelName { get; set; }
    public double Score { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    public string? Currency { get; set; }
}
