namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-021 Capa 6: monto rotulado con su moneda. Se usa en los desgloses por moneda de tesoreria y
/// reportes para NUNCA mezclar ARS+USD en un solo total (o se separa por moneda, o se filtra a una).
/// </summary>
public class CurrencyAmountDto
{
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
}

public class TreasurySummaryDto
{
    public decimal AccountsReceivable { get; set; }
    public decimal AfipEligiblePending { get; set; }
    public decimal CashInThisMonth { get; set; }
    public decimal CashOutThisMonth { get; set; }
    public decimal NetCashThisMonth { get; set; }

    // ADR-021 Capa 6: desgloses por moneda (aditivos; los escalares de arriba quedan para compat y hoy,
    // con todo en ARS, coinciden con el unico item ARS de cada lista).
    /// <summary>Cuentas por cobrar por moneda del SALDO (agregado en SQL contra ReservaMoneyByCurrency).</summary>
    public List<CurrencyAmountDto> AccountsReceivableByCurrency { get; set; } = new();
    /// <summary>Entradas de caja del mes por moneda REAL del cobro (Payment.Currency / movimiento manual).</summary>
    public List<CurrencyAmountDto> CashInByCurrency { get; set; } = new();
    /// <summary>Salidas de caja del mes por moneda REAL del egreso (SupplierPayment.Currency / movimiento manual).</summary>
    public List<CurrencyAmountDto> CashOutByCurrency { get; set; } = new();

    // ADR-022 Capa 7 (T4): cuentas por PAGAR (AP) por moneda, desde la MISMA fuente unica que el dashboard
    // (IFinancePositionService -> SupplierBalanceByCurrency). Antes tesoreria no exponia AP; ahora si, para
    // que dashboard y tesoreria muestren EXACTAMENTE el mismo numero. Aditivo: el front actual lo ignora.
    /// <summary>
    /// Cuentas por pagar por moneda (deuda a proveedores). Es dato de COSTO: se enmascara (lista vacia)
    /// para usuarios sin <c>cobranzas.see_cost</c>, igual que en el dashboard.
    /// </summary>
    public List<CurrencyAmountDto> AccountsPayableByCurrency { get; set; } = new();
}

public class CashMovementDto
{
    public string SourceType { get; set; } = string.Empty;
    public Guid SourcePublicId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>ADR-021 Capa 6: moneda REAL del movimiento de caja (lo que efectivamente entro/salio). Default ARS.</summary>
    public string Currency { get; set; } = "ARS";
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public Guid? SupplierPublicId { get; set; }
    public string? SupplierName { get; set; }
    public bool IsManual { get; set; }

    /// <summary>
    /// ADR-022 §4.6 (fix S2, 2026-06-11): tipo de origen REAL del asiento del Libro de Caja
    /// (<c>CashLedgerSourceTypes</c>: CustomerPayment/SupplierPayment/OperatorRefund/ClientCreditWithdrawal/
    /// ManualAdjustment). El front sigue usando <see cref="SourceType"/> (contrato de 3 valores), pero el
    /// servidor necesita el tipo crudo para enmascarar el monto de los refund de operador (= dato de costo,
    /// RK-9) sin ocultar los ajustes manuales genuinos ni la devolucion fisica al cliente. Aditivo: el front
    /// actual lo ignora.
    /// </summary>
    public string LedgerSourceType { get; set; } = string.Empty;
}

public class ManualCashMovementDto
{
    public Guid PublicId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsVoided { get; set; }
    public Guid? RelatedReservaPublicId { get; set; }
    public Guid? RelatedSupplierPublicId { get; set; }
}

public class UpsertManualCashMovementRequest
{
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    /// <summary>
    /// ADR-022 §4.12 (T2): moneda REAL del gasto/ajuste manual. Opcional; null/vacio = ARS (legacy). Su
    /// asiento de caja (ManualAdjustment) toma la moneda de aca.
    /// </summary>
    public string? Currency { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? RelatedReservaPublicId { get; set; }
    public string? RelatedSupplierPublicId { get; set; }
}
