using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (2026-07-08): regla UNICA y PURA que deriva el
/// <see cref="OperatorPenaltySituationState"/> a partir de los campos sueltos de la cancelacion vigente. Es
/// testeable sin base (mismo estilo que <c>CancellationPenaltyRules</c>): recibe primitivos, no la entidad ni el
/// DbContext, asi la matriz de estados se prueba caso por caso.
///
/// <para><b>Por que una funcion de dominio y no un <c>switch</c> en el service</b>: el mapeo "que paso mostrar"
/// gobierna que boton ve el usuario en la ficha. Centralizarlo evita que el front (o dos lectores del backend)
/// reimplementen la matriz y diverjan. El service solo arma los primitivos y llama aca.</para>
/// </summary>
public static class OperatorPenaltySituationRules
{
    /// <summary>
    /// Campos sueltos de la cancelacion vigente que deciden el paso de la multa. Se pasan primitivos (no la entidad
    /// <c>BookingCancellation</c>) para que la regla sea pura y testeable, y para no acoplar el Dominio a EF.
    /// </summary>
    /// <param name="HasLiveCancellation">
    /// true si la reserva tiene una cancelacion vigente (no abortada). false = la ficha no muestra el paso (None).
    /// </param>
    /// <param name="PenaltyStatus">Estado de la penalidad del operador (Estimated / Confirmed / Waived).</param>
    /// <param name="DebitNoteStatus">Estado observable de la Nota de Debito de la multa.</param>
    /// <param name="HasDebitNoteInvoice">true si ya hay una factura de ND vinculada (DebitNoteInvoiceId != null).</param>
    /// <param name="IsPendingDecision">
    /// true si la multa esta PENDIENTE de decidir AHORA (misma regla canonica que gobierna el boton "Confirmar multa /
    /// Cerrar sin multa": flag ON + NC total con CAE + penalidad aun Estimated + sin ND en juego). Se calcula afuera
    /// (reusa <c>EvaluateCanConfirmPenalty</c>) para no duplicar esa logica aca.
    /// </param>
    public readonly record struct Fields(
        bool HasLiveCancellation,
        PenaltyStatus PenaltyStatus,
        DebitNoteStatus DebitNoteStatus,
        bool HasDebitNoteInvoice,
        bool IsPendingDecision);

    /// <summary>
    /// Deriva el paso en que esta la multa del operador. Orden de decision de arriba hacia abajo (flujo lineal):
    /// primero los terminales (sin cancelacion, cerrada sin multa), despues el desglose de "confirmada" segun la ND,
    /// y por ultimo el pendiente-de-decidir.
    /// </summary>
    public static OperatorPenaltySituationState Derive(Fields fields)
    {
        // Sin cancelacion vigente: no hay paso de multa que mostrar.
        if (!fields.HasLiveCancellation)
            return OperatorPenaltySituationState.None;

        // Terminal "sin multa": el operador no cobro penalidad. No hay ND ni nada mas que hacer.
        if (fields.PenaltyStatus == PenaltyStatus.Waived)
            return OperatorPenaltySituationState.Waived;

        // Multa CONFIRMADA: el paso lo define donde quedo su Nota de Debito.
        if (fields.PenaltyStatus == PenaltyStatus.Confirmed)
        {
            return fields.DebitNoteStatus switch
            {
                // Emitida con CAE -> la pata quedo resuelta.
                DebitNoteStatus.Issued => OperatorPenaltySituationState.Done,
                // Encolada, esperando CAE -> no hay accion, solo esperar.
                DebitNoteStatus.Pending => OperatorPenaltySituationState.DebitNoteQueued,
                // Fallo el CAE (ARCA pudo estar caido) -> se puede reintentar.
                DebitNoteStatus.Failed => OperatorPenaltySituationState.DebitNoteFailed,
                // Trabada en revision manual (tipico: moneda declarada != moneda de la factura, o sin moneda
                // registrada) -> se resuelve re-capturando monto + moneda (correct-penalty), no con un retry a secas.
                DebitNoteStatus.ManualReview => OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency,
                // Confirmada pero la ND nunca se encolo (diferida / quedo a medias por un fallo previo). Este estado
                // EXISTE: el confirm marca Confirmed + NotApplicable ANTES de encolar la ND, y si la emision quedo a
                // medias (crash entre confirmar y encolar) puede persistir asi (mismo caso que la bandeja de NDs
                // "ConfirmedWithoutDebitNote"). Se destraba reintentando la emision.
                DebitNoteStatus.NotApplicable => OperatorPenaltySituationState.ConfirmedNoDebitNote,
                // Defensivo: cualquier valor futuro cae en "confirmada sin ND" (accionable por retry), nunca None.
                _ => OperatorPenaltySituationState.ConfirmedNoDebitNote,
            };
        }

        // No-terminal (Estimated): solo mostramos el paso si HAY algo para decidir ahora. Si no (NC sin CAE aun,
        // subsistema deshabilitado, etc.), la ficha no ofrece nada -> None.
        _ = fields.HasDebitNoteInvoice; // sin uso en Estimated; se conserva en el struct para simetria/futuro.
        return fields.IsPendingDecision
            ? OperatorPenaltySituationState.PendingDecision
            : OperatorPenaltySituationState.None;
    }

    /// <summary>
    /// Colapsa el paso FINO (<see cref="OperatorPenaltySituationState"/>) al RESULTADO grueso
    /// (<see cref="OperatorPenaltyOutcome"/>: None/Pending/Confirmed/Waived) que consumen las capacidades de la
    /// reserva. Existe para que la ficha derive el outcome de la situacion YA calculada, en vez de re-consultar la
    /// cancelacion por segunda vez (N2, review 2026-07-08). El mapeo es exacto: todos los sub-estados de "confirmada"
    /// (encolada/fallida/trabada/emitida) colapsan a <see cref="OperatorPenaltyOutcome.Confirmed"/>.
    /// </summary>
    public static OperatorPenaltyOutcome ToOutcome(OperatorPenaltySituationState state) => state switch
    {
        OperatorPenaltySituationState.None => OperatorPenaltyOutcome.None,
        OperatorPenaltySituationState.PendingDecision => OperatorPenaltyOutcome.Pending,
        OperatorPenaltySituationState.Waived => OperatorPenaltyOutcome.Waived,
        // Confirmada, sin importar donde quedo la ND (encolada / fallida / trabada por moneda / confirmada-sin-ND / emitida).
        _ => OperatorPenaltyOutcome.Confirmed,
    };
}
