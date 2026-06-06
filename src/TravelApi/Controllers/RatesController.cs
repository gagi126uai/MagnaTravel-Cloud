using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
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
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public RatesController(IRateService rateService, IEntityReferenceResolver entityReferenceResolver)
    {
        _rateService = rateService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<RateListItemDto>>> GetAll([FromQuery] RateListQuery query, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _rateService.GetAllAsync(query, ct));
        }
        catch (ArgumentException)
        {
            return NotFound("Proveedor no encontrado.");
        }
    }

    [HttpGet("groups")]
    public async Task<ActionResult<PagedResponse<RateGroupDto>>> GetGroups([FromQuery] RateGroupsQuery query, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _rateService.GetGroupsAsync(query, ct));
        }
        catch (ArgumentException)
        {
            return NotFound("Proveedor no encontrado.");
        }
    }

    [HttpGet("hotels")]
    public async Task<ActionResult<PagedResponse<HotelRateGroupDto>>> GetHotels([FromQuery] HotelRateGroupsQuery query, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _rateService.GetHotelGroupsAsync(query, ct));
        }
        catch (ArgumentException)
        {
            return NotFound("Proveedor no encontrado.");
        }
    }

    [HttpGet("summary")]
    public async Task<ActionResult<RateSummaryDto>> GetSummary([FromQuery] RateSummaryQuery query, CancellationToken ct = default)
    {
        try
        {
            return Ok(await _rateService.GetSummaryAsync(query, ct));
        }
        catch (ArgumentException)
        {
            return NotFound("Proveedor no encontrado.");
        }
    }

    [HttpGet("{publicId}")]
    public async Task<IActionResult> GetById(string publicId, CancellationToken ct)
    {
        var rate = await _rateService.GetByPublicIdAsync(publicId, ct);
        if (rate == null) return NotFound();
        return Ok(rate);
    }

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

    /// <summary>
    /// ADR-017 F1.2 (catalogo find-or-create, buscador): busca productos del catalogo del tipo pedido
    /// cuyo nombre se parece a <paramref name="q"/> (difuso). Es supplier-agnostico (el producto manda)
    /// y deduplica las tarifas legacy del mismo producto. Cada item trae el contexto de la "ultima vez".
    ///
    /// <para>Mismo gate que los creates de bookings (<c>[Authorize]</c> de clase, NO Admin-only). El
    /// costo se enmascara para callers sin <c>cobranzas.see_cost</c> (R1/D1). Gateado por el flag
    /// <c>EnableCatalogFindOrCreate</c>: con el flag OFF el service devuelve null y aca respondemos 404,
    /// como si el endpoint no existiera (ADR §2.3 / R4).</para>
    /// </summary>
    [HttpGet("catalog-search")]
    public async Task<IActionResult> CatalogSearch(
        [FromQuery] string? serviceType,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        var results = await _rateService.CatalogSearchAsync(serviceType, q, ct);
        if (results is null)
        {
            // El service devuelve null SOLO cuando el flag esta apagado: el endpoint "no existe".
            return NotFound();
        }

        return Ok(results);
    }

    /// <summary>
    /// Pieza C "tarifario que se llena solo": antes de crear una tarifa, avisa si
    /// ya existe una identica o muy parecida (para no cargar duplicados).
    /// Mismo gate que crear/editar tarifas (Admin): expone precios netos/venta.
    /// </summary>
    [HttpPost("duplicate-check")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<RateDuplicateCheckResponse>> DuplicateCheck(
        [FromBody] RateDuplicateCheckRequest req,
        CancellationToken ct)
    {
        try
        {
            var result = await _rateService.FindDuplicateCandidatesAsync(req, ct);
            return Ok(result);
        }
        catch (ArgumentException)
        {
            // El service tira ArgumentException si el SupplierId no resuelve.
            return NotFound("Proveedor no encontrado.");
        }
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] RateDto req, CancellationToken ct)
    {
        try
        {
            var rate = await _rateService.CreateAsync(req, ct);
            return CreatedAtAction(nameof(GetById), new { publicId = rate.PublicId }, rate);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo crear la tarifa." });
        }
    }

    [HttpPut("{publicId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string publicId, [FromBody] RateDto req, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Rate>(publicId, ct);
            var rate = await _rateService.UpdateAsync(id, req, ct);
            if (rate == null) return NotFound();
            return Ok(rate);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar la tarifa." });
        }
    }

    [HttpDelete("{publicId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string publicId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Rate>(publicId, ct);
            var deleted = await _rateService.DeleteAsync(id, ct);
            if (!deleted) return NotFound();
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPatch("{publicId}/deactivate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Deactivate(string publicId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Rate>(publicId, ct);
            var rate = await _rateService.DeactivateAsync(id, ct);
            if (rate == null) return NotFound();
            return Ok(rate);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPatch("{publicId}/reactivate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reactivate(string publicId, CancellationToken ct)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Rate>(publicId, ct);
            var rate = await _rateService.ReactivateAsync(id, ct);
            if (rate == null) return NotFound();
            return Ok(rate);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private async Task<int?> ResolveOptionalSupplierIdAsync(string? supplierPublicIdOrLegacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicIdOrLegacyId))
            return null;

        var supplier = await _entityReferenceResolver.FindAsync<Supplier>(supplierPublicIdOrLegacyId, ct);
        return supplier?.Id;
    }
}
