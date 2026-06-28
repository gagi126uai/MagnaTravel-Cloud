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
}

/// <summary>
/// ADR-041 TANDA 4: pedido de REABRIR una cancelacion abandonada para registrar un reembolso tardio. El motivo
/// es OBLIGATORIO (minimo 10 chars, lo valida el service) — es la justificacion auditada de por que se reabre una
/// cuenta que ya se habia dado por perdida.
/// </summary>
public record ReopenForLateRefundRequest(string Reason);
