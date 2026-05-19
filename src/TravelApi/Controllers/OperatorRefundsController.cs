using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

namespace TravelApi.Controllers;

/// <summary>
/// FC1.2.4 v3 (2026-05-18): expone el modulo de ingresos de operador (T2 del
/// flujo cancelacion/refund) por HTTP. Es back-office puro — solo cashier o
/// admin operan estos endpoints; no hay vista cliente.
///
/// <para>
/// <b>Diseño de permisos</b>: <c>caja.edit</c> para registrar el ingreso fisico
/// (es un <see cref="ManualCashMovement"/> Income) y para imputarlo /
/// anularlo / reasociarlo. El service hace el gating fino (feature flag,
/// concurrencia xmin, matriz fiscal); el controller solo gate por permiso.
/// </para>
///
/// <para>
/// <b>Ownership intencionalmente ausente</b>: a diferencia de los pagos del
/// cliente (que son por reserva = ownership Reserva), un ingreso de operador
/// cubre N reservas. No tiene sentido scope-por-responsable. El permiso
/// <c>caja.edit</c> ya restringe a quien puede operarlo (back-office), y
/// <c>OwnedEntity</c> no incluye <c>OperatorRefundReceived</c>/<c>Allocation</c>
/// por decision explicita (ver comentario en IOwnershipResolver.cs).
/// </para>
/// </summary>
[ApiController]
[Route("api/operator-refunds")]
[Authorize]
public class OperatorRefundsController : ControllerBase
{
    private readonly IOperatorRefundService _refundService;

    public OperatorRefundsController(IOperatorRefundService refundService)
    {
        _refundService = refundService;
    }

    /// <summary>
    /// T2 inicial: registrar el ingreso fisico que un operador envia. El
    /// cashier carga monto + metodo + fecha. Crea <c>OperatorRefundReceived</c>
    /// + <c>ManualCashMovement</c> Income asociado.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.CajaEdit)]
    public async Task<ActionResult<OperatorRefundReceivedDto>> RecordReceived(
        RecordOperatorRefundRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _refundService.RecordReceivedAsync(request, userId, userName, cancellationToken);
            return CreatedAtAction(
                actionName: nameof(GetByPublicId),
                routeValues: new { publicId = dto.PublicId },
                value: dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>Lectura por PublicId. Necesaria para que el frontend pueda
    /// refetch despues de allocates y para el redirect del Created.</summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(Permissions.CajaView)]
    public async Task<ActionResult<OperatorRefundReceivedDto>> GetByPublicId(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var dto = await _refundService.GetByPublicIdAsync(publicId, cancellationToken);
        if (dto is null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// Imputa parte del refund contra UN BookingCancellation. Genera la
    /// <c>OperatorRefundAllocation</c> + sus <c>DeductionLine</c>s + el
    /// <c>ClientCreditEntry</c> + side-effects en una sola tx.
    /// </summary>
    [HttpPost("{publicId:guid}/allocations")]
    [RequirePermission(Permissions.CajaEdit)]
    public async Task<ActionResult<OperatorRefundAllocationDto>> Allocate(
        Guid publicId,
        AllocateRefundRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _refundService.AllocateAsync(publicId, request, userId, userName, cancellationToken);
            return CreatedAtAction(
                actionName: nameof(GetByPublicId),
                routeValues: new { publicId },
                value: dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Anula una allocation (soft-void: la fila se preserva con IsVoided=true).
    /// Libera el cap del refund. Rechaza si el ClientCreditEntry asociado tiene
    /// withdrawals consumidos.
    /// </summary>
    [HttpDelete("allocations/{allocationPublicId:guid}")]
    [RequirePermission(Permissions.CajaEdit)]
    public async Task<ActionResult<OperatorRefundAllocationDto>> VoidAllocation(
        Guid allocationPublicId,
        [FromBody] VoidAllocationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _refundService.VoidAllocationAsync(allocationPublicId, request, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Mueve una allocation entre BCs en una sola tx atomica. Caso: contador
    /// detecta imputacion incorrecta. El service rechaza si hay withdrawals
    /// consumidos contra la allocation vieja.
    /// </summary>
    [HttpPatch("allocations/{allocationPublicId:guid}/reassociate")]
    [RequirePermission(Permissions.CajaEdit)]
    public async Task<ActionResult<OperatorRefundAllocationDto>> Reassociate(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _refundService.ReassociateAllocationAsync(allocationPublicId, request, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            // Si en el futuro el service exige approval MisassociationReversal,
            // ya tenemos el handler listo (mismo contrato 409 que InvoicesController).
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }
}
