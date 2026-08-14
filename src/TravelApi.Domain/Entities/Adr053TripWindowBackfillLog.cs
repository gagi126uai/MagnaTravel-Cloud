namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-053 (2026-08-13, "fechas del viaje calculadas y de solo lectura"): rastro DURABLE de qué reservas
/// cambiaron su <see cref="Reserva.StartDate"/>/<see cref="Reserva.EndDate"/> el día del backfill masivo
/// (decisión del dueño 2026-08-11: recalcular TODAS las reservas existentes, no solo las nuevas).
///
/// <para><b>Por qué existe esta tabla (y no un simple <c>SELECT</c> de consulta al momento del deploy)</b>:
/// el backfill puede CAMBIAR un valor que ya existía (una reserva vieja con un servicio anulado que hoy
/// contaba en el MIN/MAX puede terminar con otra fecha tras el recálculo) — eso mueve, de un día para el
/// otro, vencimientos de Cobranzas y candidaturas de los jobs automáticos de estado. Sin este rastro, esas
/// preguntas ("¿por qué esta reserva cambió de vencimiento?") no tendrían respuesta con datos. Se conserva
/// PERMANENTEMENTE (no se exporta y borra): es una tabla de una sola escritura, con una fila por reserva
/// CUYO valor cambió (no una fila por cada reserva del sistema), volumen chico y acotado.</para>
///
/// <para>Se llena UNA sola vez, dentro de la migración <c>Adr053_M1_TripWindowRecalculatedAndPromisedDates</c>
/// (SQL crudo en <see cref="TravelApi.Infrastructure.Reservations.Adr053BackfillSql"/>) — nunca por código
/// en runtime.</para>
/// </summary>
public class Adr053TripWindowBackfillLog
{
    /// <summary>PK identity.</summary>
    public int Id { get; set; }

    /// <summary>Reserva cuya ventana cambió con el backfill (FK a <c>TravelFiles</c>, <c>ON DELETE CASCADE</c>).</summary>
    public int ReservaId { get; set; }

    /// <summary>Ventana ANTES del backfill (lo que tenía persistido <c>Reserva.StartDate</c>/<c>EndDate</c>).</summary>
    public DateTime? OldStartDate { get; set; }
    public DateTime? OldEndDate { get; set; }

    /// <summary>Ventana DESPUÉS del backfill (el nuevo MIN/MAX excluyendo servicios anulados, predicado D1.1).</summary>
    public DateTime? NewStartDate { get; set; }
    public DateTime? NewEndDate { get; set; }

    /// <summary>Momento UTC exacto en que corrió el backfill (mismo valor para todas las filas de esa corrida).</summary>
    public DateTime MigratedAtUtc { get; set; }
}
