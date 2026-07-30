namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-052 (D3, cierra el bloqueante B4): las DOS políticas con las que se puede correr la secuencia de
/// actualización de esquema. Es el MISMO código en los dos casos (por eso existe el puerto); lo que cambia es
/// cuánto se reintenta y qué se tolera.
/// </summary>
public enum SchemaUpdatePolicy
{
    /// <summary>
    /// Arranque de la app / contenedor <c>migrate</c>: hasta 5 intentos con espera (la base puede estar todavía
    /// levantando) y los backfills que fallan se LOGUEAN y siguen — en el arranque, un backfill que falla se
    /// recupera en el próximo deploy, y frenar el arranque entero por eso sería peor.
    /// </summary>
    Startup,

    /// <summary>
    /// Dentro de una restauración total: UN solo intento y NO se tolera ningún fallo de backfill → el paso
    /// falla y el restore vuelve atrás. Motivo: en un restore, dejar la plata derivada (saldos por moneda,
    /// libro de caja, líneas de cancelación) en cero es exactamente el dato silencioso falso que este ERP no
    /// puede mostrar; y a diferencia del arranque, acá hay a dónde volver (la base original sigue viva bajo
    /// otro nombre).
    /// </summary>
    Restore,
}

/// <summary>
/// ADR-052 (D3/B4): resultado de la actualización de esquema. <see cref="MigrationsApplied"/> es un NÚMERO (va
/// a la auditoría, nunca la lista de ids — T-5). <see cref="ErrorMessage"/> es detalle INTERNO para el log.
/// </summary>
public sealed record SchemaUpdateResult(bool Success, int MigrationsApplied, string? ErrorMessage);

/// <summary>
/// ADR-052 (D3, cierra B4): puerto que sabe llevar el esquema de la base al día — la MISMA secuencia que corre
/// un deploy limpio: los 3 bootstrappers de SQL crudo → <c>MigrateAsync()</c> → los 3 backfills idempotentes
/// (ADR-021 saldos por moneda, ADR-022 libro de caja, ADR-025 líneas de cancelación).
///
/// <para><b>Por qué es un puerto y no un <c>_context.Database.MigrateAsync()</c> suelto</b>: (a) el contexto del
/// pedido HTTP tiene el <c>CommandTimeout</c> default de Npgsql (30 s) y hay migraciones con SQL crudo largo;
/// (b) sin puerto, el único camino nuevo peligroso ("la actualización falla → el sistema vuelve atrás") queda
/// intesteable.</para>
///
/// <para><b>Regla citable que se mantiene y NO reemplaza a este puerto</b>: toda migración que cree una
/// tabla/columna DERIVADA lleva su propio backfill adentro (ya es la práctica del repo). Un reviewer puede
/// bloquear citándola.</para>
/// </summary>
public interface ISchemaUpdatePort
{
    /// <summary>
    /// Lleva el esquema al día con la <paramref name="policy"/> indicada. NUNCA tira: los fallos vienen en el
    /// <see cref="SchemaUpdateResult"/> (el caller de restore necesita decidir "vuelvo atrás", y el de arranque
    /// "aborto el arranque").
    /// </summary>
    Task<SchemaUpdateResult> UpdateAsync(SchemaUpdatePolicy policy, CancellationToken ct);
}
