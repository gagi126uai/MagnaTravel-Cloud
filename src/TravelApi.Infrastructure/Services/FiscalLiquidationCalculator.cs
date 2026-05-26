using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3 (ADR-009 §2.6 + §2.9, 2026-05-21): implementacion del clasificador
/// fiscal puro. Aplica los STEP 0..6 sobre el input + settings y devuelve un
/// <see cref="FiscalLiquidationDto"/> inmutable.
///
/// <para><b>NO toca BD, NO es async</b>. Recibe todo pre-cargado. Logger se usa
/// solo para warnings de fallback (JSON malformado, regex invalida) — nunca
/// tira al caller por error de datos del operador o de settings.</para>
///
/// <para><b>Orden de aplicacion</b>:
///  <list type="number">
///   <item>STEP 0 — short-circuit <see cref="SupplierInvoicingMode.CommissionOnly"/> (GR-003).</item>
///   <item>STEP 1 — disparadores acumulativos (Factura A, no reintegrables, NC en cadena, moneda, etc.).</item>
///   <item>STEP 2 — heuristicas caso 4 (factura confusa) — DESACTIVADAS por default (RH-008).</item>
///   <item>STEP 3 — calculo de montos (modo TotalToCustomer) + flag GR-006 si penalty &gt; 0.</item>
///   <item>STEP 4 — disparadores de monto contra thresholds.</item>
///   <item>STEP 5 — clasificacion del caso 1..8 (informativo).</item>
///   <item>STEP 6 — <see cref="CreditNoteKind"/>.</item>
///  </list>
/// El STEP 7 (rechazo de <see cref="CreditNoteKind.TotalPlusNewInvoice"/> con
/// <c>InvalidOperationException</c>) lo aplica el caller, NO este service.</para>
/// </summary>
public class FiscalLiquidationCalculator : IFiscalLiquidationCalculator
{
    // Tolerancia para comparaciones decimales (INV-FC1.3-005: la suma de partes
    // debe igualar el total con error <= 1 centavo). Igual valor que el ADR.
    private const decimal SumTolerance = 0.01m;

    // TipoComprobante AFIP: 1 = Factura A. Cliente RI obliga revision manual.
    // Constantes en lugar de magic numbers para que el lector entienda sin abrir el ADR.
    private const int TipoFacturaA = 1;

    private readonly ILogger<FiscalLiquidationCalculator> _logger;

    public FiscalLiquidationCalculator(ILogger<FiscalLiquidationCalculator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public FiscalLiquidationDto Calculate(FiscalLiquidationInput input, OperationalFinanceSettings settings)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        // Determinar el modo del operador: snapshot al momento de facturar tiene
        // prioridad; si la factura es legacy y no tiene snapshot, usamos el actual.
        // Esto puede generar inconsistencia si el operador cambio de modo despues
        // — flag LegacyInvoice (cuando esta activado) lo cubre.
        var mode = input.InvoicingModeAtEvent ?? input.Supplier.InvoicingMode;

        // ========================================================
        // STEP 0 — EARLY EXIT por modo CommissionOnly (GR-003)
        // ========================================================
        // El modo CommissionOnly cambia toda la formula fiscal (la NC parcial
        // depende solo de la comision facturada). Hasta que el contador
        // responda la pregunta F2 round 3, derivamos TODO ese caso a manual.
        // No corremos formula ni clasificamos por monto.
        if (mode == SupplierInvoicingMode.CommissionOnly)
        {
            var commissionCase = input.CancellationAmount >= input.OriginalInvoiceAmount
                ? PartialCreditNoteCase.Case6_CommissionOnlyFull
                : PartialCreditNoteCase.Case5_CommissionOnlyPartial;

            return new FiscalLiquidationDto(
                OriginalInvoiceAmount: input.OriginalInvoiceAmount,
                CancellationAmount: input.CancellationAmount,
                OperatorPenaltyAmount: input.OperatorPenaltyAmount,
                NonRefundableItemsAmount: 0m,
                FiscalAmountToCredit: 0m,
                AmountToRefundCustomer: 0m,
                FinalNetInvoiced: input.OriginalInvoiceAmount,
                Case: commissionCase,
                Kind: CreditNoteKind.PartialOnOriginal,
                ReviewRequiredReason: ReviewRequiredReason.InvoicingModeCommissionOnly,
                Currency: input.Currency,
                ClassificationExplanation:
                    "Modo CommissionOnly diferido a revision manual hasta respuesta F2 contador (GR-003). " +
                    "El calculator NO corre la formula. Admin define la liquidacion manualmente.");
        }

        // ========================================================
        // STEP 1 — disparadores acumulativos
        // ========================================================
        var reason = ReviewRequiredReason.None;

        // Factura A => cliente RI, revision manual obligatoria (caso 8 del contador).
        if (input.OriginatingInvoice.TipoComprobante == TipoFacturaA)
        {
            reason |= ReviewRequiredReason.CustomerIsRiOrFacturaA;
        }

        // Suma de items con IsRefundable=false (cargo gestion, seguros, anticipos).
        // No entran en el monto fiscal a acreditar (G1).
        var nonRefundableTotal = SumNonRefundableItems(input.Items);
        if (nonRefundableTotal > 0)
        {
            reason |= ReviewRequiredReason.HasNonRefundableItems;
        }

        // Factura origen es ya una NC en cadena (OriginalInvoiceId no null en una Invoice
        // que ya es NC apuntando a otra factura). Caso raro pero existente.
        if (input.OriginatingInvoice.OriginalInvoiceId is not null)
        {
            reason |= ReviewRequiredReason.Other;
        }

        // G-F2-C (FC1.3 Fase 2, 2026-05-22): factura con tributos provinciales
        // (IIBB, percepciones de Capital, percepciones de provincia). El prorrateo
        // de estos tributos NO esta modelado en Fase 2 — el contador definira el
        // criterio en una sub-fase posterior. Mientras tanto, FRENAMOS la
        // auto-emision y derivamos a revision manual obligatoria (admin decide
        // si emite manualmente con tributos prorrateados o reformula la operacion).
        //
        // Detalle de implementacion: Invoice.Tributes es una coleccion EF (default
        // List<InvoiceTribute>() en el constructor de Invoice, nunca null en la
        // practica). El null-check defensivo cubre el caso de tests u objetos
        // construidos por reflexion.
        //
        // IMPORTANTE para el caller: la coleccion Tributes debe venir cargada
        // (Include + ThenInclude en EF). Si NO se carga y no hay lazy proxies
        // —el caso de este proyecto—, .Any() devuelve false aunque la BD tenga
        // tributos. Los 2 callers FC1.3 (ConfirmAsync y EditLiquidationAsync de
        // BookingCancellationService) cargan Tributes via .ThenInclude(i => i.Tributes).
        // Cualquier nuevo caller debe hacer lo mismo.
        if (input.OriginatingInvoice.Tributes != null && input.OriginatingInvoice.Tributes.Any())
        {
            reason |= ReviewRequiredReason.HasProvincialTributes;

            // B-003 fix (2026-05-26): log structured para auditar disparos de
            // G-F2-C en prod. Si un admin reporta "no salio a manual review pero
            // la factura tenia IIBB", buscamos en Serilog este evento para
            // confirmar si el flag se disparo o no. InvoiceId + count son los
            // datos minimos para reproducir/debuggear.
            _logger.LogInformation(
                "FiscalLiquidationCalculator: tributos provinciales detectados, disparando HasProvincialTributes. " +
                "InvoiceId={InvoiceId} TributesCount={TributesCount}",
                input.OriginatingInvoice.Id, input.OriginatingInvoice.Tributes.Count);
        }

        // Moneda distinta de ARS — Fase 2 implementa prorrateo multimoneda.
        // string.Equals con InvariantCulture evita falsos negativos por casing.
        if (!string.Equals(input.Currency, "ARS", StringComparison.OrdinalIgnoreCase))
        {
            reason |= ReviewRequiredReason.MultiCurrency;
        }

        // Heuristica LegacyInvoice — DESACTIVADA por default (RH-008).
        // Solo dispara si el contador habilito el setting Y el setting esta no-null
        // Y la factura es anterior al deploy.
        if (!string.IsNullOrWhiteSpace(settings.GenericDescriptionPatterns)
            && settings.Fc13DeployDate.HasValue
            && input.OriginatingInvoice.CreatedAt < settings.Fc13DeployDate.Value)
        {
            reason |= ReviewRequiredReason.LegacyInvoice;
        }

        // Snapshot del modo distinto al actual = caso 7 (retencion cambia naturaleza).
        // Solo dispara si tenemos snapshot Y el modo cambio.
        if (input.InvoicingModeAtEvent.HasValue
            && input.InvoicingModeAtEvent.Value != input.Supplier.InvoicingMode)
        {
            reason |= ReviewRequiredReason.RetentionChangesNature;
        }

        if (input.RetentionNatureChangedByUser)
        {
            reason |= ReviewRequiredReason.RetentionChangesNature;
        }

        if (input.OriginalInvoiceUnclearByUser)
        {
            reason |= ReviewRequiredReason.OriginalInvoiceUnclear;
        }

        // ========================================================
        // STEP 2 — heuristicas caso 4 (factura confusa) — RH-008
        // ========================================================
        // Las heuristicas SOLO se evaluan si el setting tiene un regex configurado.
        // Default (settings.GenericDescriptionPatterns = string.Empty) => skip todo.
        // Esto evita falsos positivos en historicos legacy.
        if (!string.IsNullOrWhiteSpace(settings.GenericDescriptionPatterns))
        {
            ApplyCaseFourHeuristics(input, settings, ref reason);
        }

        // ========================================================
        // STEP 3 — calcular liquidacion (modo TotalToCustomer)
        // ========================================================
        // Penalty del operador: ingresado por vendedor. Validamos no negativo
        // (defense in depth — la UI deberia validar antes).
        var penalty = input.OperatorPenaltyAmount;
        if (penalty < 0)
        {
            // No tiramos — clampeamos a 0 + warning. Una penalty negativa seria
            // un bug de UI/service caller, no del operador. Mejor calculo conservador.
            _logger.LogWarning(
                "FiscalLiquidationCalculator: OperatorPenaltyAmount negativo ({Penalty}) clampeado a 0 para Invoice {InvoiceId}",
                penalty, input.OriginatingInvoice.Id);
            penalty = 0m;
        }

        // GR-006: caso 3 con penalty > 0 en modo TotalToCustomer requiere clarificacion
        // del contador (pregunta F4 round 3). Calculamos suponiendo "penalty resta"
        // pero flagueamos para review.
        if (penalty > 0 && mode == SupplierInvoicingMode.TotalToCustomer)
        {
            reason |= ReviewRequiredReason.PenaltyResetUncertainInResellerMode;
        }

        // Try/catch del JSON malformado del operador (RH-014). El calculator NO
        // calcula penalty desde la tabla — usa el monto manual que el vendedor
        // ingreso. Pero validamos por las dudas que el JSON sea parseable; si no,
        // log warning para que el admin lo corrija y el vendedor sepa que la
        // sugerencia visual de la tabla no estaba andando.
        TryValidatePenaltyPolicyJson(input.Supplier);

        // Formula central: lo que sale en la NC parcial = total - no reintegrables - penalty.
        // Es la formula del contador para modo TotalToCustomer (Factura B/C estandar).
        var fiscalAmountToCredit = input.OriginalInvoiceAmount - nonRefundableTotal - penalty;

        // INV-FC1.3-005: la suma de las partes debe igualar el total con tolerancia.
        // Si penalty + no reintegrables > total, fiscalAmountToCredit queda negativo
        // — eso seria un input invalido (vendedor cargo penalty mas alta que la factura).
        if (fiscalAmountToCredit < -SumTolerance)
        {
            throw new BusinessInvariantViolationException(
                message:
                    $"La suma de penalidades ({penalty}) y items no reintegrables ({nonRefundableTotal}) " +
                    $"supera el total de la factura ({input.OriginalInvoiceAmount}). " +
                    $"Esto implicaria un monto fiscal a acreditar negativo ({fiscalAmountToCredit}) — invalido. " +
                    $"Verificar inputs de penalty y categorias de items.",
                invariantCode: "INV-FC1.3-005");
        }

        // Si quedo levemente negativo dentro de la tolerancia, redondeamos a 0.
        // Mejor un calculo conservador que un valor "negativo chico" que confunda en UI.
        if (fiscalAmountToCredit < 0m)
        {
            fiscalAmountToCredit = 0m;
        }

        // En este modo (TotalToCustomer + sin retenciones cliente) lo fiscal = lo a devolver.
        // En Fase 2, cuando se agreguen retenciones fiscales del cliente (Fact A), estos
        // dos valores se separan.
        var amountToRefundCustomer = fiscalAmountToCredit;

        // Saldo facturado vivo despues de la NC parcial.
        var finalNetInvoiced = input.OriginalInvoiceAmount - fiscalAmountToCredit;

        // ========================================================
        // STEP 4 — disparadores de monto
        // ========================================================
        // Orden importante: el threshold de "accounting" gana sobre "admin" (G5).
        // Si el setting de accounting es null, no aplica ese tier.
        if (settings.PartialNcAccountingReviewThreshold.HasValue
            && fiscalAmountToCredit > settings.PartialNcAccountingReviewThreshold.Value)
        {
            reason |= ReviewRequiredReason.AmountAboveAccountingThreshold;
        }
        else if (fiscalAmountToCredit > settings.PartialNcAdminReviewThreshold)
        {
            reason |= ReviewRequiredReason.AmountAboveAdminThreshold;
        }
        else if (fiscalAmountToCredit > settings.PartialNcAutoApprovalThreshold)
        {
            // Entre auto-approval y admin review: tambien dispara admin review
            // (asi el contador queda contento con cualquier monto > 500k que el admin lo mire).
            reason |= ReviewRequiredReason.AmountAboveAdminThreshold;
        }

        // ========================================================
        // STEP 5 — clasificar caso 1..8 (informativo)
        // ========================================================
        // Orden de prioridad segun ADR §2.9 STEP 5. La primera regla que matchea gana.
        var computedCase = ClassifyCase(input, reason, penalty);

        // ========================================================
        // STEP 6 — CreditNoteKind
        // ========================================================
        // Casos 4 y 7 son los unicos que requieren NC total + factura nueva.
        // Fase 1 RECHAZA estos en el caller (GR-001). Aca solo marcamos.
        var kind = (computedCase == PartialCreditNoteCase.Case4_OriginalInvoiceUnclear
                    || computedCase == PartialCreditNoteCase.Case7_RetentionChangesNature)
            ? CreditNoteKind.TotalPlusNewInvoice
            : CreditNoteKind.PartialOnOriginal;

        var explanation = BuildClassificationExplanation(computedCase, reason, kind);

        return new FiscalLiquidationDto(
            OriginalInvoiceAmount: input.OriginalInvoiceAmount,
            CancellationAmount: input.CancellationAmount,
            OperatorPenaltyAmount: penalty,
            NonRefundableItemsAmount: nonRefundableTotal,
            FiscalAmountToCredit: fiscalAmountToCredit,
            AmountToRefundCustomer: amountToRefundCustomer,
            FinalNetInvoiced: finalNetInvoiced,
            Case: computedCase,
            Kind: kind,
            ReviewRequiredReason: reason,
            Currency: input.Currency,
            ClassificationExplanation: explanation);
    }

    /// <summary>
    /// Suma los <c>Total</c> de los items con <c>IsRefundable=false</c>. Los items
    /// sin asignar (lista vacia) cuentan como 0 — caller debe pasar la lista cargada.
    /// </summary>
    private static decimal SumNonRefundableItems(IReadOnlyList<InvoiceItem> items)
    {
        if (items is null || items.Count == 0) return 0m;

        decimal total = 0m;
        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsRefundable)
            {
                total += items[i].Total;
            }
        }
        return total;
    }

    /// <summary>
    /// STEP 2: evalua las 3 sub-heuristicas de caso 4 SOLO si el regex configurado
    /// es valido. Si el regex esta malformado, log warning + skip (no tira).
    /// </summary>
    private void ApplyCaseFourHeuristics(
        FiscalLiquidationInput input,
        OperationalFinanceSettings settings,
        ref ReviewRequiredReason reason)
    {
        // Compilar el regex con timeout corto (proteccion contra catastrophic backtracking).
        // Si malformed, log + return (no tiramos al caller).
        Regex? regex;
        try
        {
            regex = new Regex(
                settings.GenericDescriptionPatterns,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex,
                "FiscalLiquidationCalculator: GenericDescriptionPatterns regex malformado ({Pattern}). " +
                "Skip heuristica caso 4. Corregir setting OperationalFinanceSettings.GenericDescriptionPatterns.",
                settings.GenericDescriptionPatterns);
            return;
        }

        // Sub-heuristica 1: factura con un solo item Y descripcion matchea pattern generico.
        if (input.Items.Count == 1)
        {
            try
            {
                if (regex.IsMatch(input.Items[0].Description ?? string.Empty))
                {
                    reason |= ReviewRequiredReason.OriginalInvoiceUnclear;
                }
            }
            catch (RegexMatchTimeoutException ex)
            {
                // Si el match toma > 100ms es probable catastrophic backtracking.
                _logger.LogWarning(ex,
                    "FiscalLiquidationCalculator: regex match timeout sobre Description={Description}. Skip.",
                    input.Items[0].Description);
            }
        }

        // Sub-heuristica 2: > 50% del total tiene items sin trazabilidad al servicio origen
        // (SourceServicioReservaId = null indica que no sabemos a que reserva pertenecen).
        if (input.OriginalInvoiceAmount > 0)
        {
            decimal totalSinOrigen = 0m;
            for (int i = 0; i < input.Items.Count; i++)
            {
                if (input.Items[i].SourceServicioReservaId is null)
                {
                    totalSinOrigen += input.Items[i].Total;
                }
            }

            if (totalSinOrigen > input.OriginalInvoiceAmount * 0.5m)
            {
                reason |= ReviewRequiredReason.OriginalInvoiceUnclear;
            }
        }

        // Sub-heuristica 3: la suma de IVA de los items no cierra con el IVA total
        // de la factura (tolerancia $0.50). Indica datos inconsistentes.
        decimal sumIva = 0m;
        for (int i = 0; i < input.Items.Count; i++)
        {
            sumIva += input.Items[i].ImporteIva;
        }

        if (Math.Abs(sumIva - input.OriginatingInvoice.ImporteIva) > 0.50m)
        {
            reason |= ReviewRequiredReason.OriginalInvoiceUnclear;
        }
    }

    /// <summary>
    /// Intenta parsear <c>Supplier.PenaltyPolicyJson</c> para detectar si esta
    /// malformado. NO usa el resultado para nada (el vendedor ya ingreso el monto
    /// manual o desde la tabla via UI). Si malformed, log warning para que el admin
    /// corrija — es proteccion preventiva (RH-014).
    /// </summary>
    private void TryValidatePenaltyPolicyJson(Supplier supplier)
    {
        if (string.IsNullOrWhiteSpace(supplier.PenaltyPolicyJson)) return;

        try
        {
            // Solo deserializamos a JsonDocument para validar sintaxis. No mapeamos a
            // un tipo concreto porque NO usamos el contenido aca.
            using var _ = JsonDocument.Parse(supplier.PenaltyPolicyJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "FiscalLiquidationCalculator: Supplier {SupplierId} ({SupplierName}) tiene PenaltyPolicyJson malformado. " +
                "El calculator usa el monto manual del vendedor (fallback). Revisar tabla del operador en panel admin.",
                supplier.Id, supplier.Name);
        }
    }

    /// <summary>
    /// STEP 5: clasifica el caso 1..8 segun el ADR §2.9. Orden de prioridad
    /// (primera que matchea gana):
    ///  1. Factura A => Case8
    ///  2. OriginalInvoiceUnclear => Case4 (sera rechazado por kind=TotalPlusNewInvoice)
    ///  3. RetentionChangesNature => Case7 (idem)
    ///  4. penalty &gt; 0 => Case3
    ///  5. cancellation total == original total => Case2
    ///  6. parcial sin penalty => Case1
    /// Casos 5 y 6 ya fueron retornados en STEP 0 (CommissionOnly short-circuit).
    /// </summary>
    private static PartialCreditNoteCase ClassifyCase(
        FiscalLiquidationInput input,
        ReviewRequiredReason reason,
        decimal penalty)
    {
        if (reason.HasFlag(ReviewRequiredReason.CustomerIsRiOrFacturaA))
            return PartialCreditNoteCase.Case8_FacturaA;

        if (reason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear))
            return PartialCreditNoteCase.Case4_OriginalInvoiceUnclear;

        if (reason.HasFlag(ReviewRequiredReason.RetentionChangesNature))
            return PartialCreditNoteCase.Case7_RetentionChangesNature;

        if (penalty > 0)
            return PartialCreditNoteCase.Case3_FullCancellationWithPenalty;

        // Cancelacion total vs parcial: comparamos con tolerancia para evitar
        // cuestiones de centavos por redondeo.
        if (Math.Abs(input.CancellationAmount - input.OriginalInvoiceAmount) <= SumTolerance)
            return PartialCreditNoteCase.Case2_FullCancellationNoRetention;

        return PartialCreditNoteCase.Case1_PartialCancellationNoRetention;
    }

    /// <summary>
    /// Construye un texto explicativo human-readable para audit y UI. Incluye
    /// el nombre del caso + los flags activos.
    ///
    /// <para>El nombre alinea con <c>FiscalLiquidationDto.ClassificationExplanation</c>
    /// y con el campo <c>Metadata.classificationExplanation</c> del JSON del approval
    /// (ADR-009 §2.3.1 / §2.7).</para>
    /// </summary>
    private static string BuildClassificationExplanation(
        PartialCreditNoteCase computedCase,
        ReviewRequiredReason reason,
        CreditNoteKind kind)
    {
        var flagsText = reason == ReviewRequiredReason.None
            ? "Sin disparadores de revision manual — auto-emite si el caller lo permite."
            : $"Disparadores activos: {reason}.";

        var kindText = kind == CreditNoteKind.TotalPlusNewInvoice
            ? "Requiere NC total + factura nueva (Fase 2). Fase 1 lo RECHAZA en el caller."
            : "NC parcial sobre la factura original.";

        return $"Caso clasificado: {computedCase}. Tipo: {kind}. {kindText} {flagsText}";
    }
}
