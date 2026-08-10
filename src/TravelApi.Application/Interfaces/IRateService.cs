
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
    /// ADR-017 F1.2 (catalogo find-or-create, buscador): busca productos del catalogo (Rates) que se
    /// parezcan a <paramref name="query"/>. Es supplier-AGNOSTICO (el producto manda, no el operador)
    /// y deduplica las N tarifas legacy del mismo producto en un solo resultado. Cada item trae el
    /// contexto de la "ultima vez".
    ///
    /// <para><b>Como busca</b> (mejora 2026-08-10): parte lo escrito en palabras y busca cada una por
    /// separado en el nombre del producto, la ciudad, el nombre del hotel y los operadores con los que
    /// se vendio; tolera errores de tipeo. Si algun producto cubre TODAS las palabras, muestra solo
    /// esos; si ninguno, muestra los que cubren mas primero.</para>
    ///
    /// <para><b>El <paramref name="serviceType"/> ya NO filtra</b>: es el tipo PREFERIDO (la solapa
    /// donde esta parado el vendedor) y solo empuja esos productos arriba en la lista. La busqueda
    /// recorre los 5 tipos de la ficha (Hotel, Aereo, Traslado, Paquete, Asistencia); puede venir
    /// vacio. Antes, sin tipo devolvia lista vacia y con tipo escondia todo lo demas.</para>
    ///
    /// <para><b>Sin llave desde el 2026-08-06</b> (spec firmada de Tarifario, P8=A): el buscador esta
    /// disponible para todos los que pueden ver el tarifario. Devuelve siempre una lista (puede ser vacia).</para>
    /// </summary>
    Task<IReadOnlyList<CatalogSearchItemDto>> CatalogSearchAsync(string? serviceType, string? query, CancellationToken ct);

    /// <summary>
    /// "Tarifario que se arma solo" (spec firmada 2026-08-06, M-1/M-2): lista de PRODUCTOS aprendidos,
    /// con un renglon por operador que trae el ultimo precio conocido, su moneda, su unidad, cuando fue y
    /// (si vino de una venta) el numero de reserva que lo dejo. Incluye tambien las tarifas viejas
    /// cargadas a mano, como un producto mas (P2=A).
    /// </summary>
    Task<LearnedProductsResponse> GetLearnedProductsAsync(LearnedProductsQuery query, CancellationToken ct);

    /// <summary>
    /// La ficha de UN producto (spec 2026-08-07, §7): lo mismo que trae la lista pero con TODOS sus
    /// precios, sin el tope de 3 renglones. Null si ese producto no existe.
    /// </summary>
    Task<LearnedProductDto?> GetLearnedProductAsync(Guid ratePublicId, CancellationToken ct);

    /// <summary>
    /// Que precio sugerir al vender ESTA habitacion (M-15 / V9=A): el de la misma variante si existe, o el
    /// de la mas parecida marcado para NO precargarlo. Null si el producto no tiene precios aprendidos.
    /// </summary>
    Task<VariantPriceSuggestionDto?> GetVariantPriceSuggestionAsync(
        VariantPriceSuggestionQuery query, CancellationToken ct);

    /// <summary>
    /// Los nombres finos de habitacion (o vehiculos) que ya se usaron alguna vez, para ofrecerlos antes de
    /// que alguien invente una variacion nueva (M-19, texto libre CON memoria).
    /// </summary>
    Task<IReadOnlyList<string>> GetVariantNameSuggestionsAsync(
        string? serviceType, string? search, CancellationToken ct);

    /// <summary>
    /// Unifica una variacion de tipeo con el nombre que ya existe ("SUPERIOR" -> "Superior"). Si no se
    /// parece a nada conocido, devuelve lo escrito tal cual (M-19).
    /// </summary>
    Task<string?> ResolveVariantNameAsync(string? serviceType, string? writtenName, CancellationToken ct);

    /// <summary>
    /// Corrige como se llama una habitacion del producto; si queda igual que otra, las junta y deja el
    /// precio mas nuevo (M-18). NUNCA toca importes.
    /// </summary>
    Task<RenameVariantResult> RenameVariantAsync(RenameVariantRequest request, CancellationToken ct);

    /// <summary>
    /// Alta simple de producto desde el Tarifario (spec firmada 2026-08-06, M-3 + P7): pocos campos y
    /// freno de repetidos OBLIGATORIO en el servidor. Si encuentra productos muy parecidos y el pedido no
    /// trae la confirmacion explicita del usuario, NO crea nada y devuelve los parecidos para que elija.
    /// </summary>
    Task<SimpleProductCreationResult> CreateSimpleProductAsync(CreateSimpleProductRequest request, CancellationToken ct);

    /// <summary>
    /// Renombra un PRODUCTO del Tarifario (spec firmada 2026-08-06, §2.2): corrige el nombre (y la ciudad,
    /// en hotel) de TODAS las tarifas que forman ese producto, en una sola transaccion. Si el nombre nuevo
    /// ya lo tiene otro producto, NO fusiona: lanza
    /// <see cref="Domain.Exceptions.RateProductNameTakenException"/> para que el usuario decida.
    /// </summary>
    Task<RenameLearnedProductResult> RenameLearnedProductAsync(RenameLearnedProductRequest request, CancellationToken ct);
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
