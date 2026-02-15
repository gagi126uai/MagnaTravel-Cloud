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

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor
            if (flight.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { flight.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += flight.NetCost;
            }
            
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

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor si cambió el costo
            var oldSupplierId = flight.SupplierId;
            var oldNetCost = flight.NetCost;

            _mapper.Map(req, flight);

            // Fix 3: Ajustar saldo del proveedor
            if (oldSupplierId > 0 && oldSupplierId == flight.SupplierId)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { flight.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += (flight.NetCost - oldNetCost);
            }

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

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Restar del saldo del proveedor
            if (flight.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { flight.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance -= flight.NetCost;
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


