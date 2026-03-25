using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public FlightSegmentsController(IBookingService bookingService, EntityReferenceResolver entityReferenceResolver)
    {
        _bookingService = bookingService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
        var flights = await _bookingService.GetFlightsAsync(resolvedReservaId, ct);
        return Ok(flights);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var flight = await _bookingService.CreateFlightAsync(resolvedReservaId, req, ct);
            return Ok(flight);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
             return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el vuelo.");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedFlightId = await _entityReferenceResolver.ResolveRequiredIdAsync<FlightSegment>(id, ct);
            var flight = await _bookingService.UpdateFlightAsync(resolvedReservaId, resolvedFlightId, req, ct);
            return Ok(flight);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el vuelo.");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedFlightId = await _entityReferenceResolver.ResolveRequiredIdAsync<FlightSegment>(id, ct);
            await _bookingService.DeleteFlightAsync(resolvedReservaId, resolvedFlightId, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el vuelo.");
        }
    }
}


