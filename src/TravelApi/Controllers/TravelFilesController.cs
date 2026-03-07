using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/travelfiles")]
[Authorize]
public class TravelFilesController : ControllerBase
{
    private readonly ITravelFileService _travelFileService;
    private readonly IVoucherService _voucherService;

    public TravelFilesController(ITravelFileService travelFileService, IVoucherService voucherService)
    {
        _travelFileService = travelFileService;
        _voucherService = voucherService;
    }

    [HttpGet]
    public async Task<IActionResult> GetFiles()
    {
        try
        {
            var files = await _travelFileService.GetFilesAsync();
            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, Stack = ex.ToString() });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetFile(int id)
    {
        try 
        {
            var dto = await _travelFileService.GetFileAsync(id);
            return Ok(dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, Stack = ex.StackTrace });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateFile(CreateFileRequest request)
    {
        try 
        {
            var file = await _travelFileService.CreateFileAsync(request);
            return CreatedAtAction(nameof(GetFile), new { id = file.Id }, file);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando expediente: {ex.Message}");
        }
    }

    [HttpPost("{id}/services")]
    public async Task<IActionResult> AddService(int id, AddServiceRequest request)
    {
        try
        {
            var (reservation, warning) = await _travelFileService.AddServiceAsync(id, request);
            if (warning != null)
                return Ok(new { reservation, Warning = warning });
                
            return Ok(reservation);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error agregando servicio: {ex.Message}");
        }
    }

    [HttpPut("services/{serviceId}")]
    public async Task<IActionResult> UpdateService(int serviceId, AddServiceRequest request)
    {
        try
        {
            var service = await _travelFileService.UpdateServiceAsync(serviceId, request);
            return Ok(service);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando servicio: {ex.Message}");
        }
    }

    [HttpDelete("services/{serviceId}")]
    public async Task<IActionResult> RemoveService(int serviceId)
    {
        try
        {
            await _travelFileService.RemoveServiceAsync(serviceId);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error eliminando servicio: {ex.Message}");
        }
    }

    // ==================== PASSENGERS ====================
    [HttpGet("{id}/passengers")]
    public async Task<ActionResult> GetPassengers(int id)
    {
        var passengers = await _travelFileService.GetPassengersAsync(id);
        return Ok(passengers);
    }

    [HttpPost("{id}/passengers")]
    public async Task<ActionResult> AddPassenger(int id, Passenger passenger)
    {
        try
        {
            var dto = await _travelFileService.AddPassengerAsync(id, passenger);
            return CreatedAtAction(nameof(GetFile), new { id }, dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error agregando pasajero: {ex.Message}");
        }
    }

    [HttpPut("passengers/{passengerId}")]
    public async Task<ActionResult> UpdatePassenger(int passengerId, Passenger updated)
    {
        try
        {
            var dto = await _travelFileService.UpdatePassengerAsync(passengerId, updated);
            return Ok(dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando pasajero: {ex.Message}");
        }
    }

    [HttpDelete("passengers/{passengerId}")]
    public async Task<IActionResult> RemovePassenger(int passengerId)
    {
        try
        {
            await _travelFileService.RemovePassengerAsync(passengerId);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando pasajero: {ex.Message}");
        }
    }

    // ==================== PAYMENTS ====================
    [HttpGet("{id}/payments")]
    public async Task<ActionResult> GetFilePayments(int id)
    {
        var payments = await _travelFileService.GetFilePaymentsAsync(id);
        return Ok(payments);
    }

    [HttpPost("{id}/payments")]
    public async Task<ActionResult> AddPayment(int id, Payment payment)
    {
        try
        {
            var dto = await _travelFileService.AddPaymentAsync(id, payment);
            return CreatedAtAction(nameof(GetFile), new { id }, dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error registrando pago: {ex.Message}");
        }
    }

    [HttpPut("{id}/payments/{paymentId}")]
    public async Task<ActionResult> UpdatePayment(int id, int paymentId, Payment updatedPayment)
    {
        try
        {
            var dto = await _travelFileService.UpdatePaymentAsync(id, paymentId, updatedPayment);
            return Ok(dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando pago: {ex.Message}");
        }
    }

    [HttpDelete("{id}/payments/{paymentId}")]
    public async Task<IActionResult> DeletePayment(int id, int paymentId)
    {
        try
        {
            await _travelFileService.DeletePaymentAsync(id, paymentId);
            return Ok();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando pago: {ex.Message}");
        }
    }

    // ==================== STATUS ====================
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusDto request)
    {
        try
        {
            var file = await _travelFileService.UpdateStatusAsync(id, request.Status);
            return Ok(file);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
             return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error actualizando estado: {ex.Message}");
        }
    }

    [HttpPut("{id}/archive")]
    public async Task<IActionResult> ArchiveFile(int id)
    {
        try
        {
            var file = await _travelFileService.ArchiveFileAsync(id);
            return Ok(file);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error archivando file: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteFile(int id)
    {
        try
        {
            await _travelFileService.DeleteFileAsync(id);
            return Ok();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando expediente: {ex.Message}");
        }
    }

    // ==================== VOUCHER ====================
    [HttpGet("{id}/voucher")]
    public async Task<IActionResult> GenerateVoucher(int id, CancellationToken cancellationToken)
    {
        try
        {
            var html = await _voucherService.GenerateVoucherAsync(id, cancellationToken);
            return File(html, "text/html", $"voucher-{id}.html");
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generando voucher: {ex.Message}");
        }
    }
}

public record UpdateStatusDto(string Status);
