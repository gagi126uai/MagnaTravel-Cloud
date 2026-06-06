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

    public async Task<object> GetAlertsAsync(AlertCallerContext caller, CancellationToken cancellationToken)
    {
        // Fuga 2 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): UrgentTrips y
        // SupplierDebts son informacion financiera de TODA la agencia (deudas a
        // proveedores, saldos de clientes). Antes el gating "solo admin" vivia
        // UNICAMENTE en el frontend (AlertsContext.jsx), asi que cualquier logueado
        // podia leerlos con un curl. Ahora el server decide.
        //
        // El no-admin recibe el payload con la MISMA forma pero vacio (no un 403):
        // asi el contrato queda listo para que F3 agregue buckets por-vendedor
        // (deadlines de SUS reservas) sin otro cambio de contrato. El gate es
        // POR BUCKET (variable propia), no un "if admin" global, para que F3
        // pueda sumar buckets con otro criterio de visibilidad.
        var canSeeAgencyFinancialBuckets = caller.IsAdmin;
        if (!canSeeAgencyFinancialBuckets)
        {
            return new
            {
                UrgentTrips = Array.Empty<object>(),
                SupplierDebts = Array.Empty<object>(),
                TotalCount = 0
            };
        }

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var threshold = today.AddDays(Math.Max(settings.UpcomingUnpaidReservationAlertDays, 1));

        // Fase D (rediseño Sold/ToSettle): "viajes urgentes" = reservas activas con viaje
        // inminente y saldo pendiente. Sumamos Sold (vendida pero el operador todavia no
        // confirmo): una venta con viaje a la vuelta de la esquina TIENE que alertar, porque
        // falta confirmar al operador Y cobrar. NO sumamos ToSettle: es post-viaje (el viaje
        // ya termino), no es un viaje "proximo". Con el flag EnableSoldToSettleStates OFF
        // nunca hay filas en Sold, asi que el resultado es identico al historico.
        var urgentTrips = await _context.Reservas
            .Where(f => (f.Status == EstadoReserva.Sold ||
                         f.Status == EstadoReserva.Confirmed ||
                         f.Status == EstadoReserva.Traveling) &&
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
