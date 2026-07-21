using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/transfers")]
[Authorize]
public class TransferBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public TransferBookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    // Lectura de sub-coleccion: solo el dueño de la reserva (o reservas.view_all /
    // Admin). Antes cualquier usuario logueado veia servicios (con NetCost) de
    // reservas ajenas.
    [HttpGet]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetTransfersAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.GetTransferByIdAsync(reservaId, id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // POST/PUT/DELETE: mismo patron que Hotel. Antes [Authorize(Roles="Admin")]
    // hardcodeado bloqueaba a vendedores no-admin. Ahora reservas.edit + ownership.
    [HttpPost]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateTransferRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.CreateTransferAsync(reservaId, req, ct));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el traslado.");
        }
    }

    [HttpPut("{id}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdateTransferRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateTransferAsync(reservaId, id, req, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            // Fix B1 (review 2026-07-21): SupplierCreditReconciler puede rechazar la edicion (ej. subir el
            // costo o reasignar el operador saca un saldo a favor que YA se aplico a otra reserva -
            // INV-SUPCREDIT-001). Sin este catch el GlobalExceptionHandler adjunta el codigo interno de
            // invariante a la respuesta (gate de exposicion de datos); aca viaja SOLO el mensaje en
            // criollo, que el reconciler ya redacta pensado para el usuario final.
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-04): MutationGuards + guards de status. 409.
            // P1 "circuito proveedor" (2026-07-21): mismo envelope aditivo `code` que
            // HotelBookingsController.Update — el frontend lo usara en otra tanda.
            if (ex is ServiceCancellationRejectedException rejected)
                return Conflict(new { code = rejected.Code, message = ex.Message });
            // P2 "circuito proveedor" (2026-07-21): AVISO de costo por debajo de lo pagado al operador.
            if (ex is CostBelowPaidConfirmationRequiredException costBelowPaid)
                return Conflict(new { code = costBelowPaid.Code, message = ex.Message });
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el traslado.");
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
            return Ok(await _bookingService.ConfirmTransferCostAsync(reservaId, id, req ?? new ConfirmCostRequest(), ct));
        }
        catch (FeatureNotEnabledException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    // Autorizacion: antes solo [Authorize] (cualquier logueado tocaba el status de
    // un traslado ajeno y veia NetCost). Se exige reservas.edit + ownership.
    [HttpPatch]
    [Route("/api/transfer-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    [RequirePermission(Permissions.ReservasEdit)]
    // ADR-020: ownership fino por el id del traslado (la ruta no trae reservaId). Antes faltaba.
    [RequireOwnership(OwnedEntity.TransferBooking, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateTransferStatusAsync(publicIdOrLegacyId, req.Status, req.ConfirmationNumber, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            // P1 "circuito proveedor" (2026-07-21): mismo envelope aditivo `code` que Update (arriba).
            if (ex is ServiceCancellationRejectedException rejected)
                return Conflict(new { code = rejected.Code, message = ex.Message });
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ADR-020 F2: marca "no requiere confirmacion" (cualquier vendedor). El traslado pasa a contar
    // como RESUELTO aunque el operador no lo haya confirmado; puede gatillar la confirmacion del file.
    [HttpPost("{id}/no-confirmation")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> MarkNoConfirmation(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.MarkTransferNoConfirmationRequiredAsync(reservaId, id, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeleteTransferAsync(reservaId, id, ct);
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el traslado.");
        }
    }
}
