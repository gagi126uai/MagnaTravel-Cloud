using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3 (ADR-009 §2.3.1, 2026-05-21): DTO transitorio que devuelve el
/// <see cref="TravelApi.Application.Interfaces.IFiscalLiquidationCalculator"/>.
///
/// <para><b>NO se persiste en BD en Fase 1 (GR-004)</b>. Solo se persisten 5 campos
/// summary en <c>BookingCancellation</c> (<c>CreditNoteKind</c>,
/// <c>ReviewRequiredReason</c>, <c>LiquidationComputedAt</c>,
/// <c>LiquidationComputedByUserId/Name</c>). El detalle entero se serializa al
/// <c>ApprovalRequest.Metadata</c> JSON SOLO cuando hay revision manual.</para>
///
/// <para><b>Inmutable</b> (record con init-only): el caller decide que hacer con
/// el resultado pero NO lo muta. Para "editar" la liquidacion (G3), el admin
/// invoca <c>EditLiquidationAsync</c> que vuelve a correr el calculator con
/// inputs modificados — se descarta el DTO anterior y se persiste el nuevo en
/// el Metadata JSON con un entry en <c>edits[]</c>.</para>
///
/// <para><b>Ejemplo pelotudo</b>: factura $1.000.000 (1M) Tipo A, cancelacion total,
/// penalidad operador $200.000, items no reintegrables $50.000 (cargo gestion $30k + seguro $20k).
///  - <c>OriginalInvoiceAmount</c> = 1.000.000
///  - <c>CancellationAmount</c> = 1.000.000 (cancelacion total — igual al original)
///  - <c>OperatorPenaltyAmount</c> = 200.000
///  - <c>NonRefundableItemsAmount</c> = 50.000
///  - <c>FiscalAmountToCredit</c> = 750.000 (lo que sale en la NC parcial)
///  - <c>AmountToRefundCustomer</c> = 750.000 (igual al fiscal en este caso)
///  - <c>FinalNetInvoiced</c> = 250.000 (lo que queda vivo de la factura original)
///  - <c>Case</c> = Case8_FacturaA
///  - <c>Kind</c> = PartialOnOriginal
///  - <c>ReviewRequiredReason</c> = CustomerIsRiOrFacturaA | HasNonRefundableItems
/// </para>
///
/// <para><b>Por que <c>CancellationAmount</c> esta separado de <c>OriginalInvoiceAmount</c></b>:
/// en general son iguales (cancelacion total), pero podrian diferir si la
/// cancelacion es parcial sobre un sub-monto del comprobante. El admin lo necesita
/// para auditar el monto cancelado real (input) vs el monto fiscal calculado (output).</para>
///
/// <para><b>Sobre <c>ClassificationExplanation</c></b>: es el texto human-readable que
/// el admin lee en la UI de manual review. Antes se llamaba <c>Narrative</c>; se
/// renombro para alinear con el schema JSON del ADR §2.3.1 / §2.7 (Metadata.classificationExplanation).</para>
/// </summary>
/// <param name="OriginalInvoiceAmount">Total de la factura original (sin tocar).</param>
/// <param name="CancellationAmount">Monto a cancelar (input del flow). En general = OriginalInvoiceAmount, pero puede ser un sub-monto en cancelaciones parciales.</param>
/// <param name="OperatorPenaltyAmount">Penalidad cobrada por el operador (ingresada por vendedor).</param>
/// <param name="NonRefundableItemsAmount">Suma de items con <c>IsRefundable=false</c>.</param>
/// <param name="FiscalAmountToCredit">Monto que sale en la NC parcial (lo que pierde causa fiscal).</param>
/// <param name="AmountToRefundCustomer">Monto a devolver al cliente. Igual al fiscal cuando no hay split.</param>
/// <param name="FinalNetInvoiced">Saldo facturado vivo despues de la NC parcial.</param>
/// <param name="Case">Cual de los 8 casos de la matriz aplico.</param>
/// <param name="Kind">PartialOnOriginal (casos 1,2,3,5,6,8) o TotalPlusNewInvoice (casos 4,7 — Fase 1 rechaza).</param>
/// <param name="ReviewRequiredReason">Bitflag con los motivos para revision manual. <c>None</c> = auto-emite.</param>
/// <param name="Currency">Moneda de la factura (ISO 4217).</param>
/// <param name="ClassificationExplanation">Texto explicativo human-readable. Util para audit y UI. Alineado con ADR §2.3.1 / §2.7 schema JSON Metadata.</param>
public record FiscalLiquidationDto(
    decimal OriginalInvoiceAmount,
    decimal CancellationAmount,
    decimal OperatorPenaltyAmount,
    decimal NonRefundableItemsAmount,
    decimal FiscalAmountToCredit,
    decimal AmountToRefundCustomer,
    decimal FinalNetInvoiced,
    PartialCreditNoteCase Case,
    CreditNoteKind Kind,
    ReviewRequiredReason ReviewRequiredReason,
    string Currency,
    string ClassificationExplanation);
