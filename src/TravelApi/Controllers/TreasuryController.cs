using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
public class TreasuryController : ControllerBase
{
    private readonly ITreasuryService _treasuryService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public TreasuryController(ITreasuryService treasuryService, EntityReferenceResolver entityReferenceResolver)
    {
        _treasuryService = treasuryService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<TreasurySummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        return Ok(await _treasuryService.GetSummaryAsync(cancellationToken));
    }

    [HttpGet("cash-summary")]
    public async Task<ActionResult<CashSummaryDto>> GetCashSummary(CancellationToken cancellationToken)
    {
        return Ok(await _treasuryService.GetCashSummaryAsync(cancellationToken));
    }

    [HttpGet("movements")]
    public async Task<ActionResult<PagedResponse<CashMovementDto>>> GetMovements([FromQuery] TreasuryMovementsQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _treasuryService.GetMovementsAsync(query, cancellationToken));
    }

    [HttpPost("manual-movements")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ManualCashMovementDto>> CreateManualMovement(
        [FromBody] UpsertManualCashMovementRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdBy = User.FindFirst(ClaimTypes.Name)?.Value ?? "Admin";
            var movement = await _treasuryService.CreateManualMovementAsync(request, createdBy, cancellationToken);
            return Ok(movement);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo registrar el movimiento manual." });
        }
    }

    [HttpPut("manual-movements/{publicIdOrLegacyId}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ManualCashMovementDto>> UpdateManualMovement(
        string publicIdOrLegacyId,
        [FromBody] UpsertManualCashMovementRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<ManualCashMovement>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _treasuryService.UpdateManualMovementAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el movimiento manual." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo actualizar el movimiento manual." });
        }
    }

    [HttpDelete("manual-movements/{publicIdOrLegacyId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteManualMovement(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<ManualCashMovement>(publicIdOrLegacyId, cancellationToken);
            await _treasuryService.DeleteManualMovementAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
