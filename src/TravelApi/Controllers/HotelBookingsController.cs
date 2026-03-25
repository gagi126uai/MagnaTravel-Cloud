using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/hotels")]
[Authorize]
public class HotelBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public HotelBookingsController(IBookingService bookingService, EntityReferenceResolver entityReferenceResolver)
    {
        _bookingService = bookingService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
        var hotels = await _bookingService.GetHotelsAsync(resolvedReservaId, ct);
        return Ok(hotels);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedHotelId = await _entityReferenceResolver.ResolveRequiredIdAsync<HotelBooking>(id, ct);
            var hotel = await _bookingService.GetHotelByIdAsync(resolvedReservaId, resolvedHotelId, ct);
            return Ok(hotel);
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
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var hotel = await _bookingService.CreateHotelAsync(resolvedReservaId, req, ct);
            return Ok(hotel);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
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
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedHotelId = await _entityReferenceResolver.ResolveRequiredIdAsync<HotelBooking>(id, ct);
            var hotel = await _bookingService.UpdateHotelAsync(resolvedReservaId, resolvedHotelId, req, ct);
            return Ok(hotel);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar la reserva de hotel.");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedHotelId = await _entityReferenceResolver.ResolveRequiredIdAsync<HotelBooking>(id, ct);
            await _bookingService.DeleteHotelAsync(resolvedReservaId, resolvedHotelId, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar la reserva de hotel.");
        }
    }
}


