using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): implementacion de la bandeja de reconciliacion
/// de NC parciales con recibos vivos. Solo LISTA y CIERRA casos; la creacion del caso
/// vive en <c>AfipService.ApplyPartialCreditNoteReversalAsync</c> (transaccional con el
/// Payment reversal).
/// </summary>
public class PartialCreditNoteReconciliationService : IPartialCreditNoteReconciliationService
{
    private readonly AppDbContext _db;
    private readonly IOperationalFinanceSettingsService _settings;
    private readonly IFourEyesBypassEvaluator _fourEyesBypassEvaluator;
    private readonly IAuditService _auditService;
    private readonly ILogger<PartialCreditNoteReconciliationService> _logger;

    public PartialCreditNoteReconciliationService(
        AppDbContext db,
        IOperationalFinanceSettingsService settings,
        IFourEyesBypassEvaluator fourEyesBypassEvaluator,
        IAuditService auditService,
        ILogger<PartialCreditNoteReconciliationService> logger)
    {
        _db = db;
        _settings = settings;
        _fourEyesBypassEvaluator = fourEyesBypassEvaluator;
        _auditService = auditService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResponse<PartialCreditNoteReconciliationDto>> ListAsync(
        PartialCreditNoteReconciliationListQuery query,
        CancellationToken ct)
    {
        var page = query.GetNormalizedPage();
        var pageSize = query.GetNormalizedPageSize();

        // Base query con los Includes necesarios para armar el DTO. Traemos las hijas
        // (Receipts) + el PaymentReceipt real de cada una (para el estado VIGENTE) +
        // las facturas (para el numero) + la reserva (para el contexto). Un solo viaje a
        // la BD por pagina: NO hay N+1 porque todo se proyecta desde este grafo cargado.
        var baseQuery = _db.PartialCreditNoteReconciliations
            .AsNoTracking()
            .Include(r => r.CreditNoteInvoice)
            .Include(r => r.OriginalInvoice)
            .Include(r => r.Reserva)
            .Include(r => r.Receipts)
                .ThenInclude(c => c.PaymentReceipt)
                    .ThenInclude(pr => pr.Payment)
            .AsQueryable();

        baseQuery = ApplyStatusFilter(baseQuery, query.Status);
        baseQuery = ApplyMonthFilter(baseQuery, query.Year, query.Month);

        // Orden: mas nuevos primero (los casos pendientes mas recientes arriba).
        baseQuery = query.IsSortDescending()
            ? baseQuery.OrderByDescending(r => r.OpenedAt)
            : baseQuery.OrderBy(r => r.OpenedAt);

        var totalCount = await baseQuery.CountAsync(ct);

        var entities = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = entities.Select(MapToDto).ToList();

        return PagedResponse<PartialCreditNoteReconciliationDto>.Create(items, page, pageSize, totalCount);
    }

    /// <inheritdoc />
    public async Task<PartialCreditNoteReconciliationDto> ResolveAsync(
        Guid publicId,
        ResolvePartialCreditNoteReconciliationRequest request,
        string currentUserId,
        string? currentUserName,
        CancellationToken ct)
    {
        // Cargamos CON tracking (vamos a mutar) + el grafo necesario para el DTO de salida.
        var reconciliation = await _db.PartialCreditNoteReconciliations
            .Include(r => r.CreditNoteInvoice)
            .Include(r => r.OriginalInvoice)
            .Include(r => r.Reserva)
            .Include(r => r.Receipts)
                .ThenInclude(c => c.PaymentReceipt)
                    .ThenInclude(pr => pr.Payment)
            .FirstOrDefaultAsync(r => r.PublicId == publicId, ct);

        if (reconciliation is null)
        {
            throw new KeyNotFoundException("Caso de reconciliacion no encontrado.");
        }

        // Idempotencia del cierre: si ya esta Resolved, NO re-cerramos ni pisamos quien
        // cerro. 409 para que el frontend refresque y oculte el boton.
        if (reconciliation.Status != PartialCreditNoteReconciliationStatus.Pending)
        {
            throw new InvalidOperationException("Este caso ya fue resuelto.");
        }

        var notes = request.Notes?.Trim();

        // ¿Hay recibos todavia vivos (Issued) al momento de cerrar? Se lee EN VIVO del
        // PaymentReceipt real (no del snapshot StatusAtOpen).
        var hasLiveReceipts = reconciliation.Receipts
            .Any(c => c.PaymentReceipt != null
                      && string.Equals(c.PaymentReceipt.Status, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase));

        // Self-close: el que cierra es el mismo que abrio el caso.
        // OJO (ADR-010 N3): si OpenedByUserId == "system" (cancelacion automatica), esto
        // nunca matchea -> cualquier encargado cierra sin exigir bypass. Comportamiento
        // esperado: no hay una persona que abrio a la cual aplicarle 4-ojos.
        var isSelfClose = string.Equals(reconciliation.OpenedByUserId, currentUserId, StringComparison.Ordinal);

        var settings = await _settings.GetEntityAsync(ct);
        var bypassApplied = false;

        if (isSelfClose)
        {
            // 4-ojos: el que abrio no puede cerrar... salvo bypass de admin unico (G5).
            // El bypass exige notes >= 100 chars (lo valida el evaluator).
            bypassApplied = await _fourEyesBypassEvaluator.EvaluateAsync(notes, settings, ct);
            if (!bypassApplied)
            {
                throw new InvalidOperationException(
                    "No podes cerrar un caso que vos mismo abriste. Que lo cierre otra persona, " +
                    "o (si sos el unico admin) escribi un motivo de al menos 100 caracteres " +
                    "explicando por que.");
            }
        }

        // R4: si se cierra con recibos vivos (plata potencialmente no devuelta), las notas
        // son OBLIGATORIAS aunque NO sea self-close. La bandeja no obliga a anular
        // (decision D2), pero si obliga a justificar y deja trazable. En el caso self-close
        // las notas ya estan garantizadas (>=100 por el bypass), asi que este check
        // alcanza al cierre "otra persona + recibos vivos sin notas".
        if (hasLiveReceipts && string.IsNullOrWhiteSpace(notes))
        {
            throw new InvalidOperationException(
                "Hay recibos sin anular en este caso. Para cerrarlo igual, escribi un motivo " +
                "explicando que pasa con esa plata (ej. queda como saldo a favor del cliente).");
        }

        // Cierre.
        reconciliation.Status = PartialCreditNoteReconciliationStatus.Resolved;
        reconciliation.ResolvedAt = DateTime.UtcNow;
        reconciliation.ResolvedByUserId = currentUserId;
        reconciliation.ResolvedByUserName = currentUserName;
        reconciliation.ResolutionNotes = notes;
        reconciliation.ClosedWithLiveReceipts = hasLiveReceipts;
        reconciliation.FourEyesBypassApplied = bypassApplied;

        // El SaveChanges puede tirar DbUpdateConcurrencyException (xmin) si otro encargado
        // cerro el mismo caso en paralelo. NO lo atrapamos aca: el controller lo mapea a 409.
        await _db.SaveChangesAsync(ct);

        // Audit obligatorio (ADR-010 §5.4), pero best-effort DE VERDAD: el cierre del caso
        // ya quedo commiteado en el SaveChanges de arriba y es valido. Si grabar la auditoria
        // falla, NO podemos devolverle un error al cliente porque la operacion principal ya
        // sucedio (devolver 500 seria mentirle: el caso esta cerrado).
        //
        // Por que el try/catch es necesario: LogBusinessEventAsync RE-LANZA las excepciones
        // de base de datos (DbUpdateException / DbUpdateConcurrencyException) y las de
        // invariantes de negocio (BusinessInvariantViolationException); solo se traga las
        // genericas. Sin este catch, una de esas excepciones tumbaria la respuesta a pesar
        // del cierre exitoso. Atrapamos Exception general a proposito: ningun fallo de auditoria
        // debe tumbar la respuesta. Logueamos con detalle (publicId del caso + el error) para
        // no perder la trazabilidad y poder reconstruir el evento a mano si hiciera falta.
        try
        {
            await _auditService.LogBusinessEventAsync(
                action: "PartialCreditNoteReconciliationResolved",
                entityName: "PartialCreditNoteReconciliation",
                entityId: reconciliation.Id.ToString(CultureInfo.InvariantCulture),
                details: System.Text.Json.JsonSerializer.Serialize(new
                {
                    publicId = reconciliation.PublicId,
                    resolvedBy = currentUserId,
                    fourEyesBypassApplied = bypassApplied,
                    closedWithLiveReceipts = hasLiveReceipts,
                    notes = notes,
                }),
                userId: currentUserId,
                userName: currentUserName,
                ct: ct);
        }
        catch (Exception auditException)
        {
            _logger.LogError(
                auditException,
                "Fallo al grabar la auditoria del cierre de PartialCreditNoteReconciliation, " +
                "pero el cierre YA quedo commiteado y es valido. PublicId={PublicId} ByUser={UserId} " +
                "Bypass={Bypass} ClosedWithLiveReceipts={LiveReceipts}",
                reconciliation.PublicId, currentUserId, bypassApplied, hasLiveReceipts);
        }

        _logger.LogInformation(
            "PartialCreditNoteReconciliation resuelto. PublicId={PublicId} ByUser={UserId} " +
            "Bypass={Bypass} ClosedWithLiveReceipts={LiveReceipts}",
            reconciliation.PublicId, currentUserId, bypassApplied, hasLiveReceipts);

        return MapToDto(reconciliation);
    }

    // ============================================================
    // Helpers privados.
    // ============================================================

    private static IQueryable<PartialCreditNoteReconciliation> ApplyStatusFilter(
        IQueryable<PartialCreditNoteReconciliation> source,
        string? status)
    {
        // "all" no filtra. "resolved" trae cerrados. Cualquier otra cosa (incluido el
        // default "pending") trae solo pendientes — la opcion segura para que la bandeja
        // muestre lo que falta hacer si llega un valor raro.
        return status?.Trim().ToLowerInvariant() switch
        {
            "all" => source,
            "resolved" => source.Where(r => r.Status == PartialCreditNoteReconciliationStatus.Resolved),
            _ => source.Where(r => r.Status == PartialCreditNoteReconciliationStatus.Pending),
        };
    }

    private static IQueryable<PartialCreditNoteReconciliation> ApplyMonthFilter(
        IQueryable<PartialCreditNoteReconciliation> source,
        int? year,
        int? month)
    {
        // Filtro mensual estilo MonthNavigator: solo aplica si vienen AMBOS year + month
        // validos. Filtramos por OpenedAt (la fecha de apertura del caso).
        if (year is null || month is null || month < 1 || month > 12)
        {
            return source;
        }

        // Rango [inicio de mes, inicio del mes siguiente) en UTC. Usamos rango en vez de
        // r.OpenedAt.Month == month para que el indice/orden por OpenedAt sea aprovechable
        // y para evitar ambiguedad de zona horaria.
        var monthStart = new DateTime(year.Value, month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);

        return source.Where(r => r.OpenedAt >= monthStart && r.OpenedAt < monthEnd);
    }

    private static PartialCreditNoteReconciliationDto MapToDto(PartialCreditNoteReconciliation entity)
    {
        return new PartialCreditNoteReconciliationDto
        {
            PublicId = entity.PublicId,
            Status = entity.Status.ToString(),
            OpenedAt = entity.OpenedAt,
            OpenedByUserName = entity.OpenedByUserName,
            CreditNoteNumber = FormatInvoiceNumber(entity.CreditNoteInvoice),
            OriginalInvoiceNumber = FormatInvoiceNumber(entity.OriginalInvoice),
            FiscalAmountCredited = entity.FiscalAmountCredited,
            Currency = entity.Currency,
            ReservaPublicId = entity.Reserva?.PublicId,
            ReservaName = entity.Reserva?.Name,
            ResolvedAt = entity.ResolvedAt,
            ResolvedByUserName = entity.ResolvedByUserName,
            ResolutionNotes = entity.ResolutionNotes,
            ClosedWithLiveReceipts = entity.ClosedWithLiveReceipts,
            FourEyesBypassApplied = entity.FourEyesBypassApplied,
            Receipts = entity.Receipts.Select(MapReceiptToDto).ToList(),
        };
    }

    private static PartialCreditNoteReconciliationReceiptDto MapReceiptToDto(
        PartialCreditNoteReconciliationReceipt snapshot)
    {
        // currentStatus se lee EN VIVO del PaymentReceipt real (ADR-010 §5.1). Si por
        // algun motivo el recibo no esta cargado, caemos al StatusAtOpen (defensive).
        var liveReceipt = snapshot.PaymentReceipt;
        var currentStatus = liveReceipt?.Status ?? snapshot.StatusAtOpen;

        return new PartialCreditNoteReconciliationReceiptDto
        {
            // El PublicId del Payment lo expone el recibo real (lo necesita el frontend
            // para llamar al endpoint de anular, que resuelve por Payment).
            PaymentPublicId = liveReceipt?.Payment?.PublicId ?? Guid.Empty,
            ReceiptId = snapshot.PaymentReceiptId,
            ReceiptNumber = liveReceipt?.ReceiptNumber,
            Amount = snapshot.Amount,
            StatusAtOpen = snapshot.StatusAtOpen,
            CurrentStatus = currentStatus,
            VoidedAt = liveReceipt?.VoidedAt,
            VoidedByUserName = liveReceipt?.VoidedByUserName,
        };
    }

    private static string FormatInvoiceNumber(Invoice? invoice)
    {
        if (invoice is null)
        {
            return string.Empty;
        }

        // Mismo formato que usa AfipService para las referencias: PV de 5 digitos +
        // numero de 8 digitos (ej. "00003-00000123").
        return $"{invoice.PuntoDeVenta:D5}-{invoice.NumeroComprobante:D8}";
    }
}
