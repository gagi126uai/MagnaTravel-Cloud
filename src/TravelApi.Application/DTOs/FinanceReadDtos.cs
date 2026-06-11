namespace TravelApi.Application.DTOs;

public class CollectionsSummaryDto
{
    public decimal PendingAmount { get; set; }
    public decimal CollectedThisMonth { get; set; }
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
