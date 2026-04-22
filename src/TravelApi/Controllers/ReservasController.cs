using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

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
    private readonly ILogger<ReservasController> _logger;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        IWhatsAppDeliveryService whatsAppDeliveryService,
        ITimelineService timelineService,
        ILogger<ReservasController> logger)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
        _timelineService = timelineService;
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
    public async Task<IActionResult> GetReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetReservaByIdAsync(publicIdOrLegacyId, cancellationToken);
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
            var timeline = await _timelineService.GetTimelineAsync(publicIdOrLegacyId, cancellationToken);
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
    public async Task<IActionResult> CreateReserva(CreateReservaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var createdByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var reserva = await _reservaService.CreateReservaAsync(request, createdByUserId, cancellationToken);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId = reserva.PublicId }, reserva);
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
    public async Task<IActionResult> AddService(string publicIdOrLegacyId, AddServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reservaService.AddServiceAsync(publicIdOrLegacyId, request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                return Ok(new { servicio = result.Servicio, result.Warning });
            }

            return Ok(result.Servicio);
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
    public async Task<IActionResult> UpdateService(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var service = await _reservaService.UpdateServiceAsync(servicePublicIdOrLegacyId, request, cancellationToken);
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
    public async Task<IActionResult> RemoveService(string servicePublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemoveServiceAsync(servicePublicIdOrLegacyId, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/passengers")]
    public async Task<ActionResult> GetPassengers(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var passengers = await _reservaService.GetPassengersAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(passengers);
    }

    [HttpPost("{publicIdOrLegacyId}/passengers")]
    public async Task<ActionResult> AddPassenger(string publicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.AddPassengerAsync(publicIdOrLegacyId, passenger, cancellationToken);
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
    public async Task<ActionResult> UpdatePassenger(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePassengerAsync(passengerPublicIdOrLegacyId, updated, cancellationToken);
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
    public async Task<IActionResult> RemovePassenger(string passengerPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemovePassengerAsync(passengerPublicIdOrLegacyId, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/payments")]
    public async Task<ActionResult> GetReservaPayments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var payments = await _reservaService.GetReservaPaymentsAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(payments);
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    public async Task<ActionResult> AddPayment(string publicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.AddPaymentAsync(publicIdOrLegacyId, payment, cancellationToken);
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
    public async Task<ActionResult> UpdatePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, updatedPayment, cancellationToken);
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
    public async Task<IActionResult> DeletePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.DeletePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, cancellationToken);
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

    [HttpPut("{publicIdOrLegacyId}/status")]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var reserva = await _reservaService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, cancellationToken);
            if (request.Status == EstadoReserva.Operational)
            {
                await _whatsAppDeliveryService.PrepareVoucherDraftAsync(publicIdOrLegacyId, cancellationToken);
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
    public async Task<IActionResult> ArchiveReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var reserva = await _reservaService.ArchiveReservaAsync(publicIdOrLegacyId, cancellationToken);
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
    public async Task<IActionResult> DeleteReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.DeleteReservaAsync(publicIdOrLegacyId, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/voucher")]
    public async Task<IActionResult> GenerateVoucher(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(publicIdOrLegacyId, cancellationToken);
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
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(publicIdOrLegacyId, cancellationToken);
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
            var pdf = await _voucherService.GenerateVoucherPdfAsync(publicIdOrLegacyId, cancellationToken);
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
            var preview = await _whatsAppDeliveryService.UpdateReservaWhatsAppContactAsync(
                publicIdOrLegacyId,
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
            var preview = await _whatsAppDeliveryService.GetVoucherPreviewAsync(publicIdOrLegacyId, cancellationToken);
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
            var performedBy = User.Identity?.Name ?? "Agente";
            var delivery = await _whatsAppDeliveryService.SendVoucherAsync(publicIdOrLegacyId, request.Caption, performedBy, cancellationToken);
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
            var history = await _whatsAppDeliveryService.GetHistoryAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting WhatsApp history for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el historial de WhatsApp.");
        }
    }
}
