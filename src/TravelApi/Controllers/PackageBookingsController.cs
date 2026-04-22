using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas/{reservaId}/packages")]
[Authorize]
public class PackageBookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public PackageBookingsController(IBookingService bookingService, IEntityReferenceResolver entityReferenceResolver)
    {
        _bookingService = bookingService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
        var packages = await _bookingService.GetPackagesAsync(resolvedReservaId, ct);
        return Ok(packages);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(string reservaId, [FromBody] CreatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var package = await _bookingService.CreatePackageAsync(resolvedReservaId, req, ct);
            return Ok(package);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el paquete.");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string reservaId, string id, [FromBody] UpdatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedPackageId = await _entityReferenceResolver.ResolveRequiredIdAsync<PackageBooking>(id, ct);
            var package = await _bookingService.UpdatePackageAsync(resolvedReservaId, resolvedPackageId, req, ct);
            return Ok(package);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el paquete.");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string reservaId, string id, CancellationToken ct)
    {
        try
        {
            var resolvedReservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaId, ct);
            var resolvedPackageId = await _entityReferenceResolver.ResolveRequiredIdAsync<PackageBooking>(id, ct);
            await _bookingService.DeletePackageAsync(resolvedReservaId, resolvedPackageId, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
             return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el paquete.");
        }
    }
}


