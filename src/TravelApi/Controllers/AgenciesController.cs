using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/agencies")]
[Authorize] // Should probably be Admin only, but flexible for now
public class AgenciesController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public AgenciesController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Agency>>> GetAgencies(CancellationToken cancellationToken)
    {
        var agencies = await _dbContext.Agencies
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
        
        return Ok(agencies);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Agency>> GetAgency(int id, CancellationToken cancellationToken)
    {
        var agency = await _dbContext.Agencies
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (agency is null)
        {
            return NotFound();
        }

        return Ok(agency);
    }

    [HttpPost]
    public async Task<ActionResult<Agency>> CreateAgency(Agency agency, CancellationToken cancellationToken)
    {
        // Basic validation
        if (string.IsNullOrWhiteSpace(agency.Name))
        {
            return BadRequest("El nombre de la agencia es requerido.");
        }

        agency.CreatedAt = DateTime.UtcNow;
        _dbContext.Agencies.Add(agency);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetAgency), new { id = agency.Id }, agency);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Agency>> UpdateAgency(int id, Agency agency, CancellationToken cancellationToken)
    {
        if (id != agency.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _dbContext.Agencies.FindAsync(new object[] { id }, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.Name = agency.Name;
        existing.TaxId = agency.TaxId;
        existing.Email = agency.Email;
        existing.Phone = agency.Phone;
        existing.Address = agency.Address;
        existing.CreditLimit = agency.CreditLimit;
        // Not updating CurrentBalance here directly usually, but allowed for Admin corrections
        existing.CurrentBalance = agency.CurrentBalance; 
        existing.IsActive = agency.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(existing);
    }
}
