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
    public Guid SourcePublicId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public decimal Amount { get; set; }
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
