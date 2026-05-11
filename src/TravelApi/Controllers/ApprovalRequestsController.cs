using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

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
}
