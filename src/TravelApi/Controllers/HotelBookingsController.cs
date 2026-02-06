using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/hotels")]
[Authorize]
public class HotelBookingsController : ControllerBase
{
    private readonly AppDbContext _db;

    public HotelBookingsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var hotels = await _db.HotelBookings
            .Where(h => h.TravelFileId == fileId)
            .Include(h => h.Supplier)
            .OrderBy(h => h.CheckIn)
            .Select(h => new {
                h.Id, h.HotelName, h.StarRating, h.City, h.Country,
                h.CheckIn, h.CheckOut, h.Nights, h.RoomType, h.MealPlan,
                h.Adults, h.Children, h.Rooms, h.ConfirmationNumber, h.Status,
                h.NetCost, h.SalePrice, h.Commission, h.Notes,
                SupplierId = h.SupplierId,
                SupplierName = h.Supplier != null ? h.Supplier.Name : null
            })
            .ToListAsync(ct);
        return Ok(hotels);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int fileId, int id, CancellationToken ct)
    {
        var hotel = await _db.HotelBookings.FindAsync(new object[] { id }, ct);
        if (hotel == null || hotel.TravelFileId != fileId) return NotFound();
        return Ok(hotel);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateHotelRequest req, CancellationToken ct)
    {
        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file == null) return NotFound("File no encontrado");

        var hotel = new HotelBooking
        {
            TravelFileId = fileId,
            SupplierId = req.SupplierId,
            HotelName = req.HotelName,
            StarRating = req.StarRating,
            City = req.City,
            Country = req.Country,
            CheckIn = req.CheckIn,
            CheckOut = req.CheckOut,
            Nights = (req.CheckOut - req.CheckIn).Days,
            RoomType = req.RoomType,
            MealPlan = req.MealPlan,
            Adults = req.Adults,
            Children = req.Children,
            Rooms = req.Rooms,
            ConfirmationNumber = req.ConfirmationNumber,
            NetCost = req.NetCost,
            SalePrice = req.SalePrice,
            Commission = req.Commission,
            Notes = req.Notes
        };

        _db.HotelBookings.Add(hotel);
        
        // Actualizar totales del File
        file.TotalCost += hotel.NetCost;
        file.TotalSale += hotel.SalePrice;
        file.Balance = file.TotalSale - file.TotalCost;
        
        await _db.SaveChangesAsync(ct);
        return Ok(hotel);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateHotelRequest req, CancellationToken ct)
    {
        var hotel = await _db.HotelBookings.FindAsync(new object[] { id }, ct);
        if (hotel == null || hotel.TravelFileId != fileId) return NotFound();

        hotel.ConfirmationNumber = req.ConfirmationNumber;
        hotel.Status = req.Status;
        hotel.Notes = req.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(hotel);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        var hotel = await _db.HotelBookings.FindAsync(new object[] { id }, ct);
        if (hotel == null || hotel.TravelFileId != fileId) return NotFound();

        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file != null)
        {
            file.TotalCost -= hotel.NetCost;
            file.TotalSale -= hotel.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
        }

        _db.HotelBookings.Remove(hotel);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record CreateHotelRequest(
    int SupplierId, string HotelName, int? StarRating, string City, string? Country,
    DateTime CheckIn, DateTime CheckOut, string RoomType, string MealPlan,
    int Adults, int Children, int Rooms, string? ConfirmationNumber,
    decimal NetCost, decimal SalePrice, decimal Commission, string? Notes
);

public record UpdateHotelRequest(string? ConfirmationNumber, string Status, string? Notes);
