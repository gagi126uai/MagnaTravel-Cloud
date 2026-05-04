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
    private readonly ITimelineService _timelineService;
    private readonly ILogger<ReservasController> _logger;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        ITimelineService timelineService,
        ILogger<ReservasController> logger)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
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

    [HttpPut("{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}")]
    public Task<IActionResult> UpdateNestedService(
        string publicIdOrLegacyId,
        string servicePublicIdOrLegacyId,
        AddServiceRequest request,
        CancellationToken cancellationToken)
    {
        _ = publicIdOrLegacyId;
        return UpdateService(servicePublicIdOrLegacyId, request, cancellationToken);
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
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

    [HttpDelete("{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}")]
    public Task<IActionResult> RemoveNestedService(
        string publicIdOrLegacyId,
        string servicePublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        _ = publicIdOrLegacyId;
        return RemoveService(servicePublicIdOrLegacyId, cancellationToken);
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
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

    // ============= Phase 2.1 — Pasajero <-> Servicio =============

    [HttpGet("{publicIdOrLegacyId}/assignments")]
    public async Task<ActionResult<IReadOnlyList<PassengerServiceAssignmentDto>>> GetAssignments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var list = await _reservaService.GetAssignmentsAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(list);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/assignments")]
    public async Task<ActionResult<PassengerServiceAssignmentDto>> CreateAssignment(string publicIdOrLegacyId, [FromBody] CreatePassengerAssignmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.CreateAssignmentAsync(publicIdOrLegacyId, request, cancellationToken);
            return Created($"/api/reservas/assignments/{dto.PublicId}", dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("assignments/{assignmentPublicIdOrLegacyId}")]
    public async Task<IActionResult> RemoveAssignment(string assignmentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemoveAssignmentAsync(assignmentPublicIdOrLegacyId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ============= /Phase 2.1 =============

    // ============= Phase 2.4 — Revert status con autorizacion =============

    [HttpGet("{publicIdOrLegacyId}/revert-options")]
    public async Task<ActionResult<RevertOptionsDto>> GetRevertOptions(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var isAdmin = User.IsInRole("Admin");
            var dto = await _reservaService.GetRevertOptionsAsync(publicIdOrLegacyId, actorUserId, isAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/revert-status")]
    public async Task<ActionResult<ReservaDto>> RevertStatus(string publicIdOrLegacyId, [FromBody] RevertStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var isAdmin = User.IsInRole("Admin");
            var dto = await _reservaService.RevertStatusAsync(publicIdOrLegacyId, request, actorUserId, actorUserName, isAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ============= /Phase 2.4 =============

    [HttpGet("{publicIdOrLegacyId}/transition-readiness")]
    public async Task<ActionResult<TransitionReadinessDto>> GetTransitionReadiness(string publicIdOrLegacyId, [FromQuery] string to, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetTransitionReadinessAsync(publicIdOrLegacyId, to, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPatch("{publicIdOrLegacyId}/passenger-counts")]
    public async Task<ActionResult> UpdatePassengerCounts(string publicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePassengerCountsAsync(publicIdOrLegacyId, counts, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating passenger counts for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudieron actualizar las cantidades.");
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
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
            return Ok(reserva);
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher PDF for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el voucher PDF.");
        }
    }

}
