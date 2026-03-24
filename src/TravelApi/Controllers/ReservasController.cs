using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas")]
[Authorize]
public class ReservasController : ControllerBase
{
    private readonly IReservaService _reservaService;
    private readonly IVoucherService _voucherService;
    private readonly IWhatsAppDeliveryService _whatsAppDeliveryService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        IWhatsAppDeliveryService whatsAppDeliveryService,
        EntityReferenceResolver entityReferenceResolver)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
        _entityReferenceResolver = entityReferenceResolver;
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

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<IActionResult> GetReserva(string publicIdOrLegacyId)
    {
        try 
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
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
            var dto = await _reservaService.GetReservaByIdAsync(reserva.Id);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId = reserva.PublicId }, dto);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando reserva: {ex.Message}");
        }
    }

    [HttpPost("{publicIdOrLegacyId}/services")]
    public async Task<IActionResult> AddService(string publicIdOrLegacyId, AddServiceRequest request)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpPut("services/{servicePublicIdOrLegacyId}")]
    public async Task<IActionResult> UpdateService(string servicePublicIdOrLegacyId, AddServiceRequest request)
    {
        try
        {
            var serviceId = await _entityReferenceResolver.ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpDelete("services/{servicePublicIdOrLegacyId}")]
    public async Task<IActionResult> RemoveService(string servicePublicIdOrLegacyId)
    {
        try
        {
            var serviceId = await _entityReferenceResolver.ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, HttpContext.RequestAborted);
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
    [HttpGet("{publicIdOrLegacyId}/passengers")]
    public async Task<ActionResult> GetPassengers(string publicIdOrLegacyId)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
        var passengers = await _reservaService.GetPassengersAsync(id);
        return Ok(passengers);
    }

    [HttpPost("{publicIdOrLegacyId}/passengers")]
    public async Task<ActionResult> AddPassenger(string publicIdOrLegacyId, Passenger passenger)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.AddPassengerAsync(id, passenger);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
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

    [HttpPut("passengers/{passengerPublicIdOrLegacyId}")]
    public async Task<ActionResult> UpdatePassenger(string passengerPublicIdOrLegacyId, Passenger updated)
    {
        try
        {
            var passengerId = await _entityReferenceResolver.ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpDelete("passengers/{passengerPublicIdOrLegacyId}")]
    public async Task<IActionResult> RemovePassenger(string passengerPublicIdOrLegacyId)
    {
        try
        {
            var passengerId = await _entityReferenceResolver.ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, HttpContext.RequestAborted);
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
    [HttpGet("{publicIdOrLegacyId}/payments")]
    public async Task<ActionResult> GetReservaPayments(string publicIdOrLegacyId)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
        var payments = await _reservaService.GetReservaPaymentsAsync(id);
        return Ok(payments);
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    public async Task<ActionResult> AddPayment(string publicIdOrLegacyId, Payment payment)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.AddPaymentAsync(id, payment);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
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

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<ActionResult> UpdatePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, Payment updatedPayment)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<IActionResult> DeletePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, HttpContext.RequestAborted);
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
    [HttpPut("{publicIdOrLegacyId}/status")]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] UpdateStatusDto request)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpPut("{publicIdOrLegacyId}/archive")]
    public async Task<IActionResult> ArchiveReserva(string publicIdOrLegacyId)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
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

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<IActionResult> DeleteReserva(string publicIdOrLegacyId)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
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
    [HttpGet("{publicIdOrLegacyId}/voucher")]
    public async Task<IActionResult> GenerateVoucher(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var html = await _voucherService.GenerateVoucherHtmlAsync(id, cancellationToken);
            return File(html, "text/html", $"voucher-{publicIdOrLegacyId}.html");
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

    [HttpGet("{publicIdOrLegacyId}/voucher/pdf")]
    public async Task<IActionResult> GenerateVoucherPdf(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var pdf = await _voucherService.GenerateVoucherPdfAsync(id, cancellationToken);
            return File(pdf, "application/pdf", $"voucher-{publicIdOrLegacyId}.pdf");
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

    [HttpPatch("{publicIdOrLegacyId}/whatsapp-contact")]
    public async Task<IActionResult> UpdateWhatsAppContact(
        string publicIdOrLegacyId,
        [FromBody] UpdateReservaWhatsAppContactRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/whatsapp/voucher-preview")]
    public async Task<IActionResult> GetVoucherPreview(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
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

    [HttpPost("{publicIdOrLegacyId}/whatsapp/send-voucher")]
    public async Task<IActionResult> SendVoucher(
        string publicIdOrLegacyId,
        [FromBody] SendVoucherRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/whatsapp/history")]
    public async Task<IActionResult> GetWhatsAppHistory(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
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
