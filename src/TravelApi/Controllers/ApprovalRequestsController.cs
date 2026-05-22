using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;

namespace TravelApi.Controllers;

/// <summary>
/// B1.15 Fase B' (2026-05-11): endpoints REST del workflow de aprobaciones.
///
/// Permisos:
///  - <c>approvals.request</c> para POST de creacion y listado de "mis solicitudes".
///  - <c>approvals.review</c> para bandeja pending + approve/reject.
///  - GET por publicId: el solicitante ve la suya, el reviewer ve cualquiera.
/// </summary>
[ApiController]
[Route("api/approvals")]
[Authorize]
public class ApprovalRequestsController : ControllerBase
{
    private readonly IApprovalRequestService _service;

    public ApprovalRequestsController(IApprovalRequestService service)
    {
        _service = service;
    }

    /// <summary>Solicitante crea un ApprovalRequest. Idempotente (Pending existente se reusa).</summary>
    [HttpPost]
    [RequirePermission(Permissions.ApprovalsRequest)]
    public async Task<ActionResult<ApprovalRequestDto>> Create([FromBody] CreateApprovalRequestPayload payload, CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException("Usuario no identificado.");
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            var result = await _service.CreateAsync(payload, userId, userName, ct);
            return Created($"/api/approvals/{result.PublicId}", result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Cooldown post-rechazo.
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = ex.Message });
        }
    }

    /// <summary>Bandeja del reviewer: solicitudes Pending de todos los usuarios.</summary>
    [HttpGet("pending")]
    [RequirePermission(Permissions.ApprovalsReview)]
    public async Task<ActionResult<IReadOnlyList<ApprovalRequestDto>>> GetPending(CancellationToken ct)
        => Ok(await _service.GetPendingAsync(ct));

    /// <summary>Mis solicitudes (todas las del usuario actual, cualquier estado).</summary>
    [HttpGet("my-requests")]
    [RequirePermission(Permissions.ApprovalsRequest)]
    public async Task<ActionResult<IReadOnlyList<ApprovalRequestDto>>> GetMyRequests(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("Usuario no identificado.");
        return Ok(await _service.GetMyRequestsAsync(userId, ct));
    }

    /// <summary>Detalle por publicId. El solicitante ve solo las suyas; el reviewer cualquiera.</summary>
    [HttpGet("{publicId:guid}")]
    public async Task<ActionResult<ApprovalRequestDto>> GetByPublicId(Guid publicId, CancellationToken ct)
    {
        var result = await _service.GetByPublicIdAsync(publicId, ct);
        if (result is null) return NotFound();

        // Ownership manual: solicitante ve la suya; reviewer (approvals.review) ve cualquiera.
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var isOwner = result.RequestedByUserId == userId;
        var isReviewer = User.HasClaim("permission", Permissions.ApprovalsReview)
            || User.IsInRole("Admin");
        if (!isOwner && !isReviewer) return Forbid();

        return Ok(result);
    }

    [HttpPost("{publicId:guid}/approve")]
    [RequirePermission(Permissions.ApprovalsReview)]
    public async Task<ActionResult<ApprovalRequestDto>> Approve(Guid publicId, [FromBody] ResolveApprovalRequestPayload? payload, CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException("Usuario no identificado.");
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            var result = await _service.ApproveAsync(publicId, userId, userName, payload?.Notes, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpPost("{publicId:guid}/reject")]
    [RequirePermission(Permissions.ApprovalsReview)]
    public async Task<ActionResult<ApprovalRequestDto>> Reject(Guid publicId, [FromBody] ResolveApprovalRequestPayload? payload, CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException("Usuario no identificado.");
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            var result = await _service.RejectAsync(publicId, userId, userName, payload?.Notes, ct);
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    /// <summary>
    /// FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): admin fuerza la re-emision
    /// del callback del bridge sobre un <c>PartialCreditNoteApproval</c> que el
    /// job de reconciliacion agoto sus reintentos automaticos.
    ///
    /// <para>Permiso reutilizado: <c>cobranzas.invoice_annul</c> — la accion es
    /// la "ultima instancia" de un flujo de NC parcial y por lo tanto fiscal-sensitive.
    /// No creamos permiso nuevo a proposito (los admins que ya pueden anular
    /// facturas ya tienen autoridad equivalente).</para>
    ///
    /// <para>Pre-requisito: el caller debio crear primero un <c>ApprovalRequest</c>
    /// tipo <c>InvariantOverride</c> scoped a este target approval, y haberlo
    /// hecho aprobar por otro admin (4-eyes). El service valida los detalles
    /// del override exhaustivamente.</para>
    /// </summary>
    [HttpPost("{publicId:guid}/force-bridge-callback")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    public async Task<IActionResult> ForceBridgeCallback(
        Guid publicId,
        [FromBody] ForceBridgeCallbackRequest payload,
        CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new UnauthorizedAccessException("Usuario no identificado.");
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

            await _service.ForceBridgeCallbackAsync(
                targetApprovalPublicId: publicId,
                overrideApprovalPublicId: payload.ApprovalRequestOverridePublicId,
                reason: payload.Reason,
                currentUserId: userId,
                currentUserName: userName,
                ct: ct);

            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (BusinessInvariantViolationException ex)
        {
            // 409 Conflict: el override / target violo una invariante. El mensaje
            // viene en espanol y es seguro mostrarselo al admin (no expone internals).
            return Conflict(new { message = ex.Message, invariantCode = ex.InvariantCode });
        }
        catch (InvalidOperationException ex)
        {
            // Caso raro: bridge no registrado. 500 lo manejaria mejor el handler global,
            // pero devolvemos 409 con el mensaje para consistencia con el resto del controller.
            return Conflict(new { message = ex.Message });
        }
    }
}
