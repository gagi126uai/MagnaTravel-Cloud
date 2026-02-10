using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
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

    public InvoicesController(AppDbContext context, IAfipService afipService)
    {
        _context = context;
        _afipService = afipService;
    }

    [HttpPost]
    public async Task<ActionResult<Invoice>> CreateInvoice([FromBody] CreateInvoiceRequest request)
    {
        try
        {
            var invoiceData = new Invoice
            {
                ImporteTotal = request.Amount
            };

            var invoice = await _afipService.CreateInvoice(request.TravelFileId, invoiceData);
            return Ok(invoice);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("file/{travelFileId}")]
    public async Task<ActionResult<IEnumerable<Invoice>>> GetByTravelFile(int travelFileId)
    {
        var invoices = await _context.Invoices
            .Where(i => i.TravelFileId == travelFileId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
        return Ok(invoices);
    }
}

public class CreateInvoiceRequest
{
    public int TravelFileId { get; set; }
    public decimal Amount { get; set; }
}
