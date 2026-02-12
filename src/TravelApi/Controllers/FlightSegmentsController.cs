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
[Route("api/files/{fileId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public FlightSegmentsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var flights = await _db.FlightSegments
            .Where(f => f.TravelFileId == fileId)
            .OrderBy(f => f.DepartureTime)
            .ProjectTo<FlightSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
        return Ok(flights);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            var flight = _mapper.Map<FlightSegment>(req);
            flight.TravelFileId = fileId;

            _db.FlightSegments.Add(flight);
            
            // Recalculate File Totals (Add new amounts)
            file.TotalCost += flight.NetCost + flight.Tax;
            file.TotalSale += flight.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
            
            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<FlightSegmentDto>(flight));
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error creando vuelo: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var flight = await _db.FlightSegments.FindAsync(new object[] { id }, ct);
            if (flight == null || flight.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            // Calculate differences for totals
            // Note: We use the OLD values from 'flight' before mapping
            var diffCost = (req.NetCost + req.Tax) - (flight.NetCost + flight.Tax);
            var diffSale = req.SalePrice - flight.SalePrice;

            file.TotalCost += diffCost;
            file.TotalSale += diffSale;
            file.Balance = file.TotalSale - file.TotalCost;

            // Update flight fields using Mapper
            _mapper.Map(req, flight);

            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<FlightSegmentDto>(flight));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando vuelo: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        try
        {
            var flight = await _db.FlightSegments.FindAsync(new object[] { id }, ct);
            if (flight == null || flight.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file != null)
            {
                file.TotalCost -= (flight.NetCost + flight.Tax);
                file.TotalSale -= flight.SalePrice;
                file.Balance = file.TotalSale - file.TotalCost;
            }

            _db.FlightSegments.Remove(flight);
            await _db.SaveChangesAsync(ct);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando vuelo: {ex.Message}");
        }
    }
}


