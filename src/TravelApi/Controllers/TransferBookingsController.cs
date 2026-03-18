using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

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

    [HttpGet]
    public async Task<IActionResult> GetAll(int reservaId, CancellationToken ct)
    {
        var transfers = await _bookingService.GetTransfersAsync(reservaId, ct);
        return Ok(transfers);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int reservaId, [FromBody] CreateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var transfer = await _bookingService.CreateTransferAsync(reservaId, req, ct);
            return Ok(transfer);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando traslado: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int reservaId, int id, [FromBody] UpdateTransferRequest req, CancellationToken ct)
    {
        try
        {
            var transfer = await _bookingService.UpdateTransferAsync(reservaId, id, req, ct);
            return Ok(transfer);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando traslado: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int reservaId, int id, CancellationToken ct)
    {
        try
        {
            await _bookingService.DeleteTransferAsync(reservaId, id, ct);
            return Ok();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando traslado: {ex.Message}");
        }
    }
}


