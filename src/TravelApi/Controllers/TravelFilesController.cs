using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Contracts.Files;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/travelfiles")]
[Authorize]
public class TravelFilesController : ControllerBase
{
    private readonly AppDbContext _context;

    public TravelFilesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetFiles()
    {
        var files = await _context.TravelFiles
            .Include(f => f.Payer)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();
        return Ok(files);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetFile(int id)
    {
        var file = await _context.TravelFiles
            .Include(f => f.Payer)
            .Include(f => f.Reservations)
                .ThenInclude(r => r.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) return NotFound();
        return Ok(file);
    }

    [HttpPost]
    public async Task<IActionResult> CreateFile(CreateFileRequest request)
    {
        var nextId = await _context.TravelFiles.CountAsync() + 1000;
        var file = new TravelFile
        {
            Name = request.Name,
            FileNumber = $"F-{DateTime.Now.Year}-{nextId}",
            PayerId = request.PayerId,
            StartDate = request.StartDate,
            Description = request.Description,
            Status = FileStatus.Budget
        };
        
        _context.TravelFiles.Add(file);
        await _context.SaveChangesAsync();
        
        return CreatedAtAction(nameof(GetFile), new { id = file.Id }, file);
    }
    [HttpPost("{id}/services")]
    public async Task<IActionResult> AddService(int id, AddServiceRequest request)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) return NotFound("Expediente no encontrado");

        var reservation = new Reservation
        {
            TravelFileId = id,
            ServiceType = request.ServiceType,
            SupplierId = request.SupplierId,
            CustomerId = file.PayerId, // Inherit Payer as Customer (Crucial Fix)
            Description = request.Description ?? request.ServiceType,
            ConfirmationNumber = request.ConfirmationNumber ?? "PENDIENTE",
            Status = "Solicitado",
            DepartureDate = request.DepartureDate.ToUniversalTime(),
            ReturnDate = request.ReturnDate?.ToUniversalTime(),
            SalePrice = request.SalePrice,
            NetCost = request.NetCost,
            // Calculate financial impacts immediately (simple logic for now)
            Commission = request.SalePrice - request.NetCost,
            CreatedAt = DateTime.UtcNow
        };

        // Update File Totals
        file.TotalSale += reservation.SalePrice;
        file.TotalCost += reservation.NetCost;
        file.Balance = file.TotalSale; // Assuming no payments yet

        _context.Reservations.Add(reservation);
        await _context.SaveChangesAsync();

        return Ok(reservation);
    }

    [HttpDelete("services/{serviceId}")]
    public async Task<IActionResult> RemoveService(int serviceId)
    {
        var service = await _context.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == serviceId);
            
        if (service == null) return NotFound();

        // Revert Totals based on what was saved
        if (service.TravelFile != null)
        {
            service.TravelFile.TotalSale -= service.SalePrice;
            service.TravelFile.TotalCost -= service.NetCost;
            service.TravelFile.Balance -= service.SalePrice; // Revert balance impact
        }

        _context.Reservations.Remove(service);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
