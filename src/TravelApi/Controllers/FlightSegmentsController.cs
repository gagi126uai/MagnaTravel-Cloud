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
[Route("api/reservas/{reservaId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public FlightSegmentsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    // Lectura de sub-coleccion: solo el dueño de la reserva (o quien tenga
    // reservas.view_all / Admin) puede listar los servicios. Antes cualquier
    // usuario logueado veia (y con NetCost) servicios de reservas ajenas.
    [HttpGet]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetFlightsAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.GetFlightByIdAsync(reservaId, id, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // POST/PUT/DELETE: mismo patron que Hotel. Antes estaba [Authorize(Roles="Admin")]
    // hardcodeado, lo que impedia que un vendedor no-admin cargara vuelos (Hotel si
    // lo permitia). Ahora requiere reservas.edit + ownership de la reserva.
    [HttpPost]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
            // ADR-020: el candado de la reserva confirmada lanza InvalidOperationException; el
            // frontend abre el flujo de autorizacion con 409 (igual que Update).
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el vuelo.");
        }
    }

    [HttpPut("{id}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
            // B1.15 Fase 0' (CODE-04): MutationGuards + guards de status. 409.
            // P1 "circuito proveedor" (2026-07-21): mismo envelope aditivo `code` que
            // HotelBookingsController.Update — el frontend lo usara en otra tanda.
            if (ex is ServiceCancellationRejectedException rejected)
                return Conflict(new { code = rejected.Code, message = ex.Message });
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el vuelo.");
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
            return Ok(await _bookingService.ConfirmFlightCostAsync(reservaId, id, req ?? new ConfirmCostRequest(), ct));
        }
        catch (FeatureNotEnabledException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    // Autorizacion: antes solo [Authorize] (cualquier logueado tocaba el status de
    // un vuelo ajeno y veia NetCost). Se exige reservas.edit como minimo.
    // TODO (follow-up ownership): la ruta identifica el FlightSegment por su id, no
    // por reservaId. Falta OwnedEntity.FlightSegment + branch en OwnershipResolver
    // (FlightSegment -> Reserva.ResponsibleUserId) para cerrar ownership fino.
    [HttpPatch]
    [Route("/api/flight-segments/{publicIdOrLegacyId}/status")]
    [Authorize]
    [RequirePermission(Permissions.ReservasEdit)]
    // ADR-020: ownership fino por el id del vuelo (la ruta no trae reservaId). Antes faltaba.
    [RequireOwnership(OwnedEntity.FlightSegment, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdateFlightStatusAsync(publicIdOrLegacyId, req.Status, req.ConfirmationNumber, ct));
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

    // ADR-020 F2: "Marcar emitido" — estampa la emision del ticket (lo que RESUELVE el aereo para
    // que el file pueda pasar a Confirmada). El TicketNumber es opcional (puede llegar despues).
    [HttpPost("{id}/mark-issued")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> MarkIssued(string reservaId, string id, [FromBody] MarkTicketIssuedRequest? req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.MarkFlightTicketIssuedAsync(reservaId, id, req?.TicketNumber, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    public class MarkTicketIssuedRequest
    {
        public string? TicketNumber { get; set; }
    }

    [HttpDelete("{id}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
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
