using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.DTOs;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/hotels")]
[Authorize]
public class HotelBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public HotelBookingsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var hotels = await _db.HotelBookings
            .Where(h => h.TravelFileId == fileId)
            .OrderBy(h => h.CheckIn)
            .ProjectTo<HotelBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
        return Ok(hotels);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int fileId, int id, CancellationToken ct)
    {
        var hotel = await _db.HotelBookings.FindAsync(new object[] { id }, ct);
        if (hotel == null || hotel.TravelFileId != fileId) return NotFound();
        return Ok(_mapper.Map<HotelBookingDto>(hotel));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateHotelRequest req, CancellationToken ct)
    {
        try
        {
            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            var hotel = _mapper.Map<HotelBooking>(req);
            hotel.TravelFileId = fileId;
            // Nights calculated in AutoMapper

            _db.HotelBookings.Add(hotel);
            
            // Actualizar totales del File
            file.TotalCost += hotel.NetCost;
            file.TotalSale += hotel.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
            
            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<HotelBookingDto>(hotel));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando hotel: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateHotelRequest req, CancellationToken ct)
    {
        try
        {
            var hotel = await _db.HotelBookings.FindAsync(new object[] { id }, ct);
            if (hotel == null || hotel.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            // Calculate differences for totals
            var diffCost = req.NetCost - hotel.NetCost;
            var diffSale = req.SalePrice - hotel.SalePrice;

            // Update File Totals
            file.TotalCost += diffCost;
            file.TotalSale += diffSale;
            file.Balance = file.TotalSale - file.TotalCost;

            // Update Hotel Fields
            _mapper.Map(req, hotel);
            // Nights calculated in AutoMapper

            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<HotelBookingDto>(hotel));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando hotel: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        try
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
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando hotel: {ex.Message}");
        }
    }
}


