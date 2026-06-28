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
    private readonly IOperatorRefundReadModelService _readModel;
    private readonly IBookingCancellationService _cancellationService;

    public OperatorRefundsController(
        IOperatorRefundService refundService,
        IOperatorRefundReadModelService readModel,
        IBookingCancellationService cancellationService)
    {
        _refundService = refundService;
        _readModel = readModel;
        _cancellationService = cancellationService;
    }

    /// <summary>
    /// ADR-041 TANDA 4 (2026-06-28): bandeja GLOBAL "reembolsos a cobrar" de TODOS los operadores (una fila por
    /// cancelacion+operador esperando o abandonada, con semaforo y estimado por moneda).
    ///
    /// <para><b>Permiso</b>: se gatea con <c>tesoreria.supplier_payments</c> (treasury/AP), NO con
    /// <c>proveedores.view</c>. Motivo de seguridad: esta bandeja cruza TODOS los clientes y operadores y expone el
    /// nombre del cliente que origino cada anulacion; gatearla con <c>proveedores.view</c> (que tiene el rol
    /// Vendedor) seria una fuga horizontal de clientes de otros vendedores (mismo precedente que la bandeja de NC
    /// por revisar). La version POR OPERADOR (dentro de la ficha del proveedor) si usa <c>proveedores.view</c>
    /// porque ya esta acotada al contexto de ese proveedor. Los montos respetan el masking <c>cobranzas.see_cost</c>.</para>
    /// </summary>
    [HttpGet("pending")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<IReadOnlyList<OperatorRefundPendingItemDto>>> GetAllPending(
        CancellationToken cancellationToken)
    {
        return Ok(await _readModel.GetAllPendingRefundsAsync(cancellationToken));
    }

    /// <summary>
    /// ADR-041 TANDA 4 (2026-06-28): REABRE una cancelacion abandonada por el operador para registrar un REEMBOLSO
    /// TARDIO. Transicion controlada (<c>AbandonedByOperator</c> -> <c>AwaitingOperatorRefund</c> con plazo nuevo);
    /// despues el cashier registra el ingreso y lo imputa con el circuito normal (genera saldo a favor del cliente).
    /// La reserva sigue cancelada. Mismo permiso que registrar el reembolso (<c>caja.edit</c>).
    /// </summary>
    [HttpPost("cancellations/{bookingCancellationPublicId:guid}/reopen-for-late-refund")]
    [RequirePermission(Permissions.CajaEdit)]
    public async Task<ActionResult> ReopenForLateRefund(
        Guid bookingCancellationPublicId,
        [FromBody] ReopenForLateRefundRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _cancellationService.ReopenAbandonedForLateRefundAsync(
                bookingCancellationPublicId, request?.Reason ?? string.Empty, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (TravelApi.Domain.Exceptions.BusinessInvariantViolationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // MENOR 2 (review 2026-06-28): doble-POST de reapertura. El perdedor del xmin choca aca; en vez de un
            // 500 devolvemos 409 con un mensaje claro (la otra reapertura ya gano; reintentar es seguro).
            return Conflict(new { message = "La cancelacion fue modificada por otra operacion. Volve a intentar." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
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
