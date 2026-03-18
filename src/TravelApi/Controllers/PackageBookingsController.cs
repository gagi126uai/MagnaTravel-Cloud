using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

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

    [HttpGet]
    public async Task<IActionResult> GetAll(int reservaId, CancellationToken ct)
    {
        var packages = await _bookingService.GetPackagesAsync(reservaId, ct);
        return Ok(packages);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int reservaId, [FromBody] CreatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var package = await _bookingService.CreatePackageAsync(reservaId, req, ct);
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
    public async Task<IActionResult> Update(int reservaId, int id, [FromBody] UpdatePackageRequest req, CancellationToken ct)
    {
        try
        {
            var package = await _bookingService.UpdatePackageAsync(reservaId, id, req, ct);
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
    public async Task<IActionResult> Delete(int reservaId, int id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeletePackageAsync(reservaId, id, ct);
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


