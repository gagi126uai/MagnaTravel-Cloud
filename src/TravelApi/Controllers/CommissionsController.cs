using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CommissionsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public CommissionsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Listar todas las reglas de comisión
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var rules = await _dbContext.CommissionRules
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Supplier != null ? r.Supplier.Name : "")
            .ToListAsync(cancellationToken);

        return Ok(rules.Select(r => new
        {
            r.Id,
            r.SupplierId,
            SupplierName = r.Supplier?.Name,
            r.ServiceType,
            r.CommissionPercent,
            r.Priority,
            r.IsActive,
            r.Description,
            r.CreatedAt
        }));
    }

    /// <summary>
    /// Crear nueva regla de comisión
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        // Verificar si ya existe una regla igual
        var existing = await _dbContext.CommissionRules
            .FirstOrDefaultAsync(r => 
                r.SupplierId == request.SupplierId && 
                r.ServiceType == request.ServiceType &&
                r.IsActive, cancellationToken);

        if (existing != null)
            return BadRequest("Ya existe una regla con ese proveedor y tipo de servicio");

        var rule = new CommissionRule
        {
            SupplierId = request.SupplierId,
            ServiceType = request.ServiceType,
            CommissionPercent = request.CommissionPercent,
            Priority = request.Priority,
            Description = request.Description,
            IsActive = true
        };

        _dbContext.CommissionRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(rule);
    }

    /// <summary>
    /// Actualizar regla de comisión
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return NotFound();

        rule.CommissionPercent = request.CommissionPercent;
        rule.Description = request.Description;
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(rule);
    }

    /// <summary>
    /// Eliminar regla de comisión
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return NotFound();

        _dbContext.CommissionRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok();
    }

    /// <summary>
    /// Calcular comisión según proveedor y tipo de servicio
    /// Retorna la regla más específica que aplique
    /// </summary>
    [HttpGet("calculate")]
    public async Task<IActionResult> Calculate([FromQuery] int? supplierId, [FromQuery] string? serviceType, CancellationToken cancellationToken)
    {
        // Buscar la regla más específica que aplique
        var rule = await _dbContext.CommissionRules
            .Where(r => r.IsActive)
            .Where(r => 
                // Regla exacta (proveedor + servicio)
                (r.SupplierId == supplierId && r.ServiceType == serviceType) ||
                // Solo proveedor
                (r.SupplierId == supplierId && r.ServiceType == null) ||
                // Solo servicio
                (r.SupplierId == null && r.ServiceType == serviceType) ||
                // Default (aplica a todos)
                (r.SupplierId == null && r.ServiceType == null)
            )
            .OrderByDescending(r => r.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (rule == null)
        {
            // Si no hay regla, usar el default de AgencySettings
            var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
            return Ok(new { commissionPercent = settings?.DefaultCommissionPercent ?? 10 });
        }

        return Ok(new { commissionPercent = rule.CommissionPercent });
    }
}

public record CreateCommissionRuleRequest(
    int? SupplierId,
    string? ServiceType,
    decimal CommissionPercent,
    int Priority,
    string? Description
);

public record UpdateCommissionRuleRequest(
    decimal CommissionPercent,
    int Priority,
    string? Description,
    bool IsActive
);
