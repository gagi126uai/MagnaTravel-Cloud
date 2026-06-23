using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Busqueda de pasajeros HISTORICOS (de cualquier reserva) para la "ficha de pasajero reutilizable":
/// al cargar un pasajero, el usuario puede buscar por documento o nombre a alguien que ya viajo antes
/// y autocompletar sus datos. Sigue el patron de <see cref="ICustomerService.SearchSimilarAsync"/>
/// (score por documento/nombre), pero DEDUPLICA: como hay un Passenger por reserva, la misma persona
/// se colapsa a un solo resultado con sus datos mas recientes.
/// </summary>
public interface IPassengerSearchService
{
    /// <summary>
    /// Busca pasajeros por documento exacto y/o nombre parcial, devuelve UNA fila por persona
    /// (deduplicada por documento, o por nombre normalizado si no hay documento), ordenada por score.
    /// Sin criterios -> lista vacia. NO expone datos de las reservas (solo identidad de la persona).
    /// </summary>
    Task<IReadOnlyList<PassengerSimilarMatchDto>> SearchSimilarAsync(
        string? fullName,
        string? documentType,
        string? documentNumber,
        int take,
        CancellationToken cancellationToken);
}
