using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly AppDbContext _db;

    public RatesController(AppDbContext db) => _db = db;

    /// <summary>
    /// Listar tarifario con filtros opcionales
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? supplierId, 
        [FromQuery] string? serviceType,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default)
    {
        var query = _db.Rates.Include(r => r.Supplier).AsQueryable();
        
        if (supplierId.HasValue)
            query = query.Where(r => r.SupplierId == supplierId.Value);
        
        if (!string.IsNullOrEmpty(serviceType))
            query = query.Where(r => r.ServiceType == serviceType);
        
        if (activeOnly)
            query = query.Where(r => r.IsActive && r.ValidTo >= DateTime.UtcNow);
        
        var rates = await query
            .OrderBy(r => r.Supplier!.Name)
            .ThenBy(r => r.ServiceType)
            .ThenBy(r => r.ProductName)
            .Select(r => new {
                r.Id, r.ServiceType, r.ProductName, r.Description,
                r.NetCost, r.SalePrice, r.Currency,
                r.ValidFrom, r.ValidTo, r.IsActive,
                SupplierId = r.SupplierId,
                SupplierName = r.Supplier != null ? r.Supplier.Name : null
            })
            .ToListAsync(ct);
        
        return Ok(rates);
    }

    /// <summary>
    /// Buscar tarifa para autocompletar al crear servicio
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] int supplierId,
        [FromQuery] string serviceType,
        [FromQuery] string? query,
        CancellationToken ct)
    {
        var rates = await _db.Rates
            .Where(r => r.SupplierId == supplierId && r.ServiceType == serviceType)
            .Where(r => r.IsActive && r.ValidTo >= DateTime.UtcNow)
            .Where(r => string.IsNullOrEmpty(query) || r.ProductName.Contains(query))
            .Take(20)
            .Select(r => new {
                r.Id, r.ProductName, r.Description, r.NetCost, r.SalePrice, r.Currency
            })
            .ToListAsync(ct);
        
        return Ok(rates);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateRateRequest req, CancellationToken ct)
    {
        var rate = new Rate
        {
            SupplierId = req.SupplierId,
            ServiceType = req.ServiceType,
            ProductName = req.ProductName,
            Description = req.Description,
            NetCost = req.NetCost,
            SalePrice = req.SalePrice,
            Currency = req.Currency,
            ValidFrom = req.ValidFrom,
            ValidTo = req.ValidTo,
            IsActive = true
        };

        _db.Rates.Add(rate);
        await _db.SaveChangesAsync(ct);
        return Ok(rate);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRateRequest req, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return NotFound();

        rate.ProductName = req.ProductName;
        rate.Description = req.Description;
        rate.NetCost = req.NetCost;
        rate.SalePrice = req.SalePrice;
        rate.Currency = req.Currency;
        rate.ValidFrom = req.ValidFrom;
        rate.ValidTo = req.ValidTo;
        rate.IsActive = req.IsActive;
        rate.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(rate);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var rate = await _db.Rates.FindAsync(new object[] { id }, ct);
        if (rate == null) return NotFound();

        _db.Rates.Remove(rate);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record CreateRateRequest(
    int SupplierId, string ServiceType, string ProductName, string? Description,
    decimal NetCost, decimal SalePrice, string Currency,
    DateTime ValidFrom, DateTime ValidTo
);

public record UpdateRateRequest(
    string ProductName, string? Description,
    decimal NetCost, decimal SalePrice, string Currency,
    DateTime ValidFrom, DateTime ValidTo, bool IsActive
);
