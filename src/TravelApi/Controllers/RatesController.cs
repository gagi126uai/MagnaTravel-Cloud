using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/rates")]
[Authorize]
[RequirePermission(Permissions.TarifarioView)]
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
            return NotFound(new { message = "No encontramos ese operador." });
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
            return NotFound(new { message = "No encontramos ese operador." });
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
            return NotFound(new { message = "No encontramos ese operador." });
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
            return NotFound(new { message = "No encontramos ese operador." });
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
            return NotFound(new { message = "No encontramos ese operador." });

        var rates = await _rateService.SearchAsync(resolvedSupplierId, serviceType, query, ct);
        return Ok(rates);
    }

    /// <summary>
    /// ADR-017 F1.2 (catalogo find-or-create, buscador): busca productos del catalogo del tipo pedido
    /// cuyo nombre se parece a <paramref name="q"/> (difuso). Es supplier-agnostico (el producto manda)
    /// y deduplica las tarifas legacy del mismo producto. Cada item trae el contexto de la "ultima vez".
    ///
    /// <para>Mismo gate que los creates de bookings (<c>[Authorize]</c> de clase, NO Admin-only). El
    /// costo se enmascara para callers sin <c>cobranzas.see_cost</c> (R1/D1). Ya no hay llave que lo
    /// apague (spec firmada 2026-08-06, P8=A): antes respondia 404 con el interruptor apagado.</para>
    /// </summary>
    [HttpGet("catalog-search")]
    public async Task<IActionResult> CatalogSearch(
        [FromQuery] string? serviceType,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        return Ok(await _rateService.CatalogSearchAsync(serviceType, q, ct));
    }

    /// <summary>
    /// "Tarifario que se arma solo" (spec firmada 2026-08-06, M-1/M-2): la lista de productos aprendidos,
    /// con un renglon por operador (ultimo precio, moneda, unidad, cuando y de que reserva salio).
    ///
    /// <para>Mismo permiso que el resto del tarifario (<c>tarifario.view</c> de la clase). Sin permiso de
    /// ver costos, el precio que viaja es el de VENTA, nunca el costo (F-14).</para>
    /// </summary>
    [HttpGet("learned-products")]
    public async Task<ActionResult<PagedResponse<LearnedProductDto>>> GetLearnedProducts(
        [FromQuery] LearnedProductsQuery query,
        CancellationToken ct)
    {
        // Un filtro por un operador que ya no existe es un pedido invalido, no una falla del sistema: se
        // responde con una frase de negocio en vez de dejar que reviente en un error generico.
        if (!string.IsNullOrWhiteSpace(query.SupplierId)
            && await ResolveOptionalSupplierIdAsync(query.SupplierId, ct) is null)
        {
            return NotFound(new { message = "No encontramos ese operador." });
        }

        return Ok(await _rateService.GetLearnedProductsAsync(query, ct));
    }

    /// <summary>
    /// Alta simple de producto desde el Tarifario (spec firmada 2026-08-06, M-3 + P7 "evitar repetidos a
    /// toda costa"). Pocos campos y freno de repetidos OBLIGATORIO del lado del servidor.
    ///
    /// <para><b>Dos respuestas posibles</b>: 201 con el producto creado, o <b>409</b> con la lista de
    /// productos parecidos cuando el sistema frena. El cliente vuelve a llamar con
    /// <c>crearIgual = true</c> (<c>createAnyway</c>) SOLO si el usuario confirmo que no es el mismo.</para>
    ///
    /// <para>No es Admin-only a proposito: un vendedor ya puede crear productos al cargar un servicio,
    /// asi que exigir Admin aca seria una puerta incoherente. El costo se enmascara igual que en el resto
    /// del tarifario.</para>
    /// </summary>
    [HttpPost("simple")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> CreateSimple([FromBody] CreateSimpleProductRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _rateService.CreateSimpleProductAsync(req, ct);

            if (result.Created is null)
            {
                // Freno de repetidos: no se creo nada todavia. 409 = "hay un conflicto que resolver".
                return Conflict(new
                {
                    message = result.Message,
                    reason = result.Reason,
                    similarProducts = result.SimilarProducts
                });
            }

            return CreatedAtAction(nameof(GetById), new { publicId = result.Created.PublicId }, result.Created);
        }
        catch (RateValidationException ex)
        {
            // SOLO se devuelve el texto de la excepcion PROPIA del tarifario, que por contrato esta escrita
            // para una persona. Cualquier otra excepcion sigue de largo al manejador global (generico
            // amable), para no filtrar nombres de campos ni sufijos tecnicos.
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Renombra un PRODUCTO del Tarifario (spec firmada 2026-08-06, §2.2): corrige el nombre —y la ciudad,
    /// si es hotel— de TODAS las tarifas que forman ese producto.
    ///
    /// <para><b>Por que no alcanza el PUT de una tarifa</b>: un producto de la lista puede estar formado por
    /// varias tarifas; renombrar una sola parte el grupo en dos productos con nombres distintos, que es
    /// justo el repetido que hay que evitar.</para>
    ///
    /// <para>Respuestas: 200 con la identidad nueva · 404 si el producto no existe · <b>409</b> si el nombre
    /// nuevo ya lo tiene otro producto (no se fusiona nada: decide el usuario).</para>
    /// </summary>
    [HttpPost("learned-products/rename")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> RenameLearnedProduct(
        [FromBody] RenameLearnedProductRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _rateService.RenameLearnedProductAsync(req, ct));
        }
        catch (RateProductNameTakenException ex)
        {
            return Conflict(new { message = ex.Message, reason = LearnedProductRenameReasons.NameAlreadyTaken });
        }
        catch (RateValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
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
            return NotFound(new { message = "No encontramos ese operador." });
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

    /// <summary>
    /// Edicion completa de UNA tarifa (el formulario largo). Decision firmada 2026-08-06: pasa de "solo
    /// Admin" al permiso <c>tarifario.edit</c>, el mismo del alta a mano — quien puede cargar un producto
    /// puede corregirlo. Los roles default NO cambian: hoy solo el Admin tiene ese permiso.
    /// </summary>
    [HttpPut("{publicId}")]
    [RequirePermission(Permissions.TarifarioEdit)]
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
