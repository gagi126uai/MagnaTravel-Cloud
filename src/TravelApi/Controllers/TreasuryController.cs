using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
[EnableRateLimiting("fiscal")]
public class TreasuryController : ControllerBase
{
    private readonly ITreasuryService _treasuryService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public TreasuryController(ITreasuryService treasuryService, IEntityReferenceResolver entityReferenceResolver)
    {
        _treasuryService = treasuryService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    // ADR-023 T3.1: el libro de caja / tesoreria es back-office. Antes estos GET eran
    // [Authorize] sin permiso fino -> cualquier usuario autenticado leia el libro completo.
    // Ahora exigen caja.view. El masking de costo (cobranzas.see_cost) sigue dentro de
    // TreasuryService y tapa montos a quien ve Caja pero no tiene ver-costos.
    // Decision del dueno (OPS-PERM-001): el Vendedor NO ve Caja (DefaultVendedor no tiene
    // caja.view; Admin y Colaborador si). No se toca ningun seed de rol.
    [HttpGet("summary")]
    [RequirePermission(Permissions.CajaView)]
    public async Task<ActionResult<TreasurySummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        return Ok(await _treasuryService.GetSummaryAsync(cancellationToken));
    }

    // year/month opcionales para la navegacion por mes (flechas de la pantalla de Caja). Si no vienen, el
    // servicio usa el mes actual (comportamiento historico, lo reusa el dashboard).
    [HttpGet("cash-summary")]
    [RequirePermission(Permissions.CajaView)]
    public async Task<ActionResult<CashSummaryDto>> GetCashSummary(
        [FromQuery] int? year,
        [FromQuery] int? month,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _treasuryService.GetCashSummaryAsync(year, month, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("movements")]
    [RequirePermission(Permissions.CajaView)]
    public async Task<ActionResult<PagedResponse<CashMovementDto>>> GetMovements([FromQuery] TreasuryMovementsQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _treasuryService.GetMovementsAsync(query, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
