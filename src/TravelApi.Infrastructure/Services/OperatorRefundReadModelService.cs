using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations; // CostMasking

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): implementacion del read-model de "reembolsos a cobrar del operador".
/// Ver <see cref="IOperatorRefundReadModelService"/>. SOLO LECTURA: arma las filas a partir de las
/// <see cref="BookingCancellation"/> que estan esperando (o ya se dieron por perdidas esperando) el reintegro.
///
/// <para><b>Por que se proyecta en memoria</b>: el universo de cancelaciones abiertas es chico (back-office) y
/// agrupar las lineas por operador + sumar el reembolso pendiente por moneda es mucho mas legible en LINQ-to-objects
/// que en una GROUP BY traducible. Cargamos solo las cancelaciones en los 2 estados relevantes con sus lineas.</para>
/// </summary>
public class OperatorRefundReadModelService : IOperatorRefundReadModelService
{
    // Ventana de aviso "por vencer": si al plazo le faltan <= estos dias, el semaforo pasa a DueSoon. No hay
    // setting dedicado todavia; 7 dias es un valor operativo razonable (una semana para reclamar al operador).
    private const int DueSoonWindowDays = 7;

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;

    public OperatorRefundReadModelService(
        AppDbContext db,
        IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    public async Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetSupplierPendingRefundsAsync(
        int supplierId, CancellationToken ct)
    {
        var canSeeCost = await CanSeeCostAsync(ct);
        var cancellations = await LoadOpenCancellationsAsync(
            filterSupplierId: supplierId, ct: ct);

        var items = new List<OperatorRefundPendingItemDto>();
        foreach (var bc in cancellations)
        {
            // Solo las lineas de ESTE operador (una cancelacion multi-operador tiene lineas de varios).
            var supplierLines = bc.Lines.Where(l => l.SupplierId == supplierId).ToList();
            var item = BuildItem(bc, supplierLines, canSeeCost);
            if (item != null) items.Add(item);
        }

        return SortBySeverity(items);
    }

    public async Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetAllPendingRefundsAsync(CancellationToken ct)
    {
        var canSeeCost = await CanSeeCostAsync(ct);
        var cancellations = await LoadOpenCancellationsAsync(filterSupplierId: null, ct: ct);

        var items = new List<OperatorRefundPendingItemDto>();
        foreach (var bc in cancellations)
        {
            // Una fila por operador: agrupamos las lineas por SupplierId. Asi cada operador que debe reembolsar en
            // esta cancelacion aparece como su propia cuenta por cobrar.
            var bySupplier = bc.Lines
                .Where(l => l.SupplierId != 0)
                .GroupBy(l => l.SupplierId);

            foreach (var group in bySupplier)
            {
                var item = BuildItem(bc, group.ToList(), canSeeCost);
                if (item != null) items.Add(item);
            }
        }

        return SortBySeverity(items);
    }

    /// <summary>
    /// Carga las cancelaciones en los 2 estados que importan (esperando refund o abandonadas) con sus lineas,
    /// reserva, cliente y los operadores de las lineas. Si <paramref name="filterSupplierId"/> viene, solo trae
    /// las que tienen al menos una linea de ese operador.
    /// </summary>
    private async Task<List<BookingCancellation>> LoadOpenCancellationsAsync(
        int? filterSupplierId, CancellationToken ct)
    {
        var query = _db.BookingCancellations
            .AsNoTracking()
            .Include(bc => bc.Reserva)
            .Include(bc => bc.Customer)
            .Include(bc => bc.Lines).ThenInclude(l => l.Supplier)
            .Where(bc => bc.Status == BookingCancellationStatus.AwaitingOperatorRefund
                      || bc.Status == BookingCancellationStatus.AbandonedByOperator);

        if (filterSupplierId.HasValue)
        {
            var sid = filterSupplierId.Value;
            query = query.Where(bc => bc.Lines.Any(l => l.SupplierId == sid));
        }

        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// Arma una fila para una cancelacion y el conjunto de lineas de UN operador. Devuelve null si el conjunto no
    /// tiene operador resoluble (defensivo). El monto estimado por moneda = max(0, lo pagado - lo ya recibido) por
    /// moneda, ENMASCARADO si el caller no puede ver costos.
    /// </summary>
    private OperatorRefundPendingItemDto? BuildItem(
        BookingCancellation bc,
        List<BookingCancellationLine> supplierLines,
        bool canSeeCost)
    {
        if (supplierLines.Count == 0) return null;

        var supplier = supplierLines.Select(l => l.Supplier).FirstOrDefault(s => s != null);
        if (supplier == null) return null;

        var semaphore = DeriveSemaphore(bc.Status, bc.OperatorRefundDueBy, DateTime.UtcNow);
        var daysOverdue = ComputeDaysOverdue(bc.OperatorRefundDueBy, DateTime.UtcNow);

        // Estimado por moneda: el pendiente de cada linea (cap - recibido), agregado por moneda del servicio.
        var estimatedByCurrency = supplierLines
            .GroupBy(l => l.Currency)
            .Select(g => new OperatorRefundEstimatedAmountDto
            {
                Currency = g.Key,
                // canSeeCost false -> 0 (masking). El estimado nunca es negativo (lo ya recibido no genera "deuda
                // negativa" del operador hacia la agencia en este read-model).
                EstimatedAmount = canSeeCost
                    ? Math.Max(0m, g.Sum(l => l.RefundCap - l.ReceivedRefundAmount))
                    : 0m,
            })
            .OrderBy(e => e.Currency, StringComparer.Ordinal)
            .ToList();

        return new OperatorRefundPendingItemDto
        {
            BookingCancellationPublicId = bc.PublicId,
            ReservaPublicId = bc.Reserva?.PublicId ?? Guid.Empty,
            NumeroReserva = bc.Reserva?.NumeroReserva ?? string.Empty,
            ClienteNombre = bc.Customer?.FullName ?? string.Empty,
            SupplierPublicId = supplier.PublicId,
            SupplierName = supplier.Name,
            Semaphore = semaphore,
            OperatorRefundDueBy = bc.OperatorRefundDueBy,
            DaysOverdue = daysOverdue,
            EstimatedRefundsByCurrency = estimatedByCurrency,
            AmountsMasked = !canSeeCost,
        };
    }

    /// <summary>
    /// Deriva el semaforo. <c>AbandonedByOperator</c> siempre es Abandonado. Para las que esperan refund:
    /// sin plazo = A tiempo; vencido = Vencido; dentro de la ventana de aviso = Por vencer; resto = A tiempo.
    /// Static + puro para poder testearlo sin DB.
    /// </summary>
    internal static OperatorRefundPendingSemaphore DeriveSemaphore(
        BookingCancellationStatus status, DateTime? dueBy, DateTime nowUtc)
    {
        if (status == BookingCancellationStatus.AbandonedByOperator)
            return OperatorRefundPendingSemaphore.Abandoned;

        if (dueBy is null)
            return OperatorRefundPendingSemaphore.OnTime;

        if (nowUtc > dueBy.Value)
            return OperatorRefundPendingSemaphore.Overdue;

        if (nowUtc >= dueBy.Value.AddDays(-DueSoonWindowDays))
            return OperatorRefundPendingSemaphore.DueSoon;

        return OperatorRefundPendingSemaphore.OnTime;
    }

    /// <summary>Dias corridos desde que vencio el plazo (&gt;= 0). 0 si no hay plazo o todavia no vencio.</summary>
    internal static int ComputeDaysOverdue(DateTime? dueBy, DateTime nowUtc)
    {
        if (dueBy is null || nowUtc <= dueBy.Value) return 0;
        return Math.Max(0, (int)(nowUtc.Date - dueBy.Value.Date).TotalDays);
    }

    /// <summary>Ordena por severidad (Abandonado/Vencido primero) y, dentro, por dias vencido desc + numero de reserva.</summary>
    private static IReadOnlyList<OperatorRefundPendingItemDto> SortBySeverity(
        List<OperatorRefundPendingItemDto> items)
    {
        return items
            .OrderByDescending(i => (int)i.Semaphore)
            .ThenByDescending(i => i.DaysOverdue)
            .ThenBy(i => i.NumeroReserva, StringComparer.Ordinal)
            .ToList();
    }

    private Task<bool> CanSeeCostAsync(CancellationToken ct)
        => CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
}
