namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-013 §3.10 (M4, 2026-06-01): estado OBSERVABLE de la Nota de Debito dentro
/// de una cancelacion. Existe para que una ND que falla su CAE no quede invisible
/// en un log de Hangfire: hace visible "la NC ya salio pero la ND quedo pendiente
/// o fallida" para la bandeja operativa.
///
/// <para><b>Por que un estado y no solo el Resultado de la Invoice</b>: la ND es un
/// paso del EVENTO de cancelacion. Modelar el estado en el BookingCancellation
/// permite la query de la bandeja "cancelaciones con NC pero sin su ND" sin cruzar
/// el ciclo de vida de la Invoice. Espeja el patron de <see cref="AnnulmentStatus"/>
/// que ya existe para la NC.</para>
///
/// <para>Default <see cref="NotApplicable"/> a proposito: las cancelaciones que NO
/// emiten ND (pass-through, flag OFF, cancelaciones historicas) quedan en este
/// valor = comportamiento de hoy.</para>
/// </summary>
public enum DebitNoteStatus
{
    /// <summary>No corresponde ND (pass-through, flag OFF, cancelacion historica). Default conservador.</summary>
    NotApplicable = 0,

    /// <summary>La ND fue encolada al pipeline de emision; esperando CAE de ARCA.</summary>
    Pending = 1,

    /// <summary>La ND obtuvo CAE de ARCA. Cancelacion fiscalmente completa.</summary>
    Issued = 2,

    /// <summary>La ND fallo su CAE tras reintentos. La NC ya salio -> cancelacion fiscalmente incompleta.</summary>
    Failed = 3,

    /// <summary>El gating ruteo el caso a revision manual (no se emite automatico).</summary>
    ManualReview = 4,
}
