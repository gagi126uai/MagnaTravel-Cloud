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
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public PackageBookingsController(IBookingService bookingService, EntityReferenceResolver entityReferenceResolver)
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
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando paquete: {ex.Message}");
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
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando paquete: {ex.Message}");
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
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error eliminando paquete: {ex.Message}");
        }
    }
}


