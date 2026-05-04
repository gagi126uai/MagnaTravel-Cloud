using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public FlightSegmentsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetFlightsAsync(reservaId, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateFlightRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.CreateFlightAsync(reservaId, req, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch
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
            return Ok(await _bookingService.UpdateFlightAsync(reservaId, id, req, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el vuelo.");
        }
    }

    [HttpPatch]
    [Route("/api/flight-segments/{publicIdOrLegacyId}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateFlightStatusAsync(publicIdOrLegacyId, req.Status, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeleteFlightAsync(reservaId, id, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el vuelo.");
        }
    }
}
