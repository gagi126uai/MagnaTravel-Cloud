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
    string CabinClass,
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
