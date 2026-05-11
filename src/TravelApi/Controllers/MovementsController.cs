using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// B1.15 Fase D' (2026-05-11): vista cronologica unificada de movimientos
/// financieros. Read-only. La logica de filter mine vive en el service —
/// Vendedor solo ve movimientos de sus reservas; Admin / Colaborador
/// (cobranzas.view_all) ven todo.
/// </summary>
[ApiController]
[Route("api/movements")]
[Authorize]
public class MovementsController : ControllerBase
{
    private readonly IMovementsService _service;

    public MovementsController(IMovementsService service)
    {
        _service = service;
    }

    [HttpGet]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<MovementDto>>> Get([FromQuery] MovementsListQuery query, CancellationToken ct)
        => Ok(await _service.GetAsync(query, ct));
}
