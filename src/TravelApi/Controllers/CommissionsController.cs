using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CommissionsController : ControllerBase
{
    private readonly ICommissionService _commissionService;

    public CommissionsController(ICommissionService commissionService)
    {
        _commissionService = commissionService;
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
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
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
    public async Task<IActionResult> Calculate([FromQuery] int? supplierId, [FromQuery] string? serviceType, CancellationToken cancellationToken)
    {
        var percent = await _commissionService.CalculateCommissionAsync(supplierId, serviceType, cancellationToken);
        return Ok(new { commissionPercent = percent });
    }
}
