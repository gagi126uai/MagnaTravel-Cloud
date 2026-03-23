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
    public int ReservaId { get; set; }
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
}

public class InvoicingSummaryDto
{
    public decimal ReadyAmount { get; set; }
    public int ReadyCount { get; set; }
    public int BlockedCount { get; set; }
    public decimal InvoicedThisMonth { get; set; }
    public int ForcedCount { get; set; }
}

public class InvoicingWorkItemDto
{
    public int ReservaId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalSale { get; set; }
    public decimal AlreadyInvoiced { get; set; }
    public decimal PendingFiscalAmount { get; set; }
    public string FiscalStatus { get; set; } = string.Empty;
    public string FiscalStatusLabel { get; set; } = string.Empty;
    public bool RequiresOverride { get; set; }
    public string? EconomicBlockReason { get; set; }
    public string? ForcedByUserName { get; set; }
}
