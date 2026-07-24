namespace TravelApi.Application.Interfaces;

public interface ISearchService
{
    Task<SearchResultsResponse> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// FIX #39 (Tanda 3 del barrido de PROD, 2026-07-23): antes, un usuario sin <c>reservas.view_all</c> (o sin
/// permiso de cobranzas/clientes) recibia una lista VACIA en la seccion recortada, indistinguible de "no hay
/// resultados". Ahora cada seccion viaja con <see cref="Scope"/>: una señal ESTRUCTURADA (P-8) que dice si el
/// recorte por permisos aplico, para que el front (T-13, no deduce nada a mano) pueda mostrar "esto es lo tuyo,
/// hay mas si tenes el permiso" en vez de un buscador que parece vacio.
/// </summary>
public record SearchResultsResponse(
    string Query,
    List<CustomerSearchResult> Customers,
    List<ReservaSearchResult> Reservas,
    List<PaymentSearchResult> Payments,
    SearchScopeInfo Scope);

/// <summary>
/// Señal por seccion de si el resultado quedo RECORTADO por los permisos del usuario actual.
///  - CustomersHidden/PaymentsHidden: el usuario no tiene el permiso base de esa seccion (clientes.view /
///    cobranzas.view) — la lista vino vacia a proposito, no porque no haya coincidencias.
///  - ReservasScopedToOwn/PaymentsScopedToOwn: el usuario ve resultados, pero SOLO los propios (sin
///    reservas.view_all / cobranzas.view_all) — puede haber mas coincidencias que no ve.
/// </summary>
public record SearchScopeInfo(
    bool CustomersHidden,
    bool ReservasScopedToOwn,
    bool PaymentsHidden,
    bool PaymentsScopedToOwn);

public record CustomerSearchResult(Guid PublicId, string FullName, string? Email, string? Phone);
public record ReservaSearchResult(Guid PublicId, string NumeroReserva, string Name, string Status, string? PayerName);
public record PaymentSearchResult(Guid PublicId, decimal Amount, string Status, string Method, string? NumeroReserva);
