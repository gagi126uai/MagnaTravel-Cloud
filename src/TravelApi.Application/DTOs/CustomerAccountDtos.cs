using TravelApi.Domain.Entities;

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

/// <summary>
/// DETALLE de un saldo a favor disponible del cliente (un <c>ClientCreditEntry</c> con
/// <c>RemainingBalance &gt; 0</c>). El cartel del front usa el AGREGADO por moneda
/// (<see cref="CustomerAccountSummaryDto.CreditBalanceByCurrency"/>); este DTO es para el
/// botón "usar saldo a favor", donde el usuario elige DE QUÉ entry retirar.
///
/// <para>Trae el <c>EntryPublicId</c> para que el front lo pase tal cual al withdraw
/// (<c>POST /api/client-credit-entries/{EntryPublicId}/withdrawals</c>). Incluye el origen
/// (reserva de la cancelación o reserva sobre-pagada) solo como contexto para que el usuario
/// reconozca de qué viene el crédito; puede ser null en créditos legacy sin origen trazable.</para>
/// </summary>
public class CustomerAvailableCreditEntryDto
{
    public Guid EntryPublicId { get; set; }

    /// <summary>Saldo aún disponible para retirar (siempre &gt; 0 en esta lista).</summary>
    public decimal RemainingBalance { get; set; }

    /// <summary>Monto original acreditado (para mostrar "quedan $X de $Y").</summary>
    public decimal CreditedAmount { get; set; }

    /// <summary>Moneda del bolsillo. El saldo en USD no compensa deuda en ARS (bolsillo por moneda).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Número de la reserva que originó el crédito (cancelación o sobrepago). Null si no es trazable.</summary>
    public string? OriginReservaNumber { get; set; }

    /// <summary>PublicId de la reserva de origen, para que el front pueda enlazarla. Null si no es trazable.</summary>
    public Guid? OriginReservaPublicId { get; set; }

    public DateTime CreatedAt { get; set; }
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

// ===================================================================================================
// Deuda del cliente DESGLOSADA POR RESERVA y por moneda. Espejo conceptual del lado proveedor
// (SupplierDebtByReservaDto). Alimenta el buscador del flujo "usar saldo a favor -> aplicar a otra
// reserva" del cliente: el front necesita saber EN QUE reservas y EN QUE moneda debe el cliente para
// ofrecer solo destinos con deuda en la misma moneda del saldo a favor (el saldo en USD no cancela
// deuda en ARS). El total global por moneda ya viaja en CustomerAccountSummaryDto.ReceivableByCurrency;
// esto es el MISMO numero abierto por reserva.
//
// Masking: la cuenta del cliente (lado VENTA) NO enmascara montos, a diferencia de la cuenta del
// proveedor (lado COSTO, que oculta cifras sin cobranzas.see_cost). El gate es clientes.view +
// cobranzas.view, igual que el resto de los endpoints de montos de la cuenta del cliente.
// ===================================================================================================

/// <summary>
/// Deuda del cliente con la agencia, abierta por reserva (expediente) y por moneda. Lista solo las
/// reservas donde el cliente es el pagador y tiene saldo pendiente (deuda &gt; 0) en al menos una moneda.
/// </summary>
public class CustomerDebtByReservaDto
{
    public Guid CustomerPublicId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Una entrada por reserva con deuda viva del cliente; dentro, una linea por moneda.</summary>
    public List<CustomerDebtReservaLineDto> Reservas { get; set; } = new();
}

/// <summary>
/// La deuda del cliente en UNA reserva, abierta por moneda. La identidad (numero, nombre del expediente)
/// viaja siempre; los montos son la deuda viva por moneda (Balance &gt; 0 de ReservaMoneyByCurrency).
/// </summary>
public class CustomerDebtReservaLineDto
{
    public Guid ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }

    /// <summary>Nombre del expediente (titular) = <c>Reserva.Name</c>. Mismo campo que la pestaña Pagos.</summary>
    public string? FileName { get; set; }

    /// <summary>Una linea por moneda en la que el cliente debe en esta reserva. Nunca mezcla monedas.</summary>
    public List<CurrencyAmountDto> DebtByCurrency { get; set; } = new();
}

public class CustomerAccountPaymentListItemDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// Moneda ISO 4217 del cobro ("ARS"/"USD"), tomada de <c>Payment.Currency</c> (la moneda real
    /// en la que entro la plata, sobre la que esta expresado <see cref="Amount"/>). El front la usa
    /// para agrupar y llevar saldo corriente POR MONEDA: el saldo en USD nunca se mezcla con el de ARS.
    /// Es un codigo limpio, no un entero interno; el front lo mapea a la etiqueta visible.
    /// </summary>
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Moneda ISO 4217 ("ARS"/"USD") de la DEUDA a la que se IMPUTO el cobro (ADR-021 "pagar en otra
    /// moneda"): si el cobro entro en USD pero canceló deuda en ARS, aca viaja "ARS". Sale de
    /// <c>Payment.ImputedCurrency ?? Payment.Currency</c>. El extracto de la cuenta del cliente AGRUPA
    /// y lleva saldo corriente por ESTA moneda (no por <see cref="Currency"/>), para que el saldo por
    /// moneda reconcilie con lo que el cliente debe — igual criterio que el extracto por reserva.
    /// </summary>
    public string ImputedCurrency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Monto del cobro EXPRESADO en la moneda imputada (<see cref="ImputedCurrency"/>): lo que
    /// efectivamente bajo del saldo de esa moneda. Sale de <c>Payment.ImputedAmount ?? Payment.Amount</c>.
    /// En un cobro NO cruzado coincide con <see cref="Amount"/>; en uno cruzado es el equivalente ya
    /// convertido. El extracto usa este monto para el saldo corriente por moneda.
    /// </summary>
    public decimal ImputedAmount { get; set; }

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
