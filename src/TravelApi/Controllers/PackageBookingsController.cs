using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/packages")]
[Authorize]
public class PackageBookingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PackageBookingsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var packages = await _db.PackageBookings
            .Where(p => p.TravelFileId == fileId)
            .Include(p => p.Supplier)
            .OrderBy(p => p.StartDate)
            .Select(p => new {
                p.Id, p.PackageName, p.Destination, p.StartDate, p.EndDate, p.Nights,
                p.IncludesHotel, p.IncludesFlight, p.IncludesTransfer, p.IncludesExcursions, p.IncludesMeals,
                p.Adults, p.Children, p.Itinerary, p.ConfirmationNumber, p.Status,
                p.NetCost, p.SalePrice, p.Commission, p.Notes,
                SupplierId = p.SupplierId,
                SupplierName = p.Supplier != null ? p.Supplier.Name : null
            })
            .ToListAsync(ct);
        return Ok(packages);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreatePackageRequest req, CancellationToken ct)
    {
        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file == null) return NotFound("File no encontrado");

        var package = new PackageBooking
        {
            TravelFileId = fileId,
            SupplierId = req.SupplierId,
            PackageName = req.PackageName,
            Destination = req.Destination,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            Nights = (req.EndDate - req.StartDate).Days,
            IncludesHotel = req.IncludesHotel,
            IncludesFlight = req.IncludesFlight,
            IncludesTransfer = req.IncludesTransfer,
            IncludesExcursions = req.IncludesExcursions,
            IncludesMeals = req.IncludesMeals,
            Adults = req.Adults,
            Children = req.Children,
            Itinerary = req.Itinerary,
            NetCost = req.NetCost,
            SalePrice = req.SalePrice,
            Commission = req.Commission,
            Notes = req.Notes
        };

        _db.PackageBookings.Add(package);
        
        file.TotalCost += package.NetCost;
        file.TotalSale += package.SalePrice;
        file.Balance = file.TotalSale - file.TotalCost;
        
        await _db.SaveChangesAsync(ct);
        return Ok(package);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdatePackageRequest req, CancellationToken ct)
    {
        var package = await _db.PackageBookings.FindAsync(new object[] { id }, ct);
        if (package == null || package.TravelFileId != fileId) return NotFound();

        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file == null) return NotFound("File no encontrado");

        var diffCost = req.NetCost - package.NetCost;
        var diffSale = req.SalePrice - package.SalePrice;

        file.TotalCost += diffCost;
        file.TotalSale += diffSale;
        file.Balance = file.TotalSale - file.TotalCost;

        package.SupplierId = req.SupplierId;
        package.PackageName = req.PackageName;
        package.Destination = req.Destination;
        package.StartDate = req.StartDate;
        package.EndDate = req.EndDate;
        package.Nights = (req.EndDate - req.StartDate).Days;
        package.IncludesHotel = req.IncludesHotel;
        package.IncludesFlight = req.IncludesFlight;
        package.IncludesTransfer = req.IncludesTransfer;
        package.IncludesExcursions = req.IncludesExcursions;
        package.IncludesMeals = req.IncludesMeals;
        package.Adults = req.Adults;
        package.Children = req.Children;
        package.Itinerary = req.Itinerary;
        package.ConfirmationNumber = req.ConfirmationNumber;
        package.NetCost = req.NetCost;
        package.SalePrice = req.SalePrice;
        package.Commission = req.Commission;
        package.Status = req.Status;
        package.Notes = req.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(package);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        var package = await _db.PackageBookings.FindAsync(new object[] { id }, ct);
        if (package == null || package.TravelFileId != fileId) return NotFound();

        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file != null)
        {
            file.TotalCost -= package.NetCost;
            file.TotalSale -= package.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
        }

        _db.PackageBookings.Remove(package);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record CreatePackageRequest(
    int SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdatePackageRequest(
    int SupplierId, string PackageName, string Destination,
    DateTime StartDate, DateTime EndDate,
    bool IncludesHotel, bool IncludesFlight, bool IncludesTransfer, bool IncludesExcursions, bool IncludesMeals,
    int Adults, int Children, string? Itinerary, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string Status, string? Notes
);
