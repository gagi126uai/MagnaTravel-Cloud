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

public record SupplierPaymentRequest(
    decimal Amount, 
    string? Method, 
    string? Reference, 
    string? Notes,
    string? ReservaId,
    string? ServicioReservaId
);
