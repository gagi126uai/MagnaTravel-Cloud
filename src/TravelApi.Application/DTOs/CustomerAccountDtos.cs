namespace TravelApi.Application.DTOs;

public class CustomerListItemDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public string? TaxId { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; }
    public int? TaxConditionId { get; set; }
    public decimal CurrentBalance { get; set; }
}

public class CustomerAccountOverviewDto
{
    public CustomerAccountCustomerDto Customer { get; set; } = new();
    public CustomerAccountSummaryDto Summary { get; set; } = new();
}

public class CustomerAccountCustomerDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal CurrentBalance { get; set; }
}

public class CustomerAccountSummaryDto
{
    public decimal TotalSales { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalBalance { get; set; }
    public int ReservaCount { get; set; }
    public int PaymentCount { get; set; }
    public int InvoiceCount { get; set; }
}

public class CustomerAccountReservaListItemDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public decimal Paid { get; set; }
}

public class CustomerAccountPaymentListItemDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? Notes { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReceiptPublicId { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? ReceiptStatus { get; set; }
}
