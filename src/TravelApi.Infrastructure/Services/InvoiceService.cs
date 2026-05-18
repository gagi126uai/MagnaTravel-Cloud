using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Globalization;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
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
                if (_bcBridge is not null)
                {
                    try
                    {
                        await _bcBridge.OnArcaSucceededAsync(invoiceId, newInvoice.Id, CancellationToken.None);
                    }
                    catch (Exception bridgeEx)
                    {
                        _logger.LogError(
                            bridgeEx,
                            "Bridge BC.OnArcaSucceededAsync fallo para Invoice {InvoiceId} (CN={CreditNoteId}). " +
                            "La NC quedo Succeeded en AFIP. Remediacion: ForceArcaConfirmationAsync manual.",
                            invoiceId, newInvoice.Id);
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
