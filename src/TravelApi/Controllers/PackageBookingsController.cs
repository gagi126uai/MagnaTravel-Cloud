using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/packages")]
[Authorize]
public class PackageBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public PackageBookingsController(IBookingService bookingService)
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
        return Ok(await _bookingService.GetPackagesAsync(reservaId, ct));
    }

    [HttpGet("{id}")]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetById(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.GetPackageByIdAsync(reservaId, id, ct));
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
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreatePackageRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.CreatePackageAsync(reservaId, req, ct));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el paquete.");
        }
    }

    [HttpPut("{id}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdatePackageRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdatePackageAsync(reservaId, id, req, ct));
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
            // viva o voucher Issued. Tambien guards de status existentes
            // (downgrade/capacity). 409 Conflict para todos.
            return Conflict(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el paquete.");
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
            return Ok(await _bookingService.ConfirmPackageCostAsync(reservaId, id, req ?? new ConfirmCostRequest(), ct));
        }
        catch (FeatureNotEnabledException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    // Autorizacion: antes solo [Authorize] (cualquier logueado tocaba el status de
    // un paquete ajeno y veia NetCost). Se exige reservas.edit + ownership.
    [HttpPatch]
    [Route("/api/package-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    [RequirePermission(Permissions.ReservasEdit)]
    // ADR-020: ownership fino por el id del paquete (la ruta no trae reservaId). Antes faltaba.
    [RequireOwnership(OwnedEntity.PackageBooking, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdatePackageStatusAsync(publicIdOrLegacyId, req.Status, req.ConfirmationNumber, ct));
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
            await _bookingService.DeletePackageAsync(reservaId, id, ct);
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el paquete.");
        }
    }
}
