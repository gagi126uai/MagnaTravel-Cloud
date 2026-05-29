namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): estado del caso de la bandeja de
/// reconciliacion de NC parciales con recibos vivos.
///
/// <para><b>Por que solo dos estados</b>: el caso nace cuando se emite una NC
/// parcial que dejo recibos de pago "vivos" (Issued) sin anular. El encargado
/// lo mira, acomoda los recibos que correspondan, y lo marca resuelto. No hay
/// un estado intermedio "en progreso" porque la bandeja solo registra
/// pendiente vs cerrado — el trabajo de acomodar recibos se ve en vivo leyendo
/// el estado de cada PaymentReceipt, no del caso.</para>
///
/// <para>Persistido como string (varchar) con CHECK constraint en BD, igual que
/// el patron del modulo (ver migracion Fase3_M1). El cierre es SIEMPRE manual
/// (decision D1 del ADR): ningun job mueve un caso a Resolved.</para>
/// </summary>
public enum PartialCreditNoteReconciliationStatus
{
    /// <summary>El caso esta abierto: hay recibos vivos que el encargado todavia no acomodo/justifico.</summary>
    Pending = 0,

    /// <summary>El encargado cerro el caso a mano. Lleva ResolvedAt + ResolvedBy + (a veces) ResolutionNotes.</summary>
    Resolved = 1,
}
