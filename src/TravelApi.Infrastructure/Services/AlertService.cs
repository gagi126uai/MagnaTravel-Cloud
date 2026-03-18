using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class AlertService : IAlertService
{
    private readonly AppDbContext _context;

    public AlertService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<object> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.Date;
        var nextWeek = today.AddDays(7);

        var urgentTrips = await _context.Reservas
            .Where(f => (f.Status == EstadoReserva.Reserved || f.Status == EstadoReserva.Operational) &&
                        f.StartDate >= today && 
                        f.StartDate <= nextWeek && 
                        f.Balance > 0)
            .Select(f => new
            {
                f.Id,
                f.NumeroReserva,
                f.Name,
                f.StartDate,
                f.Balance,
                f.Status,
                PayerName = f.Payer != null ? f.Payer.FullName : "Sin Cliente"
            })
            .OrderBy(f => f.StartDate)
            .ToListAsync(cancellationToken);

        var supplierDebts = await _context.Suppliers
            .Where(s => s.CurrentBalance > 100 && s.IsActive)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.CurrentBalance,
                s.Phone
            })
            .OrderByDescending(s => s.CurrentBalance)
            .Take(10)
            .ToListAsync(cancellationToken);

        return new
        {
            UrgentTrips = urgentTrips,
            SupplierDebts = supplierDebts,
            TotalCount = urgentTrips.Count + supplierDebts.Count
        };
    }
}
