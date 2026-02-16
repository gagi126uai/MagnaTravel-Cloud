using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.DTOs;
using TravelApi.Models;
using TravelApi.Services;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/invoices")]
[Authorize]
public class InvoicesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAfipService _afipService;
    private readonly IInvoicePdfService _pdfService;
    private readonly IMapper _mapper;

    public InvoicesController(AppDbContext context, IAfipService afipService, IInvoicePdfService pdfService, IMapper mapper)
    {
        _context = context;
        _afipService = afipService;
        _pdfService = pdfService;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<InvoiceDto>>> GetInvoices()
    {
        var invoices = await _context.Invoices
            .OrderByDescending(i => i.CreatedAt)
            .ProjectTo<InvoiceDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
        return Ok(invoices);
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> CreateInvoice([FromBody] CreateInvoiceRequest request)
    {
        try
        {
            var invoice = await _afipService.CreateInvoice(request.TravelFileId, request);
            return Ok(_mapper.Map<InvoiceDto>(invoice));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("file/{travelFileId}")]
    public async Task<ActionResult<IEnumerable<InvoiceDto>>> GetByTravelFile(int travelFileId)
    {
        var invoices = await _context.Invoices
            .Where(i => i.TravelFileId == travelFileId)
            .OrderByDescending(i => i.CreatedAt)
            .ProjectTo<InvoiceDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
        return Ok(invoices);
    }

    [HttpGet("{id}/pdf")]
    public async Task<IActionResult> GetInvoicePdf(int id)
    {
        var invoice = await _context.Invoices
            .Include(i => i.TravelFile)
            .ThenInclude(t => t.Payer)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (invoice == null) return NotFound();

        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null) return BadRequest("Configuración de AFIP no encontrada");

        var agencySettings = await _context.AgencySettings.FirstOrDefaultAsync() ?? new AgencySettings();

        try
        {
            var pdfBytes = _pdfService.GenerateInvoicePdf(invoice, invoice.TravelFile, settings, agencySettings);
            return File(pdfBytes, "application/pdf", $"Factura-{invoice.TipoComprobante}-{invoice.NumeroComprobante}.pdf");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating PDF: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return StatusCode(500, new { message = $"Error generando PDF: {ex.Message}" });
        }
    }
}


