using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Reservations;
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
    public async Task<IActionResult> GetAll(string reservaId, CancellationToken ct)
    {
        return Ok(await _bookingService.GetPackagesAsync(reservaId, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
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
            return BadRequest(new { message = ex.Message });
        }
        catch
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
            return BadRequest(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el paquete.");
        }
    }

    [HttpPatch]
    [Route("/api/package-bookings/{publicIdOrLegacyId}/status")]
    [Authorize]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] ServiceStatusUpdateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _bookingService.UpdatePackageStatusAsync(publicIdOrLegacyId, req.Status, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
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
