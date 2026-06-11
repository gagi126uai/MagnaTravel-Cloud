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
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? RelatedReservaPublicId { get; set; }
    public string? RelatedSupplierPublicId { get; set; }
}
