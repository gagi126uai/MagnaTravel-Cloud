using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface ISupplierService
{
    Task<IEnumerable<Supplier>> GetSuppliersAsync(CancellationToken cancellationToken);
    Task<Supplier> GetSupplierAsync(int id, CancellationToken cancellationToken);
    Task<Supplier> CreateSupplierAsync(Supplier supplier, CancellationToken cancellationToken);
    Task<Supplier> UpdateSupplierAsync(int id, Supplier supplier, CancellationToken cancellationToken);
    Task DeleteSupplierAsync(int id, CancellationToken cancellationToken);
    Task ForceDeleteSupplierAsync(int id, CancellationToken cancellationToken);
    Task RecalculateAllBalancesAsync(CancellationToken cancellationToken);
    Task<SupplierAccountDto> GetSupplierAccountAsync(int id, CancellationToken cancellationToken);
    Task<int> AddSupplierPaymentAsync(int id, SupplierPaymentRequest request, CancellationToken cancellationToken);
    Task UpdateSupplierPaymentAsync(int id, int paymentId, SupplierPaymentRequest request, CancellationToken cancellationToken);
    Task DeleteSupplierPaymentAsync(int id, int paymentId, CancellationToken cancellationToken);
    Task<IEnumerable<SupplierPaymentDto>> GetSupplierPaymentsHistoryAsync(int id, CancellationToken cancellationToken);
}

public class SupplierAccountDto
{
    public object Supplier { get; set; } = null!;
    public IEnumerable<SupplierServiceDto> Services { get; set; } = new List<SupplierServiceDto>();
    public IEnumerable<object> Payments { get; set; } = new List<object>();
    public object Summary { get; set; } = null!;
}

public class SupplierServiceDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? Description { get; set; } = "";
    public string? Confirmation { get; set; }
    public decimal NetCost { get; set; }
    public decimal SalePrice { get; set; }
    public DateTime Date { get; set; }
    public string Status { get; set; } = "";
    public string? FileNumber { get; set; }
    public string? FileName { get; set; }
}

public class SupplierPaymentDto
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public DateTime PaidAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? FileNumber { get; set; }
    public string? FileName { get; set; }
    public int? TravelFileId { get; set; }
}

public record SupplierPaymentRequest(
    decimal Amount, 
    string? Method, 
    string? Reference, 
    string? Notes,
    int? TravelFileId,
    int? ReservationId
);
