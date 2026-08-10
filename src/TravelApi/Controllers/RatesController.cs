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
    private readonly ICatalogLibrarianService _librarian;

    public RatesController(
        IRateService rateService,
        IEntityReferenceResolver entityReferenceResolver,
        ICatalogLibrarianService librarian)
    {
        _rateService = rateService;
        _entityReferenceResolver = entityReferenceResolver;
        _librarian = librarian;
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
    /// ADR-017 F1.2 (catalogo find-or-create, buscador): busca productos del catalogo que se parecen a
    /// <paramref name="q"/> (difuso, palabra por palabra). Es supplier-agnostico (el producto manda) y
    /// deduplica las tarifas legacy del mismo producto. Cada item trae el contexto de la "ultima vez".
    ///
    /// <para><b>El <paramref name="serviceType"/> ya no filtra</b> (mejora 2026-08-10): es el tipo
    /// PREFERIDO — la solapa donde esta parado el vendedor — y solo empuja esos productos arriba. La
    /// busqueda recorre los 5 tipos de la ficha y el parametro puede venir vacio. El nombre del
    /// parametro NO cambia (contrato del front intacto).</para>
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
    public async Task<ActionResult<LearnedProductsResponse>> GetLearnedProducts(
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
    /// La ficha de UN producto (spec 2026-08-07, §7): igual que la lista pero con TODOS sus precios,
    /// agrupados por habitación, sin el tope de 3 renglones.
    /// </summary>
    [HttpGet("learned-products/{ratePublicId:guid}")]
    public async Task<IActionResult> GetLearnedProduct(Guid ratePublicId, CancellationToken ct)
    {
        var product = await _rateService.GetLearnedProductAsync(ratePublicId, ct);
        if (product is null) return NotFound(new { message = "No encontramos ese producto en el tarifario." });
        return Ok(product);
    }

    /// <summary>
    /// Qué precio sugerir al vender ESTA habitación (spec 2026-08-07, M-15 / V9=A).
    ///
    /// <para>Devuelve <c>isSameVariant=true</c> cuando el precio es de la misma habitación (la pantalla lo
    /// puede precargar en amarillo) y <c>false</c> cuando es de otra parecida: en ese caso el casillero
    /// queda VACÍO y el precio solo se muestra abajo en gris, con la frase ya armada. 204 si el producto
    /// todavía no tiene ningún precio aprendido.</para>
    /// </summary>
    [HttpGet("variant-price-suggestion")]
    public async Task<IActionResult> GetVariantPriceSuggestion(
        [FromQuery] VariantPriceSuggestionQuery query, CancellationToken ct)
    {
        var suggestion = await _rateService.GetVariantPriceSuggestionAsync(query, ct);
        if (suggestion is null) return NoContent();
        return Ok(suggestion);
    }

    /// <summary>
    /// Los nombres finos de habitación (o los vehículos) que ya se usaron alguna vez, para ofrecerlos
    /// mientras el vendedor escribe (spec 2026-08-07, §5.2 / M-19). Texto libre CON memoria.
    /// </summary>
    [HttpGet("variant-names")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetVariantNames(
        [FromQuery] string? serviceType, [FromQuery] string? q, CancellationToken ct)
    {
        return Ok(await _rateService.GetVariantNameSuggestionsAsync(serviceType, q, ct));
    }

    /// <summary>
    /// Corrige cómo se llama una habitación de un producto (spec 2026-08-07, §7 / M-18). Si al corregirla
    /// queda igual que otra, las dos se juntan solas y queda el precio más nuevo. <b>Nunca toca importes.</b>
    /// </summary>
    [HttpPost("learned-products/variants/rename")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> RenameVariant(
        [FromBody] RenameVariantRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _rateService.RenameVariantAsync(req, ct));
        }
        catch (RateValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or CatalogProductNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    // ============================================================
    // La bandeja "Repetidos" y el ordenado automático (spec 2026-08-07, §6)
    // ============================================================

    /// <summary>
    /// La bandeja "Repetidos" (§6 / V11=B): un producto arriba y abajo todos los que se le parecen, con
    /// el contador de lo que el sistema ordenó solo esta semana.
    /// </summary>
    [HttpGet("duplicates")]
    public async Task<ActionResult<DuplicateProductsResponse>> GetDuplicates(CancellationToken ct)
    {
        return Ok(await _librarian.GetDuplicateGroupsAsync(ct));
    }

    /// <summary>
    /// "Es el mismo": el de arriba absorbe los precios y las habitaciones del otro. <b>Nada se borra</b>:
    /// el absorbido se apaga y queda con Deshacer disponible.
    /// </summary>
    [HttpPost("duplicates/merge")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> MergeDuplicates([FromBody] MergeProductsRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _librarian.MergeProductsAsync(req, ct));
        }
        catch (RateValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        // Las excepciones PROPIAS del tarifario traen un texto escrito para una persona; cualquier otra
        // sigue de largo al manejador global (generico amable), para no filtrar nada tecnico.
        catch (Exception ex) when (ex is KeyNotFoundException or CatalogProductNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>"Es otro": ese par no se vuelve a proponer nunca más.</summary>
    [HttpPost("duplicates/not-duplicates")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> MarkNotDuplicates([FromBody] NotDuplicatesRequest req, CancellationToken ct)
    {
        try
        {
            await _librarian.MarkAsNotDuplicatesAsync(req, ct);
            return NoContent();
        }
        catch (RateValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or CatalogProductNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>Lo que el sistema ordenó solo, para "Ver qué ordenó" (§6). Cada línea trae si se puede deshacer.</summary>
    [HttpGet("tidy-up-log")]
    public async Task<ActionResult<TidyUpLogResponse>> GetTidyUpLog(CancellationToken ct)
    {
        return Ok(await _librarian.GetTidyUpLogAsync(ct));
    }

    /// <summary>
    /// Deshacer una unión (o una corrección de habitación): vuelve todo a como estaba. Se puede tocar dos
    /// veces sin drama.
    ///
    /// <para>Respuestas: 204 si se deshizo · 404 si ese movimiento no existe · <b>409</b> si ya no se puede
    /// deshacer con fidelidad (hubo ventas nuevas encima, el casillero de destino quedó ocupado, o encima
    /// se ordenó otra vez). El <c>message</c> del 409 explica en criollo cuál de esas cosas pasó: es lo que
    /// la pantalla muestra tal cual.</para>
    /// </summary>
    [HttpPost("tidy-up-log/{actionPublicId:guid}/undo")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> UndoTidyUp(Guid actionPublicId, CancellationToken ct)
    {
        try
        {
            await _librarian.UndoTidyUpActionAsync(actionPublicId, ct);
            return NoContent();
        }
        catch (CatalogTidyUpNotReversibleException ex)
        {
            // 409 y no 400: no hay nada que el usuario pueda corregir y reintentar — es el estado actual
            // el que ya no permite la vuelta atras.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or CatalogTidyUpNotFoundException)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Pasada del bibliotecario: une los "casi seguros" que dejó el formulario viejo (§6 / Q3=B firmada).
    /// Es idempotente — correrla dos veces no hace nada la segunda.
    /// </summary>
    [HttpPost("librarian/tidy-up")]
    [RequirePermission(Permissions.TarifarioEdit)]
    public async Task<IActionResult> RunLibrarian(CancellationToken ct)
    {
        try
        {
            return Ok(await _librarian.TidyUpAsync(ct));
        }
        catch (RateValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
