using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

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

    // Lectura de sub-coleccion: solo el dueño de la reserva (o reservas.view_all /
    // Admin). Antes el GET solo tenia [Authorize] heredado, asi que cualquier
    // usuario logueado veia servicios (con NetCost) de reservas ajenas.
    [HttpGet]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetHotelsAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
            // B1.15 Fase 0' (CODE-04): MutationGuards rechaza con factura AFIP
            // viva o voucher Issued. Tambien guards de status existentes. 409.
            return Conflict(new { message = ex.Message });
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
    // Autorizacion: hasta ahora este PATCH tenia solo [Authorize] (cualquier
    // usuario logueado podia cambiar el status de un hotel de una reserva ajena y
    // recibir el NetCost real). Se exige reservas.edit como minimo.
    // TODO (follow-up ownership): la ruta identifica el HotelBooking por su id, no
    // por reservaId, asi que RequireOwnership(OwnedEntity.Reserva, "reservaId") no
    // aplica. Falta un OwnedEntity.HotelBooking + branch en OwnershipResolver
    // (HotelBooking -> Reserva.ResponsibleUserId) para cerrar ownership fino.
    [HttpPatch]
    [Route("/api/hotel-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    [RequirePermission(Permissions.ReservasEdit)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateHotelStatusAsync(publicIdOrLegacyId, req.Status, req.ConfirmationNumber, ct));
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
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
