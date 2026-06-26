namespace TravelApi.Application.DTOs;

public class CollectionsSummaryDto
{
    public decimal PendingAmount { get; set; }

    /// <summary>
    /// Plata REAL nueva que entró este mes (cobros que mueven caja). Escalar de compat: suma cross-moneda
    /// (igual que el resto de los escalares historicos; con todo en ARS coincide con la unica fila ARS de
    /// <see cref="CollectedThisMonthByCurrency"/>). NUNCA usar para decidir nada por moneda: el detalle real,
    /// sin mezclar ARS/USD, va en <see cref="CollectedThisMonthByCurrency"/>.
    ///
    /// <para>(2026-06-26) EXCLUYE los pagos puente (<c>AffectsCash=false</c>): el saldo a favor aplicado de
    /// OTRA reserva baja la deuda del destino pero NO es plata nueva que entró a caja, asi que no infla este KPI.</para>
    /// </summary>
    public decimal CollectedThisMonth { get; set; }

    /// <summary>
    /// (2026-06-26) Plata REAL cobrada este mes SEPARADA por moneda real del cobro (no se mezclan ARS y USD,
    /// regla dura). Mismo criterio que <see cref="CollectedThisMonth"/> (solo cobros con <c>AffectsCash=true</c>,
    /// sin puentes de saldo a favor), pero agrupado por <c>Payment.Currency</c>. Una sola fila = mono-moneda.
    /// Patron espejo de <see cref="CashByCurrencyDto"/> en la caja.
    /// </summary>
    public List<CurrencyAmountDto> CollectedThisMonthByCurrency { get; set; } = new();

    /// <summary>
    /// (2026-06-26, decision P1=B de Gaston) Saldo a favor del cliente APLICADO este mes a reservas, SEPARADO
    /// por moneda. NO es plata nueva en caja (por eso NO entra en <see cref="CollectedThisMonth"/> ni en
    /// <see cref="CollectedThisMonthByCurrency"/>): es la linea chica "+ $X aplicados de saldo a favor" que el
    /// front muestra DEBAJO del numero grande de cobrado real. Son los Payment PUENTE POSITIVOS de aplicacion
    /// de credito (<c>AppliedCreditBridge.IsAppliedCreditBridge</c>: Method "SaldoAFavorAplicado",
    /// <c>AffectsCash=false</c>, <c>AppliedFromCreditWithdrawalId != null</c>) — distinto de los puentes
    /// NEGATIVOS de retiro/sobrepago. Agrupado por <c>Payment.Currency</c> (null -&gt; ARS). Vacio si no hubo
    /// aplicaciones este mes.
    /// </summary>
    public List<CurrencyAmountDto> CreditApplicationsThisMonthByCurrency { get; set; } = new();

    public int UrgentReservationsCount { get; set; }
    public decimal UrgentPendingAmount { get; set; }
    public int BlockedOperationalCount { get; set; }
    public int BlockedVoucherCount { get; set; }
}

public class CollectionWorkItemDto
{
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public string? ResponsibleUserName { get; set; }
    public decimal TotalSale { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }
    public string CollectionStatus { get; set; } = string.Empty;
    public string UrgencyStatus { get; set; } = string.Empty;
    public bool BlocksOperational { get; set; }
    public bool BlocksVoucher { get; set; }

    /// <summary>
    /// ADR-035 (2026-06-19): moneda PRINCIPAL de la reserva (la de mayor saldo pendiente; mismo criterio y
    /// desempate que el detalle de la reserva, via <c>ReservaPrimaryCurrency</c>). El modal de cobro de la
    /// worklist la usa como moneda por defecto, asi un cobro sobre una reserva en USD NO se registra en ARS.
    /// Null si la reserva no tiene detalle por moneda materializado (legacy sin backfill); en ese caso el
    /// front cae al default historico (ARS).
    /// </summary>
    public string? MonedaPrincipal { get; set; }

    /// <summary>
    /// ADR-021/035 (2026-06-19): detalle de saldo SEPARADO por moneda (una linea por moneda con saldo). Permite
    /// que el modal de la worklist ofrezca "cobrar en otra moneda" igual que el cobro inline de la reserva, sin
    /// pegarle otra vez al backend. Se llena en batch desde la tabla materializada <c>ReservaMoneyByCurrency</c>
    /// (sin N+1). El costo (<c>TotalCost</c>) llega siempre en 0: la worklist de cobranza no expone costos.
    /// </summary>
    public List<ReservaMoneyLineDto> PorMoneda { get; set; } = new();
}

public class CashSummaryDto
{
    public decimal CashInThisMonth { get; set; }
    public decimal CashOutThisMonth { get; set; }
    public decimal NetCashThisMonth { get; set; }

    /// <summary>
    /// ADR-021 Capa 7: caja del mes SEPARADA por moneda REAL del movimiento (un cobro cruzado entra a
    /// caja en su moneda real, no en la imputada). Aditivo: los escalares de arriba quedan para compat
    /// y, con todo en ARS, coinciden con la unica fila ARS de esta lista. NUNCA mezcla ARS+USD en un total.
    /// </summary>
    public List<CashByCurrencyDto> CashByCurrency { get; set; } = new();
}

/// <summary>
/// ADR-021 Capa 7: una fila de caja por moneda (entradas, salidas y neto de ESA moneda en el mes).
/// El front la usa en la pantalla de caja (Estado de Cuenta) para mostrar un renglon por moneda.
/// </summary>
public class CashByCurrencyDto
{
    public string Currency { get; set; } = "ARS";
    public decimal CashInThisMonth { get; set; }
    public decimal CashOutThisMonth { get; set; }
    public decimal NetCashThisMonth { get; set; }
}

public class InvoicingSummaryDto
{
    public decimal ReadyAmount { get; set; }
    public int ReadyCount { get; set; }
    public int OverrideCount { get; set; }
    public int BlockedCount { get; set; }
    public decimal InvoicedThisMonth { get; set; }
    public int ForcedCount { get; set; }
}

public class InvoicingWorkItemDto
{
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public decimal TotalSale { get; set; }
    public decimal AlreadyInvoiced { get; set; }
    public decimal PendingFiscalAmount { get; set; }
    public string FiscalStatus { get; set; } = string.Empty;
    public string FiscalStatusLabel { get; set; } = string.Empty;
    public bool RequiresOverride { get; set; }
    public string? EconomicBlockReason { get; set; }
    public string? ForcedByUserName { get; set; }
}
