using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// El bibliotecario del tarifario (spec firmada 2026-08-07, §6): ordena solo lo que puede, y lo que no,
/// lo deja agrupado en la bandeja "Repetidos" para que una persona decida.
///
/// <para>La version de hoy es DETERMINÍSTICA (sin IA). Cuando llegue la version con IA va a implementar
/// esta misma interfaz: la pantalla no se entera del cambio.</para>
/// </summary>
public interface ICatalogLibrarianService
{
    /// <summary>
    /// Pasada de ordenado: une los "casi seguros" (los que nuestro propio formulario partio en varios
    /// productos pegandole la habitacion al nombre). Deja rastro reversible de cada union. Idempotente:
    /// correrla dos veces no hace nada la segunda.
    /// </summary>
    Task<TidyUpRunResult> TidyUpAsync(CancellationToken ct);

    /// <summary>La bandeja "Repetidos": un producto arriba y abajo los que se le parecen.</summary>
    Task<DuplicateProductsResponse> GetDuplicateGroupsAsync(CancellationToken ct);

    /// <summary>"Es el mismo": el de arriba absorbe precios y habitaciones del otro. Nada se borra.</summary>
    Task<MergeProductsResult> MergeProductsAsync(MergeProductsRequest request, CancellationToken ct);

    /// <summary>"Es otro": ese par no vuelve a proponerse nunca mas.</summary>
    Task MarkAsNotDuplicatesAsync(NotDuplicatesRequest request, CancellationToken ct);

    /// <summary>Lo que ordeno el sistema, para "Ver qué ordenó" (con su Deshacer).</summary>
    Task<TidyUpLogResponse> GetTidyUpLogAsync(CancellationToken ct);

    /// <summary>Deshacer una union: el producto vuelve a existir con sus precios. Idempotente.</summary>
    Task UndoTidyUpActionAsync(Guid actionPublicId, CancellationToken ct);
}
