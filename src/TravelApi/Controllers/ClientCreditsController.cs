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
/// FC1.2.4 v3 (2026-05-18): expone el modulo de saldo a favor del cliente
/// (T3 del flujo cancelacion/refund) por HTTP.
///
/// <para>
/// <b>Rutas separadas por aggregate</b>: una lista por BC vive bajo
/// <c>/api/booking-cancellations/{bcPublicId}/credit-entries</c> (nested) y
/// las acciones sobre un entry concreto bajo <c>/api/client-credit-entries</c>.
/// El controller resuelve las dos raices con <c>[HttpGet("ruta-absoluta")]</c>
/// para que sigan en el mismo archivo.
/// </para>
///
/// <para>
/// <b>Diseño de permisos</b>: lectura usa <c>reservas.view</c> + ownership por
/// BC (el cliente afectado es el de la reserva). Withdraw usa
/// <c>cobranzas.edit</c> + ownership por BC porque retirar saldo a favor es
/// una operacion de cobranza/caja (genera <see cref="ManualCashMovement"/>).
/// </para>
///
/// <para>
/// <b>No se expone CreateEntry</b>: la creacion del <c>ClientCreditEntry</c> es
/// privada del service (la dispara <c>OperatorRefundService.AllocateAsync</c>).
/// No tiene endpoint publico — alinea con la interface IClientCreditService.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public class ClientCreditsController : ControllerBase
{
    private readonly IClientCreditService _creditService;

    public ClientCreditsController(IClientCreditService creditService)
    {
        _creditService = creditService;
    }

    /// <summary>
    /// Lista los entries asociados a un BC (puede haber N por N retiros del
    /// operador). El frontend lo usa para mostrar la columna "Saldo a favor"
    /// con timeline de retiros.
    /// </summary>
    [HttpGet("/api/booking-cancellations/{bcPublicId:guid}/credit-entries")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "bcPublicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<List<ClientCreditEntryDto>>> GetEntriesByBc(
        Guid bcPublicId,
        CancellationToken cancellationToken)
    {
        var entries = await _creditService.GetEntriesByBcAsync(bcPublicId, cancellationToken);
        return Ok(entries);
    }

    /// <summary>Lectura por PublicId de un entry concreto con sus withdrawals.</summary>
    [HttpGet("/api/client-credit-entries/{publicId:guid}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.ClientCreditEntry, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ClientCreditEntryDto>> GetEntryByPublicId(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var dto = await _creditService.GetEntryByPublicIdAsync(publicId, cancellationToken);
        if (dto is null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// FC1.2.3 T3: retira saldo de un entry segun el <see cref="WithdrawalKind"/>
    /// elegido. Posibles kinds: KeptAsCredit, PhysicalCash (con tope Ley 25.345),
    /// Transfer, AppliedToNewBooking, ReversedToOperator (requiere approval).
    /// </summary>
    [HttpPost("/api/client-credit-entries/{publicId:guid}/withdrawals")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.ClientCreditEntry, "publicId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult<ClientCreditWithdrawalDto>> Withdraw(
        Guid publicId,
        WithdrawClientCreditRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _creditService.WithdrawAsync(publicId, request, userId, userName, cancellationToken);
            return CreatedAtAction(
                actionName: nameof(GetEntryByPublicId),
                routeValues: new { publicId },
                value: dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            // FC4 (fix I1): el service tira esto cuando se intenta aplicar saldo (AppliedToNewBooking) a una
            // reserva DESTINO que no esta a cargo del usuario actual (y este no ve todas las cobranzas).
            // Mismo mapeo a 403 que PaymentsController.CreatePayment para mantener el contrato uniforme.
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (ApprovalRequiredException ex)
        {
            // ReversedToOperator requiere approval ClientRefundReversal aprobado.
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
            // El service tira ArgumentException para: amount > balance,
            // KeptAsCredit con Amount!=0, kind invalido, etc. 400 Bad Request.
            return BadRequest(new { message = ex.Message });
        }
        // BusinessInvariantViolationException (INV-094 Ley 25.345, INV-085) se
        // mapea a 409 con extensions por GlobalExceptionHandler. No catcheamos.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }
}
