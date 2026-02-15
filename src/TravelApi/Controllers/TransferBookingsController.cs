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
[Route("api/files/{fileId}/transfers")]
[Authorize]
public class TransferBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public TransferBookingsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var transfers = await _db.TransferBookings
            .Where(t => t.TravelFileId == fileId)
            .OrderBy(t => t.PickupDateTime)
            .ProjectTo<TransferBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
        return Ok(transfers);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            var transfer = _mapper.Map<TransferBooking>(req);
            transfer.TravelFileId = fileId;

            _db.TransferBookings.Add(transfer);

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor
            if (transfer.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { transfer.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += transfer.NetCost;
            }
            
            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<TransferBookingDto>(transfer));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando traslado: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var transfer = await _db.TransferBookings.FindAsync(new object[] { id }, ct);
            if (transfer == null || transfer.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor si cambió el costo
            var oldNetCost = transfer.NetCost;

            _mapper.Map(req, transfer);

            if (transfer.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { transfer.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += (transfer.NetCost - oldNetCost);
            }

            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<TransferBookingDto>(transfer));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando traslado: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        try
        {
            var transfer = await _db.TransferBookings.FindAsync(new object[] { id }, ct);
            if (transfer == null || transfer.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Restar del saldo del proveedor
            if (transfer.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { transfer.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance -= transfer.NetCost;
            }

            _db.TransferBookings.Remove(transfer);
            await _db.SaveChangesAsync(ct);
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando traslado: {ex.Message}");
        }
    }
}


