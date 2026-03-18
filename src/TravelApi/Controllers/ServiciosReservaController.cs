using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/servicios-reserva")]
[Authorize]
public class ServiciosReservaController : ControllerBase
{
    private readonly IServicioReservaService _servicioReservaService;

    public ServiciosReservaController(IServicioReservaService servicioReservaService)
    {
        _servicioReservaService = servicioReservaService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ServicioReserva>>> GetServicios(CancellationToken cancellationToken)
    {
        var servicios = await _servicioReservaService.GetServiciosAsync(cancellationToken);
        return Ok(servicios);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ServicioReserva>> GetServicio(int id, CancellationToken cancellationToken)
    {
        var servicio = await _servicioReservaService.GetServicioByIdAsync(id, cancellationToken);
        if (servicio is null)
        {
            return NotFound();
        }

        return Ok(servicio);
    }

    [HttpPost("{id:int}/segments")]
    public async Task<ActionResult<FlightSegment>> CreateSegment(
        int id,
        FlightSegment segment,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdSegment = await _servicioReservaService.CreateSegmentAsync(id, segment, cancellationToken);
            return CreatedAtAction(nameof(GetServicio), new { id = id }, createdSegment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
