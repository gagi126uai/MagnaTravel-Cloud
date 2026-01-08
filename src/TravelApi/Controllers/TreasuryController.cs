using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Treasury;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
public class TreasuryController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public TreasuryController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("receipts")]
    public async Task<ActionResult<IEnumerable<TreasuryReceiptDto>>> GetReceipts(CancellationToken cancellationToken)
    {
        var receipts = await _dbContext.TreasuryReceipts
            .AsNoTracking()
            .Include(receipt => receipt.Applications)
            .OrderByDescending(receipt => receipt.ReceivedAt)
            .ToListAsync(cancellationToken);

        var payload = receipts.Select(receipt => ToDto(receipt)).ToList();

        return Ok(payload);
    }

    [HttpGet("receipts/{id:int}")]
    public async Task<ActionResult<TreasuryReceiptDto>> GetReceipt(int id, CancellationToken cancellationToken)
    {
        var receipt = await _dbContext.TreasuryReceipts
            .AsNoTracking()
            .Include(found => found.Applications)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (receipt is null)
        {
            return NotFound();
        }

        return Ok(ToDto(receipt));
    }

    [HttpPost("receipts")]
    public async Task<ActionResult<TreasuryReceiptDto>> CreateReceipt(
        CreateTreasuryReceiptRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Method))
        {
            return BadRequest("Referencia y método son obligatorios.");
        }

        var receipt = new TreasuryReceipt
        {
            Reference = request.Reference,
            Method = request.Method,
            Currency = request.Currency,
            Amount = request.Amount,
            ReceivedAt = NormalizeUtc(request.ReceivedAt) ?? DateTime.UtcNow,
            Notes = request.Notes
        };

        if (!receipt.HasValidAmount())
        {
            return BadRequest("El monto del cobro no es válido.");
        }

        _dbContext.TreasuryReceipts.Add(receipt);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetReceipt), new { id = receipt.Id }, ToDto(receipt));
    }

    [HttpPost("receipts/{id:int}/applications")]
    public async Task<ActionResult<TreasuryApplicationDto>> ApplyReceipt(
        int id,
        ApplyTreasuryReceiptRequest request,
        CancellationToken cancellationToken)
    {
        var receipt = await _dbContext.TreasuryReceipts
            .Include(found => found.Applications)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (receipt is null)
        {
            return NotFound();
        }

        var reservationExists = await _dbContext.Reservations
            .AsNoTracking()
            .AnyAsync(reservation => reservation.Id == request.ReservationId, cancellationToken);

        if (!reservationExists)
        {
            return BadRequest("La reserva no existe.");
        }

        var application = new TreasuryApplication
        {
            ReservationId = request.ReservationId,
            AmountApplied = request.AmountApplied
        };

        if (!application.HasValidAmount())
        {
            return BadRequest("El monto aplicado no es válido.");
        }

        if (request.AmountApplied > receipt.RemainingAmount)
        {
            return BadRequest("El monto excede el saldo disponible del cobro.");
        }

        receipt.Applications.Add(application);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new TreasuryApplicationDto(
            application.Id,
            application.ReservationId,
            application.AmountApplied,
            application.AppliedAt);

        return CreatedAtAction(nameof(GetReceipt), new { id }, dto);
    }

    private static TreasuryReceiptDto ToDto(TreasuryReceipt receipt)
    {
        return new TreasuryReceiptDto(
            receipt.Id,
            receipt.Reference,
            receipt.Method,
            receipt.Currency,
            receipt.Amount,
            receipt.AppliedAmount,
            receipt.RemainingAmount,
            receipt.ReceivedAt,
            receipt.Notes,
            receipt.Applications
                .OrderByDescending(application => application.AppliedAt)
                .Select(application => new TreasuryApplicationDto(
                    application.Id,
                    application.ReservationId,
                    application.AmountApplied,
                    application.AppliedAt))
                .ToList());
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }
}
