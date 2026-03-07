using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface ILeadService
{
    Task<List<Lead>> GetAllAsync(CancellationToken cancellationToken);
    Task<Lead?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task<Lead> CreateAsync(Lead lead, CancellationToken cancellationToken);
    Task<Lead> UpdateAsync(int id, Lead updated, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
    Task<Lead> UpdateStatusAsync(int id, string status, CancellationToken cancellationToken);
    Task<LeadActivity> AddActivityAsync(int leadId, LeadActivity activity, CancellationToken cancellationToken);
    Task<int> ConvertToCustomerAsync(int leadId, CancellationToken cancellationToken);
    Task<object> GetPipelineAsync(CancellationToken cancellationToken);
}
