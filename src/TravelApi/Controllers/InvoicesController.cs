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
    [HttpPost("{id}/annul")]
    public async Task<ActionResult<InvoiceDto>> AnnulInvoice(int id)
    {
        try
        {
            var original = await _context.Invoices
                .Include(i => i.Items)
                .Include(i => i.Tributes)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (original == null) return NotFound("Comprobante no encontrado");
            // Determine if we need a Credit Note or Debit Note
            bool isCreditNote = false;
            bool isDebitNote = false;

            // If Original is Invoice (1, 6, 11) or Debit Note (2, 7, 12) -> Credit Note to cancel
            if (new[] { 1, 6, 11, 2, 7, 12 }.Contains(original.TipoComprobante))
            {
                isCreditNote = true;
            }
            // If Original is Credit Note (3, 8, 13) -> Debit Note to cancel (re-debit)
            else if (new[] { 3, 8, 13 }.Contains(original.TipoComprobante))
            {
                isDebitNote = true;
            }
            else 
            {
                return BadRequest($"Tipo de comprobante no soportado para anulación: {original.TipoComprobante}");
            }

            var request = new CreateInvoiceRequest
            {
                TravelFileId = original.TravelFileId ?? 0,
                OriginalInvoiceId = original.Id,
                IsCreditNote = isCreditNote,
                IsDebitNote = isDebitNote,
                Items = original.Items.Select(i => new InvoiceItemDto 
                { 
                    Description = i.Description,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    Total = i.Total,
                    AlicuotaIvaId = i.AlicuotaIvaId
                }).ToList(),
                Tributes = original.Tributes.Select(t => new InvoiceTributeDto
                {
                    TributeId = t.TributeId,
                    Description = t.Description,
                    BaseImponible = t.BaseImponible,
                    Alicuota = t.Alicuota,
                    Importe = t.Importe
                }).ToList()
            };

            // Fallback for Legacy Invoices (No local items)
            if (!request.Items.Any())
            {
                 // Try to fetch from AFIP
                 var details = await _afipService.GetVoucherDetails(original.TipoComprobante, original.PuntoDeVenta, original.NumeroComprobante);
                 
                 if (details != null)
                 {
                     // Reconstruct Items from AFIP VAT Details
                     foreach (var vat in details.VatDetails)
                     {
                         request.Items.Add(new InvoiceItemDto
                         {
                             Description = $"Anulación Comp. {original.NumeroComprobante}",
                             Quantity = 1,
                             UnitPrice = vat.BaseImp, // Net Amount 
                             Total = vat.BaseImp,
                             AlicuotaIvaId = vat.Id
                         });
                     }

                     // If no VAT details (e.g. C invoices), use Total/Net
                     if (!request.Items.Any() && details.ImporteTotal > 0)
                     {
                          request.Items.Add(new InvoiceItemDto
                          {
                              Description = $"Anulación Comp. {original.NumeroComprobante}",
                              Quantity = 1,
                              UnitPrice = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              Total = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              AlicuotaIvaId = 3 // 0% default or 6/11 logic?
                              // For C (11), VAT is 0 usually.
                          });
                     }

                     // Reconstruct Tributes
                     foreach (var trib in details.TributeDetails)
                     {
                         request.Tributes.Add(new InvoiceTributeDto
                         {
                             TributeId = trib.Id,
                             Description = trib.Desc,
                             BaseImponible = trib.BaseImp,
                             Alicuota = trib.Alic,
                             Importe = trib.Importe
                         });
                     }
                 }
                 else 
                 {
                     // LAST RESORT: Backup failed (e.g. Homologation reset or timeout). 
                     // Use Local Header Totals to create a generic item.
                     // This prevents 400 Bad Request (Empty Items).
                     
                     decimal net = original.ImporteNeto > 0 ? original.ImporteNeto : original.ImporteTotal; // Fallback
                     decimal iva = original.ImporteIva;
                     int ivaId = 3; // 0%

                     if (iva > 0)
                     {
                         // Try to guess rate or default to 21% (Id 5)
                         // If we have Net and IVA, we can estimate. 
                         // But usually legacy data might be messy. Let's assume 21% if we have IVA.
                         ivaId = 5; 
                         
                         // Re-calculate Net if needed to match Total
                         if (original.ImporteNeto == 0)
                         {
                             net = original.ImporteTotal - iva;
                         }
                     }

                     request.Items.Add(new InvoiceItemDto
                     {
                         Description = $"Anulación Comp. {original.NumeroComprobante} (Respaldo Local)",
                         Quantity = 1,
                         UnitPrice = net,
                         Total = net,
                         AlicuotaIvaId = ivaId
                     });
                 }
            }

            var invoice = await _afipService.CreateInvoice(request.TravelFileId, request);
            return Ok(_mapper.Map<InvoiceDto>(invoice));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}


