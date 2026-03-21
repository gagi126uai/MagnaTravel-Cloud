using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using System.Security.Claims;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/invoices")]
[Authorize]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService _invoiceService;

    public InvoicesController(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<InvoiceDto>>> GetInvoices(CancellationToken ct)
    {
        var invoices = await _invoiceService.GetAllAsync(ct);
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
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/retry")]
    public async Task<IActionResult> RetryInvoice(int id, CancellationToken ct)
    {
        try
        {
            var success = await _invoiceService.RetryAsync(id, ct);
            if (!success) return NotFound();
            
            return Accepted(new { message = "Reintento encolado." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("reserva/{reservaId}")]
    public async Task<ActionResult<IEnumerable<InvoiceDto>>> GetByReservaId(int reservaId, CancellationToken ct)
    {
        var invoices = await _invoiceService.GetByReservaIdAsync(reservaId, ct);
        return Ok(invoices);
    }

    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GetInvoicePdf(int id, CancellationToken ct)
    {
        try
        {
            var pdfBytes = await _invoiceService.GetPdfAsync(id, ct);
            // We'd need the invoice details to name the file properly, but for now we use a generic name or fetch it in the service if needed.
            return File(pdfBytes, "application/pdf", $"Factura-{id}.pdf");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Error generando PDF: {ex.Message}" });
        }
    }

    [HttpPost("{id}/annul")]
    public async Task<IActionResult> AnnulInvoice(int id, CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        await _invoiceService.EnqueueAnnulmentAsync(id, userId, ct);
        return Accepted(new { Message = "La anulación se está procesando en segundo plano. Te avisaremos cuando termine." });
    }
}

