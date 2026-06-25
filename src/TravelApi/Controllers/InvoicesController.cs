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
        catch (InvalidOperationException ex)
        {
            // 2026-05-11 (fiscal critico): el service tira InvalidOperationException con
            // mensaje claro al operador para casos de estado/regla — reserva no encontrada,
            // factura PENDING en curso, override sin motivo, AFIP con deuda. 409 Conflict
            // expresa "estado actual incompatible con la operacion" y es consistente con
            // RetryInvoice y AnnulInvoice. El mensaje se preserva para la UI (vs el
            // generico anterior que escondia la causa real al operador).
            return Conflict(new { message = ex.Message });
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

    // Hallazgo auditoria ERP #9 (2026-06-13): el modal de creacion de factura pide aca los items
    // sugeridos armados desde los servicios CONFIRMADOS de la reserva (uno por servicio, por moneda)
    // y prerrellena las lineas reales en vez del unico item generico "Servicios Turisticos".
    //
    // Gateado con CobranzasInvoice (el mismo permiso que CreateInvoice: quien puede facturar puede pedir
    // la sugerencia) + ownership por reserva con bypass CobranzasViewAll (un vendedor solo arma facturas
    // sobre SUS reservas). Solo lectura: no crea ni muta nada.
    [HttpGet("reserva/{publicIdOrLegacyId}/suggested-items")]
    [RequirePermission(Permissions.CobranzasInvoice)]
    [RequireOwnership(OwnedEntity.Reserva, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult<InvoiceSuggestedItemsResponse>> GetSuggestedItems(string publicIdOrLegacyId, CancellationToken ct)
    {
        try
        {
            var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
            var suggestion = await _invoiceService.GetSuggestedItemsAsync(reservaId, ct);
            return Ok(suggestion);
        }
        catch (InvalidOperationException ex)
        {
            // Reserva no encontrada -> el service tira InvalidOperationException con mensaje claro.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
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

    /// <summary>
    /// H2 (2026-06-24): estado fiscal CLARO de las facturas de una reserva, para que el front haga POLL
    /// despues de emitir. La emision es ASINCRONA (POST /invoices encola; un job pide el CAE en segundo
    /// plano), asi que el front consulta este endpoint hasta saber como termino: en proceso, emitida
    /// (con numero + CAE + vencimiento) o rechazada (con el motivo de ARCA legible).
    ///
    /// SOLO LECTURA: no dispara ninguna emision. Mismo permiso (cobranzas.view) y ownership (bypass
    /// cobranzas.view_all) que el resto de los GET de facturas — un vendedor solo ve SUS reservas.
    /// </summary>
    [HttpGet("reserva/{publicIdOrLegacyId}/fiscal-status")]
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Reserva, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult<IEnumerable<InvoiceFiscalStatusDto>>> GetFiscalStatusByReservaId(string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var statuses = await _invoiceService.GetFiscalStatusByReservaIdAsync(reservaId, ct);
        return Ok(statuses);
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
