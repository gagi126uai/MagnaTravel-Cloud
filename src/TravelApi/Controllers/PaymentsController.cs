using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public PaymentsController(IPaymentService paymentService, EntityReferenceResolver entityReferenceResolver)
    {
        _paymentService = paymentService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet("collections-summary")]
    public async Task<ActionResult<CollectionsSummaryDto>> GetCollectionsSummary(CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsSummaryAsync(cancellationToken));
    }

    [HttpGet("collections-worklist")]
    public async Task<ActionResult<IReadOnlyList<CollectionWorkItemDto>>> GetCollectionsWorklist(CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsWorklistAsync(cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetAllPayments(CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetAllPaymentsAsync(cancellationToken);
        return Ok(payments);
    }

    [HttpGet("reserva/{reservaPublicIdOrLegacyId}")]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReserva(
        string reservaPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, cancellationToken);
        var payments = await _paymentService.GetPaymentsForReservaAsync(reservaId, cancellationToken);
        return Ok(payments);
    }

    [HttpPost]
    public async Task<ActionResult<PaymentDto>> CreatePayment(
        CreatePaymentRequest request,
        CancellationToken cancellationToken)
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
    }

    [HttpPost("{publicIdOrLegacyId}/receipt")]
    public async Task<ActionResult<PaymentReceiptDto>> IssueReceipt(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(publicIdOrLegacyId, cancellationToken);
            var receipt = await _paymentService.IssueReceiptAsync(id, cancellationToken);
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
    }

    [HttpGet("{publicIdOrLegacyId}/receipt/pdf")]
    public async Task<IActionResult> GetReceiptPdf(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(publicIdOrLegacyId, cancellationToken);
            var pdf = await _paymentService.GetReceiptPdfAsync(id, cancellationToken);
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
    }

    /// <summary>
    /// Listar pagos eliminados (papelera) - Solo Admin
    /// </summary>
    [HttpGet("trash")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDeletedPayments(CancellationToken cancellationToken)
    {
        var deletedPayments = await _paymentService.GetDeletedPaymentsAsync(cancellationToken);
        return Ok(deletedPayments);
    }

    /// <summary>
    /// Restaurar un pago eliminado - Solo Admin
    /// </summary>
    [HttpPut("{publicIdOrLegacyId}/restore")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> RestorePayment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Payment>(publicIdOrLegacyId, cancellationToken);
            var paymentPublicId = await _paymentService.RestorePaymentAsync(id, cancellationToken);
            return Ok(new { message = "Pago restaurado correctamente.", paymentPublicId });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
