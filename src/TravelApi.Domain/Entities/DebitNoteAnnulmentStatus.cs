namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): estado fiscal de UN evento de "deshacer" una Nota de
/// Debito de multa. Sigue el CAE de la Nota de Credito que anula esa ND (<see cref="BookingCancellationDebitNoteAnnulment"/>),
/// igual que <see cref="Entities.BookingCancellationCreditNoteStatus"/> sigue el CAE de una NC de anulacion normal.
/// </summary>
public enum DebitNoteAnnulmentStatus
{
    /// <summary>La NC que anula la ND se encolo (o esta por encolarse) y todavia espera el CAE de ARCA.</summary>
    Pending = 0,

    /// <summary>La NC que anula la ND obtuvo CAE aprobado. La ND queda desvinculada del BC (estado terminal exitoso).</summary>
    Succeeded = 1,

    /// <summary>ARCA rechazo la NC que anula la ND. La ND original sigue viva (Issued); se puede reintentar deshacer.</summary>
    Failed = 2,
}
