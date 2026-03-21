using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

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

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetAllPayments(CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetAllPaymentsAsync(cancellationToken);
        return Ok(payments);
    }

    [HttpGet("reserva/{ReservaId:int}")]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReserva(
        int ReservaId,
        CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetPaymentsForReservaAsync(ReservaId, cancellationToken);
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
            return CreatedAtAction(nameof(GetPaymentsForReserva), new { ReservaId = request.ReservaId }, payment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id:int}/receipt")]
    public async Task<ActionResult<PaymentReceiptDto>> IssueReceipt(int id, CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await _paymentService.IssueReceiptAsync(id, cancellationToken);
            return Ok(receipt);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id:int}/receipt/pdf")]
    public async Task<IActionResult> GetReceiptPdf(int id, CancellationToken cancellationToken)
    {
        try
        {
            var pdf = await _paymentService.GetReceiptPdfAsync(id, cancellationToken);
            return File(pdf, "application/pdf", $"recibo-pago-{id}.pdf");
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
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
    [HttpPut("{id:int}/restore")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> RestorePayment(int id, CancellationToken cancellationToken)
    {
        try
        {
            var paymentId = await _paymentService.RestorePaymentAsync(id, cancellationToken);
            return Ok(new { message = "Pago restaurado correctamente.", paymentId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
