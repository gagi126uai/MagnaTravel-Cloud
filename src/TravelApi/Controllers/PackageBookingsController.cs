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
[Route("api/files/{fileId}/packages")]
[Authorize]
public class PackageBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public PackageBookingsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var packages = await _db.PackageBookings
            .Where(p => p.TravelFileId == fileId)
            .OrderBy(p => p.StartDate)
            .ProjectTo<PackageBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
        return Ok(packages);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            var package = _mapper.Map<PackageBooking>(req);
            package.TravelFileId = fileId;
            // Nights calculated in AutoMapper

            _db.PackageBookings.Add(package);

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor
            if (package.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { package.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += package.NetCost;
            }
            
            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<PackageBookingDto>(package));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando paquete: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var package = await _db.PackageBookings.FindAsync(new object[] { id }, ct);
            if (package == null || package.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
            if (file == null) return NotFound("File no encontrado");

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Actualizar saldo del proveedor si cambió el costo
            var oldNetCost = package.NetCost;

            _mapper.Map(req, package);
            // Nights calculated in AutoMapper

            if (package.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { package.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance += (package.NetCost - oldNetCost);
            }

            await _db.SaveChangesAsync(ct);
            return Ok(_mapper.Map<PackageBookingDto>(package));
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando paquete: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        try
        {
            var package = await _db.PackageBookings.FindAsync(new object[] { id }, ct);
            if (package == null || package.TravelFileId != fileId) return NotFound();

            var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);

            // Fix 2: No manipular totales manualmente, se recalculan al leer

            // Fix 3: Restar del saldo del proveedor
            if (package.SupplierId > 0)
            {
                var supplier = await _db.Set<Supplier>().FindAsync(new object[] { package.SupplierId }, ct);
                if (supplier != null) supplier.CurrentBalance -= package.NetCost;
            }

            _db.PackageBookings.Remove(package);
            await _db.SaveChangesAsync(ct);
            return Ok();
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error eliminando paquete: {ex.Message}");
        }
    }
}


