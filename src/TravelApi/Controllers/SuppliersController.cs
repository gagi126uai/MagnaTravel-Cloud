using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/suppliers")]
[Authorize]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public SuppliersController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetSuppliers(CancellationToken cancellationToken)
    {
        var suppliers = await _dbContext.Suppliers
            .AsNoTracking()
            .OrderBy(supplier => supplier.Name)
            .ToListAsync(cancellationToken);

        return Ok(suppliers);
    }

    [HttpPost]
    public async Task<ActionResult<Supplier>> CreateSupplier(Supplier supplier, CancellationToken cancellationToken)
    {
        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetSuppliers), new { id = supplier.Id }, supplier);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Supplier>> UpdateSupplier(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Suppliers.FirstOrDefaultAsync(found => found.Id == id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.Name = supplier.Name;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.Notes = supplier.Notes;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(existing);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteSupplier(int id, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Suppliers.FirstOrDefaultAsync(found => found.Id == id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        _dbContext.Suppliers.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }
}
