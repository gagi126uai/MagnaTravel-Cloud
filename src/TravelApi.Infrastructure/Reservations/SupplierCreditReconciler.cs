using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): mantiene el POOL de saldo a favor consumible con un operador
/// (<see cref="SupplierCreditEntry"/>) en sync con el SOBREPAGO derivado (<c>SupplierBalanceByCurrency.Balance &lt; 0</c>),
/// por moneda. Es el equivalente del <see cref="OverpaymentCreditConverter"/> del lado cliente.
///
/// <para><b>Regla unica que mantiene</b>, por proveedor+moneda:
/// <c>Σ SupplierCreditEntry.CreditedAmount == max(0, -Balance)</c>. Como las aplicaciones drenan el
/// <c>RemainingBalance</c> (no el <c>CreditedAmount</c>), de ahi sale el invariante autoritativo
/// <c>Σ RemainingBalance == max(0, -Balance) - Σ aplicaciones netas</c>. Sin aplicaciones, el pool == el
/// sobrepago (lo mismo que siembra el backfill de la migracion).</para>
///
/// <para><b>Por que vive separado del calculo de la deuda</b>: la deuda (escalar + tabla hija) la sigue
/// produciendo <see cref="SupplierDebtPersister"/> SOLO con caja (compras - pagos). Este reconciler NO la toca:
/// el <c>Balance</c> agregado es la fuente del sobrepago, y este helper solo MATERIALIZA ese sobrepago como
/// credito consumible. Asi NO se duplica plata: el pago sigue contando completo en <c>TotalPaid</c> (verdad de
/// caja intacta) y el credito es la cara consumible del balance negativo, no plata nueva.</para>
///
/// <para><b>Atomicidad</b>: este helper hace su PROPIO <c>SaveChanges</c> (con la auditoria staged en la misma
/// llamada). Se invoca DESPUES de que la deuda del proveedor ya quedo persistida (commit), porque LEE el
/// <c>SupplierBalanceByCurrency</c> ya materializado para saber el sobrepago real.</para>
/// </summary>
internal static class SupplierCreditReconciler
{
    /// <summary>
    /// Reconcilia el pool de TODAS las monedas del operador contra su sobrepago actual. Crea entries cuando el
    /// sobrepago crecio y los drena cuando se redujo (edicion/baja de un pago). Si el sobrepago bajo por DEBAJO
    /// de lo que ya se aplico a otras reservas, lanza <see cref="BusinessInvariantViolationException"/>: no se
    /// puede "destruir" un saldo a favor que ya se consumio sin revertir primero esa aplicacion.
    /// </summary>
    /// <param name="sourceSupplierPaymentId">Pago que disparo la reconciliacion (origen del entry creado). Null si no aplica.</param>
    public static async Task ReconcileAsync(
        AppDbContext db,
        int supplierId,
        int? sourceSupplierPaymentId,
        string? actorUserId,
        string? actorUserName,
        IAuditService? auditService,
        CancellationToken ct)
    {
        // 1) Sobrepago por moneda = max(0, -Balance) leido de la tabla hija ya materializada (committed).
        var balanceRows = await db.SupplierBalanceByCurrency
            .Where(row => row.SupplierId == supplierId)
            .Select(row => new { row.Currency, row.Balance })
            .ToListAsync(ct);

        // 2) Entries vivos (tracked: los vamos a modificar/crear) de este operador.
        var entries = await db.SupplierCreditEntries
            .Where(entry => entry.SupplierId == supplierId)
            .ToListAsync(ct);

        // 3) Union de monedas presentes en el balance o en los entries existentes (a lo sumo ARS + USD).
        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in balanceRows) currencies.Add(Monedas.Normalizar(row.Currency));
        foreach (var entry in entries) currencies.Add(Monedas.Normalizar(entry.Currency));

        bool anyChange = false;

        foreach (var currency in currencies)
        {
            decimal balance = balanceRows
                .Where(row => Monedas.Normalizar(row.Currency) == currency)
                .Sum(row => row.Balance);

            // Sobrepago de ESTA moneda (la deuda negativa). Nunca se mezcla con otra moneda.
            decimal overpayment = balance < 0m ? -balance : 0m;
            overpayment = Math.Round(overpayment, 2, MidpointRounding.AwayFromZero);

            var currencyEntries = entries
                .Where(entry => Monedas.Normalizar(entry.Currency) == currency)
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.Id)
                .ToList();

            decimal totalCredited = currencyEntries.Sum(entry => entry.CreditedAmount);
            decimal diff = Math.Round(overpayment - totalCredited, 2, MidpointRounding.AwayFromZero);

            if (diff == 0m) continue;

            if (diff > 0m)
            {
                // El sobrepago crecio: materializamos el excedente nuevo como un entry adicional (inmutable).
                var entry = new SupplierCreditEntry
                {
                    SupplierId = supplierId,
                    Currency = currency,
                    CreditedAmount = diff,
                    RemainingBalance = diff,
                    IsFullyConsumed = false,
                    SourceSupplierPaymentId = sourceSupplierPaymentId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedByUserId = actorUserId,
                    CreatedByUserName = actorUserName,
                };
                db.SupplierCreditEntries.Add(entry);
                anyChange = true;

                auditService?.StageBusinessEvent(
                    action: AuditActions.SupplierCreditCreated,
                    entityName: AuditActions.SupplierCreditEntryEntityName,
                    entityId: entry.PublicId.ToString(),
                    details: JsonSerializer.Serialize(new
                    {
                        entryPublicId = entry.PublicId,
                        supplierId,
                        currency,
                        creditedAmount = diff,
                        sourceSupplierPaymentId,
                    }),
                    userId: actorUserId ?? "System",
                    userName: actorUserName);
            }
            else
            {
                // El sobrepago se redujo (se edito/borro un pago): drenamos el excedente que ya no esta
                // respaldado. Solo se puede drenar lo que NO se consumio todavia (RemainingBalance). Si hay
                // que sacar mas de lo disponible, es porque ese saldo ya se aplico a otra reserva -> se bloquea.
                decimal toRemove = -diff;

                // Chequeo de factibilidad ANTES de mutar nada: si no alcanza el saldo NO aplicado para absorber
                // la reduccion, lanzamos sin tocar el pool (asi no queda un drenaje parcial ni siquiera sin
                // transaccion). Es plata ya aplicada a otra reserva: hay que revertir esa aplicacion primero.
                decimal totalDrainable = currencyEntries.Sum(e => e.RemainingBalance);
                if (toRemove > totalDrainable)
                {
                    throw new BusinessInvariantViolationException(
                        $"No se puede reducir el saldo a favor con este operador en {currency}: " +
                        $"ya se aplico parte de ese saldo a otra reserva. Reverti primero esa aplicacion.",
                        invariantCode: "INV-SUPCREDIT-001");
                }

                decimal drainedTotal = 0m;
                foreach (var entry in currencyEntries)
                {
                    if (toRemove <= 0m) break;
                    if (entry.RemainingBalance <= 0m) continue;

                    decimal drain = Math.Min(entry.RemainingBalance, toRemove);
                    // Bajamos RemainingBalance y CreditedAmount en lockstep: asi se preserva el CHECK
                    // (0 <= RemainingBalance <= CreditedAmount) y la relacion Remaining = Credited - aplicaciones.
                    entry.RemainingBalance = Math.Round(entry.RemainingBalance - drain, 2, MidpointRounding.AwayFromZero);
                    entry.CreditedAmount = Math.Round(entry.CreditedAmount - drain, 2, MidpointRounding.AwayFromZero);
                    if (entry.RemainingBalance <= 0m)
                    {
                        entry.RemainingBalance = 0m;
                        entry.IsFullyConsumed = true;
                    }
                    toRemove = Math.Round(toRemove - drain, 2, MidpointRounding.AwayFromZero);
                    drainedTotal = Math.Round(drainedTotal + drain, 2, MidpointRounding.AwayFromZero);
                    anyChange = true;
                }

                // M2 (review): auditar el DRENAJE (antes solo se auditaba la creacion). Va staged en la MISMA
                // SaveChanges que la mutacion del pool, asi es atomico con la reduccion del saldo a favor.
                if (drainedTotal > 0m)
                {
                    auditService?.StageBusinessEvent(
                        action: AuditActions.SupplierCreditDrained,
                        entityName: AuditActions.SupplierCreditEntryEntityName,
                        entityId: supplierId.ToString(),
                        details: JsonSerializer.Serialize(new
                        {
                            supplierId,
                            currency,
                            drainedAmount = drainedTotal,
                            sourceSupplierPaymentId,
                        }),
                        userId: actorUserId ?? "System",
                        userName: actorUserName);
                }
            }
        }

        if (anyChange)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
