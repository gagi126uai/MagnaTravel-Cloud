using TravelApi.Domain.Entities;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ICustomerService
{
    Task<PagedResponse<CustomerListItemDto>> GetCustomersAsync(CustomerListQuery query, CancellationToken cancellationToken);
    Task<CustomerListItemDto> GetCustomerAsync(int id, CancellationToken cancellationToken);
    Task<Customer> CreateCustomerAsync(Customer customer, CancellationToken cancellationToken);
    Task<Customer> UpdateCustomerAsync(int id, Customer customer, CancellationToken cancellationToken);
    Task<CustomerDeletionResult> DeleteOrArchiveCustomerAsync(int id, CancellationToken cancellationToken);
    Task<Customer> ReactivateCustomerAsync(int id, CancellationToken cancellationToken);
    Task<CustomerAccountOverviewDto> GetCustomerAccountOverviewAsync(int id, CancellationToken cancellationToken);
    Task<PagedResponse<CustomerAccountReservaListItemDto>> GetCustomerAccountReservasAsync(int id, PagedQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<CustomerAccountPaymentListItemDto>> GetCustomerAccountPaymentsAsync(int id, PagedQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<InvoiceListDto>> GetCustomerAccountInvoicesAsync(int id, PagedQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerSimilarMatchDto>> SearchSimilarAsync(string? fullName, string? documentType, string? documentNumber, string? phone, int take, CancellationToken cancellationToken);
}

public enum CustomerDeletionOutcome
{
    HardDeleted,
    Archived
}

public record CustomerDeletionResult(CustomerDeletionOutcome Outcome, string Message);

public class CustomerSimilarMatchDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public int Score { get; set; }
}
