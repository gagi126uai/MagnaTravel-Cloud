using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface ICustomerService
{
    Task<IEnumerable<object>> GetCustomersAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<object> GetCustomerAsync(int id, CancellationToken cancellationToken);
    Task<Customer> CreateCustomerAsync(Customer customer, CancellationToken cancellationToken);
    Task<Customer> UpdateCustomerAsync(int id, Customer customer, CancellationToken cancellationToken);
    Task<CustomerAccountDto> GetCustomerAccountAsync(int id, CancellationToken cancellationToken);
}

public class CustomerAccountDto
{
    public object Customer { get; set; } = null!;
    public IEnumerable<object> Files { get; set; } = new List<object>();
    public IEnumerable<object> Payments { get; set; } = new List<object>();
    public object Summary { get; set; } = null!;
}
