using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Errors;
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
    private readonly ITimelineService _timelineService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly ILogger<ReservasController> _logger;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        IWhatsAppDeliveryService whatsAppDeliveryService,
        ITimelineService timelineService,
        IEntityReferenceResolver entityReferenceResolver,
        ILogger<ReservasController> logger)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
        _timelineService = timelineService;
        _entityReferenceResolver = entityReferenceResolver;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetReservas([FromQuery] ReservaListQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var reservas = await _reservaService.GetReservasAsync(query, cancellationToken);
            return Ok(reservas);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting reservas");
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudieron obtener las reservas.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener la reserva.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/timeline")]
    public async Task<IActionResult> GetReservaTimeline(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try 
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var timeline = await _timelineService.GetTimelineAsync(id, cancellationToken);
            return Ok(timeline);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting timeline for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el historial.");
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
            _logger.LogError(ex, "Unexpected error creating reserva");
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear la reserva.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo agregar el servicio." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding service to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo agregar el servicio.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el servicio." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating service {ServiceId}", servicePublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el servicio.");
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
            _logger.LogError(ex, "Unexpected error removing service {ServiceId}", servicePublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el servicio.");
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
    public async Task<ActionResult> AddPassenger(string publicIdOrLegacyId, PassengerUpsertRequest passenger)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.AddPassengerAsync(id, MapPassenger(passenger));
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo agregar el pasajero." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding passenger to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo agregar el pasajero.");
        }
    }

    [HttpPut("passengers/{passengerPublicIdOrLegacyId}")]
    public async Task<ActionResult> UpdatePassenger(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated)
    {
        try
        {
            var passengerId = await _entityReferenceResolver.ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.UpdatePassengerAsync(passengerId, MapPassenger(updated));
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pasajero." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating passenger {PassengerId}", passengerPublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el pasajero.");
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
            _logger.LogError(ex, "Unexpected error removing passenger {PassengerId}", passengerPublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el pasajero.");
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
    public async Task<ActionResult> AddPayment(string publicIdOrLegacyId, PaymentUpsertRequest payment)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.AddPaymentAsync(id, MapPayment(payment));
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo registrar el pago." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding payment to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo registrar el pago.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<ActionResult> UpdatePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, PaymentUpsertRequest updatedPayment)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, HttpContext.RequestAborted);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, HttpContext.RequestAborted);
            var dto = await _reservaService.UpdatePaymentAsync(id, paymentId, MapPayment(updatedPayment));
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pago." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating payment {PaymentId} for reserva {ReservaId}", paymentPublicIdOrLegacyId, publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el pago.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo eliminar el pago." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting payment {PaymentId} for reserva {ReservaId}", paymentPublicIdOrLegacyId, publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el pago.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el estado de la reserva." });
        }
        catch (InvalidOperationException)
        {
             return BadRequest(new { message = "No se pudo actualizar el estado de la reserva." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating status for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el estado de la reserva.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error archiving reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo archivar la reserva.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo eliminar la reserva." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar la reserva.");
        }
    }

    // ==================== VOUCHER ====================
    [HttpGet("{publicIdOrLegacyId}/voucher")]
    public async Task<IActionResult> GenerateVoucher(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(id, cancellationToken);
            return File(htmlBytes, "text/html", $"voucher-{publicIdOrLegacyId}.html");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el voucher.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/voucher/preview")]
    public async Task<IActionResult> GetVoucherHtmlPreview(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(id, cancellationToken);
            var html = System.Text.Encoding.UTF8.GetString(htmlBytes);
            return Ok(new { html });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher preview for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar la vista previa del voucher.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher PDF for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el voucher PDF.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating WhatsApp contact for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el contacto de WhatsApp.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting WhatsApp preview for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener la vista previa del voucher.");
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
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo enviar el voucher por WhatsApp." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending WhatsApp voucher for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo enviar el voucher por WhatsApp.");
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
            _logger.LogError(ex, "Unexpected error getting WhatsApp history for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el historial de WhatsApp.");
        }
    }

    private static Passenger MapPassenger(PassengerUpsertRequest passenger)
    {
        return new Passenger
        {
            FullName = passenger.FullName,
            DocumentType = passenger.DocumentType,
            DocumentNumber = passenger.DocumentNumber,
            BirthDate = passenger.BirthDate,
            Nationality = passenger.Nationality,
            Phone = passenger.Phone,
            Email = passenger.Email,
            Gender = passenger.Gender,
            Notes = passenger.Notes
        };
    }

    private static Payment MapPayment(PaymentUpsertRequest payment)
    {
        return new Payment
        {
            Amount = payment.Amount,
            PaidAt = payment.PaidAt,
            Method = payment.Method,
            Reference = payment.Reference,
            Notes = payment.Notes
        };
    }
}

public record UpdateStatusDto(string Status);
public record PassengerUpsertRequest(
    string FullName,
    string? DocumentType,
    string? DocumentNumber,
    DateTime? BirthDate,
    string? Nationality,
    string? Phone,
    string? Email,
    string? Gender,
    string? Notes);
public record PaymentUpsertRequest(
    decimal Amount,
    DateTime PaidAt,
    string Method,
    string? Reference,
    string? Notes);
