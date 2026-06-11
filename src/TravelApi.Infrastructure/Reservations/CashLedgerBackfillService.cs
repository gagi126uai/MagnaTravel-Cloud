using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-022 §5.2: backfill IDEMPOTENTE del Libro de Caja (<see cref="CashLedgerEntry"/>) para los hechos
/// historicos. Espejo del patron de <see cref="MultiCurrencyBackfillService"/> (ADR-021).
///
/// <para><b>Por que existe</b>: la migracion crea la tabla VACIA. Hasta que se backfillee, los hechos
/// economicos vivos (cobros, pagos a proveedor, movimientos manuales y de cancelacion) no tienen su
/// asiento. Este job recorre el universo vivo y crea UN asiento por hecho usando el mismo
/// <see cref="CashLedgerEntryFactory"/> que la escritura en vivo, asi el dato es identico.</para>
///
/// <para><b>Idempotencia (B2)</b>: NO se apoya en el indice unico parcial (eso es una invariante de
/// integridad, no la herramienta). Antes de insertar cada asiento se chequea la CLAVE NATURAL POR ORIGEN
/// (¿ya hay un asiento VIGENTE para este FK?). Correr el job dos veces no duplica: la segunda corrida
/// encuentra el asiento y lo saltea. <see cref="NeedsBackfillAsync"/> es el chequeo barato para saltar todo
/// cuando ya esta cubierto.</para>
///
/// <para><b>Universo (sin doble conteo, RK-1)</b>: recorre <c>Payment</c> vivos (<c>AffectsCash</c>),
/// <c>SupplierPayment</c> vivos y <c>ManualCashMovement</c> no-voided — y NADA MAS. Los movimientos de
/// cancelacion (refund/withdrawal) YA son <c>ManualCashMovement</c>, asi que recorrerlos los cubre; NO se
/// asientan el <c>OperatorRefundReceived</c>/<c>ClientCreditWithdrawal</c> por separado (seria duplicar).
/// La moneda del asiento de un manual de cancelacion sale del ORIGEN REAL (refund/entry), no del manual.</para>
///
/// <para><b>Borrados</b>: solo hechos vivos (no se recrea historia de borrados — no tenemos fecha de
/// anulacion confiable). El libro arranca como "saldo de apertura implicito = estado vivo actual".</para>
/// </summary>
public sealed class CashLedgerBackfillService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CashLedgerBackfillService>? _logger;

    public CashLedgerBackfillService(AppDbContext db, ILogger<CashLedgerBackfillService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Chequeo barato para saltar el backfill cuando ya esta hecho: hay backfill pendiente si existe
    /// algun hecho economico vivo SIN asiento vigente (un EXISTS por cada origen).
    /// </summary>
    public async Task<bool> NeedsBackfillAsync(CancellationToken ct = default)
    {
        bool anyPaymentPending = await _db.Payments
            .Where(p => p.AffectsCash && p.Status != "Cancelled")
            .Where(p => !_db.CashLedgerEntries.Any(e => e.PaymentId == p.Id && !e.IsReversal && !e.IsReversed))
            .AnyAsync(ct);
        if (anyPaymentPending) return true;

        bool anySupplierPaymentPending = await _db.SupplierPayments
            .Where(sp => !_db.CashLedgerEntries.Any(e => e.SupplierPaymentId == sp.Id && !e.IsReversal && !e.IsReversed))
            .AnyAsync(ct);
        if (anySupplierPaymentPending) return true;

        bool anyManualPending = await _db.ManualCashMovements
            .Where(m => !m.IsVoided)
            .Where(m => !_db.CashLedgerEntries.Any(e => e.ManualCashMovementId == m.Id && !e.IsReversal && !e.IsReversed))
            .AnyAsync(ct);
        return anyManualPending;
    }

    /// <summary>
    /// Ejecuta el backfill. Devuelve cuantos asientos se crearon por cada origen. Seguro de re-llamar
    /// (la clave natural por origen evita duplicar).
    /// </summary>
    public async Task<(int payments, int supplierPayments, int manuals)> RunAsync(CancellationToken ct = default)
    {
        int payments = await BackfillPaymentsAsync(ct);
        int supplierPayments = await BackfillSupplierPaymentsAsync(ct);
        int manuals = await BackfillManualMovementsAsync(ct);

        _logger?.LogInformation(
            "ADR-022 cash ledger backfill done. Cobros={Payments}, PagosProveedor={SupplierPayments}, Manuales={Manuals}.",
            payments, supplierPayments, manuals);

        return (payments, supplierPayments, manuals);
    }

    private async Task<int> BackfillPaymentsAsync(CancellationToken ct)
    {
        // Universo: cobros vivos que mueven caja. El query filter global ya excluye IsDeleted.
        var payments = await _db.Payments
            .Where(p => p.AffectsCash && p.Status != "Cancelled")
            .ToListAsync(ct);

        int created = 0;
        foreach (var payment in payments)
        {
            bool alreadyHasEntry = await _db.CashLedgerEntries
                .AnyAsync(e => e.PaymentId == payment.Id && !e.IsReversal && !e.IsReversed, ct);
            if (alreadyHasEntry) continue;

            var entry = CashLedgerEntryFactory.ForPayment(payment, payment.CreatedByUserId, payment.CreatedByUserName);
            // Backfill: el asiento se ESCRIBE ahora pero el hecho OCURRIO en el pasado; ForPayment ya pone
            // OccurredAt = PaidAt (correcto). CreatedAt = ahora (cuando se backfilleo) tambien es correcto.
            _db.CashLedgerEntries.Add(entry);
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<int> BackfillSupplierPaymentsAsync(CancellationToken ct)
    {
        // El query filter global ya excluye IsDeleted.
        var supplierPayments = await _db.SupplierPayments.ToListAsync(ct);

        int created = 0;
        foreach (var sp in supplierPayments)
        {
            bool alreadyHasEntry = await _db.CashLedgerEntries
                .AnyAsync(e => e.SupplierPaymentId == sp.Id && !e.IsReversal && !e.IsReversed, ct);
            if (alreadyHasEntry) continue;

            var entry = CashLedgerEntryFactory.ForSupplierPayment(sp, sp.DeletedByUserId, actorUserName: null);
            _db.CashLedgerEntries.Add(entry);
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    private async Task<int> BackfillManualMovementsAsync(CancellationToken ct)
    {
        // Universo: manuales no anulados. Incluye los de cancelacion (refund/withdrawal); cada uno genera
        // UN asiento (RK-1). La moneda del asiento de cancelacion sale del origen real (refund/entry).
        // Include de los FKs de cancelacion para resolver la moneda real sin queries N+1.
        var manuals = await _db.ManualCashMovements
            .Where(m => !m.IsVoided)
            .Include(m => m.OperatorRefundReceived)
            .Include(m => m.ClientCreditWithdrawal)
                .ThenInclude(w => w!.Entry)
            .ToListAsync(ct);

        int created = 0;
        foreach (var manual in manuals)
        {
            bool alreadyHasEntry = await _db.CashLedgerEntries
                .AnyAsync(e => e.ManualCashMovementId == manual.Id && !e.IsReversal && !e.IsReversed, ct);
            if (alreadyHasEntry) continue;

            // Moneda del origen real para los de cancelacion; null (= moneda del propio manual) para el
            // ajuste puro.
            string? currencyOverride = null;
            if (manual.OperatorRefundReceived != null)
            {
                currencyOverride = manual.OperatorRefundReceived.Currency;
            }
            else if (manual.ClientCreditWithdrawal?.Entry != null)
            {
                currencyOverride = manual.ClientCreditWithdrawal.Entry.Currency;
            }

            var entry = CashLedgerEntryFactory.ForManualMovement(
                manual, currencyOverride, actorUserId: manual.CreatedBy, actorUserName: manual.CreatedBy);
            _db.CashLedgerEntries.Add(entry);
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }
}
