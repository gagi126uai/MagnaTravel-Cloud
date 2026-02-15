using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.DTOs;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IMapper _mapper;

    public PaymentsController(AppDbContext dbContext, IMapper mapper)
    {
        _dbContext = dbContext;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetAllPayments(CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpGet("reservation/{reservationId:int}")]
    public async Task<ActionResult<IEnumerable<PaymentDto>>> GetPaymentsForReservation(
        int reservationId,
        CancellationToken cancellationToken)
    {
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.ReservationId == reservationId)
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpPost]
    public async Task<ActionResult<PaymentDto>> CreatePayment(
        CreatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        // Validate reservation exists
        var reservation = await _dbContext.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == request.ReservationId, cancellationToken);

        if (reservation is null)
        {
            return BadRequest("Reserva no encontrada.");
        }

        var payment = new Payment
        {
            ReservationId = request.ReservationId,
            Amount = request.Amount,
            Method = request.Method,
            Reference = request.Reference,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.Payments.Add(payment);

        // Fix 2: No manipular Balance manualmente, se recalcula al leer el File

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetPaymentsForReservation), new { reservationId = payment.ReservationId }, _mapper.Map<PaymentDto>(payment));
    }
}

public class CreatePaymentRequest
{
    public int ReservationId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
}
