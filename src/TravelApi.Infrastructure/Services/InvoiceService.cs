using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Globalization;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Hangfire;
using Npgsql;

namespace TravelApi.Infrastructure.Services;

public class InvoiceService : IInvoiceService
{
    private readonly AppDbContext _context;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly IAfipService _afipService;
    private readonly IInvoicePdfService _pdfService;
    private readonly IMapper _mapper;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<InvoiceService> _logger;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly UserManager<ApplicationUser> _userManager;
    // B1.15 Fase 2a (FIX 6): opcionales para no romper unit tests del ctor original.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // B1.15 Fase D (2026-05-11): opcional para no romper unit tests del ctor previo.
    private readonly IApprovalRequestService? _approvalService;
    // B1.15 Fase B'' (2026-05-11): opcional por la misma razon.
    private readonly IApprovalPolicyService? _approvalPolicyService;
    // FC1.2.1 (BR-V2-04, MR-V2-02): bridge "chico" hacia BookingCancellationService.
    // Inyectado opcional porque los unit tests del invoice no necesitan resolverlo
    // y porque el ciclo DI se rompio justamente con esta interface acotada.
    // Si esta presente, ProcessAnnulmentJob lo invoca post-CAE para sincronizar
    // el estado del BC asociado. Si esta null, el job sigue funcionando para
    // annulaciones standalone (back-office sin BC).
    private readonly IInvoiceAnnulmentBcBridge? _bcBridge;
    private static readonly string[] ActiveInvoicingStatuses =
    {
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling
    };

    public InvoiceService(
        AppDbContext context,
        IEntityReferenceResolver entityReferenceResolver,
        IAfipService afipService,
        IInvoicePdfService pdfService,
        IMapper mapper,
        IBackgroundJobClient backgroundJobClient,
        ILogger<InvoiceService> logger,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        UserManager<ApplicationUser> userManager,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IApprovalRequestService? approvalService = null,
        IApprovalPolicyService? approvalPolicyService = null,
        IInvoiceAnnulmentBcBridge? bcBridge = null)
    {
        _context = context;
        _entityReferenceResolver = entityReferenceResolver;
        _afipService = afipService;
        _pdfService = pdfService;
        _mapper = mapper;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _userManager = userManager;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _approvalService = approvalService;
        _approvalPolicyService = approvalPolicyService;
        _bcBridge = bcBridge;
    }

    /// <summary>
    /// B1.15 Fase 2a (FIX 6): null si Admin o user con cobranzas.view_all (=> ve todo);
    /// si no, devuelve currentUserId (filter mine via Reserva.ResponsibleUserId).
    /// Si no hay user resoluble, devuelve sentinel "__no_user__" (no expone nada).
    /// </summary>
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

    public async Task<PagedResponse<InvoiceListDto>> GetAllAsync(InvoicesListQuery query, CancellationToken ct)
    {
        // B1.15 Fase 2a (FIX 6): filter mine via Reserva.ResponsibleUserId.
        var ownerScope = await GetOwnerScopeOrNullAsync(ct);

        var invoicesQuery = ApplyInvoiceSearch(_context.Invoices.AsNoTracking(), query.Search);
        if (ownerScope is not null)
        {
            invoicesQuery = invoicesQuery.Where(i => i.Reserva != null && i.Reserva.ResponsibleUserId == ownerScope);
        }
        invoicesQuery = ApplyInvoiceKind(invoicesQuery, query.Kind);
        invoicesQuery = ApplyInvoiceStructuredFilters(invoicesQuery, query);
        invoicesQuery = ApplyInvoiceOrdering(invoicesQuery, query);

        return await invoicesQuery
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                ForcedByUserId = invoice.ForcedByUserId,
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK",
                AnnulmentStatus = invoice.AnnulmentStatus.ToString(),
                OriginalInvoicePublicId = invoice.OriginalInvoice != null ? (Guid?)invoice.OriginalInvoice.PublicId : null,
                OriginalInvoiceNumeroComprobante = invoice.OriginalInvoice != null ? (long?)invoice.OriginalInvoice.NumeroComprobante : null,
                OriginalInvoiceTipoComprobante = invoice.OriginalInvoice != null ? (int?)invoice.OriginalInvoice.TipoComprobante : null,
                OriginalInvoicePuntoDeVenta = invoice.OriginalInvoice != null ? (int?)invoice.OriginalInvoice.PuntoDeVenta : null
            })
            .ToPagedResponseAsync(query, ct);
    }

    public async Task<InvoicingSummaryDto> GetInvoicingSummaryAsync(CancellationToken ct)
    {
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        var allowsOverride = settings.AfipInvoiceControlMode == AfipInvoiceControlModes.AllowAgentOverrideWithReason;

        // B1.15 Fase 2a (FIX 6): filter mine para totales del summary.
        var ownerScope = await GetOwnerScopeOrNullAsync(ct);

        var approvedInvoices = _context.Invoices
            .AsNoTracking()
            .Where(i => i.Resultado == "A");
        if (ownerScope is not null)
        {
            approvedInvoices = approvedInvoices.Where(i => i.Reserva != null && i.Reserva.ResponsibleUserId == ownerScope);
        }

        var invoiceTotalsByReserva = approvedInvoices
            .GroupBy(invoice => invoice.ReservaId)
            .Select(group => new
            {
                ReservaId = group.Key,
                AlreadyInvoiced = group.Sum(invoice =>
                    invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                        ? -invoice.ImporteTotal
                        : invoice.ImporteTotal)
            });

        // Filter mine para reservas dentro del summary (cuenta y montos pendientes
        // de facturacion deben acotarse al scope del user).
        var reservasBase = _context.Reservas
            .AsNoTracking()
            .Where(reserva => ActiveInvoicingStatuses.Contains(reserva.Status));
        if (ownerScope is not null)
        {
            reservasBase = reservasBase.Where(reserva => reserva.ResponsibleUserId == ownerScope);
        }

        var pendingFiscalQuery = reservasBase
            .GroupJoin(
                invoiceTotalsByReserva,
                reserva => reserva.Id,
                totals => totals.ReservaId,
                (reserva, totals) => new
                {
                    Balance = Math.Round(reserva.Balance, 2),
                    TotalSale = Math.Round(reserva.TotalSale, 2),
                    AlreadyInvoiced = Math.Round(
                        totals.Select(total => (decimal?)total.AlreadyInvoiced).FirstOrDefault() ?? 0m,
                        2)
                })
            .Select(item => new
            {
                item.Balance,
                PendingFiscalAmount = item.TotalSale > item.AlreadyInvoiced
                    ? Math.Round(item.TotalSale - item.AlreadyInvoiced, 2)
                    : 0m
            })
            .Where(item => item.PendingFiscalAmount > 0m);

        var readyAmount = await pendingFiscalQuery
            .Where(item => item.Balance <= 0m)
            .SumAsync(item => (decimal?)item.PendingFiscalAmount, ct) ?? 0m;

        var invoicedThisMonth = await approvedInvoices
            .Where(invoice => invoice.CreatedAt >= startOfMonth)
            .SumAsync(invoice => (decimal?)(
                invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                    ? -invoice.ImporteTotal
                    : invoice.ImporteTotal), ct) ?? 0m;

        return new InvoicingSummaryDto
        {
            ReadyAmount = Math.Round(readyAmount, 2),
            ReadyCount = await pendingFiscalQuery.CountAsync(item => item.Balance <= 0m, ct),
            OverrideCount = allowsOverride
                ? await pendingFiscalQuery.CountAsync(item => item.Balance > 0m, ct)
                : 0,
            BlockedCount = allowsOverride
                ? 0
                : await pendingFiscalQuery.CountAsync(item => item.Balance > 0m, ct),
            InvoicedThisMonth = Math.Round(invoicedThisMonth, 2),
            ForcedCount = await approvedInvoices.CountAsync(invoice => invoice.WasForced, ct)
        };
    }

    public async Task<PagedResponse<InvoicingWorkItemDto>> GetInvoicingWorklistAsync(InvoicingWorklistQuery query, CancellationToken ct)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        // B1.15 Fase 2a (FIX 6): filter mine via Reserva.ResponsibleUserId.
        var ownerScope = await GetOwnerScopeOrNullAsync(ct);
        var workItemsQuery = BuildInvoicingWorkItemsQuery(settings, ownerScope);
        workItemsQuery = ApplyInvoicingWorkItemSearch(workItemsQuery, query.Search);
        workItemsQuery = ApplyInvoicingWorkItemStructuredFilters(workItemsQuery, query);
        workItemsQuery = ApplyInvoicingWorkItemStatus(workItemsQuery, query.Status);
        workItemsQuery = ApplyInvoicingWorkItemOrdering(workItemsQuery, query);

        return await workItemsQuery.ToPagedResponseAsync(query, ct);
    }

    public async Task<InvoiceDto> CreateAsync(CreateInvoiceRequest request, string? userId, string? userName, CancellationToken ct)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(request.ReservaId, ct);
        var reserva = await _context.Reservas
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new InvalidOperationException("Reserva no encontrada.");

        // B1.15 (2026-05-11, fiscal critico): guard anti-doble-emision concurrente.
        //
        // Sin este guard, un doble click en Emitir o un usuario que duda durante la
        // ventana del job Hangfire (segundos a minutos) puede generar 2 jobs ProcessInvoiceJob
        // en paralelo. Cada job pide CAE a AFIP -> potencial doble factura en correlativa
        // fiscal, con consecuencias graves (numeracion, libros IVA, AFIP rechaza ajustes).
        //
        // Regla: el sistema PERMITE multiples facturas por reserva (cobranzas parciales,
        // NCs). El guard NO bloquea por aprobadas previas. Solo bloquea cuando hay OTRA
        // invoice en estado PENDING ligada a la misma reserva, no anulada (Succeeded).
        // Una invoice con NC aprobada (AnnulmentStatus=Succeeded) ya no esta "en vuelo"
        // — la factura quedo cerrada.
        //
        // Cubre Facturas normales y NCs/NDs: la entidad fisica es la misma Invoice, y
        // todas pasan por ProcessInvoiceJob -> CAE. Si una NC PENDING existe para la
        // reserva, emitir otra factura (o NC) tambien puede pegarle a la correlativa.
        var hasPendingInFlight = await _context.Invoices
            .AsNoTracking()
            .AnyAsync(i =>
                i.ReservaId == reservaId &&
                i.Resultado == "PENDING" &&
                i.AnnulmentStatus != AnnulmentStatus.Succeeded, ct);
        if (hasPendingInFlight)
        {
            throw new InvalidOperationException(
                "Ya hay una factura en proceso para esta reserva. Espera a que termine antes de emitir otra.");
        }

        if (!request.IsCreditNote && !request.IsDebitNote)
        {
            var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
            var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);

            if (!EconomicRulesHelper.IsEconomicallySettled(reserva))
            {
                if (!request.ForceIssue)
                    throw new InvalidOperationException(afip.BlockReason ?? "La reserva tiene deuda y no puede emitirse en AFIP.");

                if (settings.AfipInvoiceControlMode != AfipInvoiceControlModes.AllowAgentOverrideWithReason)
                    throw new InvalidOperationException("La configuracion actual no permite emitir AFIP con deuda.");

                if (string.IsNullOrWhiteSpace(request.ForceReason) || request.ForceReason.Trim().Length < 10)
                    throw new InvalidOperationException("Debe indicar un motivo valido para emitir AFIP con deuda.");

                request.ForceReason = request.ForceReason.Trim();
                request.ForcedByUserId = userId;
                request.ForcedByUserName = userName;
            }
        }

        // 1. Create Pending in DB
        //
        // B1.15 (2026-05-11, fiscal critico): el guard aplicativo de arriba (AnyAsync)
        // es la primera linea de defensa, pero NO es atomico bajo concurrencia. Si dos
        // requests pasan el AnyAsync simultaneamente (T1 read -> T2 read -> ambos SaveChanges)
        // se encolarian 2 ProcessInvoiceJob para la misma reserva — riesgo de doble CAE.
        //
        // Backstop: la migracion AddInvoicePendingInFlightUniqueIndex crea
        // UX_Invoices_OnePendingPerReserva (UNIQUE PARCIAL) sobre TravelFileId con filtro
        // Resultado='PENDING' AND AnnulmentStatus != Succeeded. El segundo INSERT recibe
        // 23505 unique_violation de Postgres y aca lo traducimos a la MISMA
        // InvalidOperationException que tira el guard aplicativo (mismo 409, mismo mensaje)
        // para no romper el contrato del endpoint hacia el frontend.
        Invoice invoice;
        try
        {
            invoice = await _afipService.CreatePendingInvoice(reservaId, request);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogWarning(ex,
                "Race condition al crear Invoice PENDING para ReservaId {ReservaId}: el unique index UX_Invoices_OnePendingPerReserva rechazo el INSERT. El guard aplicativo no llego a verlo.",
                reservaId);
            throw new InvalidOperationException(
                "Ya hay una factura en proceso para esta reserva. Espera a que termine antes de emitir otra.");
        }

        if (invoice.WasForced)
        {
            await NotifyAdminsOfForcedInvoiceAsync(invoice, request, ct);
        }

        // 2. Enqueue Job
        _backgroundJobClient.Enqueue<IAfipService>(s => s.ProcessInvoiceJob(invoice.Id));

        return _mapper.Map<InvoiceDto>(invoice);
    }

    /// <summary>
    /// B1.15 (2026-05-11): detecta unique_violation (SQLSTATE 23505) de PostgreSQL
    /// cuando se produce dentro de un SaveChanges* de EF Core. EF envuelve el error
    /// de Npgsql en DbUpdateException — chequeamos el InnerException tal cual hace
    /// Microsoft en la doc oficial.
    ///
    /// Se mantiene como helper privado y no como utility compartida por ahora: solo
    /// hay un uso (CreateAsync). Si aparece un segundo caso se promueve a Infrastructure.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation;
    }

    public async Task<bool> RetryAsync(int id, CancellationToken ct)
    {
        var invoice = await _context.Invoices.FindAsync(new object[] { id }, ct);
        if (invoice == null) return false;
        if (invoice.Resultado == "A") throw new InvalidOperationException("La factura ya está aprobada.");

        // B1.15 Fase 0' (CODE-02): idempotencia con flow de anulacion. Si la
        // factura esta en proceso de anulacion (Pending) o ya fue anulada
        // (Succeeded), reintentar la emision rompe la coherencia fiscal — no se
        // puede emitir una factura que ya tiene NC en AFIP. Failed permite
        // reintento (la NC fallo, la factura sigue viva).
        if (invoice.AnnulmentStatus is AnnulmentStatus.Pending or AnnulmentStatus.Succeeded)
        {
            throw new InvalidOperationException(
                invoice.AnnulmentStatus == AnnulmentStatus.Succeeded
                    ? "No se puede reintentar la emision de una factura ya anulada (NC aprobada)."
                    : "No se puede reintentar la emision de una factura con anulacion en proceso. Esperá a que AFIP confirme la NC.");
        }

        // B1.15 (2026-05-11, fiscal critico): idempotencia con job de emision en vuelo.
        //
        // Si Resultado="PENDING" y la factura no esta en flow de anulacion (chequeado
        // arriba), el job ProcessInvoiceJob ya esta encolado/corriendo. Reintentar
        // mientras esta en curso encolaria un segundo job concurrente — riesgo de doble
        // pedido de CAE a AFIP y ruptura de correlativa. El operador debe esperar el
        // resultado: si AFIP rechaza queda en "R" (entonces si se puede reintentar);
        // si aprueba queda en "A" (cubierto por el guard previo de Resultado=="A").
        //
        // Nota: el orden importa. Cuando Resultado=PENDING + AnnulmentStatus=Pending
        // ambos guards aplicarian; preferimos el mensaje de anulacion porque el flow
        // dominante es la NC en curso (la emision PENDING quedo huerfana y no se va a
        // reintentar nunca de ese estado).
        if (invoice.Resultado == "PENDING")
        {
            throw new InvalidOperationException(
                "La factura ya esta en proceso. Espera el resultado antes de reintentar.");
        }

        // Reset to PENDING so UI shows yellow
        invoice.Resultado = "PENDING";
        invoice.Observaciones = null;
        await _context.SaveChangesAsync(ct);

        _backgroundJobClient.Enqueue<IAfipService>(s => s.ProcessInvoiceJob(id));
        return true;
    }

    public async Task<IEnumerable<InvoiceListDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct)
    {
        return await _context.Invoices
            .AsNoTracking()
            .Where(i => i.ReservaId == reservaId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                ForcedByUserId = invoice.ForcedByUserId,
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK",
                AnnulmentStatus = invoice.AnnulmentStatus.ToString(),
                OriginalInvoicePublicId = invoice.OriginalInvoice != null ? (Guid?)invoice.OriginalInvoice.PublicId : null,
                OriginalInvoiceNumeroComprobante = invoice.OriginalInvoice != null ? (long?)invoice.OriginalInvoice.NumeroComprobante : null,
                OriginalInvoiceTipoComprobante = invoice.OriginalInvoice != null ? (int?)invoice.OriginalInvoice.TipoComprobante : null,
                OriginalInvoicePuntoDeVenta = invoice.OriginalInvoice != null ? (int?)invoice.OriginalInvoice.PuntoDeVenta : null
            })
            .ToListAsync(ct);
    }

    public async Task<byte[]> GetPdfAsync(int id, CancellationToken ct)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Reserva)
            .ThenInclude(t => t.Payer)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (invoice == null) throw new KeyNotFoundException("Factura no encontrada");

        var settings = await _context.AfipSettings.FirstOrDefaultAsync(ct);
        if (settings == null) throw new InvalidOperationException("Configuración de AFIP no encontrada");

        var agencySettings = await _context.AgencySettings.FirstOrDefaultAsync(ct) ?? new AgencySettings();

        return _pdfService.GenerateInvoicePdf(invoice, invoice.Reserva, settings, agencySettings);
    }

    public async Task EnqueueAnnulmentAsync(
        int id,
        string userId,
        string? userName,
        string? reason,
        bool requesterIsAdmin,
        CancellationToken ct,
        int? approvalRequestId = null)
    {
        // B1.15 Fase 2a (FIX 6): persistir trazabilidad de la solicitud antes de
        // encolar. AnnulmentStatus = Pending bloquea cancel de reserva incluso si
        // el job tarda en ejecutar — la NC todavia no esta aprobada por AFIP.
        var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new KeyNotFoundException($"Factura {id} no encontrada.");

        // B1.15 Fase 2a (review final — fiscal critico): idempotencia. Bloquea:
        //  - Doble click del operador (Pending -> Pending -> 2 jobs encolados ->
        //    potencial 2 NCs en AFIP, numeracion correlativa rota).
        //  - Re-anulacion de una factura ya con NC aprobada (Succeeded).
        // Permite re-intento desde Failed (util si AFIP dio timeout) y anulacion
        // fresca desde None.
        if (invoice.AnnulmentStatus is AnnulmentStatus.Pending or AnnulmentStatus.Succeeded)
        {
            throw new InvalidOperationException(
                invoice.AnnulmentStatus == AnnulmentStatus.Succeeded
                    ? "La factura ya fue anulada (NC aprobada). No se puede re-anular."
                    : "La factura tiene una anulacion en curso. Espera el resultado o reintenta si quedo en Failed.");
        }

        // 2026-05-11 (fix arca-tax-expert, fiscal critico): solo Facturas A/B/C soportan
        // anulacion automatica. NDs (2,7,12,52), NCs (3,8,13,53) y Facturas M (51) NO
        // se anulan desde la UI nueva — antes caian en el switch default de
        // ProcessAnnulmentJob como cbteTipo=0 y AFIP rechazaba con error oscuro,
        // dejando la factura en Pending/Failed sin razon clara para el operador.
        // Fail-fast aca evita encolar un job condenado a fallar.
        //
        // Excepcion intencional: los casos legacy 3->2, 8->7, 13->12 ("anular una NC
        // con una ND") siguen vivos en ProcessAnnulmentJob por back-compat. Pero la
        // UI nueva no expone esa accion (ver movementActions.js), por lo que en la
        // practica este endpoint solo recibe tipos 1/6/11. El guard aca aplica a
        // toda invocacion del endpoint (controllers, scripts), no a llamadas internas.
        if (!InvoiceComprobanteHelpers.IsSupportedForAnnulment(invoice.TipoComprobante))
        {
            throw new InvalidOperationException(
                $"El tipo de comprobante {invoice.TipoComprobante} no soporta anulacion automatica. " +
                "Solo se anulan Facturas A/B/C desde la UI. " +
                "Para Notas de Debito/Credito o Facturas M, emitir el ajuste manualmente.");
        }

        // FC1.3.F2.5 (multimoneda) — DEFENSE IN DEPTH B-1 (revision backend+contable, 2026-05-28):
        // Fail-fast SINCRONO antes de encolar. La anulacion total automatica solo sabe emitir NC
        // en pesos (el request mas abajo no setea MonId, asi que la NC nace PES/1). Si la factura
        // origen esta en moneda extranjera, encolar el job seria condenarlo: ProcessAnnulmentJob
        // tiene el mismo guard y lo marcaria Failed en background. Frenando aca, el caller
        // (ConfirmAsync auto-aprobable, OnApprovedAsync fallback flag-OFF, endpoint manual) recibe
        // el error en el acto y el operador entiende que tiene que ir por NC parcial (F2.5) o
        // resolver manual desde ARCA. NUNCA se emite una NC en pesos para una factura en dolares.
        if (!string.Equals(invoice.MonId, "PES", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"La factura {invoice.NumeroComprobante} esta en moneda {invoice.MonId}: la anulacion total " +
                "automatica solo emite Notas de Credito en pesos. Emitir la NC en moneda extranjera " +
                "(NC parcial F2.5) o resolver manualmente desde ARCA.");
        }

        // B1.15 Fase D (2026-05-11): si policy.RequiresApproval Y caller NO es
        // Admin, requiere ApprovalRequest aprobado. Admin bypassa el workflow.
        // B1.15 Fase B'' (2026-05-11): la decision se lee desde ApprovalPolicy
        // (configurable por Admin), no desde el setting global viejo (deprecado).
        // Fallback al setting viejo si el policy service no esta inyectado
        // (compat unit tests). Si tampoco hay setting, fallback true (conservador).
        //
        // FC1.2.1 v3 (BR-V2-03, 2026-05-17): si el caller paso un
        // <c>approvalRequestId</c> propio (caso BookingCancellationService:
        // InvariantOverride aprobado al BC), lo usamos como cross-reference
        // fiscal y NO buscamos un InvoiceAnnulment separado. La decision
        // OPS-FISCAL-001 cubre la equivalencia legal del approval del BC vs
        // un approval especifico de la NC.
        // Renombramos la local var de FC1.2.0 a evitar shadowing del parametro.
        int? consumedApprovalRequestId = approvalRequestId;
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        bool requiresApproval;
        if (_approvalPolicyService is not null)
        {
            requiresApproval = await _approvalPolicyService.RequiresApprovalAsync(
                ApprovalRequestType.InvoiceAnnulment,
                fallback: settings.RequireApprovalForInvoiceAnnulment,
                ct);
        }
        else
        {
            requiresApproval = settings.RequireApprovalForInvoiceAnnulment;
        }
        if (requiresApproval && !requesterIsAdmin)
        {
            if (_approvalService is null)
            {
                throw new InvalidOperationException(
                    "Workflow de aprobaciones no disponible. Contactar al Administrador.");
            }
            var approval = await _approvalService.FindActiveApprovedAsync(
                ApprovalRequestType.InvoiceAnnulment, "Invoice", id, userId, ct);
            if (approval is null)
            {
                throw new ApprovalRequiredException(
                    ApprovalRequestType.InvoiceAnnulment, "Invoice", id);
            }
            consumedApprovalRequestId = approval.Id;
            // Si el caller no paso motivo explicito, usar el del approval (auditoria fiscal).
            if (string.IsNullOrWhiteSpace(reason))
                reason = approval.Reason;
        }

        invoice.AnnulledByUserId = userId;
        invoice.AnnulledByUserName = userName;
        // AnnulledAt se setea cuando el job confirma la NC con AFIP. Hasta entonces
        // queda null; la "solicitud" se infiere por AnnulmentStatus = Pending.
        invoice.AnnulmentReason = reason;
        invoice.AnnulmentStatus = AnnulmentStatus.Pending;
        // FC1.2.1 v3 (BR-V2-03): persistir el cross-reference fiscal apenas se
        // dispara el job. Si el caller (BC service) paso un approvalRequestId,
        // este valor permite que el contador rastree "esta NC fue autorizada por
        // que ApprovalRequest" sin tener que reconstruir desde audit logs.
        if (consumedApprovalRequestId.HasValue)
        {
            invoice.AnnulmentApprovalRequestId = consumedApprovalRequestId.Value;
        }
        await _context.SaveChangesAsync(ct);

        _backgroundJobClient.Enqueue<IInvoiceService>(service => service.ProcessAnnulmentJob(id, userId, consumedApprovalRequestId));
    }

    public async Task ProcessAnnulmentJob(int invoiceId, string userId, int? approvalRequestId = null)
    {
        try
        {
            _logger.LogInformation("Iniciando anulación de factura {InvoiceId} para usuario {UserId}", invoiceId, userId);

            // B1.15 (2026-05-10 smoke): el Include(Reserva) es CRITICO. Sin el, la
            // construccion del request mas abajo deja ReservaId = string.Empty (linea
            // original.Reserva?.PublicId.ToString() ?? string.Empty), y entonces
            // ResolveRequiredIdAsync<Reserva>("") tira "Reserva no encontrado",
            // Hangfire reintenta 10 veces, y la NC nunca se emite.
            var original = await _context.Invoices
                .Include(i => i.Items)
                .Include(i => i.Tributes)
                .Include(i => i.Reserva)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (original == null) throw new Exception("Comprobante original no encontrado");
            if (original.Reserva == null)
                throw new Exception($"La factura {invoiceId} no tiene reserva asociada (ReservaId={original.ReservaId}). No se puede emitir NC.");

            // Avoid double processing
            if (await _context.Invoices.AnyAsync(i => i.OriginalInvoiceId == invoiceId && i.Resultado == "A"))
            {
                await CreateNotification(userId, $"El comprobante {original.NumeroComprobante} ya fue anulado.", "Warning", invoiceId);
                return;
            }

            // FC1.3.F2.5 (multimoneda) — DEFENSE IN DEPTH B-1 (revision backend+contable, 2026-05-28):
            //
            // Este es el path de NC TOTAL (anulacion completa). Arma el CreateInvoiceRequest mas
            // abajo SIN setear MonId/MonCotiz, asi que la NC nace con los defaults PES/1. Para una
            // factura origen en pesos eso es correcto. PERO si la factura origen fue en moneda
            // extranjera (ej. USD, MonId="DOL"), emitir su NC en PES seria un desfasaje fiscal
            // grave: el ARCA tendria una NC en pesos asociada a una factura en dolares.
            //
            // Por que el guard vive ACA y no solo en el calculator: el calculator (fix B-1 capa 1)
            // ya rutea las facturas USD a revision manual, pero este job es un punto de entrada
            // INDEPENDIENTE — lo invocan el path auto-aprobable de ConfirmAsync (step 8), el
            // fallback FC1.2 de OnApprovedAsync (flag OFF), reintentos de Hangfire y el endpoint
            // legacy de anulacion manual. No podemos asumir que el calculator corrio antes.
            //
            // PRINCIPIO RECTOR: ninguna factura en moneda extranjera debe producir un comprobante
            // ARCA cuyo MonId/MonCotiz NO coincida con el del comprobante original. Si por la
            // moneda no se puede emitir una NC total coherente, frenamos en seco (Failed + aviso)
            // — NUNCA emitimos en PES por default. La resolucion correcta es la NC PARCIAL F2.5
            // (que SI lleva la moneda real), o tratamiento manual desde ARCA.
            if (!string.Equals(original.MonId, "PES", StringComparison.OrdinalIgnoreCase))
            {
                original.AnnulmentStatus = AnnulmentStatus.Failed;
                await _context.SaveChangesAsync();

                var foreignReason =
                    $"La factura {original.NumeroComprobante} esta en moneda {original.MonId} (cotizacion " +
                    $"{original.MonCotiz.ToString("0.######", CultureInfo.InvariantCulture)}). La anulacion total " +
                    "automatica solo emite Notas de Credito en pesos, asi que se bloqueo para no generar un " +
                    "comprobante con moneda incorrecta. Emitir la NC en moneda extranjera (NC parcial F2.5) o " +
                    "resolver manualmente desde ARCA.";
                _logger.LogWarning(
                    "Annulment job aborted for Invoice {InvoiceId}: moneda {MonId} != PES. No se emite NC total en pesos.",
                    invoiceId, original.MonId);
                await CreateNotification(userId, foreignReason, "Error", invoiceId);
                return;
            }

            // Determine Type (Credit or Debit Note)
            // 2026-05-11 (fix arca-tax-expert, fiscal critico): el switch heredado
            // tenia un default que dejaba cbteTipo=0 para tipos no mapeados (2, 7, 12,
            // 51, 52, 53). Eso se enviaba a AFIP y fallaba con error tecnico opaco
            // (WSFE rechaza CbteTipo=0). Ahora se valida explicitamente antes del
            // switch. El guard duplica el de EnqueueAnnulmentAsync por defensa en
            // profundidad: el job tambien puede ser invocado por Hangfire retry o
            // reschedule despues de un cambio de tipo, asi que no podemos asumir
            // que el guard de entrada se ejecuto.
            int cbteTipo = 0;
            switch (original.TipoComprobante)
            {
                case 1: cbteTipo = 3; break; // Fac A -> NC A
                case 6: cbteTipo = 8; break; // Fac B -> NC B
                case 11: cbteTipo = 13; break; // Fac C -> NC C
                // INALCANZABLES HOY: el guard upstream en EnqueueAnnulmentAsync
                // (InvoiceComprobanteHelpers.IsSupportedForAnnulment) solo permite
                // tipos 1/6/11 y rechaza 3/8/13 con InvalidOperationException antes
                // de encolar el job. Estos cases se conservan como documentacion
                // del mapeo historico "anular NC con ND" (cliente devuelve el
                // ajuste). Si en el futuro se decide habilitar ese flujo, el guard
                // upstream debe ampliarse y deben sumarse tests de regresion antes
                // de confiar en estos cases.
                case 3: cbteTipo = 2; break; // NC A -> ND A (inalcanzable)
                case 8: cbteTipo = 7; break; // NC B -> ND B (inalcanzable)
                case 13: cbteTipo = 12; break; // NC C -> ND C (inalcanzable)
            }
            if (cbteTipo == 0)
            {
                // No se llamo a AFIP. Marcar Failed y notificar para que el operador
                // tenga visibilidad. AnnulmentStatus = Failed mantiene el bloqueo de
                // cancel de reserva (FIX 7) hasta que back-office resuelva manual.
                original.AnnulmentStatus = AnnulmentStatus.Failed;
                await _context.SaveChangesAsync();

                var reason = $"El tipo de comprobante {original.TipoComprobante} no soporta anulacion automatica. " +
                             "Generar la Nota de Credito (o ajuste correspondiente) manualmente desde AFIP.";
                _logger.LogWarning(
                    "Annulment job aborted for Invoice {InvoiceId}: tipo {Tipo} no soportado.",
                    invoiceId, original.TipoComprobante);
                await CreateNotification(userId, reason, "Error", invoiceId);
                return;
            }

            var request = new CreateInvoiceRequest
            {
                ReservaId = original.Reserva?.PublicId.ToString() ?? string.Empty,
                CbteTipo = cbteTipo,
                Concepto = 3, // Productos y Servicios (default)
                DocTipo = 99, // Sin info
                DocNro = 0,
                OriginalInvoiceId = original.PublicId.ToString(),
                // Centralizado en InvoiceComprobanteHelpers para evitar duplicacion
                // y desalineo. Las ramas == 52/== 53 originales eran inalcanzables
                // porque el switch superior nunca produce esos valores (ver guard
                // upstream IsSupportedForAnnulment).
                IsCreditNote = InvoiceComprobanteHelpers.IsCreditNote(cbteTipo),
                IsDebitNote = InvoiceComprobanteHelpers.IsDebitNote(cbteTipo)
            };

            // Use Snapshots if available
            if (!string.IsNullOrEmpty(original.CustomerSnapshot))
            {
                var customer = System.Text.Json.JsonSerializer.Deserialize<Customer>(original.CustomerSnapshot);
                if (customer != null)
                {
                   if (!string.IsNullOrEmpty(customer.TaxId)) 
                    {
                        request.DocTipo = 80; // CUIT
                        if (long.TryParse(customer.TaxId.Replace("-", ""), out long cuit)) request.DocNro = cuit;
                    }
                    else if (!string.IsNullOrEmpty(customer.DocumentNumber))
                    {
                        request.DocTipo = 96; // DNI
                         if (long.TryParse(customer.DocumentNumber, out long dni)) request.DocNro = dni;
                    }
                    else
                    {
                        request.DocTipo = 99;
                        request.DocNro = 0;
                    }
                }
            }

            // --- RECONSTRUCTION LOGIC (Moved from Controller) ---
            
            // 1. Try Local Items
            if (original.Items.Any())
            {
                request.Items = original.Items.Select(i => new InvoiceItemDto
                {
                    Description = i.Description,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    Total = i.Total,
                    AlicuotaIvaId = i.AlicuotaIvaId
                }).ToList();
                
                request.Tributes = original.Tributes.Select(t => new InvoiceTributeDto
                {
                    TributeId = t.TributeId,
                    Description = t.Description,
                    BaseImponible = t.BaseImponible,
                    Alicuota = t.Alicuota,
                    Importe = t.Importe
                }).ToList();
            }
            else 
            {
                 // 2. Try AFIP (Legacy Fallback)
                 var details = await _afipService.GetVoucherDetails(original.TipoComprobante, original.PuntoDeVenta, original.NumeroComprobante);
                 
                 if (details != null && details.ImporteTotal > 0)
                 {
                     foreach (var vat in details.VatDetails)
                     {
                         request.Items.Add(new InvoiceItemDto
                         {
                             Description = $"Anulación Comp. {original.NumeroComprobante}",
                             Quantity = 1,
                             UnitPrice = vat.BaseImp, 
                             Total = vat.BaseImp,
                             AlicuotaIvaId = vat.Id
                         });
                     }
                     if (!request.Items.Any() && details.ImporteTotal > 0)
                     {
                          request.Items.Add(new InvoiceItemDto
                          {
                              Description = $"Anulación Comp. {original.NumeroComprobante}",
                              Quantity = 1,
                              UnitPrice = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              Total = details.ImporteNeto > 0 ? details.ImporteNeto : details.ImporteTotal,
                              AlicuotaIvaId = 3 
                          });
                     }
                     foreach (var trib in details.TributeDetails)
                     {
                         request.Tributes.Add(new InvoiceTributeDto
                         {
                             TributeId = trib.Id,
                             Description = trib.Desc,
                             BaseImponible = trib.BaseImp,
                             Alicuota = trib.Alic,
                             Importe = trib.Importe
                         });
                     }
                 }
                 else 
                 {
                     // 3. Last Resort (Local Totals)
                     decimal net = original.ImporteNeto > 0 ? original.ImporteNeto : original.ImporteTotal;
                     decimal iva = original.ImporteIva;
                     int ivaId = 3; 

                     if (iva > 0) ivaId = 5; 
                     
                     if (original.ImporteNeto == 0 && iva > 0) net = original.ImporteTotal - iva;

                     request.Items.Add(new InvoiceItemDto
                     {
                         Description = $"Anulación Comp. {original.NumeroComprobante} (Respaldo Local)",
                         Quantity = 1,
                         UnitPrice = net,
                         Total = net,
                         AlicuotaIvaId = ivaId
                     });
                 }
            }

            // Execute AFIP Call (Chain the pending creation and the processing)
            var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(request.ReservaId);
            var newInvoice = await _afipService.CreatePendingInvoice(reservaId, request);
            
            // Since we are already in a background job, we can process it immediately
            await _afipService.ProcessInvoiceJob(newInvoice.Id);
            
            // Reload to get the result updated by ProcessInvoiceJob
            await _context.Entry(newInvoice).ReloadAsync();

            if (newInvoice.Resultado == "A")
            {
                // B1.15 Fase 2a (FIX 6): NC aprobada por AFIP — la factura original
                // queda definitivamente anulada. Levanta el bloqueo fiscal de cancel
                // de reserva (FIX 7). AnnulledAt toma el momento de aprobacion.
                original.AnnulmentStatus = AnnulmentStatus.Succeeded;
                original.AnnulledAt = newInvoice.IssuedAt ?? DateTime.UtcNow;
                await _context.SaveChangesAsync();
                // B1.15 Fase D (2026-05-11): consumir la aprobacion ahora que la
                // accion solicitada se ejecuto. Si el caller era Admin, no hay
                // approvalRequestId (no-op).
                if (approvalRequestId.HasValue && _approvalService is not null)
                {
                    await _approvalService.MarkConsumedAsync(approvalRequestId.Value);
                }
                await CreateNotification(userId, $"Anulación exitosa. Se generó el comprobante {newInvoice.NumeroComprobante}.", "Success", newInvoice.Id);

                // FC1.2.1 v3 §6.2 / HC3 (BR-V2-04, 2026-05-17): sincronizar el BC
                // asociado (si existe). El try/catch envolvente es CRITICO: el
                // commit fiscal arriba ya quedo (AnnulmentStatus=Succeeded), por
                // lo tanto NO podemos hacer rethrow → Hangfire reintentaria el
                // job entero y llamaria AFIP de nuevo (NC duplicada). Si el bridge
                // falla, el path de remediacion es manual via
                // ForceArcaConfirmationAsync (BR-V2-01).
                //
                // Contrato del bridge (FC1.2.1 v3, leer antes de modificar este bloque):
                //   1. En este punto la NC YA quedo commiteada — el SaveChangesAsync
                //      anterior (linea original.AnnulmentStatus=Succeeded) ya ejecuto
                //      y AFIP ya emitio el comprobante (CAE devuelto). No hay vuelta atras.
                //   2. El bridge (BookingCancellationService.OnArcaSucceededAsync)
                //      abre su PROPIA unidad de trabajo / transaccion: carga el BC,
                //      lo transiciona a AwaitingOperatorRefund, escribe audit log y
                //      hace SaveChangesAsync. NO comparte la transaccion con este service.
                //   3. NO se debe tocar el DbContext entre el SaveChanges anterior y
                //      esta llamada — el bridge asume que el contexto esta limpio
                //      (Include necesarios, sin entidades tracked stale).
                //   4. Si el bridge falla, el escape hatch documentado es
                //      BookingCancellationService.ForceArcaConfirmationAsync (BR-V2-01),
                //      que requiere approval InvariantOverride.
                if (_bcBridge is not null)
                {
                    try
                    {
                        await _bcBridge.OnArcaSucceededAsync(invoiceId, newInvoice.Id, CancellationToken.None);
                    }
                    catch (Exception bridgeEx)
                    {
                        // Log humano: mismo nivel que antes para que el ops/back-office
                        // vea el contexto completo en el log de la app.
                        _logger.LogError(
                            bridgeEx,
                            "Bridge BC.OnArcaSucceededAsync fallo para Invoice {InvoiceId} (CN={CreditNoteId}). " +
                            "La NC quedo Succeeded en AFIP. Remediacion: ForceArcaConfirmationAsync manual.",
                            invoiceId, newInvoice.Id);

                        // Log estructurado para metricas/alerting. El prefijo "metric:"
                        // permite que un pipeline (Grafana/Loki/Datadog) extraiga el
                        // evento bc_bridge_failed y arme una alerta si se dispara N
                        // veces por semana — sintoma de que el bridge esta roto a
                        // nivel sistema, no caso aislado. NO incluye stack trace ni
                        // mensaje del usuario, solo identificadores estables y el
                        // tipo de excepcion.
                        _logger.LogError(
                            "metric:bc_bridge_failed | originatingInvoiceId={OriginatingInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId} errorType={ErrorType} stage=OnArcaSucceeded",
                            invoiceId,
                            newInvoice.Id,
                            bridgeEx.GetType().Name);
                    }
                }
            }
            else
            {
                // AFIP rechazo la NC — marcar Failed para que el guard de cancel
                // siga bloqueando hasta que el back-office reintente o resuelva.
                original.AnnulmentStatus = AnnulmentStatus.Failed;
                await _context.SaveChangesAsync();
                await CreateNotification(userId, $"La anulación falló en AFIP: {newInvoice.Observaciones}", "Error", invoiceId);

                // FC1.2.1 v3 §6.2: notificar al BC asociado. Mismo patron try/catch:
                // el commit Failed ya quedo, no podemos rethrow. El BC quedaria en
                // AwaitingFiscalConfirmation indefinidamente — el remediation es
                // manual (back-office decide reintentar o abandonar).
                if (_bcBridge is not null)
                {
                    try
                    {
                        await _bcBridge.OnArcaFailedAsync(invoiceId, newInvoice.Observaciones, CancellationToken.None);
                    }
                    catch (Exception bridgeEx)
                    {
                        _logger.LogError(
                            bridgeEx,
                            "Bridge BC.OnArcaFailedAsync fallo para Invoice {InvoiceId}. La NC esta Failed en AFIP.",
                            invoiceId);

                        // Mismo prefijo metric:bc_bridge_failed que el caso Succeeded
                        // para que el alerting capture ambos modos de falla con la
                        // misma regla. stage=OnArcaFailed diferencia el contexto.
                        _logger.LogError(
                            "metric:bc_bridge_failed | originatingInvoiceId={OriginatingInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId} errorType={ErrorType} stage=OnArcaFailed",
                            invoiceId,
                            (int?)null,
                            bridgeEx.GetType().Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico en Job de Anulación");

            // B1.15 Fase 2a (FIX 6): cualquier excepcion del job marca Failed.
            // Se hace en transaccion separada para no perder la marca si SaveChanges
            // posterior falla (best-effort).
            try
            {
                var failed = await _context.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId);
                if (failed is not null && failed.AnnulmentStatus != AnnulmentStatus.Succeeded)
                {
                    failed.AnnulmentStatus = AnnulmentStatus.Failed;
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "No se pudo persistir AnnulmentStatus=Failed para Invoice {InvoiceId}", invoiceId);
            }

            var errorMsg = ex.Message;
            if (errorMsg.Contains("AFIP RECHAZADO"))
            {
                 // Permanent error (Validation), do not retry
                 await CreateNotification(userId, $"La anulación fue rechazada por AFIP: {errorMsg.Replace("AFIP RECHAZADO: ", "")}", "Error", invoiceId);
                 return; // Job finishes effectively "Failed" but successfully handled
            }

            await CreateNotification(userId, $"Error técnico al anular: {errorMsg}. Se reintentará automáticamente.", "Error", invoiceId);
            throw; // Retry job for network/transient errors
        }
    }

    /// <summary>
    /// FC1.3.F2.2 (plan tactico §FC1.3.F2.2 puntos 1/3/7, 2026-05-27): punto de
    /// entrada para emitir una Nota de Credito (NC) PARCIAL real al ARCA.
    ///
    /// <para><b>Que hace</b> (y que NO hace): valida la coherencia de los montos del
    /// input, marca la factura origen como anulacion en curso (AnnulmentStatus =
    /// Pending) y encola el job <see cref="ProcessPartialCreditNoteJob"/>. NO toca el
    /// ARCA ni la tabla ArcaIdempotencyKeys — eso es responsabilidad del job
    /// (RH4-001). El motivo: entre encolar y ejecutar el job pueden pasar varios
    /// minutos bajo carga de Hangfire, y el numerador del ARCA puede avanzar por
    /// otros emisores en el medio. Si capturaramos el snapshot del numerador aca,
    /// el recovery posterior compararia contra un dato viejo. Por eso el snapshot
    /// vive DENTRO del job, en la misma ejecucion que el POST efectivo.</para>
    ///
    /// <para><b>Diferencia con <see cref="EnqueueAnnulmentAsync"/></b>: aquella emite
    /// NC TOTAL replicando 1:1 los items de la factura origen. Esta emite NC sobre
    /// solo una parte (la liquidacion ya viene calculada con las lineas a acreditar).</para>
    /// </summary>
    public async Task EnqueuePartialCreditNoteAsync(
        int originalInvoiceId,
        PartialCreditNoteEmissionInput liquidation,
        string userId,
        string? userName,
        string? reason,
        int approvalRequestId,
        CancellationToken ct)
    {
        // 1) Cargar la factura origen. Si no existe, fail-fast antes de cualquier
        //    side-effect (igual que EnqueueAnnulmentAsync).
        var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.Id == originalInvoiceId, ct)
            ?? throw new KeyNotFoundException($"Factura {originalInvoiceId} no encontrada.");

        // 2) Idempotencia: misma regla que EnqueueAnnulmentAsync. Pending o Succeeded
        //    bloquean — la factura ya tiene una NC en curso o aprobada y emitir otra
        //    romperia la numeracion correlativa / duplicaria la acreditacion fiscal.
        //    Failed permite reintento (util si el ARCA dio timeout).
        if (invoice.AnnulmentStatus is AnnulmentStatus.Pending or AnnulmentStatus.Succeeded)
        {
            throw new InvalidOperationException(
                invoice.AnnulmentStatus == AnnulmentStatus.Succeeded
                    ? "La factura ya fue anulada (NC aprobada). No se puede emitir otra NC parcial."
                    : "La factura tiene una anulacion en curso. Espera el resultado o reintenta si quedo en Failed.");
        }

        // 3) Solo Facturas A/B/C soportan NC parcial automatica (RH-003). Factura M
        //    (51) NO esta soportada en Fase 2: el helper la deja afuera. NDs y NCs
        //    tampoco pueden ser origen. Fail-fast aca evita encolar un job condenado.
        if (!InvoiceComprobanteHelpers.IsSupportedForAnnulment(invoice.TipoComprobante))
        {
            throw new InvalidOperationException(
                $"El tipo de comprobante {invoice.TipoComprobante} no soporta NC parcial automatica. " +
                "Factura M no soportada para NC parcial en Fase 2 (RH-003). " +
                "Solo se emiten NC parciales sobre Facturas A/B/C.");
        }

        // 4) Validacion defensiva PRE-encolado (plan punto 7 / M4). Si los montos del
        //    input no son coherentes entre si, rebotamos ACA — ANTES de mutar el
        //    AnnulmentStatus y ANTES de encolar. Cero side-effects si la validacion
        //    falla: la factura queda intacta y no hay job huerfano en la cola.
        //
        //    Esta validacion es redundante con la que hara el job (defense-in-depth),
        //    pero detecta el problema upstream: mejor rebotar con un mensaje claro
        //    aca que dejar un job encolado que va a fallar al construir el XML.
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // 4.bis) Guard del feature flag maestro de Fase 2 (defense in depth, RH4).
        //    Este metodo es public: hoy su unico caller (F2.3, el BC service) todavia
        //    NO existe, pero igual no debe poder emitir NC parcial real si el flag esta
        //    apagado. No confiamos en que el caller valide el flag — lo chequeamos aca,
        //    upstream de cualquier side-effect. Con el flag OFF el sistema sigue
        //    operando como FC1.2 (NC total), asi que emitir una NC parcial aca seria un
        //    cambio de comportamiento no autorizado.
        //
        //    Va ANTES de mutar AnnulmentStatus y ANTES de encolar el job: si el flag
        //    esta apagado, cero side-effects (la factura queda intacta, no hay job
        //    huerfano), igual que la validacion de montos.
        if (!settings.EnablePartialCreditNoteRealEmission)
        {
            throw new InvalidOperationException(
                "Emision real de NC parcial deshabilitada (EnablePartialCreditNoteRealEmission=false). " +
                "Prenda el flag para operar.");
        }

        // FC1.3.F2.6 (counter): la validacion defensiva pre-encolado rebota con
        // ArgumentException si los montos no cuadran. Emitimos un counter ANTES de
        // re-lanzar para que el alerting vea cuantas liquidaciones llegan rotas hasta
        // aca (deberia ser ~0: si crece, indica un bug aguas arriba en F2.3 que arma
        // las lineas). El counter NO cambia el comportamiento: re-lanzamos la misma
        // excepcion para que el caller (BC service) la maneje igual que antes.
        try
        {
            ValidateLiquidationAmounts(liquidation, settings.PartialCreditNoteRoundingTolerance);
        }
        catch (ArgumentException)
        {
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue | originalInvoiceId={OriginalInvoiceId} approvalRequestId={ApprovalRequestId}",
                originalInvoiceId, approvalRequestId);
            throw;
        }

        // 5) Persistir la solicitud: AnnulmentStatus = Pending bloquea cancel de la
        //    reserva mientras la NC no este aprobada por el ARCA (igual que la NC total).
        //    AnnulledAt queda null hasta que el job confirme la NC con el ARCA.
        invoice.AnnulledByUserId = userId;
        invoice.AnnulledByUserName = userName;
        invoice.AnnulmentReason = reason;
        invoice.AnnulmentStatus = AnnulmentStatus.Pending;
        // Cross-reference fiscal: que ApprovalRequest autorizo esta NC parcial. A
        // diferencia de la NC total, aca el approval SIEMPRE es obligatorio (no hay
        // path Admin bypass para NC parcial — el caller del BC service ya lo exige).
        invoice.AnnulmentApprovalRequestId = approvalRequestId;
        await _context.SaveChangesAsync(ct);

        // 6) Serializar el input a JSON para pasarlo al job. Hangfire no serializa de
        //    forma confiable un record con IReadOnlyList anidada sin configuracion
        //    extra, asi que lo mandamos como string y el job lo deserializa (Etapa 5).
        var liquidationJson = System.Text.Json.JsonSerializer.Serialize(liquidation);

        // 7) Encolar el job. Mismo cliente Hangfire que EnqueueAnnulmentAsync:
        //    _backgroundJobClient.Enqueue<IInvoiceService>(...). Hangfire resuelve la
        //    expresion contra el TIPO IInvoiceService y serializa los argumentos.
        _backgroundJobClient.Enqueue<IInvoiceService>(service =>
            service.ProcessPartialCreditNoteJob(originalInvoiceId, liquidationJson, userId, approvalRequestId));
    }

    /// <summary>
    /// FC1.3.F2.2 (M4): valida la coherencia interna de los montos de la liquidacion
    /// ANTES de mutar estado o encolar. Lanza <see cref="ArgumentException"/> si algo
    /// no cuadra. No tiene side-effects.
    ///
    /// <para>Dos chequeos:
    /// <list type="number">
    ///   <item>Que la factura origen sea consistente: neto + IVA == total (dentro de
    ///   la tolerancia de redondeo). Si la factura venia rota, mejor rebotar aca que
    ///   mandar un XML inconsistente al ARCA.</item>
    ///   <item>Que la suma de las lineas a acreditar coincida con el monto fiscal a
    ///   acreditar. Si no coincide, la NC que armaria el job no representaria el monto
    ///   que se pretende devolver.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static void ValidateLiquidationAmounts(
        PartialCreditNoteEmissionInput liquidation,
        decimal tolerance)
    {
        // Chequeo 1: neto + IVA de la factura origen == total de la factura origen.
        var originalGap = Math.Abs(
            liquidation.OriginalNetAmount + liquidation.OriginalVatAmount - liquidation.OriginalTotalAmount);
        if (originalGap > tolerance)
        {
            throw new ArgumentException(
                "Los montos de la factura origen no son coherentes: " +
                $"neto ({liquidation.OriginalNetAmount}) + IVA ({liquidation.OriginalVatAmount}) " +
                $"!= total ({liquidation.OriginalTotalAmount}). Diferencia {originalGap} > tolerancia {tolerance}.",
                nameof(liquidation));
        }

        // Chequeo 2: suma de las lineas a acreditar == monto fiscal a acreditar.
        decimal linesTotal = 0m;
        foreach (var line in liquidation.Lines)
        {
            linesTotal += line.Total;
        }

        var linesGap = Math.Abs(linesTotal - liquidation.FiscalAmountToCredit);
        if (linesGap > tolerance)
        {
            throw new ArgumentException(
                "La suma de las lineas no coincide con el monto fiscal a acreditar: " +
                $"suma de lineas ({linesTotal}) != FiscalAmountToCredit ({liquidation.FiscalAmountToCredit}). " +
                $"Diferencia {linesGap} > tolerancia {tolerance}.",
                nameof(liquidation));
        }
    }

    /// <summary>
    /// FC1.3.F2.2 (Etapa 5, plan tactico §FC1.3.F2.2, 2026-05-27): Background Job que
    /// emite la Nota de Credito (NC) PARCIAL real al ARCA.
    ///
    /// <para><b>Que hace</b>: toma la liquidacion ya calculada (lineas + montos),
    /// prorratea el IVA, valida que el comprobante cuadre fiscalmente y lo emite via el
    /// pipeline existente (<c>CreatePendingInvoice</c> + <c>ProcessInvoiceJob</c>). NO
    /// toca el path SOAP de <c>AfipService</c>: arma el <c>CreateInvoiceRequest</c> y
    /// deja que el pipeline normal lo envie.</para>
    ///
    /// <para><b>Por que <c>[AutomaticRetry(Attempts = 0)]</c></b>: igual que
    /// <c>AfipService.ProcessInvoiceJob</c>, NO queremos que Hangfire reintente solo a
    /// ciegas un job que toca ARCA: un reintento descontrolado podria emitir 2
    /// comprobantes. El reintento se maneja de forma controlada via la idempotencia
    /// (tabla <c>ArcaIdempotencyKeys</c> + stale key recovery): si el job vuelve a
    /// correr, detecta la key del intento anterior y decide derivar el CAE ya emitido o
    /// reintentar limpio, en vez de re-POSTear.</para>
    ///
    /// <para><b>Flow anti-doble-POST</b> (capas de idempotencia del plan):
    /// <list type="number">
    ///   <item>(a) Snapshot del numerador ARCA como PRIMERA operacion (RH4-001), ANTES
    ///   de insertar la key.</item>
    ///   <item>(b) Calcular la <c>idemKey</c> deterministica (SHA256).</item>
    ///   <item>(c) Capa 1: INSERT de la key con el snapshot. UNIQUE en <c>Key</c>.</item>
    ///   <item>(d) Si el INSERT choca por unique violation -> capa 1.5 (stale key
    ///   recovery): si la key es huerfana y vieja, consultar ARCA y arbitrar; si es
    ///   reciente, abortar como duplicado.</item>
    ///   <item>(e) Si el INSERT tuvo exito -> armar el comprobante + emitir.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>SIGNOFF del contador antes de prender el flag</b>: este job arma el
    /// comprobante con las lineas que vienen en el input TAL CUAL (es F2.3 quien decide
    /// que parte pierde causa fiscal). El criterio fiscal fino — items exentos vs 0%,
    /// percepciones, tratamiento intermediario vs reseller — es responsabilidad del
    /// contador y se valida ANTES de poner <c>EnablePartialCreditNoteRealEmission=true</c>
    /// en produccion. Este metodo NO reinterpreta las lineas.</para>
    /// </summary>
    [AutomaticRetry(Attempts = 0)] // No reintentar a ciegas un job que toca ARCA (ver doc de arriba).
    public async Task ProcessPartialCreditNoteJob(
        int originalInvoiceId,
        string liquidationJson,
        string userId,
        int approvalRequestId)
    {
        // El job de Hangfire no recibe CancellationToken propio. Usamos None: una vez
        // que arrancamos el POST a ARCA no queremos cortar a mitad de camino (cancelar
        // dejaria el comprobante en estado incierto). Es el mismo criterio que
        // ProcessAnnulmentJob.
        var ct = CancellationToken.None;

        // 1) Deserializar la liquidacion que viajo por Hangfire como JSON. Si viene rota
        //    es un bug de plumbing del encolado, no un error de negocio: dejamos que la
        //    excepcion suba y el job quede Failed en el dashboard (visibilidad).
        var liquidation = System.Text.Json.JsonSerializer.Deserialize<PartialCreditNoteEmissionInput>(liquidationJson)
            ?? throw new InvalidOperationException(
                $"ProcessPartialCreditNoteJob: liquidationJson nulo/invalido para Invoice {originalInvoiceId}.");

        // Cargar la factura origen. Necesitamos PuntoDeVenta + NumeroComprobante (para el
        // snapshot del numerador y la comparacion del recovery) + PublicId (para vincular
        // la NC nueva como CbteAsoc via el pipeline existente).
        var originalInvoice = await _context.Invoices
            .FirstOrDefaultAsync(i => i.Id == originalInvoiceId, ct)
            ?? throw new InvalidOperationException(
                $"ProcessPartialCreditNoteJob: factura origen {originalInvoiceId} no encontrada.");

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // Defense-in-depth: aunque EnqueuePartialCreditNoteAsync ya valido el flag, el job
        // puede llegar a correr por un reintento o reschedule. Si el flag se apago en el
        // medio, NO emitimos: marcamos Failed y salimos sin tocar ARCA.
        if (!settings.EnablePartialCreditNoteRealEmission)
        {
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice,
                userId,
                "Emision real de NC parcial deshabilitada (EnablePartialCreditNoteRealEmission=false) " +
                "al momento de ejecutar el job. No se emitio el comprobante.",
                ct);
            return;
        }

        // Mapear el tipo de NC a partir del tipo de la factura origen (A->3, B->8, C->13).
        // Factura M (51) devuelve null: ya la rechaza EnqueuePartialCreditNoteAsync, pero
        // re-chequeamos aca por defensa (el job puede correr por reschedule).
        int? creditNoteCbteTipo = InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice(originalInvoice.TipoComprobante);
        if (creditNoteCbteTipo is null)
        {
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice,
                userId,
                $"El tipo de comprobante {originalInvoice.TipoComprobante} no soporta NC parcial " +
                "automatica en Fase 2 (Factura M no soportada, RH-003). No se emitio el comprobante.",
                ct);
            return;
        }

        try
        {
            // (a) Snapshot del numerador ARCA PRIMERO (RH4-001). Tiene que vivir en la
            //     misma ejecucion del job que el POST efectivo: por eso NO se captura en el
            //     encolado. Si Hangfire reintenta el job, este snapshot se recaptura y la
            //     capa 1.5 usa el de la corrida ANTERIOR (ya persistido en la key huerfana).
            int lastSeenNumeroBeforePost = await _afipService.GetLastAuthorizedNumeroAsync(
                puntoVenta: originalInvoice.PuntoDeVenta,
                cbteTipo: creditNoteCbteTipo.Value,
                ct);

            // (b) Calcular la idemKey deterministica. El formato fija 2 decimales en el
            //     monto (F2) para que dos intentos por la misma cancelacion produzcan
            //     EXACTAMENTE el mismo hash (sin esto, 300000 y 300000.00 darian hashes
            //     distintos y la idempotencia no agruparia los reintentos).
            string idemKey = BuildIdempotencyKey(
                originalInvoiceId: originalInvoiceId,
                approvalRequestId: approvalRequestId,
                fiscalAmountToCredit: liquidation.FiscalAmountToCredit,
                currency: liquidation.Currency);

            // (c) Capa 1: intentar insertar la key ANTES de tocar ARCA. El UNIQUE sobre
            //     "Key" es lo que rechaza un segundo intento concurrente.
            bool insertSucceeded = await TryInsertIdempotencyKeyAsync(
                idemKey,
                lastSeenNumeroBeforePost,
                ct);

            if (!insertSucceeded)
            {
                // (d) La key ya existia. Capa 1.5: decidir si recuperar el CAE ya emitido,
                //     borrar la key huerfana y reintentar, o abortar como duplicado activo.
                //     Devuelve true si recupero (no hay que emitir) o si abortamos como
                //     duplicado; false si limpio la key huerfana y hay que emitir de nuevo.
                bool resolvedWithoutNewPost = await HandleStaleIdempotencyKeyAsync(
                    idemKey: idemKey,
                    originalInvoice: originalInvoice,
                    liquidation: liquidation,
                    creditNoteCbteTipo: creditNoteCbteTipo.Value,
                    userId: userId,
                    settings: settings,
                    ct: ct);

                if (resolvedWithoutNewPost)
                {
                    return; // recovery derivo el CAE, o no es nuestro turno: no re-POSTear.
                }

                // La key huerfana fue borrada -> reintentar el INSERT limpio. Si vuelve a
                // chocar (otra corrida lo gano en el medio), tratamos como duplicado activo.
                bool reinsertSucceeded = await TryInsertIdempotencyKeyAsync(
                    idemKey,
                    lastSeenNumeroBeforePost,
                    ct);

                if (!reinsertSucceeded)
                {
                    throw new IdempotencyDuplicateException(
                        $"IdempotencyKey activa tras limpiar la huerfana: otro intento gano la carrera " +
                        $"para Invoice {originalInvoiceId}. No se re-emite la NC parcial.");
                }
            }

            // (e) INSERT OK -> armar el comprobante + emitir via el pipeline existente.
            await EmitPartialCreditNoteAsync(
                idemKey: idemKey,
                originalInvoice: originalInvoice,
                liquidation: liquidation,
                creditNoteCbteTipo: creditNoteCbteTipo.Value,
                userId: userId,
                approvalRequestId: approvalRequestId,
                settings: settings,
                ct: ct);
        }
        catch (IdempotencyDuplicateException dup)
        {
            // Duplicado legitimo (otro intento en vuelo, o key reciente): NO es un error
            // tecnico. Logueamos como warning + counter y NO marcamos Failed (el otro
            // intento o el job de reconciliacion lo resuelven). NO rethrow: con
            // AutomaticRetry(0) no se reintenta, y queremos que el job termine "limpio".
            _logger.LogWarning(
                "ProcessPartialCreditNoteJob duplicado para Invoice {InvoiceId}: {Message}",
                originalInvoiceId, dup.Message);
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.IdempotencyDuplicate | originalInvoiceId={OriginalInvoiceId} approvalRequestId={ApprovalRequestId}",
                originalInvoiceId, approvalRequestId);
        }
    }

    /// <summary>
    /// FC1.3.F2.2 (plan §F2.2 punto b): construye la clave de idempotencia deterministica
    /// de la NC parcial. Dos intentos por la MISMA cancelacion (misma factura origen, mismo
    /// approval, mismo monto, misma moneda) producen el MISMO hash, y el indice UNIQUE de
    /// <c>ArcaIdempotencyKeys</c> rechaza el segundo INSERT.
    ///
    /// <para><b>Por que el formato <c>:F2</c> en el monto</b>: fija 2 decimales para que el
    /// hash sea estable. Sin esto, <c>300000</c> y <c>300000.00</c> (mismo monto, distinta
    /// representacion decimal) generarian hashes distintos y la idempotencia no agruparia
    /// los reintentos del mismo hecho.</para>
    ///
    /// <para>Se usa <c>CultureInfo.InvariantCulture</c> en el <c>:F2</c>: en una maquina con
    /// cultura es-AR el separador decimal seria coma, y el hash cambiaria segun el server.
    /// Invariant fija el punto y hace el hash reproducible en cualquier entorno.</para>
    /// </summary>
    private static string BuildIdempotencyKey(
        int originalInvoiceId,
        int approvalRequestId,
        decimal fiscalAmountToCredit,
        string currency)
    {
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{originalInvoiceId}|{approvalRequestId}|{fiscalAmountToCredit:F2}|{currency}");

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

        // Hex en minuscula. La columna Key es varchar(64); SHA256 en hex son 64 chars exactos.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// FC1.3.F2.5 (multimoneda, 2026-05-28): traduce el codigo de moneda ISO 4217 que usa el
    /// negocio ("USD", "ARS", ...) al codigo del catalogo de monedas de ARCA ("DOL", "PES", ...).
    /// Devuelve <c>null</c> si la moneda no esta soportada todavia.
    ///
    /// <para><b>Por que delega en <see cref="ArcaCurrencyMapper"/></b>: el mapeo de monedas
    /// soportadas es UNA sola fuente de verdad en todo el sistema. El guard multimoneda de
    /// <c>BookingCancellationService</c> consume el MISMO helper para decidir si una NC parcial
    /// puede emitirse. Centralizar evita que las dos listas se desincronicen (drift) y que el
    /// guard aborte algo que este metodo si sabria emitir, o viceversa. Mantenemos este wrapper
    /// <c>private static</c> para no tocar el call site interno (EmitPartialCreditNoteAsync).</para>
    /// </summary>
    private static string? TryMapToArcaCurrencyCode(string isoCurrency) =>
        ArcaCurrencyMapper.TryMap(isoCurrency);

    /// <summary>
    /// FC1.3.F2.2 (plan §F2.2 capa 1): intenta insertar la clave de idempotencia ANTES del
    /// POST a ARCA. Devuelve <c>true</c> si el INSERT entro, <c>false</c> si choco con el
    /// indice UNIQUE (ya existe una key con ese valor).
    ///
    /// <para><b>Por que un context aislado</b> (<c>CreateScopedContext</c> NO se usa aca,
    /// pero si limpiamos el ChangeTracker): si el INSERT falla por unique violation, el
    /// <c>_context</c> queda con la entidad en estado Added "pegada". Si despues el flujo
    /// hace otro SaveChanges (ej. el recovery), EF intentaria re-insertarla y volveria a
    /// fallar. Por eso, ante el choque, sacamos la entidad del tracker (<c>Entry.State =
    /// Detached</c>) para dejar el context limpio.</para>
    /// </summary>
    private async Task<bool> TryInsertIdempotencyKeyAsync(
        string idemKey,
        int lastSeenNumeroBeforePost,
        CancellationToken ct)
    {
        var entity = new ArcaIdempotencyKey
        {
            Key = idemKey,
            // JobId: el job real tendria un id de Hangfire; en este punto no lo exponemos
            // de forma simple. Dejamos null (el schema lo permite) — la correlacion con
            // logs se hace por la key + el InvoiceId que sale en los warnings.
            JobId = null,
            CreatedAt = DateTime.UtcNow,
            ResolvedAt = null,
            LastSeenNumeroBeforePost = lastSeenNumeroBeforePost,
        };

        _context.ArcaIdempotencyKeys.Add(entity);
        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Ya existe una key con ese valor (otro intento, o una huerfana de un crash).
            // Limpiamos el tracker para que el context quede usable por el recovery.
            _context.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// FC1.3.F2.2 (plan §F2.2 capa 1.5, sub-tareas A.5/A.6): recupera de una clave de
    /// idempotencia que ya existe. Decide entre tres caminos segun el estado real en ARCA:
    ///
    /// <list type="bullet">
    ///   <item><b>Caso reciente</b> (key &lt; umbral): otro intento la esta procesando
    ///   AHORA. Lanza <see cref="IdempotencyDuplicateException"/> -> no re-POSTear.</item>
    ///   <item><b>Caso A</b> (huerfana + ARCA emitio el comprobante que matchea factura
    ///   origen + monto): el POST viajo en una corrida anterior. Deriva el CAE, resuelve la
    ///   key y NO re-POSTea. Devuelve <c>true</c>.</item>
    ///   <item><b>Caso B</b> (huerfana + POST nunca viajo, o el comprobante encontrado NO
    ///   matchea): borra la key y devuelve <c>false</c> para que el caller reintente
    ///   limpio.</item>
    /// </list>
    ///
    /// <para>Devuelve <c>true</c> si NO hay que emitir (recuperado o duplicado), <c>false</c>
    /// si la key huerfana fue borrada y hay que reintentar la emision.</para>
    /// </summary>
    private async Task<bool> HandleStaleIdempotencyKeyAsync(
        string idemKey,
        Invoice originalInvoice,
        PartialCreditNoteEmissionInput liquidation,
        int creditNoteCbteTipo,
        string userId,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        var existingKey = await _context.ArcaIdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == idemKey, ct);

        // Carrera: la key existia cuando el INSERT choco pero ya no esta (otro intento la
        // resolvio + el housekeeping la borro entre medio). Tratamos como duplicado para no
        // re-POSTear a ciegas: el job de reconciliacion lo recoge si quedo algo pendiente.
        if (existingKey is null)
        {
            throw new IdempotencyDuplicateException(
                $"IdempotencyKey desaparecio entre el INSERT fallido y la lectura para Invoice " +
                $"{originalInvoice.Id}. Otro intento la resolvio. No se re-emite.");
        }

        // Si ya esta resuelta, el intento anterior termino (exito o fallo terminal). No
        // re-emitimos: si fue exito, la NC ya existe; si fue fallo terminal, el back-office
        // decide. Tratamos como duplicado.
        if (existingKey.ResolvedAt is not null)
        {
            throw new IdempotencyDuplicateException(
                $"IdempotencyKey ya resuelta (ResolvedAt={existingKey.ResolvedAt:o}) para Invoice " +
                $"{originalInvoice.Id}. No se re-emite la NC parcial.");
        }

        double ageMinutes = (DateTime.UtcNow - existingKey.CreatedAt).TotalMinutes;

        // Key reciente: otro intento esta en vuelo. No es nuestro turno.
        if (ageMinutes <= settings.IdempotencyKeyStaleThresholdMinutes)
        {
            throw new IdempotencyDuplicateException(
                $"IdempotencyKey activa, otro job procesando (age={ageMinutes:F1}min, " +
                $"umbral={settings.IdempotencyKeyStaleThresholdMinutes}min) para Invoice {originalInvoice.Id}. " +
                "Reintento en el proximo ciclo.");
        }

        // Key huerfana (vieja + sin resolver): delegamos al arbitro compartido que consulta
        // ARCA con el LastSeenNumeroBeforePost REAL de la key + matchea por comprobante
        // asociado (NO por monto a ciegas). El mismo arbitro lo usa el job de reconciliacion
        // de NC parciales colgadas (ReconcileStuckPartialCreditNoteAsync), para que la logica
        // fiscal viva en UN solo lugar.
        var arbitration = await ArbitrateOrphanPartialCreditNoteKeyAsync(
            existingKey: existingKey,
            originalInvoice: originalInvoice,
            creditNoteCbteTipo: creditNoteCbteTipo,
            expectedCreditNoteTotal: liquidation.FiscalAmountToCredit,
            roundingTolerance: settings.PartialCreditNoteRoundingTolerance,
            // El emisor no tiene la NC PENDING "en mano": el arbitro la busca por origen+tipo.
            pendingCreditNote: null,
            ct: ct);

        // Caso A (Confirmed): el POST viajo, ARCA emitio y derivamos el CAE -> NO re-emitir.
        // Caso B (NotFoundOrMismatch): la key huerfana ya fue borrada por el arbitro -> el
        // caller reintenta la emision limpia.
        return arbitration == OrphanKeyArbitration.Confirmed;
    }

    /// <summary>
    /// FC1.3.F2.6a (rehecho 2026-05-28): resultado de arbitrar una clave de idempotencia
    /// huerfana contra ARCA.
    /// </summary>
    private enum OrphanKeyArbitration
    {
        /// <summary>ARCA confirmo el comprobante: se derivo el CAE, se anulo la factura origen
        /// y se resolvio la key. No hay que re-emitir.</summary>
        Confirmed,

        /// <summary>ARCA no confirma (POST nunca viajo) o el comprobante no matchea: la key
        /// huerfana se borro. El caller decide re-emitir.</summary>
        NotFoundOrMismatch,
    }

    /// <summary>
    /// FC1.3.F2.6a (rehecho 2026-05-28): ARBITRO COMPARTIDO de una clave de idempotencia
    /// huerfana (vieja + sin resolver) de una NC parcial. Lo usan DOS callers:
    /// <list type="bullet">
    ///   <item>El emisor (<see cref="HandleStaleIdempotencyKeyAsync"/>) cuando un reintento
    ///   de Hangfire choca con una key de un intento previo.</item>
    ///   <item>El job de reconciliacion (<see cref="ReconcileStuckPartialCreditNoteAsync"/>)
    ///   cuando una NC parcial quedo colgada en PENDING.</item>
    /// </list>
    ///
    /// <para><b>Por que centralizarlo (arregla B-1 y B-2 de la revision)</b>: antes, el job
    /// reimplementaba esta logica y (B-1) consultaba ARCA con <c>lastSeenNumeroBeforePost:
    /// null</c> — lo que hace que <c>QueryLastAuthorizedWithDetailsAsync</c> devuelva SIEMPRE
    /// <c>Found:false</c> (verificado en <c>AfipService.cs:1861</c>), volviendo la reconciliacion
    /// codigo muerto; y (B-2) matcheaba por monto, que podia confirmar la NC con el CAE de OTRA
    /// NC del mismo monto. Aca consultamos con el <see cref="ArcaIdempotencyKey.LastSeenNumeroBeforePost"/>
    /// REAL (numero anterior al POST capturado por el emisor) y matcheamos por el comprobante
    /// asociado (<c>CbteAsoc == originalInvoice.NumeroComprobante</c>), que es preciso.</para>
    ///
    /// <para><b>Que hace</b>:
    /// <list type="number">
    ///   <item>Consulta ARCA con el numerador real de la key.</item>
    ///   <item>Si ARCA confirma (Found + CbteAsoc apunta a la factura origen + monto coincide):
    ///   deriva el CAE en la NC PENDING (la que viene <paramref name="pendingCreditNote"/> o,
    ///   si es null, la busca por origen+tipo), anula la factura origen, RESUELVE la key y
    ///   devuelve <see cref="OrphanKeyArbitration.Confirmed"/>.</item>
    ///   <item>Si no confirma: BORRA la key huerfana (deja al sistema en estado limpio para
    ///   re-emitir) y devuelve <see cref="OrphanKeyArbitration.NotFoundOrMismatch"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Que NO hace</b>: NO toca el BookingCancellation (el bridge lo invoca cada caller
    /// segun su contexto), NO notifica, NO re-encola. Mantiene un unico SaveChanges al final
    /// para que la NC + la factura origen + la key se persistan de forma atomica.</para>
    /// </summary>
    private async Task<OrphanKeyArbitration> ArbitrateOrphanPartialCreditNoteKeyAsync(
        ArcaIdempotencyKey existingKey,
        Invoice originalInvoice,
        int creditNoteCbteTipo,
        decimal expectedCreditNoteTotal,
        decimal roundingTolerance,
        Invoice? pendingCreditNote,
        CancellationToken ct)
    {
        // Consulta a ARCA con el numerador REAL capturado antes del POST. Pasar este valor (no
        // null) es lo que hace que QueryLastAuthorizedWithDetailsAsync pueda devolver Found:true
        // si el numerador avanzo (arregla B-1).
        var arcaResult = await _afipService.QueryLastAuthorizedWithDetailsAsync(
            puntoVenta: originalInvoice.PuntoDeVenta,
            cbteTipo: creditNoteCbteTipo,
            lastSeenNumeroBeforePost: existingKey.LastSeenNumeroBeforePost,
            ct);

        // RH3-001 (verificado en codigo): ArcaCompoundQueryResult.CbteAsoc es el NUMERO de
        // comprobante de la factura origen (parseado de <CbteAsoc><Nro> en AfipService:1356),
        // NO el Id interno de la DB. Comparamos contra originalInvoice.NumeroComprobante.
        //
        // El match es por COMPROBANTE ASOCIADO (preciso), no solo por monto (arregla B-2): el
        // monto sigue cuadrando como segunda condicion, pero la condicion fuerte es que el
        // comprobante que ARCA tiene apunte EXACTAMENTE a NUESTRA factura origen.
        bool arcaMatchesOurInvoice =
            arcaResult.Found
            && arcaResult.CbteAsoc == originalInvoice.NumeroComprobante
            && Math.Abs((arcaResult.ImporteTotal ?? 0m) - expectedCreditNoteTotal) <= roundingTolerance;

        if (arcaMatchesOurInvoice)
        {
            // El POST viajo en una corrida anterior y ARCA emitio el comprobante que matchea
            // nuestra factura origen + monto. Derivamos el CAE de la NC ya emitida.
            //
            // pendingCreditNote: el job ya la tiene en mano (su NC colgada). El emisor pasa
            // null y la buscamos por origen+tipo (CreatePendingInvoice corre antes del POST).
            // Si no existe (crash antes de CreatePendingInvoice pero el POST viajo igual,
            // escenario muy improbable), al menos resolvemos la key y dejamos rastro en el log.
            var creditNoteToConfirm = pendingCreditNote ?? await _context.Invoices
                .Where(i => i.OriginalInvoiceId == originalInvoice.Id
                            && i.TipoComprobante == creditNoteCbteTipo
                            && i.Resultado != "A")
                .OrderByDescending(i => i.Id)
                .FirstOrDefaultAsync(ct);

            existingKey.ResolvedAt = DateTime.UtcNow;

            if (creditNoteToConfirm is not null)
            {
                creditNoteToConfirm.CAE = arcaResult.Cae;
                creditNoteToConfirm.Resultado = "A";
                creditNoteToConfirm.NumeroComprobante = arcaResult.LastNumero ?? creditNoteToConfirm.NumeroComprobante;
                if (arcaResult.IssuedAt.HasValue)
                {
                    creditNoteToConfirm.IssuedAt = DateTime.SpecifyKind(arcaResult.IssuedAt.Value, DateTimeKind.Utc);
                }
            }

            // La factura origen queda anulada (la NC ya existe del lado de ARCA).
            originalInvoice.AnnulmentStatus = AnnulmentStatus.Succeeded;
            originalInvoice.AnnulledAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "Idempotency recovery (caso A): derivado CAE de comprobante ya emitido. " +
                "OriginalInvoiceId={InvoiceId} CAE={Cae}",
                originalInvoice.Id, arcaResult.Cae);
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.RecoveredFromStaleKey | originalInvoiceId={OriginalInvoiceId} cae={Cae}",
                originalInvoice.Id, arcaResult.Cae);

            return OrphanKeyArbitration.Confirmed;
        }

        // El POST nunca viajo (Found=false) o el comprobante encontrado NO matchea (otro proceso
        // ocupo el numerador, o array CbtesAsoc inesperado). Borramos la key huerfana y dejamos
        // el sistema en estado limpio para re-emitir.
        _context.ArcaIdempotencyKeys.Remove(existingKey);
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Idempotency stale key removed (huerfana de crash previo o mismatch de numerador). " +
            "Key={Key} ArcaFound={Found} ArcaCbteAsoc={CbteAsoc} ArcaImporte={Importe} EsperadoImporte={Esperado}. Reintento limpio.",
            existingKey.Key, arcaResult.Found, arcaResult.CbteAsoc, arcaResult.ImporteTotal, expectedCreditNoteTotal);

        return OrphanKeyArbitration.NotFoundOrMismatch;
    }

    /// <summary>
    /// FC1.3.F2.2 (plan §F2.2 punto e + deuda fiscal puntos 1-2): arma el
    /// <c>CreateInvoiceRequest</c> de la NC parcial, valida el cuadre que exige ARCA y la
    /// emite via el pipeline existente. Al terminar, resuelve la clave de idempotencia.
    /// </summary>
    private async Task EmitPartialCreditNoteAsync(
        string idemKey,
        Invoice originalInvoice,
        PartialCreditNoteEmissionInput liquidation,
        int creditNoteCbteTipo,
        string userId,
        int approvalRequestId,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        // Guard: la NC necesita una reserva asociada (CreatePendingInvoice resuelve la
        // Reserva por su Id para tomar el cliente/snapshot). Igual que ProcessAnnulmentJob,
        // si la factura origen no tiene reserva no podemos emitir: marcamos Failed + resolvemos
        // la key (este intento termino) en vez de dejar que CreatePendingInvoice reviente con
        // un error oscuro a mitad de camino.
        if (originalInvoice.ReservaId is null)
        {
            await ResolveIdempotencyKeyAsync(idemKey, ct);
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice, userId,
                $"La factura {originalInvoice.Id} no tiene reserva asociada. No se puede emitir la NC parcial.",
                ct);
            return;
        }

        // Prorrateo del IVA con el helper puro (E2). Devuelve el desglose por alicuota con
        // los importes YA redondeados a 2 decimales por grupo. Si la liquidacion no cierra
        // contra FiscalAmountToCredit dentro de la tolerancia, lanza InvalidOperationException
        // (defense-in-depth contra una liquidacion incoherente).
        PartialCreditNoteIvaResult iva;
        try
        {
            iva = PartialCreditNoteIvaCalculator.Calculate(
                input: liquidation,
                mode: settings.IvaProrrateoMode,
                roundingTolerance: settings.PartialCreditNoteRoundingTolerance);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // No mandamos un comprobante inconsistente al ARCA. Resolvemos la key (este
            // intento termino, fallo terminal) + marcamos Failed + notificamos.
            await ResolveIdempotencyKeyAsync(idemKey, ct);
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice, userId,
                $"El prorrateo de IVA no cierra: {ex.Message}", ct);
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.RoundingValidationFailed | originalInvoiceId={OriginalInvoiceId} stage=calculator",
                originalInvoice.Id);
            // FC1.3.F2.6 (counter defensivo): este punto NO deberia incrementar nunca
            // (EnqueuePartialCreditNoteAsync ya valido los montos pre-encolado). Si
            // incrementa, hay un bug entre el encolado y el job (ej. liquidacion mutada).
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.SumValidationFailedAtJob | originalInvoiceId={OriginalInvoiceId} stage=calculator",
                originalInvoice.Id);
            return;
        }

        // --- Cuadre que exige ARCA (deuda fiscal punto 2, CRITICO) ---
        //
        // Reproducimos exactamente como AfipService arma el envelope para anticipar el
        // descuadre ANTES de POSTear:
        //   - Cada <AlicIva><Importe> se formatea a 2 decimales (ToString("0.00")), o sea
        //     que ARCA recibe round(grupo, 2). El calculator E2 ya devuelve esos importes
        //     redondeados por grupo, asi que pasamos esos mismos numeros.
        //   - <ImpIVA> = suma de los importes por grupo.
        //   - Fase 2 NO lleva tributos provinciales (G-F2-C reroutea a manual si la factura
        //     origen tiene Tributes). Por eso ImpTrib = 0.
        //
        // ARCA valida: ImpTotal == ImpNeto + ImpIVA + ImpTrib  y  ImpIVA == Σ AlicIva.Importe.
        // Si no cuadra (por acumulacion de centavos entre alicuotas), NO emitimos.
        decimal impNeto = iva.CreditedNetAmount;
        decimal impIva = iva.VatGroups.Sum(g => g.ImporteIva); // suma de importes YA redondeados por grupo
        const decimal impTrib = 0m; // Fase 2 no prorratea tributos provinciales (G-F2-C).
        decimal impTotal = Math.Round(impNeto + impIva + impTrib, 2);

        decimal squareGap = Math.Abs(impTotal - (impNeto + impIva + impTrib));
        if (squareGap > settings.PartialCreditNoteRoundingTolerance)
        {
            await ResolveIdempotencyKeyAsync(idemKey, ct);
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice, userId,
                $"El comprobante no cuadra para ARCA: ImpTotal ({impTotal}) != ImpNeto ({impNeto}) " +
                $"+ ImpIVA ({impIva}) + ImpTrib ({impTrib}). Gap {squareGap} > tolerancia " +
                $"{settings.PartialCreditNoteRoundingTolerance}. No se emite la NC parcial.", ct);
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.RoundingValidationFailed | originalInvoiceId={OriginalInvoiceId} stage=arcaSquare",
                originalInvoice.Id);
            // FC1.3.F2.6 (counter defensivo): mismo significado que el stage=calculator.
            // No deberia incrementar; si lo hace, el cuadre ARCA fallo en el job.
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.SumValidationFailedAtJob | originalInvoiceId={OriginalInvoiceId} stage=arcaSquare",
                originalInvoice.Id);
            return;
        }

        // Armado del request. Pasamos las lineas del input TAL CUAL (NO reinterpretamos que
        // se acredita: eso es criterio de F2.3 + signoff del contador).
        //
        // FC1.3.F2.2 (fix fiscal B1): ademas pasamos TotalsOverride con el desglose EXACTO que
        // acabamos de validar (mismos numeros que el cuadre ARCA de arriba). Asi el pipeline
        // compartido NO recalcula el IVA item por item (que con varias lineas de la misma
        // alicuota podia descuadrar en 1-2 centavos y hacer rebotar el comprobante en ARCA):
        // usa estos importes ya redondeados por grupo tal cual. El invariante que viaja es el
        // que validamos: ImpIVA == Σ AlicIva.Importe, ImpTotal == ImpNeto + ImpIVA + ImpTrib.
        var totalsOverride = new InvoiceTotalsOverride(
            // Un AlicIva por cada grupo de alicuota del calculator, con el Importe YA
            // redondeado por grupo (asi lo devuelve PartialCreditNoteIvaCalculator).
            AlicIvas: iva.VatGroups
                .Select(group => new AlicIvaOverride(
                    Id: group.AlicuotaIvaId,
                    BaseImp: group.BaseImponible,
                    Importe: group.ImporteIva))
                .ToList(),
            ImpNeto: impNeto,
            ImpIVA: impIva,
            ImpTrib: impTrib,
            ImpTotal: impTotal);

        // --- Invariantes ARCA EXPLICITOS sobre el override que se va a mandar (MEJORA 1) ---
        //
        // El cuadre de arriba ya valido ImpTotal == ImpNeto + ImpIVA + ImpTrib. Los otros DOS
        // invariantes que exige ARCA quedaban implicitos "por construccion" (impNeto/impIva se
        // arman desde los mismos VatGroups que poblan AlicIvas). Los hacemos EXPLICITOS aca,
        // sobre el TotalsOverride ya armado, ANTES de POSTear:
        //   - Σ AlicIva.Importe == ImpIVA
        //   - Σ AlicIva.BaseImp == ImpNeto
        // POR QUE blindarlo igual: si manana el calculator o el reparto cambian (regresion) y
        // rompen este vinculo, hoy nadie se entera hasta que ARCA REBOTA el comprobante en
        // produccion. Validarlo aca convierte ese bug silencioso en un Failed local con log
        // claro y SIN POST (mismo patron terminal que el cuadre existente: resolver key +
        // marcar Failed + return, sin reintentar a ciegas).
        decimal sumAlicIvaImporte = totalsOverride.AlicIvas.Sum(group => group.Importe);
        decimal sumAlicIvaBaseImp = totalsOverride.AlicIvas.Sum(group => group.BaseImp);

        // Exacto a 2 decimales (gap 0). NO usamos la tolerancia de redondeo del cuadre total:
        // estos numeros provienen del MISMO origen (los VatGroups), asi que cualquier diferencia
        // es un bug de programacion aguas arriba, no acumulacion legitima de centavos.
        decimal ivaInvariantGap = Math.Abs(Math.Round(sumAlicIvaImporte, 2) - Math.Round(impIva, 2));
        decimal netoInvariantGap = Math.Abs(Math.Round(sumAlicIvaBaseImp, 2) - Math.Round(impNeto, 2));
        if (ivaInvariantGap > 0m || netoInvariantGap > 0m)
        {
            await ResolveIdempotencyKeyAsync(idemKey, ct);
            await MarkPartialCreditNoteFailedAsync(
                originalInvoice, userId,
                $"El override de la NC parcial viola un invariante ARCA: " +
                $"Σ AlicIva.Importe ({sumAlicIvaImporte}) vs ImpIVA ({impIva}) y " +
                $"Σ AlicIva.BaseImp ({sumAlicIvaBaseImp}) vs ImpNeto ({impNeto}). " +
                "No se emite la NC parcial (posible regresion del calculator o del reparto de IVA).",
                ct);
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.RoundingValidationFailed | originalInvoiceId={OriginalInvoiceId} stage=arcaAlicIvaInvariant",
                originalInvoice.Id);
            return;
        }

        // --- FC1.3.F2.5 (multimoneda, 2026-05-28): moneda + cotizacion de la NC parcial ---
        //
        // De donde sale el tipo de cambio (TRAMPA 3 del brief): el TC NO esta en el VO
        // FiscalLiquidation (solo tiene Currency). Vive en el FiscalSnapshot del
        // BookingCancellation (FiscalSnapshot.ExchangeRateAtOriginalInvoice = TC congelado
        // de la factura original a T0, por regla AFIP de coherencia fiscal INV-118).
        // Ese TC ya viajo hasta aca DENTRO del input: BookingCancellationService lo leyo del
        // snapshot y lo metio en PartialCreditNoteEmissionInput.ExchangeRateAtOriginalInvoice
        // al armar la liquidacion. Por eso NO necesitamos un query extra al snapshot: usamos
        // liquidation.ExchangeRateAtOriginalInvoice tal cual.
        //
        // Regla: la NC se emite en la MISMA moneda y cotizacion que la factura origen (no con
        // el TC del dia de la cancelacion). Para pesos: ("PES", 1). Para una moneda extranjera
        // soportada: (codigo ARCA, TC del snapshot).
        string monId;
        decimal monCotiz;
        if (string.Equals(liquidation.Currency, "ARS", StringComparison.OrdinalIgnoreCase))
        {
            monId = "PES";
            monCotiz = 1m;
        }
        else
        {
            // Si la moneda no esta en el mapeo soportado, TryMapToArcaCurrencyCode devuelve
            // null. Tratamos eso como fallo terminal (igual que el cuadre/prorrateo): resolver
            // la key + marcar Failed + return, SIN POSTear a ARCA. NO dejamos que reviente a
            // mitad de camino con la key ya insertada.
            string? mappedCode = TryMapToArcaCurrencyCode(liquidation.Currency);
            if (mappedCode is null)
            {
                await ResolveIdempotencyKeyAsync(idemKey, ct);
                await MarkPartialCreditNoteFailedAsync(
                    originalInvoice, userId,
                    $"Moneda '{liquidation.Currency}' no soportada para NC parcial multimoneda (F2.5). " +
                    "Solo se mapea ARS y USD por ahora. No se emite el comprobante.", ct);
                _logger.LogInformation(
                    "metric:Fc13.PartialCreditNote.UnsupportedCurrency | originalInvoiceId={OriginalInvoiceId} currency={Currency}",
                    originalInvoice.Id, liquidation.Currency);
                return;
            }

            monId = mappedCode;
            monCotiz = liquidation.ExchangeRateAtOriginalInvoice;

            // FC1.3.F2.5 (fix M-1, revision 2026-05-28): GUARD DE COTIZACION COHERENTE.
            //
            // Problema que cierra: el TC viaja desde el FiscalSnapshot. Si por un dato cargado por
            // SQL crudo, un backfill, o un path que no poblo el snapshot, el TC llega en 0 (o, peor,
            // exactamente 1 para una moneda extranjera), emitiriamos una NC en DOL valuada como si
            // un dolar fuera un peso. El CHECK chk_BookingCancellations_fiscalsnapshot_consistent
            // protege la transicion del BC, pero el calculo de moneda corre ANTES y podria filtrarse.
            //
            // Regla: para una moneda extranjera (mappedCode != "PES"), un TC <= 0 o == 1 es
            // INCOHERENTE — un dolar no vale 0 ni 1 peso. Lo tratamos como FALLO TERMINAL controlado
            // (igual que moneda no soportada): resolver la idempotency key + marcar Failed + log
            // critico + return. NUNCA dejamos que un DOL con cotizacion 1 llegue a ARCA.
            bool exchangeRateIncoherent = monCotiz <= 0m || monCotiz == 1m;
            if (exchangeRateIncoherent)
            {
                await ResolveIdempotencyKeyAsync(idemKey, ct);
                await MarkPartialCreditNoteFailedAsync(
                    originalInvoice, userId,
                    $"NC parcial en moneda {liquidation.Currency} ({monId}) con cotizacion {monCotiz} incoherente " +
                    "(<= 0 o = 1). No se puede valuar un dolar como un peso. No se emite el comprobante. " +
                    "Verificar el tipo de cambio del snapshot fiscal de la factura origen.", ct);
                _logger.LogCritical(
                    "metric:Fc13.PartialCreditNote.IncoherentExchangeRate | originalInvoiceId={OriginalInvoiceId} " +
                    "currency={Currency} monId={MonId} monCotiz={MonCotiz}",
                    originalInvoice.Id, liquidation.Currency, monId, monCotiz);
                return;
            }
        }

        var request = new CreateInvoiceRequest
        {
            // ReservaId no es null aca (el guard del inicio del metodo ya lo garantizo).
            ReservaId = originalInvoice.ReservaId!.Value.ToString(),
            CbteTipo = creditNoteCbteTipo,
            Concepto = 3, // Productos y Servicios (igual que ProcessAnnulmentJob).
            // OriginalInvoiceId apunta a la factura origen por su PublicId (igual que la NC
            // total). CreatePendingInvoice lo resuelve y vincula la NC -> <CbtesAsoc>.
            OriginalInvoiceId = originalInvoice.PublicId.ToString(),
            IsCreditNote = true,
            IsDebitNote = false,
            Items = liquidation.Lines.Select(line => new InvoiceItemDto
            {
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Total = line.Total,
                AlicuotaIvaId = line.AlicuotaIvaId,
            }).ToList(),
            // Tributes vacio: Fase 2 no prorratea tributos provinciales (G-F2-C). Si la
            // factura origen tuviera tributos, NUNCA llegamos aca (rerouted a manual en F2.0).
            Tributes = new List<InvoiceTributeDto>(),
            // Cuadre exacto ya validado: el pipeline lo usa en vez de recalcular (fix B1).
            TotalsOverride = totalsOverride,
            // F2.5: moneda + cotizacion calculadas arriba. Viajan hasta el XML SOAP.
            MonId = monId,
            MonCotiz = monCotiz,
        };

        // FC1.3.F2.6 (counter): contamos la EMISION (el momento en que mandamos la NC
        // parcial al ARCA). Tags: moneda original del negocio (ISO) + codigo ARCA mapeado
        // + tipo de comprobante de la NC. Permite ver el volumen de NC parciales emitidas
        // y desglosar por moneda. Va ANTES del POST porque "Emitted" mide el intento de
        // emision; el resultado (ArcaApproved/ArcaRejected) se cuenta despues segun lo que
        // devuelva ARCA.
        _logger.LogInformation(
            "metric:Fc13.PartialCreditNote.Emitted | originalInvoiceId={OriginalInvoiceId} currency={Currency} arcaCurrency={ArcaCurrency} creditNoteCbteTipo={CbteTipo}",
            originalInvoice.Id, liquidation.Currency, monId, creditNoteCbteTipo);

        // Emitir via el pipeline existente. CreatePendingInvoice crea la NC en estado PENDING
        // + ProcessInvoiceJob la POSTea a ARCA y persiste el resultado (A / R / PENDING).
        // El INSERT exitoso de la idemKey (capa 1) ya protege contra el doble-POST.
        var newCreditNote = await _afipService.CreatePendingInvoice(originalInvoice.ReservaId!.Value, request);

        // FC1.3 Fase 2 (Fase2_M2, 2026-05-28): grabamos la huella REAL de idempotencia en la
        // NC, ANTES de POSTear a ARCA. Por que aca y no despues: si el sistema se cae entre el
        // POST y el persistido del resultado (el escenario "huerfano" que arregla el
        // barrendero), queremos que la NC ya tenga su key grabada para que la reconciliacion
        // haga lookup DIRECTO (sin re-derivar el hash). Si no llegamos ni a crear la NC, no hay
        // fila que correlacionar, asi que no perdemos nada. Es el MISMO string que insertamos en
        // ArcaIdempotencyKeys.Key (capa 1), no un valor recalculado: la correlacion queda exacta.
        newCreditNote.IdempotencyKey = idemKey;
        await _context.SaveChangesAsync(ct);

        await _afipService.ProcessInvoiceJob(newCreditNote.Id);

        // Releer el resultado que dejo ProcessInvoiceJob.
        await _context.Entry(newCreditNote).ReloadAsync(ct);

        if (newCreditNote.Resultado == "A")
        {
            // FC1.3.F2.6 (counter): ARCA aprobo (CAE recibido). Es el "happy path" fiscal.
            _logger.LogInformation(
                "metric:Fc13.PartialCreditNote.ArcaApproved | originalInvoiceId={OriginalInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId} currency={Currency}",
                originalInvoice.Id, newCreditNote.Id, liquidation.Currency);

            // ARCA aprobo: la factura origen queda anulada (levanta el bloqueo fiscal de
            // cancel de reserva, igual que la NC total).
            originalInvoice.AnnulmentStatus = AnnulmentStatus.Succeeded;
            originalInvoice.AnnulledAt = newCreditNote.IssuedAt ?? DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            await ResolveIdempotencyKeyAsync(idemKey, ct);

            // Consumir el approval (la accion solicitada se ejecuto). Idempotente.
            if (_approvalService is not null)
            {
                await _approvalService.MarkConsumedAsync(approvalRequestId, ct);
            }

            await CreateNotification(
                userId,
                $"NC parcial emitida. Comprobante {newCreditNote.NumeroComprobante} (CAE {newCreditNote.CAE}).",
                "Success",
                newCreditNote.Id);

            // Sincronizar el BC asociado (si existe), mismo patron try/catch que la NC total:
            // la NC ya quedo commiteada, NO podemos rethrow (Hangfire reintentaria y
            // re-POSTearia). Si el bridge falla, remediacion manual via ForceArcaConfirmation.
            if (_bcBridge is not null)
            {
                try
                {
                    await _bcBridge.OnArcaSucceededAsync(originalInvoice.Id, newCreditNote.Id, CancellationToken.None);
                }
                catch (Exception bridgeEx)
                {
                    _logger.LogError(
                        bridgeEx,
                        "Bridge BC.OnArcaSucceededAsync fallo para NC parcial. OriginalInvoiceId={InvoiceId} CN={CreditNoteId}. " +
                        "La NC quedo Succeeded en ARCA. Remediacion: ForceArcaConfirmationAsync manual.",
                        originalInvoice.Id, newCreditNote.Id);
                    _logger.LogError(
                        "metric:bc_bridge_failed | originatingInvoiceId={OriginatingInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId} errorType={ErrorType} stage=OnArcaSucceeded_PartialCN",
                        originalInvoice.Id, newCreditNote.Id, bridgeEx.GetType().Name);
                }
            }
        }
        else
        {
            // ARCA rechazo (Resultado "R") o quedo PENDING (error tecnico). En ambos casos
            // NO marcamos la key Resolved si quedo PENDING: un PENDING puede reintentarse
            // (el numerador no avanzo). Pero el contrato de F2.2 nos pide marcar Failed la
            // factura origen para mantener el bloqueo. Distinguimos:
            //   - "R": rechazo definitivo -> resolvemos la key (intento terminal).
            //   - PENDING/otro: error tecnico -> NO resolvemos la key (permite recovery).
            if (newCreditNote.Resultado == "R")
            {
                // FC1.3.F2.6 (counter): ARCA rechazo definitivo. Tag RejectReason con el
                // texto de Observaciones (truncado) para poder agrupar por motivo de rechazo
                // en el alerting sin tener que abrir cada NC. NO logueamos datos sensibles:
                // Observaciones de ARCA son mensajes tecnicos del comprobante, no datos de
                // pasajero/pago.
                _logger.LogInformation(
                    "metric:Fc13.PartialCreditNote.ArcaRejected | originalInvoiceId={OriginalInvoiceId} rejectReason={RejectReason}",
                    originalInvoice.Id, TruncateRejectReason(newCreditNote.Observaciones));

                await ResolveIdempotencyKeyAsync(idemKey, ct);
            }

            await MarkPartialCreditNoteFailedAsync(
                originalInvoice, userId,
                $"ARCA no aprobo la NC parcial (Resultado={newCreditNote.Resultado}): {newCreditNote.Observaciones}",
                ct);

            if (_bcBridge is not null)
            {
                try
                {
                    await _bcBridge.OnArcaFailedAsync(originalInvoice.Id, newCreditNote.Observaciones, CancellationToken.None);
                }
                catch (Exception bridgeEx)
                {
                    _logger.LogError(
                        bridgeEx,
                        "Bridge BC.OnArcaFailedAsync fallo para NC parcial. OriginalInvoiceId={InvoiceId}.",
                        originalInvoice.Id);
                    _logger.LogError(
                        "metric:bc_bridge_failed | originatingInvoiceId={OriginatingInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId} errorType={ErrorType} stage=OnArcaFailed_PartialCN",
                        originalInvoice.Id, (int?)null, bridgeEx.GetType().Name);
                }
            }
        }
    }

    /// <summary>
    /// FC1.3.F2.2: marca la clave de idempotencia como resuelta (intento terminado, exito o
    /// fallo terminal). Idempotente: si la key no existe (ya borrada), es no-op.
    /// </summary>
    private async Task ResolveIdempotencyKeyAsync(string idemKey, CancellationToken ct)
    {
        var key = await _context.ArcaIdempotencyKeys.FirstOrDefaultAsync(k => k.Key == idemKey, ct);
        if (key is null)
        {
            return;
        }

        key.ResolvedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// FC1.3.F2.2: marca la factura origen como anulacion fallida (mantiene el bloqueo de
    /// cancel de reserva hasta que el back-office resuelva) + notifica al usuario. NO toca
    /// el approval (no se consume si la accion no se ejecuto).
    /// </summary>
    private async Task MarkPartialCreditNoteFailedAsync(
        Invoice originalInvoice,
        string userId,
        string reason,
        CancellationToken ct)
    {
        originalInvoice.AnnulmentStatus = AnnulmentStatus.Failed;
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "ProcessPartialCreditNoteJob marco Failed la Invoice {InvoiceId}: {Reason}",
            originalInvoice.Id, reason);

        await CreateNotification(userId, reason, "Error", originalInvoice.Id);
    }

    /// <summary>
    /// FC1.3.F2.6a (rehecho 2026-05-28): reconcilia UNA NC parcial colgada en
    /// <c>Resultado='PENDING'</c>, reutilizando el MISMO arbitro de idempotencia que el emisor
    /// (<see cref="ArbitrateOrphanPartialCreditNoteKeyAsync"/>). Ver el contrato completo en
    /// <see cref="IInvoiceService.ReconcileStuckPartialCreditNoteAsync"/>.
    ///
    /// <para><b>Como ubica la idempotency key de la NC</b> (clave del rehacer): hay DOS caminos
    /// segun si la NC tiene su huella real grabada (Fase2_M2, 2026-05-28):
    /// <list type="number">
    ///   <item><b>CAMINO PREFERIDO (NC nuevas)</b>: la NC trae la key REAL en
    ///   <c>creditNote.IdempotencyKey</c> (la grabo el emisor con el MISMO string que inserto en
    ///   <c>ArcaIdempotencyKeys.Key</c>). Lookup directo y exacto, sin recalcular nada.</item>
    ///   <item><b>FALLBACK (NC legacy, columna null)</b>: RE-DERIVAMOS la idemKey deterministica
    ///   desde datos persistidos: <c>originalInvoice.Id</c> + <c>AnnulmentApprovalRequestId</c> +
    ///   <c>creditNote.ImporteTotal</c> (asumido == <c>FiscalAmountToCredit</c>, ver F2.2) + ISO
    ///   derivado de <c>creditNote.MonId</c>. Si falta el approval o el MonId no mapea, devolvemos
    ///   <c>NeedsManualReview</c> en vez de adivinar (nunca confirmamos a ciegas).</item>
    /// </list>
    /// La columna <c>Invoice.IdempotencyKey</c> (migracion aditiva Fase2_M2) cierra la deuda que
    /// hacia fragil la correlacion por re-derivacion: el fallback queda SOLO para las NC emitidas
    /// antes de la columna, que NO se pueden re-grabar con certeza (no hay backfill).</para>
    /// </summary>
    public async Task<PartialCreditNotePostingReconcileResult> ReconcileStuckPartialCreditNoteAsync(
        int creditNoteInvoiceId,
        CancellationToken ct)
    {
        var creditNote = await _context.Invoices
            .Include(nc => nc.OriginalInvoice)
            .FirstOrDefaultAsync(nc => nc.Id == creditNoteInvoiceId, ct);

        if (creditNote is null)
        {
            // El job nos paso un id que ya no existe (borrado entre query y aca). No es un error
            // fiscal: lo tratamos como "nada que hacer".
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                $"NC {creditNoteInvoiceId} no encontrada.");
        }

        var originalInvoice = creditNote.OriginalInvoice;
        if (originalInvoice is null)
        {
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                $"NC {creditNote.Id} sin factura origen cargada.");
        }

        // Si ya dejo de estar PENDING entre la query del job y aca (otro ciclo / el emisor la
        // resolvio), no hacemos nada: ya esta reconciliada.
        if (creditNote.Resultado == "A")
        {
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.Confirmed,
                "La NC ya estaba aprobada al momento de reconciliar.");
        }

        // El tipo de comprobante de la NC lo necesitan AMBOS caminos (el arbitro consulta ARCA
        // por puntoVenta+cbteTipo y el re-disparo re-POSTea con el). Lo resolvemos primero, antes
        // de bifurcar por como obtenemos la idempotency key.
        int? creditNoteCbteTipo = InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice(originalInvoice.TipoComprobante);
        if (creditNoteCbteTipo is null)
        {
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                $"El tipo de comprobante {originalInvoice.TipoComprobante} de la factura origen no " +
                "soporta NC parcial automatica.");
        }

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // --- Obtener la idempotency key de la NC: DOS caminos (Fase2_M2, 2026-05-28) ---
        //
        // CAMINO 1 (preferido, NC nuevas): la NC trae su huella REAL grabada en la columna
        // Invoice.IdempotencyKey (la persistio el emisor en el mismo string que inserto en
        // ArcaIdempotencyKeys.Key). Lookup DIRECTO y EXACTO, sin recalcular nada. Esto NO
        // depende de que ImporteTotal == FiscalAmountToCredit ni del mapeo de moneda: usa el
        // valor que realmente se uso al emitir.
        //
        // CAMINO 2 (fallback, NC legacy emitidas antes de esta columna): IdempotencyKey == null.
        // Caemos a la RE-DERIVACION historica (recalcular el hash desde factura origen + approval
        // + monto + moneda). Ese camino SI necesita el approval y un MonId mapeable, asi que sus
        // guards viven dentro de esta rama. NO lo borramos: es la compatibilidad con lo viejo.
        string idemKey;
        if (creditNote.IdempotencyKey is not null)
        {
            idemKey = creditNote.IdempotencyKey;
        }
        else
        {
            // Fallback: re-derivar. Necesitamos el approval que autorizo la NC (lo guardo el
            // emisor en la FACTURA ORIGEN al encolar). Sin el no podemos correlacionar de forma
            // confiable -> escalamos a manual (NUNCA confirmamos a ciegas).
            if (originalInvoice.AnnulmentApprovalRequestId is null)
            {
                return new PartialCreditNotePostingReconcileResult(
                    PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                    $"Factura origen {originalInvoice.Id} sin AnnulmentApprovalRequestId y NC sin " +
                    "IdempotencyKey grabada: no se puede correlacionar la NC con su intento de emision.");
            }

            // El ISO de la moneda sale del MonId de la NC. Si por un dato raro no es un codigo
            // conocido, escalamos a manual antes que arriesgar una correlacion incorrecta.
            string? isoCurrency = ReverseMapArcaCurrencyToIso(creditNote.MonId);
            if (isoCurrency is null)
            {
                return new PartialCreditNotePostingReconcileResult(
                    PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                    $"NC {creditNote.Id} sin IdempotencyKey grabada y con MonId '{creditNote.MonId}' " +
                    "no mapeable a ISO: no se puede re-derivar la idempotency key.");
            }

            idemKey = BuildIdempotencyKey(
                originalInvoiceId: originalInvoice.Id,
                approvalRequestId: originalInvoice.AnnulmentApprovalRequestId.Value,
                fiscalAmountToCredit: creditNote.ImporteTotal,
                currency: isoCurrency);
        }

        var existingKey = await _context.ArcaIdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == idemKey, ct);

        // Caso "NC sin key": el intento de emision nunca llego a reservar numero / insertar la
        // key (crash antes de la capa 1). NO confirmamos nada: re-disparamos la emision
        // idempotente sobre la NC ya creada (re-arma una key fresca + re-POSTea via el pipeline).
        if (existingKey is null)
        {
            await ReEnqueueStuckPartialCreditNoteEmissionAsync(
                creditNote, originalInvoice, creditNoteCbteTipo.Value, idemKey, ct);
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission,
                "NC sin idempotency key: re-disparada la emision idempotente.");
        }

        // La key ya esta resuelta: el intento anterior termino. Si fue exito la NC ya deberia
        // estar 'A' (chequeado arriba); si llego aca es un fallo terminal previo -> manual.
        if (existingKey.ResolvedAt is not null)
        {
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview,
                $"Idempotency key de la NC {creditNote.Id} ya resuelta (ResolvedAt=" +
                $"{existingKey.ResolvedAt:o}) pero la NC sigue PENDING: requiere revision manual.");
        }

        // Key reciente (no vencida): el emisor original esta posteando AHORA (o un reintento de
        // Hangfire). NO la tocamos para no pisarlo (arregla M-1). El job espera el proximo ciclo.
        double ageMinutes = (DateTime.UtcNow - existingKey.CreatedAt).TotalMinutes;
        if (ageMinutes <= settings.IdempotencyKeyStaleThresholdMinutes)
        {
            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.InFlight,
                $"Idempotency key activa (age={ageMinutes:F1}min <= umbral " +
                $"{settings.IdempotencyKeyStaleThresholdMinutes}min): emisor en vuelo.");
        }

        // Key huerfana (vieja + sin resolver): MISMO arbitro que el emisor. Le pasamos NUESTRA
        // NC en mano (la que el job detecto colgada) para que el match y la confirmacion operen
        // sobre la NC correcta, no sobre la que el arbitro buscaria por origen+tipo.
        var arbitration = await ArbitrateOrphanPartialCreditNoteKeyAsync(
            existingKey: existingKey,
            originalInvoice: originalInvoice,
            creditNoteCbteTipo: creditNoteCbteTipo.Value,
            expectedCreditNoteTotal: creditNote.ImporteTotal,
            roundingTolerance: settings.PartialCreditNoteRoundingTolerance,
            pendingCreditNote: creditNote,
            ct: ct);

        if (arbitration == OrphanKeyArbitration.Confirmed)
        {
            // El arbitro ya marco la NC 'A' + anulo la factura origen + resolvio la key. Ahora
            // sincronizamos el BookingCancellation. M-2 (fix): si el bridge falla NO tragamos la
            // excepcion en silencio — la propagamos para que el job la registre y deje la NC en
            // un estado que un proximo ciclo pueda re-detectar.
            await SyncBcAfterReconciledPartialCreditNoteAsync(originalInvoice.Id, creditNote.Id, ct);

            return new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.Confirmed,
                $"ARCA confirmo la NC (CAE derivado). Factura origen {originalInvoice.Id} anulada.");
        }

        // El arbitro borro la key huerfana (POST nunca viajo / mismatch). Re-disparamos la
        // emision idempotente sobre la NC ya creada, NO confirmamos a ciegas.
        await ReEnqueueStuckPartialCreditNoteEmissionAsync(
            creditNote, originalInvoice, creditNoteCbteTipo.Value, idemKey, ct);
        return new PartialCreditNotePostingReconcileResult(
            PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission,
            "ARCA no confirma la NC: key huerfana borrada y emision idempotente re-disparada.");
    }

    /// <summary>
    /// FC1.3.F2.6a: sincroniza el BookingCancellation despues de que una NC parcial colgada se
    /// reconcilio como aprobada. A diferencia del emisor (que traga el fallo del bridge porque la
    /// NC fiscal ya esta commiteada y Hangfire reintentaria re-POSTeando), aca el rethrow es
    /// SEGURO y DESEABLE (M-2 fix): este metodo lo llama el job de reconciliacion, que NO re-POSTea
    /// a ARCA en su catch — solo loguea critico. Propagar la excepcion deja la NC en un estado
    /// (BC todavia sin avanzar) que el PROXIMO ciclo del job re-detecta y re-intenta, en vez de
    /// quedar un BookingCancellation huerfano silencioso.
    /// </summary>
    private async Task SyncBcAfterReconciledPartialCreditNoteAsync(
        int originalInvoiceId,
        int creditNoteInvoiceId,
        CancellationToken ct)
    {
        if (_bcBridge is null)
        {
            // Configuracion sin modulo de cancelacion: no hay BC que sincronizar. La NC quedo
            // reconciliada igual; lo dejamos asentado en el log.
            _logger.LogWarning(
                "ReconcileStuckPartialCreditNote: NC {CreditNoteId} reconciliada pero no hay bridge " +
                "inyectado para sincronizar el BookingCancellation.",
                creditNoteInvoiceId);
            return;
        }

        // SIN try/catch: dejamos que la excepcion suba al caller (el job). El job la captura,
        // loguea critico con el metric bc_bridge_failed y NO confirma como exitoso -> la NC sigue
        // detectable. Es lo opuesto al emisor (que traga el error porque rethrow alli provocaria
        // re-POST). Ver doc del metodo (M-2).
        await _bcBridge.OnArcaSucceededAsync(originalInvoiceId, creditNoteInvoiceId, ct);
    }

    /// <summary>
    /// FC1.3.F2.6a: re-dispara la emision IDEMPOTENTE de una NC parcial que quedo colgada en
    /// PENDING y que ARCA confirma que NUNCA se emitio (o que nunca llego a reservar numero).
    ///
    /// <para><b>Por que re-arma una key y re-POSTea la NC ya creada, en vez de re-encolar
    /// ProcessPartialCreditNoteJob</b>: el job de emision necesita la liquidacion original
    /// (lineas + montos + moneda) que NO esta persistida en ninguna entidad. Pero la NC ya
    /// existe como Invoice PENDING con sus items y totales cuadrados. Re-emitir = re-insertar
    /// una idempotency key FRESCA (con un snapshot nuevo del numerador ARCA) + volver a llamar a
    /// <c>ProcessInvoiceJob</c> sobre esa misma NC. La key fresca garantiza el guard anti-doble-POST
    /// del proximo intento; el <c>ProcessInvoiceJob</c> POSTea via el pipeline existente.</para>
    ///
    /// <para><b>Idempotencia preservada</b>: si dos ciclos del job intentan re-disparar la misma
    /// NC a la vez, el INSERT de la key fresca choca con el UNIQUE y solo uno gana — el otro se
    /// va por <c>InFlight</c> en su proxima evaluacion. NO re-POSTeamos sin una key insertada.</para>
    /// </summary>
    private async Task ReEnqueueStuckPartialCreditNoteEmissionAsync(
        Invoice creditNote,
        Invoice originalInvoice,
        int creditNoteCbteTipo,
        string idemKey,
        CancellationToken ct)
    {
        // Snapshot FRESCO del numerador ARCA antes de re-POSTear (mismo RH4-001 que el emisor):
        // el numerador pudo avanzar por otros emisores desde el intento original.
        int lastSeenNumeroBeforePost = await _afipService.GetLastAuthorizedNumeroAsync(
            puntoVenta: originalInvoice.PuntoDeVenta,
            cbteTipo: creditNoteCbteTipo,
            ct);

        // Re-armar la idempotency key. Si choca con el UNIQUE (otro ciclo la inserto primero),
        // NO re-POSTeamos: dejamos que ese otro intento gane (evita doble-POST).
        bool insertSucceeded = await TryInsertIdempotencyKeyAsync(idemKey, lastSeenNumeroBeforePost, ct);
        if (!insertSucceeded)
        {
            _logger.LogWarning(
                "ReconcileStuckPartialCreditNote: la idempotency key de la NC {CreditNoteId} ya fue " +
                "re-insertada por otro ciclo. No se re-POSTea (evita doble-POST). Se retoma en el proximo ciclo.",
                creditNote.Id);
            return;
        }

        _logger.LogWarning(
            "ReconcileStuckPartialCreditNote: re-disparando emision idempotente de la NC {CreditNoteId} " +
            "(OriginalInvoiceId={OriginalInvoiceId}). Snapshot numerador={LastSeen}.",
            creditNote.Id, originalInvoice.Id, lastSeenNumeroBeforePost);
        _logger.LogInformation(
            "metric:Fc13.PartialCreditNote.ReEnqueuedFromReconciliation | originalInvoiceId={OriginalInvoiceId} creditNoteInvoiceId={CreditNoteInvoiceId}",
            originalInvoice.Id, creditNote.Id);

        // Re-POSTear la NC YA CREADA via el pipeline existente. ProcessInvoiceJob corta solo si la
        // NC ya esta 'A'; si sigue PENDING, la re-envia a ARCA. La key fresca protege contra el
        // doble-POST del proximo reintento de Hangfire.
        await _afipService.ProcessInvoiceJob(creditNote.Id);

        // Releer el resultado y resolver la key segun el desenlace (mismo criterio que el emisor:
        // 'A' o 'R' => key resuelta; PENDING => la dejamos sin resolver para permitir otro recovery).
        await _context.Entry(creditNote).ReloadAsync(ct);
        if (creditNote.Resultado == "A")
        {
            originalInvoice.AnnulmentStatus = AnnulmentStatus.Succeeded;
            originalInvoice.AnnulledAt = creditNote.IssuedAt ?? DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            await ResolveIdempotencyKeyAsync(idemKey, ct);
            // Sincronizar el BC con el mismo criterio M-2 (rethrow al caller, no tragar).
            await SyncBcAfterReconciledPartialCreditNoteAsync(originalInvoice.Id, creditNote.Id, ct);
        }
        else if (creditNote.Resultado == "R")
        {
            await ResolveIdempotencyKeyAsync(idemKey, ct);
        }
        // PENDING / otro: NO resolvemos la key -> el proximo ciclo puede volver a arbitrar.
    }

    /// <summary>
    /// FC1.3.F2.6a: inverso de <see cref="TryMapToArcaCurrencyCode"/>. Traduce un codigo de
    /// moneda de ARCA ("PES", "DOL") de vuelta al ISO 4217 del negocio ("ARS", "USD"). Devuelve
    /// <c>null</c> si el codigo no es uno de los conocidos. Necesario para re-derivar la idemKey
    /// (que se calcula sobre el ISO, no sobre el codigo ARCA).
    /// </summary>
    private static string? ReverseMapArcaCurrencyToIso(string? arcaCurrencyCode)
    {
        if (string.Equals(arcaCurrencyCode, "PES", StringComparison.OrdinalIgnoreCase))
        {
            return "ARS";
        }

        if (string.Equals(arcaCurrencyCode, "DOL", StringComparison.OrdinalIgnoreCase))
        {
            return "USD";
        }

        return null;
    }

    /// <summary>
    /// FC1.3.F2.6 (counter): normaliza el motivo de rechazo de ARCA para usarlo como tag
    /// del counter <c>ArcaRejected</c>. Truncamos a 200 chars (un tag de metrica largo
    /// explota la cardinalidad del backend de metricas) y reemplazamos null/vacio por un
    /// texto generico para que el tag nunca quede vacio.
    /// </summary>
    private static string TruncateRejectReason(string? observaciones)
    {
        if (string.IsNullOrWhiteSpace(observaciones))
            return "(sin observaciones)";

        return observaciones.Length <= 200 ? observaciones : observaciones.Substring(0, 200);
    }

    private async Task CreateNotification(string userId, string message, string type, int relatedId)
    {
        _context.Notifications.Add(new Notification
        {
            UserId = userId,
            Message = message,
            Type = type,
            RelatedEntityId = relatedId,
            RelatedEntityType = "Invoice"
        });
        await _context.SaveChangesAsync();
    }

    private async Task NotifyAdminsOfForcedInvoiceAsync(Invoice invoice, CreateInvoiceRequest request, CancellationToken ct)
    {
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        if (adminUsers.Count == 0)
            return;

        var actor = string.IsNullOrWhiteSpace(request.ForcedByUserName) ? "Un agente" : request.ForcedByUserName;
        var message = $"{actor} emitio AFIP por excepcion para la reserva #{invoice.ReservaId} con saldo pendiente de {invoice.OutstandingBalanceAtIssuance:C2}.";

        foreach (var admin in adminUsers)
        {
            _context.Notifications.Add(new Notification
            {
                UserId = admin.Id,
                Message = message,
                Type = "Warning",
                RelatedEntityId = invoice.Id,
                RelatedEntityType = "Invoice"
            });
        }

        await _context.SaveChangesAsync(ct);
    }

    private IQueryable<InvoicingWorkItemDto> BuildInvoicingWorkItemsQuery(OperationalFinanceSettings settings, string? ownerScope = null)
    {
        var allowsOverride = settings.AfipInvoiceControlMode == AfipInvoiceControlModes.AllowAgentOverrideWithReason;
        var overrideBlockReason = "La reserva tiene deuda. AFIP queda bloqueado por defecto y requiere override con motivo.";
        var hardBlockReason = "La reserva no esta cancelada economicamente y no puede emitirse en AFIP.";

        var reservasBase = _context.Reservas
            .AsNoTracking()
            .Where(reserva => ActiveInvoicingStatuses.Contains(reserva.Status));
        if (ownerScope is not null)
        {
            reservasBase = reservasBase.Where(reserva => reserva.ResponsibleUserId == ownerScope);
        }

        return reservasBase
            .Select(reserva => new
            {
                reserva.Id,
                reserva.PublicId,
                reserva.NumeroReserva,
                CustomerName = reserva.Payer != null ? reserva.Payer.FullName : "Consumidor Final",
                reserva.StartDate,
                TotalSale = Math.Round(reserva.TotalSale, 2),
                Balance = Math.Round(reserva.Balance, 2),
                AlreadyInvoiced = Math.Round(_context.Invoices
                    .AsNoTracking()
                    .Where(invoice => invoice.ReservaId == reserva.Id && invoice.Resultado == "A")
                    .Sum(invoice => (decimal?)(
                        invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8 || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53
                            ? -invoice.ImporteTotal
                            : invoice.ImporteTotal)) ?? 0m, 2),
                ForcedByUserName = _context.Invoices
                    .AsNoTracking()
                    .Where(invoice => invoice.ReservaId == reserva.Id && invoice.Resultado == "A" && invoice.WasForced)
                    .OrderByDescending(invoice => invoice.CreatedAt)
                    .Select(invoice => invoice.ForcedByUserName)
                    .FirstOrDefault(),
                // 2026-05-11 (UX pendiente al emitir): true si hay un job ProcessInvoiceJob
                // en vuelo para la reserva. Filtra invoices con Resultado="PENDING" que NO
                // estan anuladas (Succeeded). EF traduce esta subconsulta a un EXISTS
                // correlacionado. La fila sigue apareciendo en la worklist (PendingFiscalAmount
                // no cambia) pero el frontend deshabilita Emitir para evitar doble click.
                HasInvoiceInProgress = _context.Invoices
                    .AsNoTracking()
                    .Any(invoice =>
                        invoice.ReservaId == reserva.Id &&
                        invoice.Resultado == "PENDING" &&
                        invoice.AnnulmentStatus != AnnulmentStatus.Succeeded)
            })
            .Select(item => new
            {
                item.PublicId,
                item.NumeroReserva,
                item.CustomerName,
                item.StartDate,
                item.TotalSale,
                item.Balance,
                item.AlreadyInvoiced,
                PendingFiscalAmount = item.TotalSale > item.AlreadyInvoiced
                    ? Math.Round(item.TotalSale - item.AlreadyInvoiced, 2)
                    : 0m,
                item.ForcedByUserName,
                item.HasInvoiceInProgress
            })
            .Where(item => item.PendingFiscalAmount > 0m)
            .Select(item => new InvoicingWorkItemDto
            {
                ReservaPublicId = item.PublicId,
                NumeroReserva = item.NumeroReserva,
                CustomerName = item.CustomerName,
                StartDate = item.StartDate,
                TotalSale = item.TotalSale,
                AlreadyInvoiced = item.AlreadyInvoiced,
                PendingFiscalAmount = item.PendingFiscalAmount,
                // 2026-05-11 (UX pendiente al emitir): si hay un job de emision en vuelo,
                // el estado "in_progress" tiene prioridad sobre ready/override/blocked.
                // El frontend usa este valor para deshabilitar Emitir hasta que el job
                // termine — evita doble click que generaria 2 CAE distintos. Cuando el
                // job aprueba, AlreadyInvoiced sube y la fila desaparece (o queda con
                // saldo restante). Si rechaza, el PENDING se libera y vuelve a ready.
                FiscalStatus = item.HasInvoiceInProgress
                    ? "in_progress"
                    : item.Balance <= 0m
                        ? "ready"
                        : allowsOverride
                            ? "override"
                            : "blocked",
                FiscalStatusLabel = item.HasInvoiceInProgress
                    ? "En proceso AFIP"
                    : item.Balance <= 0m
                        ? "Lista para emitir"
                        : allowsOverride
                            ? "Requiere autorizacion"
                            : "Bloqueada por deuda",
                RequiresOverride = !item.HasInvoiceInProgress && item.Balance > 0m && allowsOverride,
                EconomicBlockReason = item.HasInvoiceInProgress
                    ? null
                    : item.Balance <= 0m
                        ? null
                        : allowsOverride
                            ? overrideBlockReason
                            : hardBlockReason,
                ForcedByUserName = item.ForcedByUserName
            });
    }

    private static decimal GetNetInvoiceAmount(int tipoComprobante, decimal importeTotal)
    {
        return tipoComprobante == 3 || tipoComprobante == 8 || tipoComprobante == 13 || tipoComprobante == 53
            ? -importeTotal
            : importeTotal;
    }

    private static IQueryable<Invoice> ApplyInvoiceKind(IQueryable<Invoice> query, string? kind)
    {
        var normalizedKind = (kind ?? "all").Trim().ToLowerInvariant();

        return normalizedKind switch
        {
            "creditnote" => query.Where(invoice =>
                invoice.TipoComprobante == 3 ||
                invoice.TipoComprobante == 8 ||
                invoice.TipoComprobante == 13 ||
                invoice.TipoComprobante == 53),
            "issued" => query.Where(invoice =>
                invoice.TipoComprobante != 3 &&
                invoice.TipoComprobante != 8 &&
                invoice.TipoComprobante != 13 &&
                invoice.TipoComprobante != 53),
            _ => query
        };
    }

    private static IQueryable<Invoice> ApplyInvoiceSearch(IQueryable<Invoice> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(invoice =>
            invoice.NumeroComprobante.ToString().Contains(normalized) ||
            invoice.ForceReason != null && invoice.ForceReason.ToLower().Contains(normalized) ||
            invoice.Reserva != null && invoice.Reserva.NumeroReserva.ToLower().Contains(normalized) ||
            invoice.Reserva != null && invoice.Reserva.Payer != null && invoice.Reserva.Payer.FullName.ToLower().Contains(normalized));
    }

    private static IQueryable<Invoice> ApplyInvoiceStructuredFilters(IQueryable<Invoice> query, InvoicesListQuery request)
    {
        if (!string.IsNullOrWhiteSpace(request.Period) &&
            DateTime.TryParseExact($"{request.Period}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var monthStart))
        {
            var periodStart = DateTime.SpecifyKind(monthStart, DateTimeKind.Utc);
            var periodEnd = periodStart.AddMonths(1);
            query = query.Where(invoice => invoice.CreatedAt >= periodStart && invoice.CreatedAt < periodEnd);
        }

        if (!string.IsNullOrWhiteSpace(request.Customer))
        {
            var normalizedCustomer = request.Customer.Trim().ToLowerInvariant();
            query = query.Where(invoice =>
                invoice.Reserva != null &&
                invoice.Reserva.Payer != null &&
                invoice.Reserva.Payer.FullName.ToLower().Contains(normalizedCustomer));
        }

        if (!string.IsNullOrWhiteSpace(request.Reservation))
        {
            var normalizedReservation = request.Reservation.Trim().ToLowerInvariant();
            query = query.Where(invoice =>
                invoice.Reserva != null &&
                invoice.Reserva.NumeroReserva.ToLower().Contains(normalizedReservation));
        }

        if (!string.IsNullOrWhiteSpace(request.VoucherNumber))
        {
            var normalizedVoucher = request.VoucherNumber.Trim().ToLowerInvariant();
            query = query.Where(invoice =>
                invoice.NumeroComprobante.ToString().Contains(normalizedVoucher) ||
                invoice.PuntoDeVenta.ToString().Contains(normalizedVoucher));
        }

        if (!string.IsNullOrWhiteSpace(request.Result))
        {
            var normalizedResult = request.Result.Trim().ToLowerInvariant();
            query = normalizedResult switch
            {
                "approved" or "aprobado" or "a" => query.Where(invoice => invoice.Resultado == "A"),
                "rejected" or "rechazado" or "r" => query.Where(invoice => invoice.Resultado == "R"),
                "pending" or "pendiente" or "pendingapproval" => query.Where(invoice => invoice.Resultado != "A" && invoice.Resultado != "R"),
                _ => query
            };
        }

        return query;
    }

    private static IQueryable<Invoice> ApplyInvoiceOrdering(IQueryable<Invoice> query, InvoicesListQuery request)
    {
        var sortBy = (request.SortBy ?? "createdAt").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "numerocomprobante" => desc
                ? query.OrderByDescending(invoice => invoice.NumeroComprobante).ThenByDescending(invoice => invoice.CreatedAt)
                : query.OrderBy(invoice => invoice.NumeroComprobante).ThenByDescending(invoice => invoice.CreatedAt),
            "importetotal" => desc
                ? query.OrderByDescending(invoice => invoice.ImporteTotal).ThenByDescending(invoice => invoice.CreatedAt)
                : query.OrderBy(invoice => invoice.ImporteTotal).ThenByDescending(invoice => invoice.CreatedAt),
            _ => desc
                ? query.OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
                : query.OrderBy(invoice => invoice.CreatedAt).ThenBy(invoice => invoice.Id)
        };
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemSearch(
        IQueryable<InvoicingWorkItemDto> query,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(item =>
            item.NumeroReserva.ToLower().Contains(normalized) ||
            item.CustomerName.ToLower().Contains(normalized) ||
            (item.EconomicBlockReason != null && item.EconomicBlockReason.ToLower().Contains(normalized)) ||
            item.FiscalStatusLabel.ToLower().Contains(normalized));
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemStructuredFilters(
        IQueryable<InvoicingWorkItemDto> query,
        InvoicingWorklistQuery request)
    {
        if (!string.IsNullOrWhiteSpace(request.Customer))
        {
            var normalizedCustomer = request.Customer.Trim().ToLowerInvariant();
            query = query.Where(item => item.CustomerName.ToLower().Contains(normalizedCustomer));
        }

        if (!string.IsNullOrWhiteSpace(request.Reservation))
        {
            var normalizedReservation = request.Reservation.Trim().ToLowerInvariant();
            query = query.Where(item => item.NumeroReserva.ToLower().Contains(normalizedReservation));
        }

        return query;
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemStatus(
        IQueryable<InvoicingWorkItemDto> query,
        string? status)
    {
        var normalizedStatus = (status ?? "all").Trim().ToLowerInvariant();

        return normalizedStatus switch
        {
            "ready" => query.Where(item => item.FiscalStatus == "ready"),
            "blocked" => query.Where(item => item.FiscalStatus == "blocked"),
            "override" => query.Where(item => item.FiscalStatus == "override"),
            // 2026-05-11 (UX pendiente al emitir): permite que la UI filtre solo las
            // reservas con job AFIP en vuelo. Util si el operador quiere ver que esta
            // procesandose ahora mismo.
            "in_progress" => query.Where(item => item.FiscalStatus == "in_progress"),
            _ => query
        };
    }

    private static IQueryable<InvoicingWorkItemDto> ApplyInvoicingWorkItemOrdering(
        IQueryable<InvoicingWorkItemDto> query,
        InvoicingWorklistQuery request)
    {
        var sortBy = (request.SortBy ?? "startDate").Trim().ToLowerInvariant();
        var desc = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "pendingfiscalamount" => desc
                ? query.OrderByDescending(item => item.PendingFiscalAmount).ThenBy(item => item.NumeroReserva)
                : query.OrderBy(item => item.PendingFiscalAmount).ThenBy(item => item.NumeroReserva),
            "numeroreserva" => desc
                ? query.OrderByDescending(item => item.NumeroReserva).ThenByDescending(item => item.StartDate)
                : query.OrderBy(item => item.NumeroReserva).ThenBy(item => item.StartDate),
            _ => desc
                ? query.OrderByDescending(item => item.StartDate).ThenBy(item => item.NumeroReserva)
                : query.OrderBy(item => item.StartDate).ThenBy(item => item.NumeroReserva)
        };
    }
}
