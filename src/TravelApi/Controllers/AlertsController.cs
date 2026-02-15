using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AlertsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AlertsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetAlerts()
    {
        var today = DateTime.UtcNow.Date;
        var nextWeek = today.AddDays(7);

        // 1. Urgent Unpaid Trips (Departing in next 7 days with debt)
        var urgentTrips = await _context.TravelFiles
            .Where(f => (f.Status == FileStatus.Reserved || f.Status == FileStatus.Operational) &&
                        f.StartDate >= today && 
                        f.StartDate <= nextWeek && 
                        f.Balance > 0)
            .Select(f => new
            {
                f.Id,
                f.FileNumber,
                f.Name,
                f.StartDate,
                f.Balance,
                f.Status,
                PayerName = f.Payer != null ? f.Payer.FullName : "Sin Cliente"
            })
            .OrderBy(f => f.StartDate)
            .ToListAsync();

        // 2. High Debt Suppliers (Suppliers we owe money to)
        // Adjust threshold as needed. Assuming > 100 is worth showing.
        var supplierDebts = await _context.Suppliers
            .Where(s => s.CurrentBalance > 100 && s.IsActive)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.CurrentBalance,
                s.Phone
            })
            .OrderByDescending(s => s.CurrentBalance)
            .Take(10) // Top 10 debtors
            .ToListAsync();

        return Ok(new
        {
            UrgentTrips = urgentTrips,
            SupplierDebts = supplierDebts,
            TotalCount = urgentTrips.Count + supplierDebts.Count
        });
    }
}
