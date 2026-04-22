using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public LeadsController(ILeadService leadService, IEntityReferenceResolver entityReferenceResolver)
    {
        _leadService = leadService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<LeadSummaryDto>>> GetAll([FromQuery] LeadListQuery query, CancellationToken cancellationToken)
    {
        var leads = await _leadService.GetAllAsync(query, cancellationToken);
        return Ok(leads);
    }

    [HttpGet("pipeline")]
    public async Task<ActionResult> GetPipeline(CancellationToken cancellationToken)
    {
        var pipeline = await _leadService.GetPipelineAsync(cancellationToken);
        return Ok(pipeline);
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<LeadDetailDto>> GetById(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await _leadService.GetByIdAsync(id, cancellationToken);
        if (lead == null) return NotFound();
        return Ok(MapLeadDetail(lead));
    }

    [HttpPost]
    public async Task<ActionResult<LeadDetailDto>> Create([FromBody] LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        var created = await _leadService.CreateAsync(MapLeadFromRequest(request), cancellationToken);
        return CreatedAtAction(
            nameof(GetById),
            new { publicIdOrLegacyId = created.PublicId },
            MapLeadDetail(created));
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<LeadDetailDto>> Update(
        string publicIdOrLegacyId,
        [FromBody] LeadUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await _leadService.UpdateAsync(id, MapLeadFromRequest(request), cancellationToken);
        return Ok(MapLeadDetail(lead));
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        await _leadService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{publicIdOrLegacyId}/status")]
    public async Task<ActionResult<LeadDetailDto>> UpdateStatus(
        string publicIdOrLegacyId,
        [FromBody] StatusUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await _leadService.UpdateStatusAsync(id, request.Status, cancellationToken);
        return Ok(MapLeadDetail(lead));
    }

    [HttpPost("{publicIdOrLegacyId}/activities")]
    public async Task<ActionResult<LeadActivityDto>> AddActivity(
        string publicIdOrLegacyId,
        [FromBody] LeadActivityUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var activity = new LeadActivity
        {
            Type = request.Type,
            Description = request.Description,
            CreatedBy = request.CreatedBy ?? User.Identity?.Name ?? "Sistema"
        };
        var created = await _leadService.AddActivityAsync(id, activity, cancellationToken);
        return Ok(MapLeadActivity(created));
    }

    [HttpPost("{publicIdOrLegacyId}/convert")]
    public async Task<ActionResult<LeadConversionResultDto>> ConvertToCustomer(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var customerId = await _leadService.ConvertToCustomerAsync(id, cancellationToken);
            var customerPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Customer>(customerId, cancellationToken);
        if (!customerPublicId.HasValue)
            return NotFound();

        return Ok(new LeadConversionResultDto
        {
            CustomerPublicId = customerPublicId.Value
        });
    }

    [HttpPost("{publicIdOrLegacyId}/quote-draft")]
    public async Task<ActionResult<QuoteDraftResultDto>> CreateQuoteDraft(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var quote = await _leadService.CreateQuoteDraftAsync(id, cancellationToken);
        var leadPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);

        return Ok(new QuoteDraftResultDto
        {
            QuotePublicId = quote.PublicId,
            QuoteNumber = quote.QuoteNumber,
            CustomerPublicId = quote.Customer?.PublicId,
            LeadPublicId = leadPublicId ?? Guid.Empty
        });
    }

    [HttpGet("{publicIdOrLegacyId}/journey")]
    public async Task<ActionResult<LeadJourneyDto>> GetJourney(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var journey = await _leadService.GetJourneyAsync(id, cancellationToken);
        return Ok(journey);
    }

    private static LeadSummaryDto MapLeadSummary(Lead lead)
    {
        return new LeadSummaryDto
        {
            PublicId = lead.PublicId,
            FullName = lead.FullName,
            Email = lead.Email,
            Phone = lead.Phone,
            Status = lead.Status,
            Source = lead.Source,
            InterestedIn = lead.InterestedIn,
            TravelDates = lead.TravelDates,
            Travelers = lead.Travelers,
            EstimatedBudget = lead.EstimatedBudget,
            Notes = lead.Notes,
            AssignedToUserId = lead.AssignedToUserId,
            AssignedToName = lead.AssignedToName,
            NextFollowUp = lead.NextFollowUp,
            CreatedAt = lead.CreatedAt,
            ClosedAt = lead.ClosedAt,
            ConvertedCustomerPublicId = lead.ConvertedCustomer?.PublicId,
            ConvertedCustomerName = lead.ConvertedCustomer?.FullName,
            ActivitiesCount = lead.Activities?.Count ?? 0,
            LastActivity = lead.Activities?
                .OrderByDescending(activity => activity.CreatedAt)
                .FirstOrDefault()?
                .Description
        };
    }

    private static LeadDetailDto MapLeadDetail(Lead lead)
    {
        var summary = MapLeadSummary(lead);
        return new LeadDetailDto
        {
            PublicId = summary.PublicId,
            FullName = summary.FullName,
            Email = summary.Email,
            Phone = summary.Phone,
            Status = summary.Status,
            Source = summary.Source,
            InterestedIn = summary.InterestedIn,
            TravelDates = summary.TravelDates,
            Travelers = summary.Travelers,
            EstimatedBudget = summary.EstimatedBudget,
            Notes = summary.Notes,
            AssignedToUserId = summary.AssignedToUserId,
            AssignedToName = summary.AssignedToName,
            NextFollowUp = summary.NextFollowUp,
            CreatedAt = summary.CreatedAt,
            ClosedAt = summary.ClosedAt,
            ConvertedCustomerPublicId = summary.ConvertedCustomerPublicId,
            ConvertedCustomerName = summary.ConvertedCustomerName,
            ActivitiesCount = summary.ActivitiesCount,
            LastActivity = summary.LastActivity,
            Activities = lead.Activities?
                .OrderByDescending(activity => activity.CreatedAt)
                .Select(MapLeadActivity)
                .ToList() ?? new List<LeadActivityDto>()
        };
    }

    private static LeadActivityDto MapLeadActivity(LeadActivity activity)
    {
        return new LeadActivityDto
        {
            PublicId = activity.PublicId,
            Type = activity.Type,
            Description = activity.Description,
            CreatedBy = activity.CreatedBy,
            CreatedAt = activity.CreatedAt
        };
    }

    private static Lead MapLeadFromRequest(LeadUpsertRequest request)
    {
        return new Lead
        {
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            Source = request.Source,
            InterestedIn = request.InterestedIn,
            TravelDates = request.TravelDates,
            Travelers = request.Travelers,
            EstimatedBudget = request.EstimatedBudget,
            Notes = request.Notes,
            AssignedToUserId = request.AssignedToUserId,
            AssignedToName = request.AssignedToName,
            NextFollowUp = request.NextFollowUp
        };
    }
}
