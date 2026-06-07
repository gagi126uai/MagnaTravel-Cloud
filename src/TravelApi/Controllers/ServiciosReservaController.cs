using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
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

    /// <summary>
    /// B1.15 Fase 2a (FIX 8): este endpoint devolvia la entidad cruda
    /// <c>ServicioReserva</c> de TODOS los travel files con costo proveedor visible
    /// y sin filtro por owner. No tiene uso conocido en el frontend (grep en
    /// TravelWeb 2026-05-08: 0 referencias). Se deprecia con 410 Gone.
    ///
    /// Ruta de reemplazo: <c>GET /api/reservas/{id}</c> devuelve los servicios
    /// embebidos con el masking y filter mine ya aplicados.
    /// </summary>
    [HttpGet]
    public IActionResult GetServicios()
    {
        return StatusCode(StatusCodes.Status410Gone, new
        {
            message = "Este endpoint fue deprecado. Use GET /api/reservas/{id} para obtener los servicios de una reserva con masking de costos y filter mine aplicados.",
            replacement = "GET /api/reservas/{id}"
        });
    }

    [HttpGet("{id:int}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Servicio, "id", bypassPermission: Permissions.ReservasViewAll)]
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
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Servicio, "id", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<FlightSegment>> CreateSegment(
        int id,
        FlightSegmentUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdSegment = await _servicioReservaService.CreateSegmentAsync(id, MapSegment(request), cancellationToken);
            return CreatedAtAction(nameof(GetServicio), new { id = id }, createdSegment);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo agregar el tramo del vuelo." });
        }
    }

    private static FlightSegment MapSegment(FlightSegmentUpsertRequest request)
    {
        return new FlightSegment
        {
            SupplierId = request.SupplierId,
            AirlineCode = request.AirlineCode,
            AirlineName = request.AirlineName,
            FlightNumber = request.FlightNumber,
            Origin = request.Origin,
            OriginCity = request.OriginCity,
            Destination = request.Destination,
            DestinationCity = request.DestinationCity,
            DepartureTime = request.DepartureTime,
            ArrivalTime = request.ArrivalTime,
            CabinClass = request.CabinClass,
            Baggage = request.Baggage,
            TicketNumber = request.TicketNumber,
            FareBase = request.FareBase,
            PNR = request.PNR,
            Status = request.Status,
            NetCost = request.NetCost,
            SalePrice = request.SalePrice,
            Commission = request.Commission,
            Tax = request.Tax,
            Notes = request.Notes
        };
    }
}

public record FlightSegmentUpsertRequest(
    int SupplierId,
    string AirlineCode,
    string? AirlineName,
    string FlightNumber,
    string Origin,
    string? OriginCity,
    string Destination,
    string? DestinationCity,
    DateTime DepartureTime,
    DateTime ArrivalTime,
    // Ronda 7 (guia UX): Cabina es OPCIONAL en todos los paths, incluido este (tramos del servicio
    // generico). Nullable para que el cliente pueda omitirla; vacio/espacios se normaliza a null
    // en ServicioReservaService.CreateSegmentAsync (nunca se persiste "").
    string? CabinClass,
    string? Baggage,
    string? TicketNumber,
    string? FareBase,
    string? PNR,
    string Status,
    decimal NetCost,
    decimal SalePrice,
    decimal Commission,
    decimal Tax,
    string? Notes);
