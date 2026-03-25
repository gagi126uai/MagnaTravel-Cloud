using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/transfers")]
[Authorize]
public class TransferBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public TransferBookingsController(IBookingService bookingService, EntityReferenceResolver entityReferenceResolver)
    {
        _bookingService = bookingService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
        var transfers = await _bookingService.GetTransfersAsync(resolvedReservaId, ct);
        return Ok(transfers);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var transfer = await _bookingService.CreateTransferAsync(resolvedReservaId, req, ct);
            return Ok(transfer);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el traslado.");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedTransferId = await _entityReferenceResolver.ResolveRequiredIdAsync<TransferBooking>(id, ct);
            var transfer = await _bookingService.UpdateTransferAsync(resolvedReservaId, resolvedTransferId, req, ct);
            return Ok(transfer);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el traslado.");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedTransferId = await _entityReferenceResolver.ResolveRequiredIdAsync<TransferBooking>(id, ct);
            await _bookingService.DeleteTransferAsync(resolvedReservaId, resolvedTransferId, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el traslado.");
        }
    }
}


