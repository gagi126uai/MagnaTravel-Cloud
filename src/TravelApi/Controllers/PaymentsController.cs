using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
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

    // B1.15 Fase 2a (FIX 5): gating uniforme + filter mine. El service decide el
    // scope segun cobranzas.view_all (Admin/Colaborador) vs cobranzas.view (Vendedor).
    [HttpGet("collections-summary")]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<CollectionsSummaryDto>> GetCollectionsSummary(CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsSummaryAsync(cancellationToken));
    }

    [HttpGet("collections-worklist")]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<CollectionWorkItemDto>>> GetCollectionsWorklist([FromQuery] CollectionWorklistQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetCollectionsWorklistAsync(query, cancellationToken));
    }

    [HttpGet]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<PaymentDto>>> GetAllPayments([FromQuery] PaymentsListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetAllPaymentsAsync(query, cancellationToken));
    }

    [HttpGet("history")]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<FinanceHistoryItemDto>>> GetHistory([FromQuery] FinanceHistoryQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _paymentService.GetHistoryAsync(query, cancellationToken));
    }

    // B1.15 Fase 2a (review final): cerrar bypass del flow nested /reservas/{id}/payments.
    // El frontend usa POST/PUT /api/payments y GET /api/payments/reserva/{id} directamente
    // (PaymentModal.jsx). Sin estos attributes, un Vendedor con cobranzas.edit otorgado
    // manualmente podria operar pagos de reservas ajenas.
    [HttpGet("reserva/{reservaPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReserva(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetPaymentsForReservaAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return Ok(payments);
    }

    // POST /api/payments NO recibe id en la ruta — el ownership lo valida
    // PaymentService.CreatePaymentAsync usando request.ReservaId. Si el user no tiene
    // cobranzas.view_all y la reserva no es propia, el service tira UnauthorizedAccessException
    // que aca traducimos a 403.
    [HttpPost]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<PaymentDto>> CreatePayment(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var payment = await _paymentService.CreatePaymentAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetPaymentsForReserva), new { reservaPublicIdOrLegacyId = request.ReservaId }, payment);
        }
        catch (UnauthorizedAccessException)
        {
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpPost("{publicIdOrLegacyId}/receipt")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.Payment, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
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
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Payment, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
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

    [HttpPut("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.Payment, "publicIdOrLegacyId", bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult> UpdatePayment(string publicIdOrLegacyId, UpdatePaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _paymentService.UpdatePaymentAsync(publicIdOrLegacyId, request, cancellationToken);
            return Ok(new { message = "Pago actualizado correctamente." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-01): MutationGuards rechaza editar pagos con
            // recibo emitido o factura AFIP viva. 409 Conflict es coherente con
            // el patron de DeleteGuards.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeletePayment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _paymentService.DeletePaymentAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(new { message = "Pago eliminado correctamente." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // 409 Conflict: el pago tiene un recibo (Issued/Voided) o esta vinculado
            // a una factura. Antes de C28 esto caia a 500 sin guard.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }
}
