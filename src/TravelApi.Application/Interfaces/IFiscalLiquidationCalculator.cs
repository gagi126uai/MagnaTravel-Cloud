using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.3 (ADR-009 §2.6, 2026-05-21): clasificador fiscal puro que decide en cual
/// de los 8 casos de la matriz del contador cae una cancelacion y devuelve los
/// montos calculados (fiscal a acreditar vs cliente a devolver) + los motivos
/// que activan revision manual.
///
/// <para><b>Por que es interface separada de <c>BookingCancellationService</c></b>:
/// permite testear la matriz 8 sin DbContext ni TestContainers. El service
/// caller (BC ConfirmAsync) decide que hacer con el resultado (persistir,
/// abrir approval, rechazar Confirm).</para>
///
/// <para><b>Puro</b>: sin DbContext, sin async, sin IO. Recibe entidades ya
/// cargadas y settings; devuelve DTO inmutable. El logger se inyecta solo para
/// avisar fallbacks (JSON malformado, regex invalida) que NO deben tirar al
/// caller — un operador con tabla rota no puede tumbar una cancelacion.</para>
///
/// <para><b>STEP 0 short-circuit (GR-003)</b>: si el modo del operador (snapshot o
/// actual) es <see cref="SupplierInvoicingMode.CommissionOnly"/>, el calculator
/// NO calcula formula y devuelve directo <c>ReviewRequiredReason.InvoicingModeCommissionOnly</c>.
/// Esperamos la respuesta del contador a la pregunta F2 round 3.</para>
///
/// <para><b>STEP 7 NO va aca</b>: si el resultado es
/// <see cref="CreditNoteKind.TotalPlusNewInvoice"/> (casos 4 y 7), el caller
/// (BC.ConfirmAsync, sub-fase FC1.3.3) es quien tira
/// <c>InvalidOperationException</c> (GR-001). Este calculator solo clasifica.</para>
/// </summary>
public interface IFiscalLiquidationCalculator
{
    /// <summary>
    /// Aplica los STEP 0..6 del ADR-009 §2.9 y devuelve la liquidacion clasificada.
    /// </summary>
    /// <param name="input">Datos ya cargados de la cancelacion en curso.</param>
    /// <param name="settings">Settings vigentes (thresholds, heuristicas).</param>
    /// <returns>DTO inmutable con montos + caso + motivos manual review + narrativa.</returns>
    FiscalLiquidationDto Calculate(FiscalLiquidationInput input, OperationalFinanceSettings settings);
}

/// <summary>
/// FC1.3 (ADR-009 §2.6): input inmutable del calculator. Todos los datos vienen
/// pre-cargados desde el service caller (BC.ConfirmAsync). El calculator NO
/// abre conexiones a BD ni hace lazy loading.
///
/// <para><b>Convencion <c>InvoicingModeAtEvent</c> nullable</b>: si la factura es
/// legacy y el snapshot no tiene el modo, el calculator usa
/// <c>Supplier.InvoicingMode</c> actual como fallback (puede tener efecto: si el
/// operador cambio de modo despues de facturar, la NC parcial usaria el modo
/// actual, no el original — riesgo documentado en ADR §6.1 test
/// <c>Calculator_LegacyInvoiceWithNullSnapshot_AndSupplierModeChanged_UsesNullDefaultBehavior</c>).</para>
///
/// <para><b>Currency</b>: agregado al record (no figura literal en ADR §2.6 pero
/// el STEP 1 lo requiere para evaluar <c>MultiCurrency</c> + popular el DTO de
/// salida). Espera ISO 4217 ("ARS", "USD", "EUR"...).</para>
/// </summary>
/// <param name="OriginatingInvoice">Factura original que se va a anular/acreditar.</param>
/// <param name="Items">Items de la factura origen ya cargados (con <c>IsRefundable</c>).</param>
/// <param name="Supplier">Operador con <c>InvoicingMode</c> + <c>PenaltyPolicyJson</c>.</param>
/// <param name="InvoicingModeAtEvent">Snapshot del modo al momento de facturar. Null = legacy.</param>
/// <param name="OriginalInvoiceAmount">Monto total de la factura original (= <c>Invoice.ImporteTotal</c>).</param>
/// <param name="CancellationAmount">Monto a cancelar (usualmente igual al total para cancel total).</param>
/// <param name="OperatorPenaltyAmount">Penalidad del operador ingresada por vendedor (manual o de tabla).</param>
/// <param name="RetentionNatureChangedByUser">Checkbox manual vendedor (caso 7).</param>
/// <param name="OriginalInvoiceUnclearByUser">Checkbox manual vendedor (caso 4).</param>
/// <param name="Currency">Moneda de la factura origen (ISO 4217). Default "ARS".</param>
public record FiscalLiquidationInput(
    Invoice OriginatingInvoice,
    IReadOnlyList<InvoiceItem> Items,
    Supplier Supplier,
    SupplierInvoicingMode? InvoicingModeAtEvent,
    decimal OriginalInvoiceAmount,
    decimal CancellationAmount,
    decimal OperatorPenaltyAmount,
    bool RetentionNatureChangedByUser,
    bool OriginalInvoiceUnclearByUser,
    string Currency = "ARS");
