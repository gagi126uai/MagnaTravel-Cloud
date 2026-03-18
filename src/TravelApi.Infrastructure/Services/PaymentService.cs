using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class PaymentService : IPaymentService
{
    private readonly AppDbContext _dbContext;
    private readonly IMapper _mapper;

    public PaymentService(AppDbContext dbContext, IMapper mapper)
    {
        _dbContext = dbContext;
        _mapper = mapper;
    }

    public async Task<IEnumerable<PaymentDto>> GetAllPaymentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(int ReservaId, CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.ReservaId == ReservaId)
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);
    }

    public async Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var reserva = await _dbContext.Reservas
            .FirstOrDefaultAsync(r => r.Id == request.ReservaId, cancellationToken);

        if (reserva == null)
            throw new ArgumentException("Reserva no encontrada.");

        var payment = new Payment
        {
            ReservaId = request.ReservaId,
            Amount = request.Amount,
            Method = request.Method,
            Reference = request.Reference,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.Payments.Add(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new {
                p.Id,
                p.Amount,
                p.Method,
                p.Reference,
                p.Status,
                p.PaidAt,
                p.DeletedAt,
                p.ReservaId,
                NumeroReserva = p.Reserva != null 
                    ? p.Reserva.NumeroReserva : null,
                FileName = p.Reserva != null 
                    ? p.Reserva.Name : null,
                CustomerName = p.Reserva != null && p.Reserva.Payer != null
                    ? p.Reserva.Payer.FullName : null
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<int> RestorePaymentAsync(int id, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken);

        if (payment == null)
            throw new KeyNotFoundException("Pago eliminado no encontrado.");

        payment.IsDeleted = false;
        payment.DeletedAt = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return payment.Id;
    }
}
