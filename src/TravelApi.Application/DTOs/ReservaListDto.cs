namespace TravelApi.Application.DTOs;

public class ReservaListDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Budget";
    public string? CustomerName { get; set; }
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public decimal TotalCost { get; set; }
    public decimal TotalPaid { get; set; }
    public int PassengerCount { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public bool IsEconomicallySettled { get; set; }
    public bool CanMoveToOperativo { get; set; }
    public bool CanEmitVoucher { get; set; }
    public bool CanEmitAfipInvoice { get; set; }
    public string? EconomicBlockReason { get; set; }
    public bool IsInProgress { get; set; }
    /// <summary>True si el cliente no debe nada (Balance == 0). Chip verde "Pagada".</summary>
    public bool IsFullyPaid { get; set; }
    /// <summary>True si el viaje termino y todavia hay deuda (EndDate &lt; hoy AND Balance &gt; 0). Chip rojo "Vencida con deuda".</summary>
    public bool HasOverdueDebt { get; set; }

    /// <summary>
    /// Contexto de PLATA REAL en una reserva anulada. Null salvo en estados de cancelacion (Cancelled /
    /// PendingOperatorRefund). Mismo criterio y tokens que el detalle (ver <c>ReservaDto.CancelledMoneyContext</c>
    /// y <c>ReservationDebtRules</c>): "SaldoAFavorPendiente" | "MultaPorCobrar" | "MultaEnRevision" |
    /// "Inconsistente" | null. Se llena en una query batcheada por pagina (sin N+1), solo para las filas anuladas.
    /// </summary>
    public string? CancelledMoneyContext { get; set; }

    /// <summary>
    /// Monto de la multa por anulacion PENDIENTE de cobro (neto: multa bruta menos lo ya pagado en su moneda), para
    /// mostrar junto al cartel "Multa pendiente de cobro" en la fila. Solo con valor cuando
    /// <see cref="CancelledMoneyContext"/> es "MultaPorCobrar"; null en el resto. Mismo criterio que el detalle
    /// (<c>ReservaDto.CancelledPenaltyAmount</c>).
    /// </summary>
    public decimal? CancelledPenaltyAmount { get; set; }

    /// <summary>
    /// Moneda ISO 4217 ("ARS"/"USD") de <see cref="CancelledPenaltyAmount"/>. Solo con valor cuando el contexto
    /// es "MultaPorCobrar"; null en el resto.
    /// </summary>
    public string? CancelledPenaltyCurrency { get; set; }

    /// <summary>
    /// ADR-044 T5 Addendum, Revision 2, fix B2 (2026-07-11): mismo desglose por moneda que
    /// <c>ReservaDto.CancelledPenaltiesByCurrency</c>, para la fila del listado. Con 1 sola multa viva (el
    /// 100% de los casos hasta hoy) tiene un unico elemento, igual a los escalares de arriba.
    /// </summary>
    public List<CancelledPenaltyByCurrencyDto> CancelledPenaltiesByCurrency { get; set; } = new();

    /// <summary>
    /// ADR-021 Capa 5: detalle de plata por moneda para la fila del listado. Se llena leyendo la tabla
    /// hija materializada (no recalcula on-read fila por fila). Una sola linea = reserva mono-moneda.
    /// El <c>TotalCost</c> de cada linea se enmascara igual que el escalar para usuarios sin ver-costos.
    /// </summary>
    public List<ReservaMoneyLineDto> PorMoneda { get; set; } = new();

    /// <summary>ADR-021: true si la reserva mueve mas de una moneda.</summary>
    public bool EsMultimoneda { get; set; }

    /// <summary>
    /// ADR-033 (E7/A5, 2026-06-16): estado de cobro derivado del saldo POR MONEDA (ver
    /// <see cref="ReservaCollectionStatus"/>). Mismo criterio que el detalle. Se llena junto con PorMoneda.
    /// H1 (2026-06-24): default "SinMovimientos" (no "Saldado") para no mostrar "pagada" sin datos de plata.
    /// </summary>
    public string CollectionStatus { get; set; } = ReservaCollectionStatus.NoCharges;

    /// <summary>
    /// ADR-037 (2026-06-21): estado de FACTURACION derivado del cuadre VENDIDO vs FACTURADO NETO (ver
    /// <see cref="ReservaInvoicingStatus"/>). Mismo carril/criterio que el detalle, para que la fila del
    /// listado muestre el mismo chip que la ficha. Se llena en una query batcheada por pagina (sin N+1).
    /// </summary>
    public string InvoicingStatus { get; set; } = ReservaInvoicingStatus.NotInvoiced;
}
