using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class AlertService : IAlertService
{
    private readonly AppDbContext _context;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public AlertService(AppDbContext context, IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _context = context;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
    }

    public async Task<object> GetAlertsAsync(CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        var urgentTrips = await _context.Reservas
            .Where(f => (f.Status == EstadoReserva.Reserved || f.Status == EstadoReserva.Operational) &&
                        f.StartDate >= today && 
                        f.StartDate <= threshold && 
                        f.Balance > 0)
            .Select(f => new
            {
                f.PublicId,
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
                s.PublicId,
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
