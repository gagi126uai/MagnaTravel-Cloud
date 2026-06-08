using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// Bloque 3: endpoints CRUD de Asistencia al viajero (seguro) anidados bajo una reserva.
/// Espejo EXACTO de <see cref="HotelBookingsController"/>: mismos permisos (ReservasEdit para
/// POST/PUT, ReservasDelete para DELETE) y mismo ownership (la reserva del path) desde el dia uno.
/// El PATCH /status absoluto sirve a la cuenta corriente del proveedor (aseguradora).
/// </summary>
[ApiController]
[Route("api/reservas/{reservaId}/assistances")]
[Authorize]
public class AssistanceBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public AssistanceBookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    // Lectura de sub-coleccion: solo el dueño de la reserva (o reservas.view_all / Admin).
    [HttpGet]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetAssistancesAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.GetAssistanceByIdAsync(reservaId, id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateAssistanceRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.CreateAssistanceAsync(reservaId, req, ct));
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
            // ADR-020: candado de reserva confirmada -> 409 para que el front abra autorizacion.
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear la asistencia.");
        }
    }

    [HttpPut("{id}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdateAssistanceRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateAssistanceAsync(reservaId, id, req, ct));
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
            // B1.15 Fase 0' (CODE-04): MutationGuards rechaza con factura AFIP viva o
            // voucher Issued. Tambien guards de status existentes. 409.
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar la asistencia.");
        }
    }

    // ADR-017 F1.3 (§2.8, D8c): boton "Confirmar costo". Ver HotelBookingsController.
    [HttpPost("{id}/confirm-cost")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> ConfirmCost(string reservaId, string id, [FromBody] ConfirmCostRequest? req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.ConfirmAssistanceCostAsync(reservaId, id, req ?? new ConfirmCostRequest(), ct));
        }
        catch (FeatureNotEnabledException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    /// <summary>
    /// PATCH absoluto (sin reservaId) para cambiar SOLO el Status de la asistencia desde la
    /// cuenta corriente del proveedor (aseguradora). Aplica los mismos guards que Update completo.
    /// </summary>
    // Autorizacion: se exige reservas.edit + ownership (igual que HotelBookingsController).
    [HttpPatch]
    [Route("/api/assistance-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    [RequirePermission(Permissions.ReservasEdit)]
    // ADR-020: ownership fino por el id de la asistencia (la ruta no trae reservaId). Antes faltaba.
    [RequireOwnership(OwnedEntity.AssistanceBooking, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateAssistanceStatusAsync(publicIdOrLegacyId, req.Status, req.ConfirmationNumber, ct));
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
            await _bookingService.DeleteAssistanceAsync(reservaId, id, ct);
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar la asistencia.");
        }
    }
}
