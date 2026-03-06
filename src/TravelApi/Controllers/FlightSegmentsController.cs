using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public FlightSegmentsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var flights = await _bookingService.GetFlightsAsync(fileId, ct);
        return Ok(flights);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var flight = await _bookingService.CreateFlightAsync(fileId, req, ct);
            return Ok(flight);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error creando vuelo: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateFlightRequest req, CancellationToken ct)
    {
        try
        {
            var flight = await _bookingService.UpdateFlightAsync(fileId, id, req, ct);
            return Ok(flight);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando vuelo: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeleteFlightAsync(fileId, id, ct);
            return Ok();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando vuelo: {ex.Message}");
        }
    }
}


