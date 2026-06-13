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

    /// <summary>
    /// ADR-021 (multimoneda, eje proveedor, §15.4): moneda en que va este servicio (la deuda con el
    /// operador por este servicio esta en ESTA moneda). <c>null</c> = legacy = ARS (se normaliza con
    /// <c>Monedas.Normalizar</c> al agrupar la deuda por moneda). Proyectada desde <c>x.Currency</c>
    /// de cada tipo de servicio.
    /// </summary>
    public string? Currency { get; set; }

    public DateTime Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
}

// ===================================================================================================
// Auditoria ERP hallazgo #4 (2026-06-12): deuda al proveedor DESGLOSADA POR EXPEDIENTE (reserva).
// La cuenta corriente del proveedor hasta hoy era GLOBAL (todo lo que se le debe al operador, sumado).
// El dueño concilia con los mayoristas POR EXPEDIENTE, asi que necesita ver, para un proveedor dado:
//   - cuanto se le debe en CADA reserva (por moneda), descontando los pagos imputados a esa reserva; y
//   - un bucket "a cuenta / anticipos" con los pagos que NO estan atados a ninguna reserva.
// Invariante (verificado por test): la suma de los saldos por reserva + los anticipos a cuenta RECONCILIA
// exactamente con el total global por moneda de SupplierService.CalculateSupplierDebtPorMonedaAsync.
// ===================================================================================================

/// <summary>
/// Vista completa de la deuda de la agencia con UN proveedor, abierta por expediente (reserva) y por
/// moneda, mas el bucket de anticipos a cuenta y el total global de reconciliacion.
/// </summary>
public class SupplierDebtByReservaDto
{
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>Una entrada por reserva donde el proveedor tiene deuda y/o pagos imputados.</summary>
    public List<SupplierDebtReservaLineDto> Reservas { get; set; } = new();

    /// <summary>
    /// Anticipos "a cuenta": pagos al proveedor SIN reserva imputada (incluye el legacy con ReservaId null).
    /// Una linea por moneda. Estos montos NO estan atados a un expediente concreto.
    /// </summary>
    public List<SupplierDebtCurrencyAmountDto> AdvancesToAccount { get; set; } = new();

    /// <summary>
    /// Total global de la deuda por moneda (el mismo numero que la cuenta corriente global ya calculaba).
    /// Sirve de control de reconciliacion: por moneda, debe igualar la suma de los saldos por reserva mas
    /// los anticipos a cuenta de esa moneda.
    /// </summary>
    public List<SupplierDebtCurrencyAmountDto> GlobalTotals { get; set; } = new();
}

/// <summary>
/// La deuda del proveedor en UNA reserva, abierta por moneda. Datos de identidad de la reserva (numero,
/// nombre) viajan SIEMPRE; los montos respetan el masking see_cost igual que el resto de la cuenta.
/// </summary>
public class SupplierDebtReservaLineDto
{
    public Guid ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }

    /// <summary>Una linea por moneda: compras confirmadas, pagado imputado y saldo de ESTE proveedor en ESTA reserva.</summary>
    public List<SupplierDebtCurrencyLineDto> Currencies { get; set; } = new();
}

/// <summary>Compras confirmadas, pagado y saldo de una moneda. Espejo de <c>SupplierDebtLine</c> del dominio.</summary>
public class SupplierDebtCurrencyLineDto
{
    public string Currency { get; set; } = "ARS";
    public decimal ConfirmedPurchases { get; set; }
    public decimal TotalPaid { get; set; }

    /// <summary>Saldo = compras confirmadas - pagado. Puede ser negativo (sobrepago a esta reserva/proveedor).</summary>
    public decimal Balance { get; set; }
}

/// <summary>Par moneda + monto. Para anticipos a cuenta y totales globales de reconciliacion.</summary>
public class SupplierDebtCurrencyAmountDto
{
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
}

public class SupplierPaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>ADR-021: moneda REAL del egreso (lo que salio de caja). Default ARS para el legacy.</summary>
    public string Currency { get; set; } = "ARS";
    public string Method { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
    /// <summary>
    /// ADR-022 §4 P4: true = anticipo "a cuenta" del proveedor (no imputado a ninguna reserva). Se deriva de
    /// la ausencia de reserva (<see cref="ReservaPublicId"/> null). El front lo usa para mostrar "a cuenta"
    /// en vez de un numero de reserva.
    /// </summary>
    public bool IsAdvanceToAccount { get; set; }
}
