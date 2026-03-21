using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
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
    private readonly IWhatsAppDeliveryService _whatsAppDeliveryService;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        IWhatsAppDeliveryService whatsAppDeliveryService)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
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
            var createdByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var reserva = await _reservaService.CreateReservaAsync(request, createdByUserId);
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
            if (request.Status == EstadoReserva.Operational)
            {
                await _whatsAppDeliveryService.PrepareVoucherDraftAsync(id, HttpContext.RequestAborted);
            }
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
            var html = await _voucherService.GenerateVoucherHtmlAsync(id, cancellationToken);
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

    [HttpGet("{id}/voucher/pdf")]
    public async Task<IActionResult> GenerateVoucherPdf(int id, CancellationToken cancellationToken)
    {
        try
        {
            var pdf = await _voucherService.GenerateVoucherPdfAsync(id, cancellationToken);
            return File(pdf, "application/pdf", $"voucher-{id}.pdf");
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generando voucher PDF: {ex.Message}");
        }
    }

    [HttpPatch("{id}/whatsapp-contact")]
    public async Task<IActionResult> UpdateWhatsAppContact(
        int id,
        [FromBody] UpdateReservaWhatsAppContactRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var preview = await _whatsAppDeliveryService.UpdateReservaWhatsAppContactAsync(
                id,
                request.WhatsAppPhoneOverride,
                cancellationToken);
            return Ok(preview);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando contacto WhatsApp: {ex.Message}");
        }
    }

    [HttpGet("{id}/whatsapp/voucher-preview")]
    public async Task<IActionResult> GetVoucherPreview(int id, CancellationToken cancellationToken)
    {
        try
        {
            var preview = await _whatsAppDeliveryService.GetVoucherPreviewAsync(id, cancellationToken);
            return Ok(preview);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error obteniendo preview WhatsApp: {ex.Message}");
        }
    }

    [HttpPost("{id}/whatsapp/send-voucher")]
    public async Task<IActionResult> SendVoucher(
        int id,
        [FromBody] SendVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var performedBy = User.Identity?.Name ?? "Agente";
            var delivery = await _whatsAppDeliveryService.SendVoucherAsync(id, request.Caption, performedBy, cancellationToken);
            return Ok(delivery);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = ex.Message });
        }
    }

    [HttpGet("{id}/whatsapp/history")]
    public async Task<IActionResult> GetWhatsAppHistory(int id, CancellationToken cancellationToken)
    {
        try
        {
            var history = await _whatsAppDeliveryService.GetHistoryAsync(id, cancellationToken);
            return Ok(history);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error obteniendo historial WhatsApp: {ex.Message}");
        }
    }
}

public record UpdateStatusDto(string Status);
