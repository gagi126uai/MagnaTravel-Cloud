namespace TravelApi.Application.DTOs;

public class ReservaListSummaryDto
{
    // ADR-020 (2026-06-07): contadores por etapa del ciclo unico. QuotationCount/InManagementCount/
    // LostCount nacen aca; SoldCount (Vendida) murio junto con el estado. ReservedCount sigue siendo
    // el conteo de Confirmadas (la clave de tab cambio de "reserved" a "confirmed" en F3 frontend).
    public int QuotationCount { get; set; }
    public int BudgetCount { get; set; }
    public int InManagementCount { get; set; }
    public int ActiveCount { get; set; }
    public int ReservedCount { get; set; }
    public int OperativeCount { get; set; }
    // ADR-036 (2026-06-21, prepago puro): ToSettleCount se ELIMINO (el estado "A liquidar" murio). El
    // frontend que consumia este campo (chip/tab "A liquidar") debe quitarlo en el lote de UI.
    // FIX #37/#38 (Tanda 3, 2026-07-23): ClosedCount pasa a ser SOLO reservas Closed ("Finalizadas").
    // Antes tambien sumaba Cancelled y Archived, y no coincidia con lo que mostraba la pestaña.
    public int ClosedCount { get; set; }
    // Pestaña nueva "Anuladas": Cancelled + PendingOperatorRefund (EstadoReserva.VoidedStatuses).
    public int CancelledCount { get; set; }
    // Pestaña "Archivadas" (soft-delete legacy).
    public int ArchivedCount { get; set; }
    public int LostCount { get; set; }

    /// <summary>
    /// Tanda 1 rediseño listado (2026-08-04, P-3⭐/T-4): reemplaza a los escalares
    /// <c>TotalSaleActive</c>/<c>TotalCostActive</c>/<c>TotalPendingBalance</c>/<c>GrossProfit</c>
    /// (eliminados: mezclaban pesos y dólares en un solo número, la regla de plata mas importante
    /// del producto es que las monedas NUNCA se suman). "Vendido" de las reservas ACTIVAS (mismo
    /// alcance que antes usaba TotalSaleActive: excluye Cerradas/Anuladas/Perdidas/Archivadas),
    /// una linea por moneda. Una moneda en $0 no viaja (el front pinta "$ 0,00" gris cuando la
    /// lista viene vacia, no hace falta mandar un cero explicito).
    /// </summary>
    public List<ReservaSummaryAmountByCurrencyDto> VendidoPorMoneda { get; set; } = new();

    /// <summary>
    /// Tanda 1 (2026-08-04, fix N3 de review): saldo PENDIENTE de cobro de las reservas activas, por
    /// moneda. Mismo alcance de reservas que antes usaba el escalar TotalPendingBalance, pero el
    /// filtro de saldo positivo ahora se aplica POR FILA DE MONEDA (<c>money.Balance &gt; 0</c> sobre
    /// cada linea de <c>ReservaMoneyByCurrency</c>), no sobre un escalar unico de la reserva — una
    /// reserva puede deber en ARS y tener saldo a favor en USD al mismo tiempo (P-3⭐: cada moneda se
    /// evalua sola, nunca se compensan entre si).
    /// </summary>
    public List<ReservaSummaryAmountByCurrencyDto> PorCobrarPorMoneda { get; set; } = new();
}

/// <summary>
/// Tanda 1 (2026-08-04): una linea {moneda, monto} del resumen del listado de reservas. Mismo
/// patron minimo que <c>CashByCurrencyDto</c>/<c>CancelledPenaltyByCurrencyDto</c> — se reusa la
/// forma en vez de inventar una nueva por cada pantalla que necesita un total separado por moneda.
/// </summary>
public class ReservaSummaryAmountByCurrencyDto
{
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
}

public class ReservaListPageDto : PagedResponse<ReservaListDto>
{
    public ReservaListSummaryDto Summary { get; init; } = new();

    public static ReservaListPageDto Create(
        IReadOnlyList<ReservaListDto> items,
        int page,
        int pageSize,
        int totalCount,
        ReservaListSummaryDto summary)
    {
        var basePage = PagedResponse<ReservaListDto>.Create(items, page, pageSize, totalCount);

        return new ReservaListPageDto
        {
            Items = basePage.Items,
            Page = basePage.Page,
            PageSize = basePage.PageSize,
            TotalCount = basePage.TotalCount,
            TotalPages = basePage.TotalPages,
            HasPreviousPage = basePage.HasPreviousPage,
            HasNextPage = basePage.HasNextPage,
            Summary = summary
        };
    }
}
