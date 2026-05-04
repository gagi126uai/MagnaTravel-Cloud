using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
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
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetHotelsAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.GetHotelByIdAsync(reservaId, id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateHotelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.CreateHotelAsync(reservaId, req, ct));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear la reserva de hotel.");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdateHotelRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateHotelAsync(reservaId, id, req, ct));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar la reserva de hotel.");
        }
    }

    /// <summary>
    /// PATCH absoluto (sin reservaId) para cambiar SOLO el Status del hotel desde
    /// la cuenta corriente del proveedor. Aplica los mismos guards que Update completo.
    /// </summary>
    [HttpPatch]
    [Route("/api/hotel-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateHotelStatusAsync(publicIdOrLegacyId, req.Status, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar la reserva de hotel.");
        }
    }
}
