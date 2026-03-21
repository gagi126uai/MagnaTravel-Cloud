namespace TravelApi.Application.DTOs;

public class TreasurySummaryDto
{
    public decimal AccountsReceivable { get; set; }
    public decimal AfipEligiblePending { get; set; }
    public decimal CashInThisMonth { get; set; }
    public decimal CashOutThisMonth { get; set; }
    public decimal NetCashThisMonth { get; set; }
}

public class CashMovementDto
{
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public int? ReservaId { get; set; }
    public string? NumeroReserva { get; set; }
    public int? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public bool IsManual { get; set; }
}

public class ManualCashMovementDto
{
    public int Id { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsVoided { get; set; }
    public int? RelatedReservaId { get; set; }
    public int? RelatedSupplierId { get; set; }
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
    public int? RelatedReservaId { get; set; }
    public int? RelatedSupplierId { get; set; }
}
