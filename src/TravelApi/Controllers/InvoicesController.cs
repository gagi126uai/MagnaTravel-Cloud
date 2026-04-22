using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
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

    [HttpGet("summary")]
    public async Task<ActionResult<InvoicingSummaryDto>> GetSummary(CancellationToken ct)
    {
        return Ok(await _invoiceService.GetInvoicingSummaryAsync(ct));
    }

    [HttpGet("worklist")]
    public async Task<ActionResult<PagedResponse<InvoicingWorkItemDto>>> GetWorklist([FromQuery] InvoicingWorklistQuery query, CancellationToken ct)
    {
        return Ok(await _invoiceService.GetInvoicingWorklistAsync(query, ct));
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<InvoiceListDto>>> GetInvoices([FromQuery] InvoicesListQuery query, CancellationToken ct)
    {
        var invoices = await _invoiceService.GetAllAsync(query, ct);
        return Ok(invoices);
    }

    [HttpPost]
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
    public async Task<IActionResult> RetryInvoice(string publicIdOrLegacyId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Invoice>(publicIdOrLegacyId, ct);
            var success = await _invoiceService.RetryAsync(id, ct);
            if (!success) return NotFound();

            return Accepted(new { message = "Reintento encolado." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "La factura no pudo reintentarse." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpGet("reserva/{publicIdOrLegacyId}")]
    public async Task<ActionResult<IEnumerable<InvoiceListDto>>> GetByReservaId(string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var invoices = await _invoiceService.GetByReservaIdAsync(reservaId, ct);
        return Ok(invoices);
    }

    [HttpGet("{publicIdOrLegacyId}/pdf")]
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

    [HttpPost("{publicIdOrLegacyId}/annul")]
    public async Task<IActionResult> AnnulInvoice(string publicIdOrLegacyId, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Invoice>(publicIdOrLegacyId, ct);
        await _invoiceService.EnqueueAnnulmentAsync(id, userId, ct);
        return Accepted(new { Message = "La anulacion se esta procesando en segundo plano. Te avisaremos cuando termine." });
    }
}
