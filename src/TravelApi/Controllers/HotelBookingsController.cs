using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/hotels")]
[Authorize]
public class HotelBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public HotelBookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int reservaId, CancellationToken ct)
    {
        var hotels = await _bookingService.GetHotelsAsync(reservaId, ct);
        return Ok(hotels);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int reservaId, int id, CancellationToken ct)
    {
        try
        {
            var hotel = await _bookingService.GetHotelByIdAsync(reservaId, id, ct);
            return Ok(hotel);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int reservaId, [FromBody] CreateHotelRequest req, CancellationToken ct)
    {
        try
        {
            var hotel = await _bookingService.CreateHotelAsync(reservaId, req, ct);
            return Ok(hotel);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando hotel: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int reservaId, int id, [FromBody] UpdateHotelRequest req, CancellationToken ct)
    {
        try
        {
            var hotel = await _bookingService.UpdateHotelAsync(reservaId, id, req, ct);
            return Ok(hotel);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando hotel: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int reservaId, int id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeleteHotelAsync(reservaId, id, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando hotel: {ex.Message}");
        }
    }
}


