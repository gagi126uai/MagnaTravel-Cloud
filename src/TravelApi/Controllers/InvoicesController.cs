using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/invoices")]
[Authorize]
[EnableRateLimiting("fiscal")]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public InvoicesController(IInvoiceService invoiceService, IEntityReferenceResolver entityReferenceResolver)
    {
        _invoiceService = invoiceService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    // B1.15 Fase 2a (FIX 6): listings exigen cobranzas.view + filter mine via service.
    [HttpGet("summary")]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<InvoicingSummaryDto>> GetSummary(CancellationToken ct)
    {
        return Ok(await _invoiceService.GetInvoicingSummaryAsync(ct));
    }

    [HttpGet("worklist")]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<InvoicingWorkItemDto>>> GetWorklist([FromQuery] InvoicingWorklistQuery query, CancellationToken ct)
    {
        return Ok(await _invoiceService.GetInvoicingWorklistAsync(query, ct));
    }

    [HttpGet]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<InvoiceListDto>>> GetInvoices([FromQuery] InvoicesListQuery query, CancellationToken ct)
    {
        var invoices = await _invoiceService.GetAllAsync(query, ct);
        return Ok(invoices);
    }

    [HttpPost]
    [RequirePermission(Permissions.CobranzasInvoice)]
    public async Task<ActionResult<InvoiceDto>> CreateInvoice([FromBody] CreateInvoiceRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userName = User.FindFirst(ClaimTypes.Name)?.Value;
            var invoice = await _invoiceService.CreateAsync(request, userId, userName, ct);
            return Accepted(invoice);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo generar la factura." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar la factura.");
        }
    }

    [HttpPost("{publicIdOrLegacyId}/retry")]
    [RequirePermission(Permissions.CobranzasInvoice)]
    [RequireOwnership(OwnedEntity.Invoice, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<IActionResult> RetryInvoice(string publicIdOrLegacyId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Invoice>(publicIdOrLegacyId, ct);
            var success = await _invoiceService.RetryAsync(id, ct);
            if (!success) return NotFound();

            return Accepted(new { message = "Reintento encolado." });
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-02): el service rechaza con motivo claro cuando
            // hay anulacion Pending/Succeeded o cuando la factura ya esta aprobada.
            // 409 Conflict expresa "estado actual incompatible con la operacion".
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpGet("reserva/{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Reserva, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult<IEnumerable<InvoiceListDto>>> GetByReservaId(string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var invoices = await _invoiceService.GetByReservaIdAsync(reservaId, ct);
        return Ok(invoices);
    }

    [HttpGet("{publicIdOrLegacyId}/pdf")]
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Invoice, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<IActionResult> GetInvoicePdf(string publicIdOrLegacyId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Invoice>(publicIdOrLegacyId, ct);
            var pdfBytes = await _invoiceService.GetPdfAsync(id, ct);
            return File(pdfBytes, "application/pdf", $"Factura-{publicIdOrLegacyId}.pdf");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo generar el PDF de la factura." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el PDF de la factura.");
        }
    }

    /// <summary>
    /// B1.15 Fase 2a (FIX 6 — fiscal critico): anular una factura emite NC en AFIP.
    /// Requiere <c>cobranzas.invoice_annul</c> (back-office, no Vendedor) y ownership
    /// con bypass via <c>cobranzas.view_all</c>. Persistimos AnnulledByUser*, AnnulledAt
    /// y AnnulmentReason para auditoria fiscal del flujo CAE -> NC.
    /// </summary>
    [HttpPost("{publicIdOrLegacyId}/annul")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    [RequireOwnership(OwnedEntity.Invoice, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<IActionResult> AnnulInvoice(string publicIdOrLegacyId, [FromBody] AnnulInvoiceRequest? request, CancellationToken ct)
    {
        // B1.15 Fase 2a (review final): idempotencia. EnqueueAnnulmentAsync rechaza
        // re-encolar si la factura esta Pending o Succeeded (evita doble NC en AFIP).
        // B1.15 Fase D (2026-05-11): tambien puede tirar ApprovalRequiredException si
        // el setting on + caller no Admin + no hay aprobacion vigente; devolvemos 409
        // con body que indica al frontend que abra RequestApprovalModal.
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
            var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            var reason = request?.Reason?.Trim();
            var requesterIsAdmin = User.IsInRole("Admin");
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Invoice>(publicIdOrLegacyId, ct);
            await _invoiceService.EnqueueAnnulmentAsync(id, userId, userName, reason, requesterIsAdmin, ct);
            return Accepted(new { Message = "La anulacion se esta procesando en segundo plano. Te avisaremos cuando termine." });
        }
        catch (Application.Exceptions.ApprovalRequiredException ex)
        {
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa del Administrador o Colaborador.",
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
    }
}

/// <summary>
/// B1.15 Fase 2a (FIX 6): payload opcional para anular factura. <c>Reason</c> queda
/// persistido en <c>Invoice.AnnulmentReason</c> para auditoria fiscal.
/// </summary>
public record AnnulInvoiceRequest(string? Reason);
