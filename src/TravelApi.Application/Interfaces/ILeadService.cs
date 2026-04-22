using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface ILeadService
{
    Task<PagedResponse<LeadSummaryDto>> GetAllAsync(LeadListQuery query, CancellationToken cancellationToken);
    Task<LeadDetailDto?> GetByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<LeadDetailDto> CreateAsync(LeadUpsertRequest request, CancellationToken cancellationToken);
    Task<LeadDetailDto> UpdateAsync(string publicIdOrLegacyId, LeadUpsertRequest updated, CancellationToken cancellationToken);
    Task DeleteAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<LeadDetailDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken cancellationToken);
    Task<LeadActivityDto> AddActivityAsync(string publicIdOrLegacyId, LeadActivityUpsertRequest activity, string? createdBy, CancellationToken cancellationToken);
    Task<LeadConversionResultDto> ConvertToCustomerAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<QuoteDraftResultDto> CreateQuoteDraftAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<LeadJourneyDto> GetJourneyAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<object> GetPipelineAsync(CancellationToken cancellationToken);
}
