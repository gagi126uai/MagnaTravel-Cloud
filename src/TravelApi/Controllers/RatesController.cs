using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly IRateService _rateService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public RatesController(IRateService rateService, EntityReferenceResolver entityReferenceResolver)
    {
        _rateService = rateService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    /// <summary>
    /// Listar tarifario con filtros opcionales
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? supplierId, 
        [FromQuery] string? serviceType,
        [FromQuery] bool activeOnly = false,
        CancellationToken ct = default)
    {
        var resolvedSupplierId = await ResolveOptionalSupplierIdAsync(supplierId, ct);
        if (supplierId is not null && resolvedSupplierId is null)
            return NotFound("Proveedor no encontrado.");

        var rates = await _rateService.GetAllAsync(resolvedSupplierId, serviceType, activeOnly, ct);
        return Ok(rates);
    }

    /// <summary>
    /// Obtener tarifa por ID con todos los campos
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var rate = await _rateService.GetByIdAsync(id, ct);
        if (rate == null) return NotFound();
        return Ok(rate);
    }

    /// <summary>
    /// Buscar tarifa para autocompletar al crear servicio
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? supplierId,
        [FromQuery] string? serviceType,
        [FromQuery] string? query,
        CancellationToken ct)
    {
        var resolvedSupplierId = await ResolveOptionalSupplierIdAsync(supplierId, ct);
        if (supplierId is not null && resolvedSupplierId is null)
            return NotFound("Proveedor no encontrado.");

        var rates = await _rateService.SearchAsync(resolvedSupplierId, serviceType, query, ct);
        return Ok(rates);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] RateDto req, CancellationToken ct)
    {
        try
        {
            var rate = await _rateService.CreateAsync(req, ct);
            return Ok(rate);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo crear la tarifa." });
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] RateDto req, CancellationToken ct)
    {
        try
        {
            var rate = await _rateService.UpdateAsync(id, req, ct);
            if (rate == null) return NotFound();
            return Ok(rate);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar la tarifa." });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _rateService.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return Ok();
    }

    private async Task<int?> ResolveOptionalSupplierIdAsync(string? supplierPublicIdOrLegacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicIdOrLegacyId))
            return null;

        var supplier = await _entityReferenceResolver.FindAsync<Supplier>(supplierPublicIdOrLegacyId, ct);
        return supplier?.Id;
    }
}
