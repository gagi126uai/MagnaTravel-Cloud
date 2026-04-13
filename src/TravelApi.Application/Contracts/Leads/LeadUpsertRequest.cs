namespace TravelApi.Application.Contracts.Leads;

public record LeadUpsertRequest(
    string FullName,
    string? Email,
    string? Phone,
    string? Source,
    string? InterestedIn,
    string? TravelDates,
    string? Travelers,
    decimal EstimatedBudget,
    string? Notes,
    string? AssignedToUserId,
    string? AssignedToName,
    DateTime? NextFollowUp);

public record LeadActivityUpsertRequest(
    string Type,
    string Description,
    string? CreatedBy);
