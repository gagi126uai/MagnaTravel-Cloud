using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
public class TreasuryController : ControllerBase
{
    private readonly ITreasuryService _treasuryService;

    public TreasuryController(ITreasuryService treasuryService)
    {
        _treasuryService = treasuryService;
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
    public async Task<ActionResult<IReadOnlyList<CashMovementDto>>> GetMovements(CancellationToken cancellationToken)
    {
        return Ok(await _treasuryService.GetMovementsAsync(cancellationToken));
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
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("manual-movements/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ManualCashMovementDto>> UpdateManualMovement(
        int id,
        [FromBody] UpsertManualCashMovementRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _treasuryService.UpdateManualMovementAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("manual-movements/{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteManualMovement(int id, CancellationToken cancellationToken)
    {
        try
        {
            await _treasuryService.DeleteManualMovementAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
