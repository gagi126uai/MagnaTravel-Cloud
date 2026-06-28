using TravelApi.Domain.Entities;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ISupplierService
{
    Task<PagedResponse<SupplierListItemDto>> GetSuppliersAsync(SupplierListQuery query, CancellationToken cancellationToken);
    Task<Supplier> GetSupplierAsync(int id, CancellationToken cancellationToken);
    Task<Supplier> CreateSupplierAsync(Supplier supplier, CancellationToken cancellationToken);
    Task<Supplier> UpdateSupplierAsync(int id, Supplier supplier, CancellationToken cancellationToken);
    Task DeleteSupplierAsync(int id, CancellationToken cancellationToken);
    Task RecalculateAllBalancesAsync(CancellationToken cancellationToken);
    Task UpdateBalanceAsync(int id, CancellationToken cancellationToken);
    Task<SupplierAccountOverviewDto> GetSupplierAccountOverviewAsync(int id, CancellationToken cancellationToken);
    Task<PagedResponse<SupplierAccountServiceListItemDto>> GetSupplierAccountServicesAsync(int id, SupplierAccountServicesQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<SupplierPaymentDto>> GetSupplierAccountPaymentsAsync(int id, SupplierAccountPaymentsQuery query, CancellationToken cancellationToken);
    Task<Guid> AddSupplierPaymentAsync(int id, SupplierPaymentRequest request, CancellationToken cancellationToken);
    Task UpdateSupplierPaymentAsync(int id, int paymentId, SupplierPaymentRequest request, CancellationToken cancellationToken);
    Task DeleteSupplierPaymentAsync(int id, int paymentId, CancellationToken cancellationToken);
    Task<IEnumerable<SupplierPaymentDto>> GetSupplierPaymentsHistoryAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Auditoria ERP hallazgo #4: deuda con el proveedor DESGLOSADA POR EXPEDIENTE (reserva) y por moneda,
    /// mas el bucket de anticipos "a cuenta" (pagos sin reserva imputada). Reconcilia por moneda con el
    /// total global de la cuenta corriente del proveedor. Los montos respetan el masking see_cost.
    /// </summary>
    Task<SupplierDebtByReservaDto> GetSupplierDebtByReservaAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-036 4c: estado "pagado al operador" de TODOS los servicios de una reserva. Por cada servicio
    /// (de las 6 tablas) devuelve su costo, lo pagado al operador imputado a ese servicio, el saldo y el
    /// estado derivado (paid/partial/unpaid). El estado lo ven todos; los montos respetan see_cost.
    /// <paramref name="reservaId"/> es el id interno de la reserva (ya resuelto desde el publicId).
    /// </summary>
    Task<ReservaSupplierPaymentStatusDto> GetReservaSupplierPaymentStatusAsync(int reservaId, CancellationToken cancellationToken);

    /// <summary>
    /// TANDA 1 (cuenta corriente del proveedor): EXTRACTO de la Cuenta por Pagar como libro mayor, SEPARADO
    /// por moneda y con saldo corriente. Cargos = compras confirmadas del operador; abonos = pagos al operador.
    /// El saldo de cierre de cada moneda coincide con <c>SupplierBalanceByCurrency.Balance</c> (misma fuente de
    /// verdad que la deuda: mismas compras que cuentan, misma imputacion de pagos, misma exclusion de
    /// CommissionOnly / soft-deleted). Los montos respetan el masking see_cost.
    /// </summary>
    Task<SupplierAccountStatementDto> GetSupplierAccountStatementAsync(int id, CancellationToken cancellationToken);
}

/// <summary>
/// Alta/edicion de un pago a un proveedor.
///
/// <para><b>Imputacion (ADR-022 §4 P4)</b>: un pago a proveedor se imputa a UNA reserva concreta (caso
/// normal, <see cref="ReservaId"/> seteado) o se registra como ANTICIPO "a cuenta" del proveedor sin
/// reserva (<see cref="IsAdvanceToAccount"/> = true, <see cref="ReservaId"/> vacio). Legacy con
/// <see cref="ReservaId"/> null y sin el flag se tolera (no se migra). Cuando viene imputado a reserva, la
/// deuda no se puede exceder ni contra el global del proveedor ni contra la deuda de ESA reserva en ESA
/// moneda.</para>
///
/// <para><b>Bloque de moneda (ADR-021)</b>: <see cref="Currency"/> es la moneda REAL del egreso.
/// <see cref="ImputedCurrency"/> + el bloque de TC solo se mandan en un pago CRUZADO (egreso en una moneda
/// imputado a deuda de otra). El backend valida y recalcula el equivalente; nunca confia en el front.</para>
/// </summary>
public record SupplierPaymentRequest(
    decimal Amount,
    string? Method,
    string? Reference,
    string? Notes,
    string? ReservaId,
    string? ServicioReservaId,
    // ADR-022 §4 P4: anticipo "a cuenta" del proveedor (sin reserva). Mutuamente excluyente con ReservaId.
    bool IsAdvanceToAccount = false,
    // ADR-036 4c: imputar el pago a UN servicio concreto de la reserva (referencia polimorfica
    // recordKind + publicId, el mismo identificador que usa el front). Si se manda ServicePublicId hay
    // que mandar tambien ServiceRecordKind; el servicio debe pertenecer a este proveedor y a la reserva
    // imputada. Ambos null = pago a nivel reserva (comportamiento previo intacto).
    string? ServiceRecordKind = null,
    string? ServicePublicId = null,
    // ADR-021: bloque de moneda/TC del egreso. Todos opcionales -> default ARS no cruzado = identico a hoy.
    string? Currency = null,
    string? ImputedCurrency = null,
    decimal? ExchangeRate = null,
    int? ExchangeRateSource = null,
    DateTime? ExchangeRateAt = null,
    decimal? ImputedAmount = null
);
