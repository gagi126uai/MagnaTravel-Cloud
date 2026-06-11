using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// B1.15 Fase D' (2026-05-11): vista cronologica unificada de movimientos.
///
/// Estrategia: hacer 2 queries (Payments + Invoices) materializadas a memoria,
/// proyectar a MovementDto, fusionar en una sola lista ordenada por fecha desc,
/// aplicar paginacion in-memory. Acepatable para volumenes reales de PYMEs
/// (decenas/cientos por reserva, miles globales). Si se vuelve cuello de
/// botella se puede:
///  - Cachear el merge en una vista materializada en BD.
///  - O sumar paginacion server-side mas inteligente con UNION ALL SQL.
///
/// Filter mine: aplica ownership tipo Vendedor (Reserva.ResponsibleUserId).
/// Mismo criterio que InvoiceService.GetOwnerScopeOrNullAsync.
/// </summary>
public class MovementsService : IMovementsService
{
    private readonly AppDbContext _context;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;

    public MovementsService(
        AppDbContext context,
        IEntityReferenceResolver entityReferenceResolver,
        IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _context = context;
        _entityReferenceResolver = entityReferenceResolver;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    public async Task<PagedResponse<MovementDto>> GetAsync(MovementsListQuery query, CancellationToken ct = default)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(ct);
        var page = query.GetNormalizedPage();
        var pageSize = query.GetNormalizedPageSize();
        var kinds = ParseKinds(query.Kinds);

        // Resolver filtros por entidad (acepta legacy int id o public id Guid).
        int? reservaId = await ResolveLegacyIdAsync<Reserva>(query.ReservaId, ct);
        int? customerId = await ResolveLegacyIdAsync<Customer>(query.CustomerId, ct);

        var movements = new List<MovementDto>();

        // === Payments (cobros + reversals de NC) ===
        if (kinds.Contains(MovementKinds.Payment) || kinds.Contains(MovementKinds.CreditNoteReversal))
        {
            var paymentsQuery = _context.Payments
                .AsNoTracking()
                .Where(p => !p.IsDeleted && p.Status != "Cancelled")
                // ADR-022 (fix #3): el Payment "puente" de sobrepago (Method=SaldoAFavor, AffectsCash=false)
                // NO es un movimiento de caja: solo traslada el excedente al bolsillo del cliente. No debe
                // listarse en Movimientos como un pago negativo. Se filtra SOLO ese puente, a proposito: el
                // puente de reversion de NC (EntryType=CreditNoteReversal, tambien AffectsCash=false) SI se
                // sigue mostrando como su propio tipo (MovementKinds.CreditNoteReversal), comportamiento
                // historico que esta pantalla ya tenia. Por eso filtramos por Method, no por AffectsCash.
                .Where(p => p.Method != OverpaymentCreditCleanup.BridgeMethod);

            if (ownerScope is not null)
            {
                paymentsQuery = paymentsQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerScope);
            }
            if (reservaId.HasValue)
            {
                paymentsQuery = paymentsQuery.Where(p => p.ReservaId == reservaId.Value);
            }
            if (customerId.HasValue)
            {
                paymentsQuery = paymentsQuery.Where(p => p.Reserva != null && p.Reserva.PayerId == customerId.Value);
            }
            if (query.DateFrom.HasValue)
            {
                var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.DateFrom.Value, isEndOfDay: false);
                paymentsQuery = paymentsQuery.Where(p => p.PaidAt >= fromUtc);
            }
            if (query.DateTo.HasValue)
            {
                var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.DateTo.Value, isEndOfDay: true);
                // EXCLUSIVE end: rango cerrado-abierto [from, to+1day). Captura todo el dia "to" local.
                paymentsQuery = paymentsQuery.Where(p => p.PaidAt < toUtc);
            }
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var search = query.Search.ToLower();
                paymentsQuery = paymentsQuery.Where(p =>
                    p.Method.ToLower().Contains(search) ||
                    (p.Reference != null && p.Reference.ToLower().Contains(search)) ||
                    (p.Notes != null && p.Notes.ToLower().Contains(search)) ||
                    (p.Reserva != null && p.Reserva.NumeroReserva.ToLower().Contains(search)));
            }

            var payments = await paymentsQuery
                .Select(p => new
                {
                    p.PublicId,
                    p.Id,
                    p.EntryType,
                    p.Amount,
                    p.PaidAt,
                    p.Status,
                    p.Method,
                    p.Reference,
                    p.Notes,
                    p.CreatedByUserName,
                    ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null,
                    ReservaId = p.ReservaId,
                    NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                    CustomerPublicId = p.Reserva != null && p.Reserva.Payer != null ? (Guid?)p.Reserva.Payer.PublicId : null,
                    CustomerName = p.Reserva != null && p.Reserva.Payer != null ? p.Reserva.Payer.FullName : null,
                    RelatedInvoicePublicId = p.RelatedInvoice != null ? (Guid?)p.RelatedInvoice.PublicId : null,
                    RelatedInvoiceId = p.RelatedInvoiceId,
                    RelatedInvoiceNumeroComprobante = p.RelatedInvoice != null ? (long?)p.RelatedInvoice.NumeroComprobante : null,
                    RelatedInvoicePuntoDeVenta = p.RelatedInvoice != null ? (int?)p.RelatedInvoice.PuntoDeVenta : null,
                    ReceiptStatus = p.Receipt != null ? p.Receipt.Status : null,
                    ReceiptPublicId = p.Receipt != null ? (Guid?)p.Receipt.PublicId : null,
                })
                .ToListAsync(ct);

            foreach (var p in payments)
            {
                var isReversal = p.EntryType == PaymentEntryTypes.CreditNoteReversal;
                var kind = isReversal ? MovementKinds.CreditNoteReversal : MovementKinds.Payment;
                if (!kinds.Contains(kind)) continue;

                movements.Add(new MovementDto
                {
                    PublicId = p.PublicId,
                    LegacyId = p.Id,
                    Kind = kind,
                    Date = p.PaidAt,
                    Amount = p.Amount,
                    Status = p.Status,
                    ReservaPublicId = p.ReservaPublicId,
                    ReservaLegacyId = p.ReservaId,
                    NumeroReserva = p.NumeroReserva,
                    CustomerPublicId = p.CustomerPublicId,
                    CustomerName = p.CustomerName,
                    Reference = BuildPaymentReference(p.Method, p.Reference, isReversal),
                    Notes = p.Notes,
                    CreatedByUserName = p.CreatedByUserName,
                    RelatedTo = isReversal && p.RelatedInvoicePublicId.HasValue
                        ? new MovementRelatedToDto
                        {
                            Kind = MovementKinds.CreditNote,
                            PublicId = p.RelatedInvoicePublicId.Value,
                            LegacyId = p.RelatedInvoiceId ?? 0,
                            Label = $"NC AFIP {(p.RelatedInvoicePuntoDeVenta ?? 0):D5}-{(p.RelatedInvoiceNumeroComprobante ?? 0):D8}"
                        }
                        : null,
                    ReceiptStatus = isReversal ? null : p.ReceiptStatus,
                    ReceiptPublicId = isReversal ? null : p.ReceiptPublicId,
                });
            }
        }

        // === Invoices (facturas + NDs + NCs) ===
        // Nota: las Notas de Debito tambien viven en la tabla Invoices (mismo modelo
        // AFIP). Antes se proyectaban como kind="invoice" — la UI las trataba como
        // facturas anulables. Ahora se discriminan via InvoiceComprobanteHelpers.
        if (kinds.Contains(MovementKinds.Invoice)
            || kinds.Contains(MovementKinds.CreditNote)
            || kinds.Contains(MovementKinds.DebitNote))
        {
            var invoicesQuery = _context.Invoices
                .AsNoTracking()
                .Where(i => i.Reserva != null);

            if (ownerScope is not null)
            {
                invoicesQuery = invoicesQuery.Where(i => i.Reserva!.ResponsibleUserId == ownerScope);
            }
            if (reservaId.HasValue)
            {
                invoicesQuery = invoicesQuery.Where(i => i.ReservaId == reservaId.Value);
            }
            if (customerId.HasValue)
            {
                invoicesQuery = invoicesQuery.Where(i => i.Reserva!.PayerId == customerId.Value);
            }
            if (query.DateFrom.HasValue)
            {
                var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.DateFrom.Value, isEndOfDay: false);
                invoicesQuery = invoicesQuery.Where(i => i.CreatedAt >= fromUtc);
            }
            if (query.DateTo.HasValue)
            {
                var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.DateTo.Value, isEndOfDay: true);
                // EXCLUSIVE end: rango cerrado-abierto [from, to+1day). Captura todo el dia "to" local.
                invoicesQuery = invoicesQuery.Where(i => i.CreatedAt < toUtc);
            }
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var search = query.Search.ToLower();
                invoicesQuery = invoicesQuery.Where(i =>
                    i.NumeroComprobante.ToString().Contains(search) ||
                    (i.Reserva != null && i.Reserva.NumeroReserva.ToLower().Contains(search)) ||
                    (i.Reserva != null && i.Reserva.Payer != null && i.Reserva.Payer.FullName.ToLower().Contains(search)));
            }

            var invoices = await invoicesQuery
                .Select(i => new
                {
                    i.PublicId,
                    i.Id,
                    i.TipoComprobante,
                    i.PuntoDeVenta,
                    i.NumeroComprobante,
                    i.ImporteTotal,
                    i.CreatedAt,
                    i.Resultado,
                    i.AnnulmentStatus,
                    ReservaPublicId = i.Reserva!.PublicId,
                    ReservaId = i.ReservaId,
                    NumeroReserva = i.Reserva.NumeroReserva,
                    CustomerPublicId = i.Reserva.Payer != null ? (Guid?)i.Reserva.Payer.PublicId : null,
                    CustomerName = i.Reserva.Payer != null ? i.Reserva.Payer.FullName : null,
                    OriginalInvoicePublicId = i.OriginalInvoice != null ? (Guid?)i.OriginalInvoice.PublicId : null,
                    OriginalInvoiceId = i.OriginalInvoiceId,
                    OriginalInvoiceTipoComprobante = i.OriginalInvoice != null ? (int?)i.OriginalInvoice.TipoComprobante : null,
                    OriginalInvoicePuntoDeVenta = i.OriginalInvoice != null ? (int?)i.OriginalInvoice.PuntoDeVenta : null,
                    OriginalInvoiceNumeroComprobante = i.OriginalInvoice != null ? (long?)i.OriginalInvoice.NumeroComprobante : null,
                })
                .ToListAsync(ct);

            foreach (var i in invoices)
            {
                var kind = GetKindForInvoiceTipo(i.TipoComprobante);
                if (!kinds.Contains(kind)) continue;

                // El monto se muestra en negativo para NC (efecto reductor en deuda).
                // Para ND el signo queda positivo (incrementa deuda). Para facturas, positivo.
                var isCreditNote = kind == MovementKinds.CreditNote;

                movements.Add(new MovementDto
                {
                    PublicId = i.PublicId,
                    LegacyId = i.Id,
                    Kind = kind,
                    Date = i.CreatedAt,
                    Amount = isCreditNote ? -i.ImporteTotal : i.ImporteTotal,
                    Status = BuildInvoiceStatus(i.Resultado, i.AnnulmentStatus),
                    ReservaPublicId = i.ReservaPublicId,
                    ReservaLegacyId = i.ReservaId,
                    NumeroReserva = i.NumeroReserva,
                    CustomerPublicId = i.CustomerPublicId,
                    CustomerName = i.CustomerName,
                    Reference = BuildInvoiceReference(i.TipoComprobante, i.PuntoDeVenta, i.NumeroComprobante),
                    Notes = null,
                    CreatedByUserName = null,
                    // RelatedTo: NC apunta a su factura origen. NDs tambien pueden
                    // apuntar a una factura origen (cuando se emite por ajuste) —
                    // mismo modelo OriginalInvoice. Si no hay original, queda null.
                    RelatedTo = (isCreditNote || kind == MovementKinds.DebitNote)
                        && i.OriginalInvoicePublicId.HasValue
                        ? new MovementRelatedToDto
                        {
                            Kind = MovementKinds.Invoice,
                            PublicId = i.OriginalInvoicePublicId.Value,
                            LegacyId = i.OriginalInvoiceId ?? 0,
                            Label = BuildInvoiceReference(i.OriginalInvoiceTipoComprobante ?? 0, i.OriginalInvoicePuntoDeVenta ?? 0, i.OriginalInvoiceNumeroComprobante ?? 0)
                        }
                        : null,
                });
            }
        }

        // Sort + paginate in-memory. Tie-breaker estable LegacyId desc.
        var ordered = (query.IsSortDescending() ? movements.OrderByDescending(m => m.Date) : movements.OrderBy(m => m.Date))
            .ThenByDescending(m => m.LegacyId)
            .ToList();

        var totalCount = ordered.Count;
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return PagedResponse<MovementDto>.Create(pageItems, page, pageSize, totalCount);
    }

    private async Task<string?> GetOwnerScopeOrNullAsync(CancellationToken ct)
    {
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        if (httpUser is null) return null;
        if (httpUser.IsInRole("Admin")) return null;

        var currentUserId = httpUser.FindFirstValue(ClaimTypes.NameIdentifier);
        if (_permissionResolver is null || string.IsNullOrEmpty(currentUserId))
        {
            return string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
        }

        var perms = await _permissionResolver.GetPermissionsAsync(currentUserId, ct);
        return perms.Contains(Permissions.CobranzasViewAll) ? null : currentUserId;
    }

    private async Task<int?> ResolveLegacyIdAsync<T>(string? rawId, CancellationToken ct) where T : class, IHasPublicId
    {
        if (string.IsNullOrWhiteSpace(rawId)) return null;
        // Acepta legacy int (1, 2, ...) o Guid public id. Util en queries que
        // pueden recibir cualquier forma desde el cliente.
        if (int.TryParse(rawId, out var legacyId)) return legacyId;
        try { return await _entityReferenceResolver.ResolveRequiredIdAsync<T>(rawId, ct); }
        catch (KeyNotFoundException) { return -1; } // Forzar match vacio en filtros si la entidad no existe.
    }

    // Defaults: si Kinds es null, traer los 5. Si viene csv, parsear.
    private static HashSet<string> ParseKinds(string? raw)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MovementKinds.Payment,
            MovementKinds.Invoice,
            MovementKinds.DebitNote,
            MovementKinds.CreditNote,
            MovementKinds.CreditNoteReversal,
        };
        if (string.IsNullOrWhiteSpace(raw)) return all;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var requested = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(all.Intersect(requested), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mapea el cbteTipo AFIP al discriminador de MovementDto.Kind. Centralizado
    /// para que cualquier proyeccion de Invoice -> MovementDto use la misma logica.
    /// Tipos desconocidos quedan como "invoice" (fallback conservador hasta que el
    /// dato sucio se observe y limpie); loguearlos como warning seria una mejora.
    /// </summary>
    internal static string GetKindForInvoiceTipo(int tipoComprobante) =>
        InvoiceComprobanteHelpers.Categorize(tipoComprobante) switch
        {
            InvoiceComprobanteCategory.CreditNote => MovementKinds.CreditNote,
            InvoiceComprobanteCategory.DebitNote => MovementKinds.DebitNote,
            InvoiceComprobanteCategory.Invoice => MovementKinds.Invoice,
            // Tipo no reconocido — comportamiento conservador: no exponer acciones
            // de la UI (la matriz frontend devuelve [] para kinds desconocidos).
            _ => MovementKinds.Invoice,
        };

    private static string BuildPaymentReference(string method, string? reference, bool isReversal)
    {
        if (isReversal)
            return reference ?? "Reversion por NC";
        if (string.IsNullOrWhiteSpace(reference))
            return method;
        return $"{method} · {reference}";
    }

    private static string BuildInvoiceReference(int tipoComprobante, int puntoDeVenta, long numero)
    {
        var label = tipoComprobante switch
        {
            1 => "Factura A",
            6 => "Factura B",
            11 => "Factura C",
            51 => "Factura M",
            2 => "ND A",
            7 => "ND B",
            12 => "ND C",
            52 => "ND M",
            3 => "NC A",
            8 => "NC B",
            13 => "NC C",
            53 => "NC M",
            _ => $"Comprobante #{tipoComprobante}",
        };
        return $"{label} {puntoDeVenta:D5}-{numero:D8}";
    }

    // Status humanizado para movements (alineado con frontend hoy):
    //  - Annulled si AnnulmentStatus == Succeeded (factura quedo anulada).
    //  - Approved si Resultado == "A".
    //  - Rejected si Resultado == "R".
    //  - InProgress en cualquier otro caso (PENDING, null).
    private static string BuildInvoiceStatus(string? resultado, AnnulmentStatus annulmentStatus) =>
        annulmentStatus == AnnulmentStatus.Succeeded
            ? "Annulled"
            : resultado switch
            {
                "A" => "Approved",
                "R" => "Rejected",
                _ => "InProgress",
            };
}
