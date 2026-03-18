using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas")]
[Authorize]
public class ReservasController : ControllerBase
{
    private readonly IReservaService _reservaService;
    private readonly IVoucherService _voucherService;

    public ReservasController(IReservaService reservaService, IVoucherService voucherService)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
    }

    [HttpGet]
    public async Task<IActionResult> GetReservas()
    {
        try
        {
            var reservas = await _reservaService.GetReservasAsync();
            return Ok(reservas);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, Stack = ex.ToString() });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetReserva(int id)
    {
        try 
        {
            var dto = await _reservaService.GetReservaByIdAsync(id);
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
    public async Task<IActionResult> CreateReserva(CreateReservaRequest request)
    {
        try 
        {
            var reserva = await _reservaService.CreateReservaAsync(request);
            return CreatedAtAction(nameof(GetReserva), new { id = reserva.Id }, reserva);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando reserva: {ex.Message}");
        }
    }

    [HttpPost("{id}/services")]
    public async Task<IActionResult> AddService(int id, AddServiceRequest request)
    {
        try
        {
            var (servicio, warning) = await _reservaService.AddServiceAsync(id, request);
            if (warning != null)
                return Ok(new { servicio, Warning = warning });
                
            return Ok(servicio);
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
            var service = await _reservaService.UpdateServiceAsync(serviceId, request);
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
            await _reservaService.RemoveServiceAsync(serviceId);
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

    // ==================== PASAJEROS ====================
    [HttpGet("{id}/passengers")]
    public async Task<ActionResult> GetPassengers(int id)
    {
        var passengers = await _reservaService.GetPassengersAsync(id);
        return Ok(passengers);
    }

    [HttpPost("{id}/passengers")]
    public async Task<ActionResult> AddPassenger(int id, Passenger passenger)
    {
        try
        {
            var dto = await _reservaService.AddPassengerAsync(id, passenger);
            return CreatedAtAction(nameof(GetReserva), new { id }, dto);
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
            var dto = await _reservaService.UpdatePassengerAsync(passengerId, updated);
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
            await _reservaService.RemovePassengerAsync(passengerId);
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

    // ==================== PAGOS ====================
    [HttpGet("{id}/payments")]
    public async Task<ActionResult> GetReservaPayments(int id)
    {
        var payments = await _reservaService.GetReservaPaymentsAsync(id);
        return Ok(payments);
    }

    [HttpPost("{id}/payments")]
    public async Task<ActionResult> AddPayment(int id, Payment payment)
    {
        try
        {
            var dto = await _reservaService.AddPaymentAsync(id, payment);
            return CreatedAtAction(nameof(GetReserva), new { id }, dto);
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
            var dto = await _reservaService.UpdatePaymentAsync(id, paymentId, updatedPayment);
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
            await _reservaService.DeletePaymentAsync(id, paymentId);
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

    // ==================== ESTADOS ====================
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusDto request)
    {
        try
        {
            var reserva = await _reservaService.UpdateStatusAsync(id, request.Status);
            return Ok(reserva);
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
    public async Task<IActionResult> ArchiveReserva(int id)
    {
        try
        {
            var reserva = await _reservaService.ArchiveReservaAsync(id);
            return Ok(reserva);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error archivando reserva: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteReserva(int id)
    {
        try
        {
            await _reservaService.DeleteReservaAsync(id);
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
            return StatusCode(500, $"Error eliminando reserva: {ex.Message}");
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
