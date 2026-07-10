using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T3b Decision 3 (2026-07-10): registro AUDITABLE (gestion interna, sin asiento de mayor formal
/// todavia — firma contable pendiente, ver el Addendum T3b) de la diferencia entre el TC con que se emitio la
/// Nota de Debito de un cargo de operador (<see cref="BookingCancellationLineOperatorCharge.DefinitiveExchangeRateAtNdEmission"/>)
/// y el TC real con que ese cargo se LIQUIDA de verdad (reembolso recibido si es <c>Retenida</c>, o pago al
/// proveedor si es <c>FacturadaAparte</c>).
///
/// <para><b>Por que existe (el problema que resuelve)</b>: la ND sale con un TC "del dia de emision" (Decision
/// 2). La plata REAL del cargo se mueve DESPUES (dias/semanas), a otro TC. Esa diferencia es un resultado de
/// tesoreria (ganancia o perdida por el desfasaje cambiario), NO un error de facturacion — el comprobante
/// nunca se recotiza. Sin este registro esa diferencia queda invisible (mismo antipatron que P2 vino a cerrar
/// para la multa retenida).</para>
///
/// <para><b>Por que NO es una fila de <see cref="CashLedgerEntry"/></b>: no entra ni sale plata adicional por
/// este hecho — es una reconciliacion de VALUACION entre dos numeros que ya existen. <c>CashLedgerEntry</c>
/// modela EXCLUSIVAMENTE movimientos de caja reales con sus 5 origenes tipados cerrados; forzar un 6to origen
/// aca mezclaria un concepto sin contabilizacion formal aprobada con el libro de caja real.</para>
///
/// <para><b>0 o 1 fila VIGENTE por cargo</b> (indice unico filtrado a <see cref="IsSuperseded"/> = false): no
/// hay razon de negocio para mas de un ajuste vigente sobre el mismo cargo. Si la liquidacion de origen se
/// anula (soft-void ADR-002) y se reemplaza, la fila vieja se marca superseded y se crea una nueva (M4).</para>
/// </summary>
public class BookingCancellationLineTreasuryFxAdjustment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al cargo del operador cuya liquidacion genero este ajuste. Cascade: el ajuste no tiene sentido sin su cargo.</summary>
    public int OperatorChargeId { get; set; }
    public BookingCancellationLineOperatorCharge OperatorCharge { get; set; } = null!;

    // ====================================================================================
    // B1 del Addendum T3b: DOS origenes alternativos de la liquidacion real, invariante
    // "exactamente uno no-null" (CHECK SQL, mismo patron que CashLedgerEntry). Cual de los
    // dos se usa depende de PenaltyCollectionMode del cargo: Retenida -> la allocation del
    // reembolso del operador; FacturadaAparte -> el pago que la agencia le hizo al operador
    // por ese cargo puntual.
    // ====================================================================================

    /// <summary>Origen para cargos <see cref="PenaltyCollectionMode.Retenida"/>: la imputacion del reembolso del operador que liquido este cargo.</summary>
    public int? OperatorRefundAllocationId { get; set; }
    public OperatorRefundAllocation? OperatorRefundAllocation { get; set; }

    /// <summary>Origen para cargos <see cref="PenaltyCollectionMode.FacturadaAparte"/>: el pago a proveedor que liquido el documento del cargo.</summary>
    public int? SupplierPaymentId { get; set; }
    public SupplierPayment? SupplierPayment { get; set; }

    /// <summary>TC DEFINITIVO con que salio la Nota de Debito (copia congelada de <see cref="BookingCancellationLineOperatorCharge.DefinitiveExchangeRateAtNdEmission"/> al momento del calculo).</summary>
    [Column(TypeName = "numeric(18,6)")]
    public decimal RateAtNdEmission { get; set; }

    /// <summary>
    /// TC real de la liquidacion: para <c>Retenida</c>, <c>OperatorRefundReceived.ExchangeRateAtReceipt</c>;
    /// para <c>FacturadaAparte</c>, el <c>SupplierPayment.ExchangeRate</c> (o 1 si el pago no fue cruzado —
    /// pago y cargo en la misma moneda de liquidacion).
    /// </summary>
    [Column(TypeName = "numeric(18,6)")]
    public decimal RateAtSettlement { get; set; }

    /// <summary>Foto de <see cref="BookingCancellationLineOperatorCharge.Amount"/> al momento del calculo (no vivo: si el cargo cambiara despues, este ajuste ya calculado no se recalcula solo).</summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal ChargeAmount { get; set; }

    /// <summary>Foto de <see cref="BookingCancellationLineOperatorCharge.Currency"/> al momento del calculo.</summary>
    [MaxLength(3)]
    public string ChargeCurrency { get; set; } = Monedas.ARS;

    /// <summary>
    /// <c>(RateAtSettlement - RateAtNdEmission) x ChargeAmount</c>, redondeado a 2 decimales. Positivo = a favor
    /// de la agencia (el TC de liquidacion resulto mejor que el de emision); negativo = en contra.
    /// </summary>
    [Column(TypeName = "numeric(18,2)")]
    public decimal DeltaAmount { get; set; }

    /// <summary>Moneda de la Nota de Debito del cliente (la misma que la de <see cref="BookingCancellationLineOperatorCharge.TargetInvoice"/>).</summary>
    [MaxLength(3)]
    public string SettlementCurrency { get; set; } = Monedas.ARS;

    /// <summary>Snapshot (no lectura en vivo) de quien asume esta diferencia, al momento del calculo. Ver <see cref="TreasuryFxAssumedBy"/>.</summary>
    public TreasuryFxAssumedBy AssumedBy { get; set; } = TreasuryFxAssumedBy.Client;

    // ====================================================================================
    // M4 del Addendum T3b: soft-void/reemplazo de la liquidacion de origen (ADR-002). El
    // ajuste NO se borra: se marca superseded y, si hay reemplazo, se enlaza a la fila nueva.
    // ====================================================================================

    /// <summary>true = la allocation/pago de origen se anulo (soft-void): este ajuste ya NO es vigente. La fila se conserva (historia), pero el indice unico filtrado la excluye.</summary>
    public bool IsSuperseded { get; set; }

    /// <summary>Si hubo reemplazo (nueva allocation/pago), FK a la fila de ajuste recalculada que la sucede.</summary>
    public int? SupersededByAdjustmentId { get; set; }
    public BookingCancellationLineTreasuryFxAdjustment? SupersededByAdjustment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(1000)]
    public string? Notes { get; set; }
}
