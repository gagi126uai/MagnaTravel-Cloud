using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/transfers")]
[Authorize]
public class TransferBookingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TransferBookingsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var transfers = await _db.TransferBookings
            .Where(t => t.TravelFileId == fileId)
            .Include(t => t.Supplier)
            .OrderBy(t => t.PickupDateTime)
            .Select(t => new {
                t.Id, t.PickupLocation, t.DropoffLocation, t.PickupDateTime,
                t.FlightNumber, t.VehicleType, t.Passengers,
                t.IsRoundTrip, t.ReturnDateTime, t.ConfirmationNumber, t.Status,
                t.NetCost, t.SalePrice, t.Commission, t.Notes,
                SupplierId = t.SupplierId,
                SupplierName = t.Supplier != null ? t.Supplier.Name : null
            })
            .ToListAsync(ct);
        return Ok(transfers);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateTransferRequest req, CancellationToken ct)
    {
        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file == null) return NotFound("File no encontrado");

        var transfer = new TransferBooking
        {
            TravelFileId = fileId,
            SupplierId = req.SupplierId,
            PickupLocation = req.PickupLocation,
            DropoffLocation = req.DropoffLocation,
            PickupDateTime = req.PickupDateTime,
            FlightNumber = req.FlightNumber,
            VehicleType = req.VehicleType,
            Passengers = req.Passengers,
            IsRoundTrip = req.IsRoundTrip,
            ReturnDateTime = req.ReturnDateTime,
            NetCost = req.NetCost,
            SalePrice = req.SalePrice,
            Commission = req.Commission,
            Notes = req.Notes
        };

        _db.TransferBookings.Add(transfer);
        
        file.TotalCost += transfer.NetCost;
        file.TotalSale += transfer.SalePrice;
        file.Balance = file.TotalSale - file.TotalCost;
        
        await _db.SaveChangesAsync(ct);
        return Ok(transfer);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateTransferRequest req, CancellationToken ct)
    {
        var transfer = await _db.TransferBookings.FindAsync(new object[] { id }, ct);
        if (transfer == null || transfer.TravelFileId != fileId) return NotFound();

        transfer.ConfirmationNumber = req.ConfirmationNumber;
        transfer.Status = req.Status;
        transfer.Notes = req.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(transfer);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        var transfer = await _db.TransferBookings.FindAsync(new object[] { id }, ct);
        if (transfer == null || transfer.TravelFileId != fileId) return NotFound();

        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file != null)
        {
            file.TotalCost -= transfer.NetCost;
            file.TotalSale -= transfer.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
        }

        _db.TransferBookings.Remove(transfer);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record CreateTransferRequest(
    int SupplierId, string PickupLocation, string DropoffLocation,
    DateTime PickupDateTime, string? FlightNumber, string VehicleType, int Passengers,
    bool IsRoundTrip, DateTime? ReturnDateTime,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdateTransferRequest(string? ConfirmationNumber, string Status, string? Notes);
