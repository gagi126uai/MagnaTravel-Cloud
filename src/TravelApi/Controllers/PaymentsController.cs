using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Errors;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public PaymentsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    [HttpGet("collections-summary")]
    public async Task<ActionResult<CollectionsSummaryDto>> GetCollectionsSummary(CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsSummaryAsync(cancellationToken));
    }

    [HttpGet("collections-worklist")]
    public async Task<ActionResult<PagedResponse<CollectionWorkItemDto>>> GetCollectionsWorklist([FromQuery] CollectionWorklistQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsWorklistAsync(query, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<PaymentDto>>> GetAllPayments([FromQuery] PaymentsListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetAllPaymentsAsync(query, cancellationToken));
    }

    [HttpGet("history")]
    public async Task<ActionResult<PagedResponse<FinanceHistoryItemDto>>> GetHistory([FromQuery] FinanceHistoryQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetHistoryAsync(query, cancellationToken));
    }

    [HttpGet("reserva/{reservaPublicIdOrLegacyId}")]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReserva(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetPaymentsForReservaAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return Ok(payments);
    }

    [HttpPost]
    public async Task<ActionResult<PaymentDto>> CreatePayment(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var payment = await _paymentService.CreatePaymentAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetPaymentsForReserva), new { reservaPublicIdOrLegacyId = request.ReservaId }, payment);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo registrar el pago." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpPost("{publicIdOrLegacyId}/receipt")]
    public async Task<ActionResult<PaymentReceiptDto>> IssueReceipt(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await _paymentService.IssueReceiptAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(receipt);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo emitir el comprobante del pago." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpGet("{publicIdOrLegacyId}/receipt/pdf")]
    public async Task<IActionResult> GetReceiptPdf(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var pdf = await _paymentService.GetReceiptPdfAsync(publicIdOrLegacyId, cancellationToken);
            return File(pdf, "application/pdf", $"recibo-pago-{publicIdOrLegacyId}.pdf");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo generar el PDF del comprobante." });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpGet("trash")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDeletedPayments(CancellationToken cancellationToken)
    {
        var deletedPayments = await _paymentService.GetDeletedPaymentsAsync(cancellationToken);
        return Ok(deletedPayments);
    }

    [HttpPut("{publicIdOrLegacyId}/restore")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> RestorePayment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var paymentPublicId = await _paymentService.RestorePaymentAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(new { message = "Pago restaurado correctamente.", paymentPublicId });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }
}
