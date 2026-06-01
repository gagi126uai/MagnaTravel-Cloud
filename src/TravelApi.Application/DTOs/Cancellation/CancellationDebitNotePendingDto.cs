namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// ADR-013 §3.10 (M4, 2026-06-01): fila de la bandeja operativa "cancelaciones con NC
/// emitida pero sin su ND". Existe para hacer OBSERVABLE el caso en que la NC total ya
/// salio con CAE pero la ND quedo pendiente o fallida -> la cancelacion esta fiscalmente
/// incompleta y alguien tiene que reintentar/revisar.
///
/// <para>Es un DTO de lectura plano (no expone la entidad de persistencia). Solo lleva lo
/// que la bandeja necesita mostrar; nada sensible del cliente/pasajero.</para>
/// </summary>
public class CancellationDebitNotePendingDto
{
    /// <summary>PublicId del BookingCancellation (la UI navega por PublicId, nunca por Id int).</summary>
    public Guid BookingCancellationPublicId { get; set; }

    /// <summary>Numero de reserva (legible para el operador de la bandeja).</summary>
    public string ReservaNumero { get; set; } = string.Empty;

    /// <summary>Estado de la ND: Pending (encolada) o Failed (fallo el CAE). En texto.</summary>
    public string DebitNoteStatus { get; set; } = string.Empty;

    /// <summary>Monto de la penalidad congelado al momento del evento.</summary>
    public decimal? PenaltyAmount { get; set; }

    /// <summary>Moneda de la penalidad (ARS en el MVP).</summary>
    public string? PenaltyCurrency { get; set; }

    /// <summary>Tipo de comprobante de la ND (12 = ND C en el MVP).</summary>
    public int? DebitNoteCbteTipo { get; set; }

    /// <summary>Mensaje de error de ARCA si la ND fallo (motivo para el operador).</summary>
    public string? ArcaErrorMessage { get; set; }

    /// <summary>Momento en que se confirmo la cancelacion (para ordenar/priorizar).</summary>
    public DateTime? ConfirmedAt { get; set; }
}
