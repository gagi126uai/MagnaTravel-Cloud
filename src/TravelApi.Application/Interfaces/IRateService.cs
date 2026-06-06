
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IRateService
{
    Task<PagedResponse<RateListItemDto>> GetAllAsync(RateListQuery query, CancellationToken ct);
    Task<PagedResponse<RateGroupDto>> GetGroupsAsync(RateGroupsQuery query, CancellationToken ct);
    Task<PagedResponse<HotelRateGroupDto>> GetHotelGroupsAsync(HotelRateGroupsQuery query, CancellationToken ct);
    Task<RateSummaryDto> GetSummaryAsync(RateSummaryQuery query, CancellationToken ct);
    Task<RateListItemDto?> GetByIdAsync(int id, CancellationToken ct);
    Task<RateListItemDto?> GetByPublicIdAsync(string publicId, CancellationToken ct);
    Task<IReadOnlyList<RateSearchItemDto>> SearchAsync(int? supplierId, string? serviceType, string? query, CancellationToken ct);
    Task<RateListItemDto> CreateAsync(RateDto request, CancellationToken ct);
    Task<RateListItemDto?> UpdateAsync(int id, RateDto request, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
    Task<RateListItemDto?> DeactivateAsync(int id, CancellationToken ct);
    Task<RateListItemDto?> ReactivateAsync(int id, CancellationToken ct);

    /// <summary>
    /// Pieza C "tarifario que se llena solo": detecta tarifas existentes parecidas
    /// ANTES de crear una nueva, para evitar duplicados. Devuelve un match exacto
    /// (misma huella) si lo hay, mas hasta 5 candidatos con nombre similar.
    /// El <paramref name="request"/>.SupplierId se resuelve a id interno aca dentro.
    /// </summary>
    Task<RateDuplicateCheckResponse> FindDuplicateCandidatesAsync(RateDuplicateCheckRequest request, CancellationToken ct);

    /// <summary>
    /// ADR-017 F1.2 (catalogo find-or-create, buscador): busca productos del catalogo (Rates) del
    /// <paramref name="serviceType"/> pedido cuyo nombre se parece a <paramref name="query"/> (difuso,
    /// pg_trgm). Es supplier-AGNOSTICO (el producto manda, no el operador) y deduplica las N tarifas
    /// legacy del mismo producto en un solo resultado. Cada item trae el contexto de la "ultima vez".
    ///
    /// <para><b>Gateado por el flag <c>EnableCatalogFindOrCreate</c></b>: si esta OFF (o no hay forma de
    /// leerlo — fail-closed), devuelve <c>null</c> para que el controller responda 404, como si el
    /// endpoint no existiera (ADR §2.3 / R4). Si esta ON, devuelve la lista (puede ser vacia).</para>
    /// </summary>
    Task<IReadOnlyList<CatalogSearchItemDto>?> CatalogSearchAsync(string? serviceType, string? query, CancellationToken ct);
}

// Moving RateDto from controllers namespace to application layer
public record RateDto(
    string? SupplierId,
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
