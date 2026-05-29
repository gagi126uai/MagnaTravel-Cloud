using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

namespace TravelApi.Controllers;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): bandeja de reconciliacion de NC parciales con
/// recibos vivos. Endpoints finos que delegan en
/// <see cref="IPartialCreditNoteReconciliationService"/>.
///
/// <para><b>Permiso</b>: <c>approvals.review</c> (back-office: Admin/Colaborador, NO
/// Vendedor — ADR-010 B3). Es el mismo permiso de la inbox de aprobaciones porque es el
/// mismo tipo de gente: revision back-office.</para>
///
/// <para><b>Anular un recibo NO se hace aca</b>: se reusa el endpoint existente
/// <c>POST /api/payments/{paymentPublicId}/receipt/void</c>. Ese endpoint pide
/// <c>cobranzas.receipt_void</c> y, segun el rol, puede disparar un WORKFLOW de
/// aprobacion en vez de anular directo (devuelve 409 con requiresApproval). El backend
/// de esta bandeja no cambia ese comportamiento.</para>
/// </summary>
[ApiController]
[Route("api/credit-note-reconciliation")]
[Authorize]
public class PartialCreditNoteReconciliationController : ControllerBase
{
    private readonly IPartialCreditNoteReconciliationService _service;

    public PartialCreditNoteReconciliationController(IPartialCreditNoteReconciliationService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lista los casos de la bandeja, paginado. Filtros: status (pending/resolved/all)
    /// + year/month (filtro mensual opcional, estilo MonthNavigator).
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.ApprovalsReview)]
    public async Task<ActionResult<PagedResponse<PartialCreditNoteReconciliationDto>>> List(
        [FromQuery] PartialCreditNoteReconciliationListQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.ListAsync(query, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Cierra manualmente un caso (lo marca Resolved). Aplica 4-ojos + bypass de admin
    /// unico (G5) y exige notas si se cierra con recibos vivos.
    /// </summary>
    [HttpPost("{publicId:guid}/resolve")]
    [RequirePermission(Permissions.ApprovalsReview)]
    public async Task<IActionResult> Resolve(
        Guid publicId,
        [FromBody] ResolvePartialCreditNoteReconciliationRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
            var currentUserName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

            var result = await _service.ResolveAsync(
                publicId,
                request ?? new ResolvePartialCreditNoteReconciliationRequest(),
                currentUserId,
                currentUserName,
                cancellationToken);

            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otro encargado cerro el mismo caso en paralelo (xmin). 409 para que el
            // frontend refresque y vea el estado real.
            return Conflict(new { message = "Otro usuario modifico este caso al mismo tiempo. Refresca y volve a intentar." });
        }
        catch (InvalidOperationException ex)
        {
            // Caso ya resuelto, 4-ojos no cumplido, o notas faltantes con recibos vivos.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }
}
