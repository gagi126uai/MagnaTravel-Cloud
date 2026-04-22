using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelReservations.Api.Controllers;

[ApiController]
[Route("api/reservas")]
[Authorize]
public class ReservasController : ControllerBase
{
    private readonly IReservaService _reservaService;
    private readonly IPaymentService _paymentService;
    private readonly IBookingService _bookingService;
    private readonly ITimelineService _timelineService;
    private readonly IVoucherService _voucherService;
    private readonly IAttachmentService _attachmentService;
    private readonly ILogger<ReservasController> _logger;

    public ReservasController(
        IReservaService reservaService,
        IPaymentService paymentService,
        IBookingService bookingService,
        ITimelineService timelineService,
        IVoucherService voucherService,
        IAttachmentService attachmentService,
        ILogger<ReservasController> logger)
    {
        _reservaService = reservaService;
        _paymentService = paymentService;
        _bookingService = bookingService;
        _timelineService = timelineService;
        _voucherService = voucherService;
        _attachmentService = attachmentService;
        _logger = logger;
    }

    // --- Reserva Operations ---
    [HttpGet]
    public async Task<IActionResult> GetReservas([FromQuery] ReservaListQuery query, CancellationToken cancellationToken)
    {
        var result = await _reservaService.GetReservasAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<IActionResult> GetReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _reservaService.GetReservaByIdAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReservaRequest request, CancellationToken cancellationToken)
    {
        // En el microservicio, el userId ya viene en los headers y se parsea en el Service si es necesario, 
        // o podemos pasarlo aquí si el contrato lo requiere. 
        // El proxy no manda el userId por argumento sino que el Service lo extrae.
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        var result = await _reservaService.CreateReservaAsync(request, userId, cancellationToken);
        return Ok(result);
    }

    [HttpPut("{publicIdOrLegacyId}/status")]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, cancellationToken);
        return Ok(result);
    }

    [HttpPut("{publicIdOrLegacyId}/archive")]
    public async Task<IActionResult> Archive(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _reservaService.ArchiveReservaAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<IActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _reservaService.DeleteReservaAsync(publicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    // --- Services ---
    [HttpPost("{publicIdOrLegacyId}/services")]
    public async Task<IActionResult> AddService(string publicIdOrLegacyId, [FromBody] AddServiceRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.AddServiceAsync(publicIdOrLegacyId, request, cancellationToken);
        return Ok(new { servicio = result.Servicio, warning = result.Warning });
    }

    [HttpPut("services/{servicePublicIdOrLegacyId}")]
    public async Task<IActionResult> UpdateService(string servicePublicIdOrLegacyId, [FromBody] AddServiceRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.UpdateServiceAsync(servicePublicIdOrLegacyId, request, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("services/{servicePublicIdOrLegacyId}")]
    public async Task<IActionResult> RemoveService(string servicePublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _reservaService.RemoveServiceAsync(servicePublicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    // --- Passengers ---
    [HttpGet("{publicIdOrLegacyId}/passengers")]
    public async Task<IActionResult> GetPassengers(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _reservaService.GetPassengersAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{publicIdOrLegacyId}/passengers")]
    public async Task<IActionResult> AddPassenger(string publicIdOrLegacyId, [FromBody] PassengerUpsertRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.AddPassengerAsync(publicIdOrLegacyId, request, cancellationToken);
        return Ok(result);
    }

    [HttpPut("passengers/{passengerPublicIdOrLegacyId}")]
    public async Task<IActionResult> UpdatePassenger(string passengerPublicIdOrLegacyId, [FromBody] PassengerUpsertRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.UpdatePassengerAsync(passengerPublicIdOrLegacyId, request, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("passengers/{passengerPublicIdOrLegacyId}")]
    public async Task<IActionResult> RemovePassenger(string passengerPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _reservaService.RemovePassengerAsync(passengerPublicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    // --- Payments ---
    [HttpGet("{publicIdOrLegacyId}/payments")]
    public async Task<IActionResult> GetPayments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _reservaService.GetReservaPaymentsAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    public async Task<IActionResult> AddPayment(string publicIdOrLegacyId, [FromBody] ReservationPaymentUpsertRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.AddPaymentAsync(publicIdOrLegacyId, request, cancellationToken);
        return Ok(result);
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<IActionResult> UpdatePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, [FromBody] ReservationPaymentUpsertRequest request, CancellationToken cancellationToken)
    {
        var result = await _reservaService.UpdatePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, request, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<IActionResult> DeletePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _reservaService.DeletePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    // --- Timeline ---
    [HttpGet("{publicIdOrLegacyId}/timeline")]
    public async Task<IActionResult> GetTimeline(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _timelineService.GetTimelineAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    // --- Vouchers ---
    [HttpGet("{publicIdOrLegacyId}/voucher")]
    public async Task<IActionResult> GenerateVoucher(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _voucherService.GenerateVoucherHtmlAsync(publicIdOrLegacyId, cancellationToken);
        return File(result, "text/html", $"voucher-{publicIdOrLegacyId}.html");
    }

    [HttpGet("{publicIdOrLegacyId}/voucher/pdf")]
    public async Task<IActionResult> GenerateVoucherPdf(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _voucherService.GenerateVoucherPdfAsync(publicIdOrLegacyId, cancellationToken);
        return File(result, "application/pdf", $"voucher-{publicIdOrLegacyId}.pdf");
    }

    // --- Attachments ---
    [HttpGet("{publicIdOrLegacyId}/attachments")]
    public async Task<IActionResult> GetAttachments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var result = await _attachmentService.GetAttachmentsAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{publicIdOrLegacyId}/attachments")]
    public async Task<IActionResult> AddAttachment(string publicIdOrLegacyId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded.");
        
        var uploadedBy = User.Identity?.Name ?? "System";
        using var stream = file.OpenReadStream();
        
        var result = await _attachmentService.UploadAttachmentAsync(
            publicIdOrLegacyId, 
            stream, 
            file.FileName, 
            file.ContentType, 
            uploadedBy, 
            cancellationToken);
            
        return Ok(result);
    }

    [HttpGet("attachments/{attachmentPublicIdOrLegacyId}")]
    public async Task<IActionResult> DownloadAttachment(string attachmentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var (bytes, contentType, fileName) = await _attachmentService.DownloadAttachmentAsync(attachmentPublicIdOrLegacyId, cancellationToken);
        return File(bytes, contentType, fileName);
    }

    [HttpDelete("attachments/{attachmentPublicIdOrLegacyId}")]
    public async Task<IActionResult> DeleteAttachment(string attachmentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _attachmentService.DeleteAttachmentAsync(attachmentPublicIdOrLegacyId, cancellationToken);
        return NoContent();
    }
}
