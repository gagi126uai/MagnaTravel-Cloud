using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ILeadService _leadService;

    public LeadsController(ILeadService leadService)
    {
        _leadService = leadService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<LeadSummaryDto>>> GetAll([FromQuery] LeadListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetAllAsync(query, cancellationToken));
    }

    [HttpGet("pipeline")]
    public async Task<ActionResult> GetPipeline(CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetPipelineAsync(cancellationToken));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<LeadDetailDto>> GetById(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var lead = await _leadService.GetByIdAsync(publicIdOrLegacyId, cancellationToken);
        if (lead == null)
        {
            return NotFound();
        }

        return Ok(lead);
    }

    [HttpPost]
    public async Task<ActionResult<LeadDetailDto>> Create([FromBody] LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        var created = await _leadService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { publicIdOrLegacyId = created.PublicId }, created);
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<LeadDetailDto>> Update(string publicIdOrLegacyId, [FromBody] LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.UpdateAsync(publicIdOrLegacyId, request, cancellationToken));
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _leadService.DeleteAsync(publicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{publicIdOrLegacyId}/status")]
    public async Task<ActionResult<LeadDetailDto>> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/activities")]
    public async Task<ActionResult<LeadActivityDto>> AddActivity(string publicIdOrLegacyId, [FromBody] LeadActivityUpsertRequest request, CancellationToken cancellationToken)
    {
        var createdBy = request.CreatedBy ?? User.Identity?.Name ?? "Sistema";
        return Ok(await _leadService.AddActivityAsync(publicIdOrLegacyId, request, createdBy, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/convert")]
    public async Task<ActionResult<LeadConversionResultDto>> ConvertToCustomer(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.ConvertToCustomerAsync(publicIdOrLegacyId, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/quote-draft")]
    [Obsolete("Cotizaciones discontinuadas. Crear Reserva en estado Presupuesto desde el modulo de Reservas.")]
    public ActionResult<QuoteDraftResultDto> CreateQuoteDraft(string publicIdOrLegacyId)
    {
        _ = publicIdOrLegacyId;
        return StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Las cotizaciones quedaron discontinuadas. Crear una Reserva en estado Presupuesto desde el modulo de Reservas."
        });
    }

    [HttpGet("{publicIdOrLegacyId}/journey")]
    public async Task<ActionResult<LeadJourneyDto>> GetJourney(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetJourneyAsync(publicIdOrLegacyId, cancellationToken));
    }
}
