using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/rates")]
[Authorize]
public class RatesController : ControllerBase
{
    private readonly IRateService _rateService;

    public RatesController(IRateService rateService) => _rateService = rateService;

    /// <summary>
    /// Listar tarifario con filtros opcionales
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? supplierId, 
        [FromQuery] string? serviceType,
        [FromQuery] bool activeOnly = false,
        CancellationToken ct = default)
    {
        var rates = await _rateService.GetAllAsync(supplierId, serviceType, activeOnly, ct);
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
        [FromQuery] int? supplierId,
        [FromQuery] string? serviceType,
        [FromQuery] string? query,
        CancellationToken ct)
    {
        var rates = await _rateService.SearchAsync(supplierId, serviceType, query, ct);
        return Ok(rates);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] RateDto req, CancellationToken ct)
    {
        var rate = await _rateService.CreateAsync(req, ct);
        return Ok(rate);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] RateDto req, CancellationToken ct)
    {
        var rate = await _rateService.UpdateAsync(id, req, ct);
        if (rate == null) return NotFound();
        return Ok(rate);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _rateService.DeleteAsync(id, ct);
        if (!deleted) return NotFound();
        return Ok();
    }
}
