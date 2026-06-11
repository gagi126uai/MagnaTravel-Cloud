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
    // ADR-021: bloque de moneda/TC del egreso. Todos opcionales -> default ARS no cruzado = identico a hoy.
    string? Currency = null,
    string? ImputedCurrency = null,
    decimal? ExchangeRate = null,
    int? ExchangeRateSource = null,
    DateTime? ExchangeRateAt = null,
    decimal? ImputedAmount = null
);
