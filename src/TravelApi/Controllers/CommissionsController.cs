using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CommissionsController : ControllerBase
{
    private readonly ICommissionService _commissionService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public CommissionsController(ICommissionService commissionService, IEntityReferenceResolver entityReferenceResolver)
    {
        _commissionService = commissionService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    /// <summary>
    /// Listar todas las reglas de comisión
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var rules = await _commissionService.GetAllRulesAsync(cancellationToken);
        return Ok(rules);
    }

    /// <summary>
    /// Crear nueva regla de comisión
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var rule = await _commissionService.CreateRuleAsync(request, cancellationToken);
            return Ok(rule);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo crear la regla de comision." });
        }
    }

    /// <summary>
    /// Actualizar regla de comisión
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _commissionService.UpdateRuleAsync(id, request, cancellationToken);
        if (rule == null)
            return NotFound();

        return Ok(rule);
    }

    /// <summary>
    /// Eliminar regla de comisión
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await _commissionService.DeleteRuleAsync(id, cancellationToken);
        if (!result)
            return NotFound();

        return Ok();
    }

    /// <summary>
    /// Calcular comisión según proveedor y tipo de servicio
    /// Retorna la regla más específica que aplique
    /// </summary>
    [HttpGet("calculate")]
    public async Task<IActionResult> Calculate([FromQuery] string? supplierId, [FromQuery] string? serviceType, CancellationToken cancellationToken)
    {
        var resolvedSupplierId = await ResolveOptionalSupplierIdAsync(supplierId, cancellationToken);
        if (supplierId is not null && resolvedSupplierId is null)
            return NotFound("Proveedor no encontrado.");

        var percent = await _commissionService.CalculateCommissionAsync(resolvedSupplierId, serviceType, cancellationToken);
        return Ok(new { commissionPercent = percent });
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1): listado de comisiones de vendedor devengadas (insumo de la
    /// futura pantalla de liquidacion). Filtrable por vendedor / periodo / estado.
    ///
    /// <para><b>Gate de permiso</b>: la comision revela margen/ganancia (dato sensible tipo costo), por eso
    /// exige <c>cobranzas.see_cost</c> — el mismo permiso que destapa los costos en el resto del sistema. Un
    /// vendedor comun (que NO tiene see_cost) no puede leer comisiones, ni las propias: este endpoint es para
    /// el back-office que liquida. No hace falta enmascarado parcial porque el gate es todo-o-nada.</para>
    /// </summary>
    [HttpGet("accruals")]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    public async Task<ActionResult<PagedResponse<CommissionAccrualDto>>> GetAccruals(
        [FromQuery] CommissionAccrualsQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _commissionService.GetAccrualsAsync(query, cancellationToken));
    }

    /// <summary>
    /// Auditoria ERP 2026-06-13 (decision del dueño): resumen MENSUAL de comisiones por vendedor (pantalla
    /// "Comisiones"). Devuelve, para el (año, mes) pedido, un renglon por vendedor con su total de comision
    /// por moneda. El detalle reserva-por-reserva se obtiene con <c>GET /api/commissions/accruals</c>
    /// filtrando por <c>sellerUserId</c> + <c>from/to</c>.
    ///
    /// <para><b>Gate (decision del dueño)</b>: esta pantalla la ve SOLO el dueño/admin. Por eso
    /// <c>[Authorize(Roles="Admin")]</c> y NO un permiso (el Colaborador tiene <c>cobranzas.see_cost</c>, asi
    /// que reusar ese permiso —como hace <c>/accruals</c>— dejaria entrar al back-office, no solo al dueño).
    /// El gate por rol Admin es el que ya usan los endpoints de alta/baja de reglas de este mismo controller.</para>
    /// </summary>
    [HttpGet("summary")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CommissionMonthlySummaryDto>> GetMonthlySummary(
        [FromQuery] int year, [FromQuery] int month, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _commissionService.GetMonthlySummaryAsync(year, month, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<int?> ResolveOptionalSupplierIdAsync(string? supplierPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplierPublicIdOrLegacyId))
            return null;

        var supplier = await _entityReferenceResolver.FindAsync<Supplier>(supplierPublicIdOrLegacyId, cancellationToken);
        return supplier?.Id;
    }
}
