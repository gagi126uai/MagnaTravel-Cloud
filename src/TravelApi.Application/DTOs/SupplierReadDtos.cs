namespace TravelApi.Application.DTOs;

public class SupplierListItemDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? TaxCondition { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; }
    public decimal CurrentBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SupplierAccountOverviewDto
{
    public SupplierAccountSupplierDto Supplier { get; set; } = new();
    public SupplierAccountSummaryDto Summary { get; set; } = new();
}

public class SupplierAccountSupplierDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? TaxCondition { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; }
    public decimal CurrentBalance { get; set; }
}

public class SupplierAccountSummaryDto
{
    public decimal TotalPurchases { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }
    public int ServiceCount { get; set; }
    public int PaymentCount { get; set; }
}

public class SupplierAccountServiceListItemDto
{
    public Guid PublicId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Confirmation { get; set; }
    public decimal NetCost { get; set; }
    public decimal SalePrice { get; set; }
    public DateTime Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
    /// <summary>
    /// PublicId de la ultima factura AFIP emitida para la reserva del servicio (si existe).
    /// Permite que la cuenta corriente del proveedor linkee directo al PDF de AFIP.
    /// </summary>
    public Guid? LatestInvoicePublicId { get; set; }
}

public class SupplierPaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
}
