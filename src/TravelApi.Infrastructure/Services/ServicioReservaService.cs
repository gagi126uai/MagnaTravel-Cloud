using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class ServicioReservaService : IServicioReservaService
{
    private readonly AppDbContext _dbContext;

    public ServicioReservaService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<ServicioReserva>> GetServiciosAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Servicios
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<ServicioReserva?> GetServicioByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbContext.Servicios
            .AsNoTracking()
            .Include(r => r.Customer)
            .Include(r => r.Supplier)
            .Include(r => r.Payments)
            .Include(r => r.Segments)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<FlightSegment> CreateSegmentAsync(int servicioId, FlightSegment segment, CancellationToken cancellationToken)
    {
        var servicio = await _dbContext.Servicios
            .FirstOrDefaultAsync(r => r.Id == servicioId, cancellationToken);
 
        if (servicio is null)
            throw new ArgumentException("Servicio no encontrado.");
 
        // BUG 2 (2026-06-08): ArrivalTime es nullable (vuelos solo de ida). Solo validamos el orden
        // de fechas cuando HAY hora de llegada; un segmento sin llegada no puede estar "antes" de la salida.
        if (segment.ArrivalTime.HasValue && segment.ArrivalTime.Value < segment.DepartureTime)
            throw new ArgumentException("La fecha de llegada no puede ser anterior a la de salida.");

        segment.ServicioReservaId = servicioId;
        segment.DepartureTime = NormalizeUtc(segment.DepartureTime);
        // null se preserva (sin hora de llegada); con valor se normaliza el Kind a Utc.
        segment.ArrivalTime = segment.ArrivalTime.HasValue ? NormalizeUtc(segment.ArrivalTime.Value) : (DateTime?)null;
        // Ronda 7 (guia UX): Cabina es OPCIONAL — vacio/espacios se persiste como null, nunca "".
        // Se normaliza aca (y no en el controller) para que CUALQUIER caller respete el invariante.
        segment.CabinClass = BookingService.NormalizeOptionalText(segment.CabinClass);
 
        _dbContext.FlightSegments.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken);
 
        return segment;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
