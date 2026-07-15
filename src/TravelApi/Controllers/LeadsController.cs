using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly IReservaService _reservaService;

    public LeadsController(ILeadService leadService, IReservaService reservaService)
    {
        _leadService = leadService;
        _reservaService = reservaService;
    }

    // ADR-023 T3.3: los permisos crm.view/crm.edit ya existian y se sembraban, pero ningun
    // endpoint los verificaba -> cualquier autenticado leia y editaba el pipeline de Leads.
    // Ahora lecturas exigen crm.view y escrituras crm.edit. Decision del dueno (OPS-PERM-002):
    // Leads = Vendedor y Admin. El Colaborador NO recibe crm.* y queda fuera de Leads a
    // proposito. No se toca ningun seed de rol.
    [HttpGet]
    [RequirePermission(Permissions.CrmView)]
    public async Task<ActionResult<PagedResponse<LeadSummaryDto>>> GetAll([FromQuery] LeadListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetAllAsync(query, cancellationToken));
    }

    [HttpGet("pipeline")]
    [RequirePermission(Permissions.CrmView)]
    public async Task<ActionResult> GetPipeline(CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetPipelineAsync(cancellationToken));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.CrmView)]
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
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult<LeadDetailDto>> Create([FromBody] LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        var created = await _leadService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { publicIdOrLegacyId = created.PublicId }, created);
    }

    [HttpPut("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult<LeadDetailDto>> Update(string publicIdOrLegacyId, [FromBody] LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.UpdateAsync(publicIdOrLegacyId, request, cancellationToken));
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _leadService.DeleteAsync(publicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{publicIdOrLegacyId}/status")]
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult<LeadDetailDto>> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/activities")]
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult<LeadActivityDto>> AddActivity(string publicIdOrLegacyId, [FromBody] LeadActivityUpsertRequest request, CancellationToken cancellationToken)
    {
        var createdBy = request.CreatedBy ?? User.Identity?.Name ?? "Sistema";
        return Ok(await _leadService.AddActivityAsync(publicIdOrLegacyId, request, createdBy, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/convert")]
    [RequirePermission(Permissions.CrmEdit)]
    public async Task<ActionResult<LeadConversionResultDto>> ConvertToCustomer(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.ConvertToCustomerAsync(publicIdOrLegacyId, cancellationToken));
    }

    // ADR-023 T3.3 (m4): aunque hoy devuelve 410 Gone, se gatea igual con crm.edit por
    // coherencia (es una escritura sobre un lead) y para que no quede un hueco si se reactiva.
    [HttpPost("{publicIdOrLegacyId}/quote-draft")]
    [RequirePermission(Permissions.CrmEdit)]
    [Obsolete("Cotizaciones discontinuadas. Crear Reserva en estado Presupuesto desde el modulo de Reservas.")]
    public ActionResult<QuoteDraftResultDto> CreateQuoteDraft(string publicIdOrLegacyId)
    {
        _ = publicIdOrLegacyId;
        return StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Las cotizaciones quedaron discontinuadas. Crear una Reserva en estado Presupuesto desde el modulo de Reservas."
        });
    }

    [HttpPost("{publicIdOrLegacyId}/budget")]
    [RequirePermission(Permissions.CrmEdit)]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Lead, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ReservaDto>> CreateBudget(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var createdByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var reserva = await _reservaService.CreateReservaAsync(new CreateReservaRequest
        {
            SourceLeadPublicId = publicIdOrLegacyId
        }, createdByUserId, cancellationToken);
        return Ok(reserva);
    }

    [HttpGet("{publicIdOrLegacyId}/journey")]
    [RequirePermission(Permissions.CrmView)]
    public async Task<ActionResult<LeadJourneyDto>> GetJourney(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        return Ok(await _leadService.GetJourneyAsync(publicIdOrLegacyId, cancellationToken));
    }
}
