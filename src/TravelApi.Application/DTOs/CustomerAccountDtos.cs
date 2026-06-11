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

    // ADR-022 Capa 8 (C2): la cuenta corriente del cliente deja de ser un escalar surrogate y pasa a
    // exponerse POR MONEDA, alineada con ADR-021 (el saldo en USD no compensa el saldo en ARS). Los
    // escalares de arriba quedan para compat (el front actual los usa); estas listas son ADITIVAS.

    /// <summary>
    /// Saldo a COBRAR por moneda (deuda del cliente con la agencia), desde ReservaMoneyByCurrency de sus
    /// reservas activas. Misma fuente que el AR de tesoreria. NUNCA mezcla monedas en un total.
    /// </summary>
    public List<CurrencyAmountDto> ReceivableByCurrency { get; set; } = new();

    /// <summary>
    /// Saldo A FAVOR del cliente por moneda (el "bolsillo" unificado): suma de los ClientCreditEntry
    /// activos (RemainingBalance &gt; 0), cualquier origen (cancelacion o sobrepago). Eje OPUESTO al de cobrar;
    /// se exponen separados y NO se netea uno contra otro (ni dentro de una moneda ni entre monedas).
    /// </summary>
    public List<CurrencyAmountDto> CreditBalanceByCurrency { get; set; } = new();
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
