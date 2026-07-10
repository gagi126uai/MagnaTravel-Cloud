using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-044 T3b Decision 3 (2026-07-10): calcula y persiste el <see cref="BookingCancellationLineTreasuryFxAdjustment"/>
/// de un cargo del operador cuando se LIQUIDA de verdad (reembolso recibido si <c>Retenida</c>, pago al
/// proveedor si <c>FacturadaAparte</c>). Es un motor de gestion interna (sin asiento de mayor formal
/// todavia — firma contable pendiente): NO toca comprobantes, NO participa de <c>ReservaMoneyCalculator</c>
/// ni de <c>CashLedgerEntry</c> (ver el XML-doc de la entidad para el porque).
///
/// <para><b>Patron del archivo</b>: metodos <c>static</c> que reciben <see cref="AppDbContext"/> directo (no
/// inyectado), igual que <c>SupplierCancellationCircuitReader</c>/<c>SupplierCreditReconciler</c>: asi
/// <c>OperatorRefundService</c> y <c>SupplierService</c> lo llaman sin agregar una dependencia nueva al
/// constructor (que romperia los tests que instancian esos services a mano). El caller hace el
/// <c>SaveChangesAsync</c> — este motor solo hace <c>Add()</c>/marca <c>IsSuperseded</c> en memoria, para
/// participar de la MISMA transaccion que el hecho que dispara el calculo.</para>
///
/// <para><b>Por que "0 o 1 fila VIGENTE, nunca se recalcula sola"</b>: si un cargo ya tiene un ajuste vigente
/// (<c>IsSuperseded=false</c>), este motor NO crea uno nuevo — el indice unico parcial de la BD lo impediria
/// igual, pero el chequeo aca evita el roundtrip fallido. Un ajuste solo se reemplaza via
/// <see cref="SupersedeForVoidedOriginAsync"/> (M4, cuando la liquidacion de origen se anula).</para>
/// </summary>
internal static class TreasuryFxAdjustmentEngine
{
    /// <summary>
    /// Se llama cuando una <see cref="OperatorRefundAllocation"/> queda creada/confirmada contra un
    /// <see cref="BookingCancellation"/>: recorre las lineas de ESE operador y, para cada cargo
    /// <c>Retenida</c> (no <c>Withholding</c>) que YA tiene TC definitivo (la ND se emitio con conversion),
    /// calcula el delta contra <c>allocation.Refund.ExchangeRateAtReceipt</c> y lo persiste.
    ///
    /// <para>Sin efecto (no crea nada) si: el cargo no necesito conversion (<c>DefinitiveExchangeRateAtNdEmission</c>
    /// null — caso simple, mono-moneda, T3a); el TC de recibo del reembolso no es confiable (&lt;= 0, dato sin
    /// cargar); o el cargo ya tiene un ajuste vigente (idempotente ante llamadas repetidas).</para>
    /// </summary>
    public static async Task<List<BookingCancellationLineTreasuryFxAdjustment>> RegisterForRetainedChargesAsync(
        AppDbContext db, OperatorRefundAllocation allocation, ILogger? logger, CancellationToken ct)
    {
        var created = new List<BookingCancellationLineTreasuryFxAdjustment>();
        if (allocation.Refund.ExchangeRateAtReceipt <= 0m)
            return created; // sin TC de recibo confiable: nada que comparar (evita un ajuste con datos basura).

        var lines = await db.BookingCancellationLines
            .Include(l => l.OperatorCharges).ThenInclude(c => c.TargetInvoice)
            .Where(l => l.BookingCancellationId == allocation.BookingCancellationId
                     && l.SupplierId == allocation.Refund.SupplierId)
            .ToListAsync(ct);

        foreach (var line in lines)
        {
            foreach (var charge in line.OperatorCharges)
            {
                if (charge.CollectionMode != PenaltyCollectionMode.Retenida) continue;
                if (charge.Kind == OperatorChargeKind.Withholding) continue;
                if (charge.DefinitiveExchangeRateAtNdEmission is null) continue; // sin conversion: nada que reconciliar.

                var alreadyVigente = await db.BookingCancellationLineTreasuryFxAdjustments
                    .AnyAsync(a => a.OperatorChargeId == charge.Id && !a.IsSuperseded, ct);
                if (alreadyVigente) continue;

                // Seteamos la NAVIGATION (no allocation.Id): si esta allocation todavia no se persistio en esta
                // misma unidad de trabajo (HC1, un solo SaveChanges al final de AllocateAsync), su Id sigue en 0
                // y escribirlo ahora dejaria una FK invalida. EF resuelve el Id real en orden topologico al
                // guardar (mismo patron que ClientCreditService.CreateEntryAsync con la allocation).
                var adjustment = BuildAdjustment(
                    charge,
                    rateAtSettlement: allocation.Refund.ExchangeRateAtReceipt,
                    operatorRefundAllocationId: null,
                    supplierPaymentId: null);
                adjustment.OperatorRefundAllocation = allocation;
                db.BookingCancellationLineTreasuryFxAdjustments.Add(adjustment);
                created.Add(adjustment);

                logger?.LogInformation(
                    "metric:treasury_fx_adjustment_registered | OperatorChargeId={ChargeId} Mode=Retenida " +
                    "Delta={Delta} {Currency}", charge.Id, adjustment.DeltaAmount, adjustment.SettlementCurrency);
            }
        }

        return created;
    }

    /// <summary>
    /// Se llama cuando se registra el <see cref="SupplierPayment"/> que liquida el documento de un cargo
    /// <c>FacturadaAparte</c> puntual (el caller ya sabe A QUE cargo corresponde ese pago — ver el campo
    /// <c>SettlesOperatorChargePublicId</c> de <c>SupplierPaymentRequest</c>, contrato para la pantalla T4).
    ///
    /// <para>Sin efecto si: el cargo no necesito conversion (sin TC definitivo); el pago no registro un TC
    /// cruzado confiable (<c>SupplierPayment.ExchangeRate</c> null o &lt;= 0 — sin eso no hay con que comparar);
    /// o el cargo ya tiene un ajuste vigente.</para>
    /// </summary>
    public static async Task RegisterForInvoicedChargeAsync(
        AppDbContext db, BookingCancellationLineOperatorCharge charge, SupplierPayment supplierPayment,
        ILogger? logger, CancellationToken ct)
    {
        if (charge.CollectionMode != PenaltyCollectionMode.FacturadaAparte) return;
        if (charge.Kind == OperatorChargeKind.Withholding) return;
        if (charge.DefinitiveExchangeRateAtNdEmission is null) return;
        if (supplierPayment.ExchangeRate is null || supplierPayment.ExchangeRate <= 0m) return;

        var alreadyVigente = await db.BookingCancellationLineTreasuryFxAdjustments
            .AnyAsync(a => a.OperatorChargeId == charge.Id && !a.IsSuperseded, ct);
        if (alreadyVigente) return;

        // El TargetInvoice puede no venir cargado si el caller no hizo el Include: lo resolvemos si falta,
        // solo para la moneda de asentamiento (SettlementCurrency).
        var targetInvoiceMonId = charge.TargetInvoice?.MonId
            ?? await db.Invoices.Where(i => i.Id == charge.TargetInvoiceId).Select(i => i.MonId).FirstOrDefaultAsync(ct);

        var adjustment = BuildAdjustment(
            charge,
            rateAtSettlement: supplierPayment.ExchangeRate.Value,
            operatorRefundAllocationId: null,
            supplierPaymentId: supplierPayment.Id,
            targetInvoiceMonIdOverride: targetInvoiceMonId);
        db.BookingCancellationLineTreasuryFxAdjustments.Add(adjustment);

        logger?.LogInformation(
            "metric:treasury_fx_adjustment_registered | OperatorChargeId={ChargeId} Mode=FacturadaAparte " +
            "Delta={Delta} {Currency}", charge.Id, adjustment.DeltaAmount, adjustment.SettlementCurrency);
    }

    /// <summary>
    /// M4 (soft-void/reemplazo, ADR-002): marca <c>IsSuperseded=true</c> la fila vigente (si existe) de un
    /// <see cref="BookingCancellationLineTreasuryFxAdjustment"/> ligada al origen indicado (allocation o pago).
    /// NO borra la fila (historia intacta). Si despues llega un reemplazo, el caller crea la fila nueva
    /// (llamando de nuevo a <see cref="RegisterForRetainedChargesAsync"/>/<see cref="RegisterForInvoicedChargeAsync"/>)
    /// y la enlaza via <see cref="BookingCancellationLineTreasuryFxAdjustment.SupersededByAdjustmentId"/>.
    /// </summary>
    public static async Task<List<BookingCancellationLineTreasuryFxAdjustment>> SupersedeForVoidedOriginAsync(
        AppDbContext db,
        CancellationToken ct,
        int? voidedOperatorRefundAllocationId = null,
        int? voidedSupplierPaymentId = null)
    {
        var query = db.BookingCancellationLineTreasuryFxAdjustments.Where(a => !a.IsSuperseded);
        query = voidedOperatorRefundAllocationId is int allocId
            ? query.Where(a => a.OperatorRefundAllocationId == allocId)
            : query.Where(a => a.SupplierPaymentId == voidedSupplierPaymentId);

        var vigentes = await query.ToListAsync(ct);
        foreach (var row in vigentes)
            row.IsSuperseded = true;

        return vigentes;
    }

    /// <summary>
    /// Enlaza una fila VIEJA (recien marcada superseded) con la fila NUEVA que la reemplaza, si el caller ya
    /// sabe cual es (M4: reemplazo explicito, no solo anulacion sin sustituto).
    ///
    /// <para>Setea la NAVIGATION (no el Id): la fila nueva puede estar todavia sin persistir (Id == 0) en la
    /// misma unidad de trabajo del caller; EF resuelve la FK en orden topologico al guardar. Escribir el Id 0
    /// dejaria una FK invalida.</para>
    /// </summary>
    public static void LinkSupersededTo(
        BookingCancellationLineTreasuryFxAdjustment oldAdjustment,
        BookingCancellationLineTreasuryFxAdjustment newAdjustment)
    {
        oldAdjustment.SupersededByAdjustment = newAdjustment;
    }

    /// <summary>
    /// Arma (sin persistir) la fila del ajuste con las formulas de la Decision 3: <c>DeltaAmount = (RateAtSettlement
    /// - RateAtNdEmission) x ChargeAmount</c>. Positivo = a favor de la agencia (liquido a mejor TC que el que
    /// salio en la ND); negativo = en contra. <c>AssumedBy</c> queda en el default de la entidad (<c>Client</c>):
    /// no existe todavia un parametro de agencia para este eje (deuda tecnica anotada, ver ADR-044).
    ///
    /// <para><b>LIMITE CONOCIDO (menor 2, 2026-07-10) — pagos/reembolsos PARCIALES</b>: el delta usa el monto
    /// TOTAL del cargo (<c>charge.Amount</c>), asumiendo que la liquidacion cubre el cargo COMPLETO en un solo
    /// evento. Si un cargo <c>FacturadaAparte</c> se pagara en cuotas (varios <c>SupplierPayment</c> parciales), o
    /// un reembolso <c>Retenida</c> llegara fraccionado, el delta quedaria sobre-dimensionado respecto de la
    /// porcion realmente liquidada en ese evento. Hoy el flujo liquida el cargo de una (el modelo no soporta
    /// liquidacion parcial de un cargo puntual), asi que es correcto; documentado por si a futuro se habilita el
    /// pago parcial, en cuyo caso el delta debe prorratearse por el monto efectivamente liquidado.</para>
    /// </summary>
    private static BookingCancellationLineTreasuryFxAdjustment BuildAdjustment(
        BookingCancellationLineOperatorCharge charge,
        decimal rateAtSettlement,
        int? operatorRefundAllocationId,
        int? supplierPaymentId,
        string? targetInvoiceMonIdOverride = null)
    {
        var rateAtNdEmission = charge.DefinitiveExchangeRateAtNdEmission!.Value;
        var delta = Math.Round((rateAtSettlement - rateAtNdEmission) * charge.Amount, 2, MidpointRounding.AwayFromZero);
        var settlementCurrencyIso = ArcaCurrencyMapper.ToIso(
            targetInvoiceMonIdOverride ?? charge.TargetInvoice?.MonId) ?? Monedas.ARS;

        return new BookingCancellationLineTreasuryFxAdjustment
        {
            OperatorChargeId = charge.Id,
            OperatorRefundAllocationId = operatorRefundAllocationId,
            SupplierPaymentId = supplierPaymentId,
            RateAtNdEmission = rateAtNdEmission,
            RateAtSettlement = rateAtSettlement,
            ChargeAmount = charge.Amount,
            ChargeCurrency = charge.Currency,
            DeltaAmount = delta,
            SettlementCurrency = settlementCurrencyIso,
            AssumedBy = TreasuryFxAssumedBy.Client,
        };
    }
}
