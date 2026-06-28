using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): lectura, APLICACION y REVERSA del saldo a favor consumible
/// con un operador (<see cref="SupplierCreditEntry"/> / <see cref="SupplierCreditApplication"/>). Espejo del
/// <see cref="ClientCreditService"/> del lado cliente.
///
/// <para><b>Verdad de caja intacta</b>: aplicar saldo a favor a otra reserva NO mueve caja ni
/// <c>SupplierBalanceByCurrency.TotalPaid</c>. Solo drena el pool (baja <c>RemainingBalance</c>) y, via la
/// <see cref="SupplierCreditApplication"/>, baja la deuda-por-reserva del destino (la resta la vista
/// <c>SupplierService.GetSupplierDebtByReservaAsync</c>). Por eso el <c>Balance</c> agregado del operador NO
/// cambia con un apply: solo se reparte entre reservas.</para>
///
/// <para><b>Concurrencia</b>: dos applies paralelos sobre el mismo entry pelean por el <c>RemainingBalance</c>.
/// xmin (concurrency token) + el CHECK SQL del saldo no-negativo garantizan que no se drene de mas; el service
/// reintenta con <c>ChangeTracker.Clear</c> (mismo patron que <see cref="OperatorRefundService"/>).</para>
/// </summary>
public class SupplierCreditService : ISupplierCreditService
{
    private const int MaxConcurrencyRetries = 3;

    private readonly AppDbContext _db;
    private readonly IAuditService _auditService;
    private readonly ILogger<SupplierCreditService> _logger;
    // Masking de montos por cobranzas.see_cost. Opcionales para no romper construcciones de test legacy
    // (mismo criterio que SupplierService): sin ellos el masking es fail-closed (oculta los montos).
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _httpContextAccessor;
    private readonly IUserPermissionResolver? _permissionResolver;

    public SupplierCreditService(
        AppDbContext db,
        IAuditService auditService,
        ILogger<SupplierCreditService> logger,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _db = db;
        _auditService = auditService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _permissionResolver = permissionResolver;
    }

    // =========================================================================
    // GetSupplierCreditAsync
    // =========================================================================

    public async Task<SupplierCreditOverviewDto> GetSupplierCreditAsync(int supplierId, CancellationToken ct)
    {
        var supplier = await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.Id == supplierId)
            .Select(s => new { s.PublicId, s.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Proveedor no encontrado");

        // Solo bolsillos con saldo disponible (RemainingBalance > 0): es lo que se puede aplicar.
        var entries = await _db.SupplierCreditEntries
            .AsNoTracking()
            .Where(e => e.SupplierId == supplierId && e.RemainingBalance > 0m)
            .Select(e => new { e.PublicId, e.Currency, e.CreditedAmount, e.RemainingBalance, e.CreatedAt })
            .ToListAsync(ct);

        var dto = new SupplierCreditOverviewDto
        {
            SupplierPublicId = supplier.PublicId,
            SupplierName = supplier.Name,
        };

        // Agrupamos por moneda (ARS/USD nunca se mezclan).
        var byCurrency = entries
            .GroupBy(e => Monedas.Normalizar(e.Currency))
            .OrderBy(g => g.Key);

        foreach (var group in byCurrency)
        {
            var line = new SupplierCreditCurrencyLineDto
            {
                Currency = group.Key,
                AvailableBalance = group.Sum(e => e.RemainingBalance),
                Entries = group
                    .OrderBy(e => e.CreatedAt)
                    .Select(e => new SupplierCreditEntryLineDto
                    {
                        PublicId = e.PublicId,
                        CreditedAmount = e.CreditedAmount,
                        RemainingBalance = e.RemainingBalance,
                        CreatedAt = e.CreatedAt,
                    })
                    .ToList(),
            };
            dto.Currencies.Add(line);
        }

        // Aplicaciones VIVAS de saldo a favor de ESTE operador: una SupplierCreditApplication de Kind=Applied que
        // todavia NO tiene su contra-fila Reversed apuntandola. Una sola query (proyeccion) para no disparar N+1:
        // trae directo el numero y el titular de la reserva destino. El front las lista para revertir cada una por
        // su ApplicationPublicId.
        var applicationRows = await _db.SupplierCreditApplications
            .AsNoTracking()
            .Where(a => a.Entry.SupplierId == supplierId
                     && a.Kind == SupplierCreditApplicationKind.Applied
                     && !_db.SupplierCreditApplications.Any(r => r.ReversesApplicationId == a.Id))
            .Select(a => new
            {
                a.PublicId,
                EntryPublicId = a.Entry.PublicId,
                a.Entry.Currency,
                a.Amount,
                AppliedAt = a.CreatedAt,
                TargetReservaPublicId = a.TargetReserva != null ? a.TargetReserva.PublicId : Guid.Empty,
                TargetReservaNumber = a.TargetReserva != null ? a.TargetReserva.NumeroReserva : null,
                TargetReservaHolderName = a.TargetReserva != null && a.TargetReserva.Payer != null
                    ? a.TargetReserva.Payer.FullName
                    : null,
            })
            .ToListAsync(ct);

        dto.ActiveApplications = applicationRows
            .OrderByDescending(r => r.AppliedAt)
            .Select(r => new SupplierCreditApplicationLineDto
            {
                ApplicationPublicId = r.PublicId,
                EntryPublicId = r.EntryPublicId,
                Currency = Monedas.Normalizar(r.Currency),
                Amount = r.Amount,
                TargetReservaPublicId = r.TargetReservaPublicId,
                TargetReservaNumber = r.TargetReservaNumber,
                TargetReservaHolderName = r.TargetReservaHolderName,
                AppliedAt = r.AppliedAt,
            })
            .ToList();

        // Masking see_cost: sin permiso, la estructura (monedas/bolsillos/aplicaciones) queda visible pero los
        // montos en 0. El saldo a favor del operador es COSTO de la agencia, por eso se enmascara (a diferencia del
        // lado cliente, que es plata del cliente y no se enmascara).
        if (!await CanSeeCostAsync(ct))
        {
            foreach (var line in dto.Currencies)
            {
                line.AvailableBalance = 0m;
                foreach (var entry in line.Entries)
                {
                    entry.CreditedAmount = 0m;
                    entry.RemainingBalance = 0m;
                }
            }
            foreach (var application in dto.ActiveApplications)
            {
                application.Amount = 0m;
            }
        }

        return dto;
    }

    private Task<bool> CanSeeCostAsync(CancellationToken ct)
        => CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);

    // =========================================================================
    // ApplyCreditAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<SupplierCreditApplicationResultDto> ApplyCreditAsync(
        int supplierId,
        ApplySupplierCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        if (request.Amount <= 0m)
        {
            throw new ArgumentException("El monto a aplicar debe ser mayor a 0.", nameof(request));
        }
        if (!Monedas.EsSoportada(request.Currency))
        {
            throw new ArgumentException("Moneda no soportada.", nameof(request));
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryApplyOnceAsync(supplierId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ApplyCreditAsync concurrency conflict on attempt {Attempt}/{Max} for supplier {SupplierId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, supplierId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ApplyCreditAsync retry loop exhausted sin resultado.");
    }

    private async Task<SupplierCreditApplicationResultDto> TryApplyOnceAsync(
        int supplierId,
        ApplySupplierCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        string currency = Monedas.Normalizar(request.Currency);
        decimal amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct)
            ?? throw new KeyNotFoundException("Proveedor no encontrado");

        // 1) Resolver la reserva destino por PublicId.
        var targetReserva = await _db.Reservas
            .Where(r => r.PublicId == request.TargetReservaPublicId)
            .Select(r => new { r.Id, r.PublicId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Reserva destino no encontrada");

        // 2) (c) La reserva destino tiene que ser del MISMO operador (tener al menos un servicio de este
        //    proveedor). Sin esto, aplicar saldo a una reserva ajena al operador no tiene sentido contable.
        var supplierIdsInReserva = await SupplierDebtPersister.GetReservaSupplierIdsAsync(_db, targetReserva.Id, ct);
        if (!supplierIdsInReserva.Contains(supplierId))
        {
            throw new BusinessInvariantViolationException(
                "La reserva destino no tiene servicios de este operador. No se puede aplicarle su saldo a favor.",
                invariantCode: "INV-SUPCREDIT-002");
        }

        // 2-bis) (b/M1) Deuda VIVA del operador en la reserva destino EN ESA MONEDA (compras confirmadas - pagos de
        //        caja imputados - saldo a favor ya aplicado). Si es <= 0 no hay nada que aplicar en esa moneda:
        //        cubre tanto "no cruzar monedas" (sin deuda en la moneda del bolsillo) como "ya saldada". M1: el
        //        monto a aplicar nunca puede superar esta deuda, para no dejar saldo a favor ATRAPADO en el destino.
        decimal targetDebtInCurrency = await GetSupplierLiveDebtInReservaCurrencyAsync(
            supplier.InvoicingMode, supplierId, targetReserva.Id, currency, ct);
        if (targetDebtInCurrency <= 0m)
        {
            throw new BusinessInvariantViolationException(
                "La reserva destino no tiene deuda con este operador en esa moneda " +
                "(el saldo a favor solo se aplica a una deuda de la misma moneda; ARS nunca toca USD).",
                invariantCode: "INV-SUPCREDIT-003");
        }

        // 3) Pool disponible en esa moneda (tracked: vamos a drenar). FIFO por antiguedad.
        var entries = await _db.SupplierCreditEntries
            .Where(e => e.SupplierId == supplierId && e.RemainingBalance > 0m)
            .ToListAsync(ct);
        var currencyEntries = entries
            .Where(e => Monedas.Normalizar(e.Currency) == currency)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToList();

        decimal pool = currencyEntries.Sum(e => e.RemainingBalance);

        // B1 (review, anti "credito fantasma gastable"): el saldo a favor DISPONIBLE para aplicar nunca puede
        // superar el sobrepago que el Balance DERIVADO todavia respalda. Releemos el Balance FRESCO y topeamos:
        //   available = min( Σ RemainingBalance del pool , max(0, -Balance) ).
        // Asi, si un cambio de servicio consumio el sobrepago y el reconciler todavia no corrio (solo corre en
        // eventos de PAGO), el pool optimista NO se puede gastar: lo que no respalda el sobrepago real se frena.
        decimal freshBalance = await SupplierBalanceForCurrencyAsync(supplierId, currency, ct);
        decimal overpaymentBacking = freshBalance < 0m ? -freshBalance : 0m;
        decimal available = Math.Min(pool, overpaymentBacking);

        // (a) No exceder el saldo a favor disponible en ESA moneda. SEGURIDAD (review): el mensaje NO revela la
        //     cifra disponible (es dato de costo, gateado por cobranzas.see_cost) — solo el endpoint esta gateado
        //     por tesoreria.supplier_payments. El CHECK SQL es la red dura bajo concurrencia.
        if (amount > available)
        {
            throw new BusinessInvariantViolationException(
                "El monto a aplicar supera el saldo a favor disponible con este operador en esa moneda.",
                invariantCode: "INV-SUPCREDIT-004");
        }

        // M1 (review): no sobre-aplicar. El monto no puede superar la deuda viva del destino en esa moneda, para
        //     no dejar la reserva destino con saldo a favor atrapado. Mensaje generico (sin cifra de costo).
        if (amount > targetDebtInCurrency)
        {
            throw new BusinessInvariantViolationException(
                "El monto a aplicar supera la deuda de la reserva destino con este operador en esa moneda.",
                invariantCode: "INV-SUPCREDIT-007");
        }

        // 4) Drenar FIFO. Por cada bolsillo tocado creamos una SupplierCreditApplication(Applied): asi cada
        //    consumo es reversible de forma independiente. El total drenado == amount (lo garantiza el tope).
        decimal remainingToApply = amount;
        SupplierCreditApplication? firstApplication = null;

        foreach (var entry in currencyEntries)
        {
            if (remainingToApply <= 0m) break;

            decimal take = Math.Min(entry.RemainingBalance, remainingToApply);
            entry.RemainingBalance = Math.Round(entry.RemainingBalance - take, 2, MidpointRounding.AwayFromZero);
            if (entry.RemainingBalance <= 0m)
            {
                entry.RemainingBalance = 0m;
                entry.IsFullyConsumed = true;
            }
            remainingToApply = Math.Round(remainingToApply - take, 2, MidpointRounding.AwayFromZero);

            var application = new SupplierCreditApplication
            {
                Entry = entry,                 // navigation: entry.Id puede ser != 0 (ya persistido), pero usamos nav por consistencia
                SupplierCreditEntryId = entry.Id,
                Amount = take,
                TargetReservaId = targetReserva.Id,
                Kind = SupplierCreditApplicationKind.Applied,
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = userId,
                CreatedByUserName = userName,
            };
            _db.SupplierCreditApplications.Add(application);
            firstApplication ??= application;

            _auditService.StageBusinessEvent(
                action: AuditActions.SupplierCreditApplied,
                entityName: AuditActions.SupplierCreditApplicationEntityName,
                entityId: application.PublicId.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    applicationPublicId = application.PublicId,
                    entryPublicId = entry.PublicId,
                    supplierId,
                    currency,
                    amount = take,
                    targetReservaPublicId = targetReserva.PublicId,
                }),
                userId: userId,
                userName: userName);
        }

        // 5) SaveChanges UNICO: drenaje del pool + applications + auditoria staged, todo atomico. El xmin de
        //    cada entry detecta si otro apply lo movio en paralelo -> DbUpdateConcurrencyException -> retry.
        await _db.SaveChangesAsync(ct);

        decimal availableAfter = await GetAvailableBalanceAsync(supplierId, currency, ct);

        _logger.LogInformation(
            "metric:supplier_credit_applied | SupplierId={SupplierId} Currency={Currency} Amount={Amount} TargetReservaId={TargetReservaId}",
            supplierId, currency, amount, targetReserva.Id);

        return new SupplierCreditApplicationResultDto
        {
            ApplicationPublicId = firstApplication!.PublicId,
            EntryPublicId = firstApplication.Entry.PublicId,
            Currency = currency,
            Amount = amount,
            TargetReservaPublicId = targetReserva.PublicId,
            IsReversal = false,
            AvailableBalanceAfter = availableAfter,
        };
    }

    // =========================================================================
    // ReverseApplicationAsync (con retry de concurrencia)
    // =========================================================================

    public async Task<SupplierCreditApplicationResultDto> ReverseApplicationAsync(
        int supplierId,
        Guid applicationPublicId,
        ReverseSupplierCreditApplicationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // Motivo OPCIONAL (decision del dueño, simetrico con el lado cliente): puede venir null/vacio y la reversa
        // procede igual. Si viene, se normaliza y se audita; si no, queda null. NO se exige minimo de caracteres.
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryReverseOnceAsync(supplierId, applicationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ReverseApplicationAsync concurrency conflict on attempt {Attempt}/{Max} for application {ApplicationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, applicationPublicId);

                _db.ChangeTracker.Clear();
                if (attempt == MaxConcurrencyRetries - 1) throw;
                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ReverseApplicationAsync retry loop exhausted sin resultado.");
    }

    private async Task<SupplierCreditApplicationResultDto> TryReverseOnceAsync(
        int supplierId,
        Guid applicationPublicId,
        ReverseSupplierCreditApplicationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        var application = await _db.SupplierCreditApplications
            .Include(a => a.Entry)
            .Include(a => a.TargetReserva)
            .FirstOrDefaultAsync(a => a.PublicId == applicationPublicId, ct)
            ?? throw new KeyNotFoundException("Aplicacion de saldo a favor no encontrada");

        // La aplicacion tiene que ser de ESTE operador (su bolsillo pertenece al supplier de la ruta).
        if (application.Entry.SupplierId != supplierId)
        {
            throw new KeyNotFoundException("Aplicacion de saldo a favor no encontrada");
        }

        // Solo se revierte una aplicacion (Applied); una contra-fila Reversed no se re-revierte.
        if (application.Kind != SupplierCreditApplicationKind.Applied)
        {
            throw new BusinessInvariantViolationException(
                "Solo se puede revertir una aplicacion de saldo a favor (no una reversa).",
                invariantCode: "INV-SUPCREDIT-005");
        }

        // Anti doble-reversa: si ya existe una contra-fila apuntando a esta aplicacion, no se revierte de nuevo.
        bool alreadyReversed = await _db.SupplierCreditApplications
            .AnyAsync(a => a.ReversesApplicationId == application.Id, ct);
        if (alreadyReversed)
        {
            throw new BusinessInvariantViolationException(
                "Esta aplicacion de saldo a favor ya fue revertida.",
                invariantCode: "INV-SUPCREDIT-006");
        }

        var entry = application.Entry;
        string currency = Monedas.Normalizar(entry.Currency);

        // Reponer el pool: el saldo vuelve al bolsillo (RemainingBalance sube; no puede superar CreditedAmount
        // porque solo reponemos lo que esta aplicacion habia drenado). El CHECK SQL es la red dura.
        entry.RemainingBalance = Math.Round(entry.RemainingBalance + application.Amount, 2, MidpointRounding.AwayFromZero);
        if (entry.RemainingBalance > 0m)
        {
            entry.IsFullyConsumed = false;
        }

        // Contra-fila inmutable (patron Void). Deshace la imputacion en la reserva destino (la vista de deuda
        // por reserva resta Applied - Reversed).
        var reversal = new SupplierCreditApplication
        {
            Entry = entry,
            SupplierCreditEntryId = entry.Id,
            Amount = application.Amount,
            TargetReservaId = application.TargetReservaId,
            Kind = SupplierCreditApplicationKind.Reversed,
            ReversesApplication = application,
            ReversesApplicationId = application.Id,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
            CreatedByUserName = userName,
            // Motivo opcional: si vino lo normalizamos, si no queda null.
            ReversalReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
        };
        _db.SupplierCreditApplications.Add(reversal);

        _auditService.StageBusinessEvent(
            action: AuditActions.SupplierCreditApplicationReversed,
            entityName: AuditActions.SupplierCreditApplicationEntityName,
            entityId: reversal.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                reversalPublicId = reversal.PublicId,
                reversedApplicationPublicId = application.PublicId,
                entryPublicId = entry.PublicId,
                supplierId,
                currency,
                amount = application.Amount,
                targetReservaPublicId = application.TargetReserva?.PublicId,
                reason = reversal.ReversalReason,
            }),
            userId: userId,
            userName: userName);

        await _db.SaveChangesAsync(ct);

        decimal availableAfter = await GetAvailableBalanceAsync(supplierId, currency, ct);

        _logger.LogInformation(
            "metric:supplier_credit_application_reversed | SupplierId={SupplierId} Currency={Currency} Amount={Amount} ApplicationPublicId={ApplicationPublicId}",
            supplierId, currency, application.Amount, application.PublicId);

        return new SupplierCreditApplicationResultDto
        {
            ApplicationPublicId = reversal.PublicId,
            EntryPublicId = entry.PublicId,
            Currency = currency,
            Amount = application.Amount,
            TargetReservaPublicId = application.TargetReserva?.PublicId ?? Guid.Empty,
            IsReversal = true,
            AvailableBalanceAfter = availableAfter,
        };
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Saldo a favor disponible (suma de RemainingBalance) del operador en una moneda, leido fresco.</summary>
    private async Task<decimal> GetAvailableBalanceAsync(int supplierId, string currency, CancellationToken ct)
    {
        var rows = await _db.SupplierCreditEntries
            .Where(e => e.SupplierId == supplierId && e.RemainingBalance > 0m)
            .Select(e => new { e.Currency, e.RemainingBalance })
            .ToListAsync(ct);
        return rows
            .Where(r => Monedas.Normalizar(r.Currency) == currency)
            .Sum(r => r.RemainingBalance);
    }

    /// <summary>Balance derivado del operador en una moneda (SupplierBalanceByCurrency), leido fresco. 0 si no hay fila.</summary>
    private async Task<decimal> SupplierBalanceForCurrencyAsync(int supplierId, string currency, CancellationToken ct)
    {
        var rows = await _db.SupplierBalanceByCurrency.AsNoTracking()
            .Where(r => r.SupplierId == supplierId)
            .Select(r => new { r.Currency, r.Balance })
            .ToListAsync(ct);
        return rows.Where(r => Monedas.Normalizar(r.Currency) == currency).Sum(r => r.Balance);
    }

    /// <summary>
    /// Deuda VIVA del operador en UNA reserva y UNA moneda = compras confirmadas (regla oficial por tipo,
    /// respetando <see cref="SupplierInvoicingMode"/>) - pagos de caja imputados a la reserva en esa moneda -
    /// saldo a favor YA aplicado a la reserva en esa moneda (Applied - Reversed). Usa el calculador PURO del
    /// dominio (<see cref="SupplierDebtCalculator"/>), el MISMO que la cuenta del proveedor, para no inventar otra
    /// matematica. Es UNMASKED a proposito: es el tope de la aplicacion (M1), no un dato de salida; el masking
    /// see_cost se aplica solo en lo que se le devuelve al usuario. Devuelve 0 si esa moneda no tiene deuda
    /// (cubre tambien el "no cruzar monedas": un bolsillo USD contra una reserva solo-ARS da 0 -> se rechaza).
    /// </summary>
    private async Task<decimal> GetSupplierLiveDebtInReservaCurrencyAsync(
        SupplierInvoicingMode invoicingMode, int supplierId, int reservaId, string currency, CancellationToken ct)
    {
        // 1) Compras confirmadas. Un operador CommissionOnly (intermediacion) NO genera deuda de compra.
        IEnumerable<SupplierDebtCalculator.ConfirmedPurchase> confirmedPurchases =
            Array.Empty<SupplierDebtCalculator.ConfirmedPurchase>();

        if (SupplierDebtCalculator.SupplierGeneratesPurchaseDebt(invoicingMode))
        {
            var serviceRows = await BuildSupplierServiceRowsInReservaAsync(supplierId, reservaId, ct);
            confirmedPurchases = serviceRows
                .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
                .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.NetCost))
                .ToList();
        }

        // 2) Pagos de caja imputados a la reserva (el query filter !IsDeleted excluye los anulados).
        var paymentRows = await _db.SupplierPayments
            .Where(p => p.SupplierId == supplierId && p.ReservaId == reservaId)
            .Select(p => new { p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount })
            .ToListAsync(ct);
        var payments = paymentRows.Select(p => new SupplierDebtCalculator.SupplierPaymentInput(
            p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount));

        var lines = SupplierDebtCalculator.Calculate(confirmedPurchases, payments);
        decimal cashDebt = lines.TryGetValue(currency, out var line) ? line.Balance : 0m;

        // 3) Saldo a favor YA aplicado a esta reserva en esta moneda (Applied - Reversed). Baja la deuda viva.
        var applicationRows = await _db.SupplierCreditApplications
            .Where(a => a.Entry.SupplierId == supplierId && a.TargetReservaId == reservaId)
            .Select(a => new { a.Kind, a.Amount, a.Entry.Currency })
            .ToListAsync(ct);
        decimal creditApplied = applicationRows
            .Where(a => Monedas.Normalizar(a.Currency) == currency)
            .Sum(a => a.Kind == SupplierCreditApplicationKind.Applied ? a.Amount : -a.Amount);

        return Math.Round(cashDebt - creditApplied, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Materializa (Type, Status, NetCost, Currency) de los servicios del operador en UNA reserva, recorriendo
    /// las 6 tablas (mismo universo y mismos Type que la cuenta del proveedor en <c>SupplierService</c>). Se
    /// materializa porque la regla por tipo (<c>CountsForSupplierDebtByType</c>) no se traduce a SQL.
    /// </summary>
    private async Task<List<(string Type, string Status, decimal NetCost, string Currency)>>
        BuildSupplierServiceRowsInReservaAsync(int supplierId, int reservaId, CancellationToken ct)
    {
        var result = new List<(string Type, string Status, decimal NetCost, string Currency)>();

        var flights = await _db.FlightSegments.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(flights.Select(s => ("Vuelo", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var hotels = await _db.HotelBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(hotels.Select(s => ("Hotel", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var transfers = await _db.TransferBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(transfers.Select(s => ("Traslado", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var packages = await _db.PackageBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(packages.Select(s => ("Paquete", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var assistances = await _db.AssistanceBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(assistances.Select(s => ("Asistencia", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var generics = await _db.Servicios.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.ServiceType, s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        result.AddRange(generics.Select(s => (s.ServiceType, s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        return result;
    }
}
