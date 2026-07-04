namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): semaforo del estado del reembolso esperado del operador. Lo deriva el
/// read-model a partir del estado de la cancelacion y de <c>OperatorRefundDueBy</c>. Es SOLO una pista de UI;
/// la verdad de la maquina de estados vive en <c>BookingCancellationStatus</c>.
/// </summary>
public enum OperatorRefundPendingSemaphore
{
    /// <summary>El plazo todavia no esta cerca de vencer (o no hay plazo). Esperando al operador con tiempo.</summary>
    OnTime = 0,

    /// <summary>El plazo esta por vencer (dentro de la ventana de aviso). Conviene reclamar al operador.</summary>
    DueSoon = 1,

    /// <summary>El plazo vencio y la cancelacion sigue esperando el reembolso (todavia no se dio por perdida).</summary>
    Overdue = 2,

    /// <summary>El job de timeout ya la dio por perdida (<c>AbandonedByOperator</c>). Solo entra plata por reembolso tardio.</summary>
    Abandoned = 3,
}

/// <summary>
/// Cuenta del operador (2026-07-03): motivo por el que el reembolso estimado da $0, para explicarlo en criollo
/// en vez del "$0" seco (P4). Se deriva de PaidToOperator/PenaltyRetained/AmountReceived; NO es un monto -> NO se enmascara.
/// </summary>
public enum OperatorRefundZeroReason
{
    /// <summary>No se le pagó nada al operador por este viaje -> no hay base para devolver.</summary>
    NothingPaidToOperator = 0,

    /// <summary>Se le pagó, pero la multa retenida por el operador se quedó con TODO lo pagado.</summary>
    PenaltyCoversAll = 1,

    /// <summary>Se le pagó, y el operador YA devolvió todo lo que correspondía (no queda residuo).</summary>
    FullyRefunded = 2,
}

/// <summary>
/// Cuenta del operador (2026-07-03, RESTOS): naturaleza de la fila del read-model de reembolsos, para que el
/// front la rotule distinto (la solapa muestra tanto lo pendiente completo como los RESIDUOS de cancelaciones
/// parcialmente reembolsadas o cerradas con resto). Es SOLO una pista de UI; la verdad vive en
/// <c>BookingCancellationStatus</c>. El front mapea cada valor a su texto en español.
/// </summary>
public enum OperatorRefundRowStatus
{
    /// <summary>Esperando el reembolso del operador; todavía no entró nada.</summary>
    AwaitingRefund = 0,

    /// <summary>El operador ya devolvió una parte; queda un residuo por cobrar ("parcialmente devuelto").</summary>
    PartiallyRefunded = 1,

    /// <summary>El plazo se venció y se dio por perdida; solo entra plata por reembolso tardío.</summary>
    Abandoned = 2,

    /// <summary>La cancelación se cerró pero el operador devolvió de menos: quedó un resto vivo ("cerrado con resto").</summary>
    ClosedWithResidue = 3,

    /// <summary>La anulación todavía está en proceso (falta confirmar la Nota de Crédito); el reembolso aún no se puede registrar.</summary>
    InProcess = 4,
}

/// <summary>
/// ADR-041 TANDA 4: monto ESTIMADO de reembolso esperado del operador en UNA moneda.
///
/// <para><b>IMPORTANTE</b>: <see cref="EstimatedAmount"/> es un ESTIMADO (lo pagado al operador menos su
/// penalidad conocida), SUJETO A DEDUCCIONES del operador al momento del ingreso real (retenciones, costos
/// bancarios, penalidades finales). NUNCA es una cifra firme de lo que va a entrar a caja. El front debe
/// etiquetarlo como "estimado". Respeta el masking <c>cobranzas.see_cost</c>: sin permiso vale 0 y
/// <see cref="OperatorRefundPendingItemDto.AmountsMasked"/> es true.</para>
/// </summary>
public class OperatorRefundEstimatedAmountDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Estimado del reembolso pendiente en esta moneda. SUJETO A DEDUCCIONES del operador. 0 si esta enmascarado.</summary>
    public decimal EstimatedAmount { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, decision 1): BASE REEMBOLSABLE pagada al operador por los servicios de
    /// esta cancelacion en esta moneda (el "Pagaste US$ 500" del desglose). Es la base topeada al costo del
    /// servicio (<c>capBeforePenalty = RefundCap + multa retenida</c>), NO el bruto pagado. Es COSTO -> se
    /// enmascara: 0 si el caller no tiene <c>cobranzas.see_cost</c> (con <see cref="OperatorRefundPendingItemDto.AmountsMasked"/> true).
    /// Invariante: <c>EstimatedAmount == PaidToOperator - PenaltyRetained - AmountReceived</c> (ver el builder).
    /// </summary>
    public decimal PaidToOperator { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, decision 1): MULTA CONFIRMADA que el operador retuvo y que YA descuenta
    /// el estimado (el "− Multa del operador US$ 100"). Es COSTO -> se enmascara igual que PaidToOperator.
    /// </summary>
    public decimal PenaltyRetained { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, RESTOS): REEMBOLSO YA RECIBIDO del operador en esta moneda (el
    /// "Ya te devolvió US$ 200"). En las filas con residuo (parcialmente devuelto / cerrado con resto) es &gt; 0.
    /// El front arma "quedan {EstimatedAmount} de {EstimatedAmount + AmountReceived}". Es COSTO -> se enmascara
    /// igual que los demás montos.
    /// </summary>
    public decimal AmountReceived { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, decision 4 / P4): cuando <see cref="EstimatedAmount"/> es 0, el motivo
    /// para explicarlo en criollo (<c>NothingPaidToOperator</c> / <c>PenaltyCoversAll</c> / <c>FullyRefunded</c>);
    /// null si el estimado es &gt; 0. Derivado por el backend de los montos anteriores (el front NO resta).
    /// <para><b>Enmascarado (security review 2026-07-03)</b>: el motivo es CUALITATIVO sobre costos
    /// (<c>PenaltyCoversAll</c> revela que hubo multa &gt;= lo pagado), asi que sin <c>cobranzas.see_cost</c> viaja
    /// null (<see cref="OperatorRefundPendingItemDto.AmountsMasked"/> true) — enmascarado completo server-side.</para>
    /// </summary>
    public string? ZeroRefundReason { get; set; }
}

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): una fila del read-model "reembolsos a cobrar del operador". Una fila =
/// (cancelacion, operador): para una cancelacion multi-operador hay una fila por cada operador que debe
/// reembolsar. Es SOLO LECTURA (no muta nada). Reusa <c>BookingCancellation</c> + sus lineas.
/// </summary>
public class OperatorRefundPendingItemDto
{
    /// <summary>PublicId de la cancelacion. Necesario para reabrir (reembolso tardio) o imputar el ingreso.</summary>
    public Guid BookingCancellationPublicId { get; set; }

    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;

    /// <summary>Cliente que origino la anulacion (titular de la cancelacion).</summary>
    public string ClienteNombre { get; set; } = string.Empty;

    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>Semaforo derivado (A tiempo / Por vencer / Vencido / Abandonado).</summary>
    public OperatorRefundPendingSemaphore Semaphore { get; set; }

    /// <summary>Plazo configurado para que el operador reembolse. Null si la cancelacion no tenia plazo.</summary>
    public DateTime? OperatorRefundDueBy { get; set; }

    /// <summary>Dias corridos desde que vencio el plazo (&gt;= 0). 0 si todavia no vencio.</summary>
    public int DaysOverdue { get; set; }

    /// <summary>Estimados de reembolso por moneda (SUJETOS A DEDUCCIONES del operador, ver el DTO).</summary>
    public List<OperatorRefundEstimatedAmountDto> EstimatedRefundsByCurrency { get; set; } = new();

    /// <summary>true si el caller no tiene <c>cobranzas.see_cost</c>: los montos van en 0 (la estructura se ve igual).</summary>
    public bool AmountsMasked { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, decision 2 / P2): true si la multa del operador de esta cancelacion
    /// esta TODAVIA SIN CONFIRMAR (<c>bc.PenaltyStatus == Estimated</c>, ni confirmada ni waived). Alimenta el
    /// aviso "Falta confirmar la multa de esta anulación." + boton "Ir a la reserva a confirmar" (que navega a
    /// <see cref="ReservaPublicId"/>). NO es un monto -> se expone SIEMPRE (visible aun con montos enmascarados).
    /// La confirmacion fiscal se sigue haciendo SOLO en la reserva; desde aca solo se salta a ella.
    /// </summary>
    public bool PenaltyPendingConfirmation { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, RESTOS): naturaleza de la fila para que el front la rotule
    /// (esperando / parcialmente devuelto / abandonada / cerrada con resto / en proceso). NO es un monto ->
    /// se expone SIEMPRE. Ver <see cref="OperatorRefundRowStatus"/>.
    /// </summary>
    public OperatorRefundRowStatus RowStatus { get; set; }

    /// <summary>
    /// Cuenta del operador (2026-07-03, RESTOS): true si HOY se puede registrar un reembolso recibido contra esta
    /// cancelación desde el endpoint normal (estados <c>AwaitingOperatorRefund</c> / <c>ClientCreditApplied</c>,
    /// la MISMA capacidad server-side que valida el registro del ingreso). Si es false la fila es solo informativa
    /// (p.ej. una cancelación abandonada primero hay que reabrirla; una cerrada o en proceso no admite el registro
    /// directo). El backend NO cambia por este flag: es la lectura fiel de lo que el endpoint aceptaría.
    /// </summary>
    public bool CanRegisterRefund { get; set; }

    /// <summary>
    /// FIX A (2026-07-04): true si esta cancelación se puede REABRIR para registrar un reembolso TARDÍO del operador
    /// (endpoint <c>reopen-for-late-refund</c>). Es true en DOS casos: la cancelación fue dada por perdida
    /// (<c>AbandonedByOperator</c>), o quedó CERRADA pero el operador todavía debe plata de verdad
    /// (<c>Closed</c> con residuo vivo &gt; 0, la MISMA fórmula del "me tiene que devolver" del extracto).
    /// El front usa este flag (no el semáforo) para mostrar el botón "Registrar reembolso tardío". NO es un monto
    /// -&gt; se expone SIEMPRE. Reabrir NO resucita la reserva (sigue cancelada); solo reabre el circuito de plata
    /// del operador para que después el cajero impute el ingreso con el flujo normal.
    /// </summary>
    public bool CanReopenForLateRefund { get; set; }
}

/// <summary>
/// ADR-041 TANDA 4: pedido de REABRIR una cancelacion abandonada para registrar un reembolso tardio. El motivo
/// es OBLIGATORIO (minimo 10 chars, lo valida el service) — es la justificacion auditada de por que se reabre una
/// cuenta que ya se habia dado por perdida.
/// </summary>
public record ReopenForLateRefundRequest(string Reason);
