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

    [HttpGet("reservation/{reservationId:int}")]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReservation(
        int reservationId,
        CancellationToken cancellationToken)
    {
        var payments = await _paymentService.GetPaymentsForReservationAsync(reservationId, cancellationToken);
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
            return CreatedAtAction(nameof(GetPaymentsForReservation), new { reservationId = request.ReservationId }, payment);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
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
