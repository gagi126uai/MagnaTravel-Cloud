using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TravelApi.Domain.Exceptions;

namespace TravelApi.Infrastructure.Persistence;

/// <summary>
/// FC1 (review BR3, 2026-05-14): EF Core <see cref="SaveChangesInterceptor"/> que
/// traduce violaciones de CHECK constraint de Postgres (<c>SqlState='23514'</c>)
/// a <see cref="BusinessInvariantViolationException"/>, para que el caller no
/// reciba una <see cref="DbUpdateException"/> opaca y el <c>GlobalExceptionHandler</c>
/// pueda emitir un <c>409 Conflict</c> con mensaje en espanol.
///
/// Por que un interceptor y no un middleware HTTP:
///  - El interceptor corre dentro del scope del <c>SaveChangesAsync</c>, asi que
///    cualquier <c>try/catch (BusinessInvariantViolationException)</c> en services
///    (ej. retry logic del <c>OperatorRefundService</c>) la atrapa correctamente.
///  - Un middleware solo veria la excepcion cuando ya escapo del service y rompe
///    el patron Result/retry.
///  - Ademas, el interceptor permite responder igual al caller sea web (HTTP) o
///    background job (Hangfire).
///
/// Otras excepciones NO se tragan: si la <c>DbUpdateException</c> no es un check
/// violation (ej. unique violation 23505, FK 23503, deadlock 40P01), se relanza
/// la original sin tocar — el <c>GlobalExceptionHandler</c> + <c>DatabaseExceptionClassifier</c>
/// mantienen su comportamiento previo.
/// </summary>
public sealed class BusinessInvariantInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// SqlState 23514 = check_violation (PostgreSQL Error Codes Appendix A).
    /// Es el unico SqlState que mapeamos: 23505 (unique), 23503 (FK), 22001 (string_data_right_truncation),
    /// etc. siguen su flujo normal — el caller decide como manejarlos.
    /// </summary>
    private const string PgCheckViolationSqlState = "23514";

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // Sync path: lo dejamos sin throw para que EF complete su housekeeping;
        // el async path es el que la mayoria del codigo usa. Si surge un caso
        // sync genuino, el interceptor abajo lo cubre tambien.
        TryRethrowAsBusinessInvariant(eventData.Exception);
        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TryRethrowAsBusinessInvariant(eventData.Exception);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Analiza la cadena de <c>InnerException</c> buscando una
    /// <see cref="PostgresException"/> con SqlState 23514. Si la encuentra,
    /// lanza <see cref="BusinessInvariantViolationException"/> con el mensaje
    /// resuelto desde <see cref="CheckConstraintMessages"/>.
    /// </summary>
    private static void TryRethrowAsBusinessInvariant(Exception? exception)
    {
        if (exception is null) return;

        // Atravesar la cadena: EF envuelve a Npgsql en DbUpdateException, y a veces
        // hay InvalidOperationException intermedio. Limite de 5 niveles para evitar
        // ciclos teoricos.
        var current = exception;
        for (var depth = 0; depth < 5 && current is not null; depth++)
        {
            if (current is PostgresException pg && pg.SqlState == PgCheckViolationSqlState)
            {
                var constraintName = pg.ConstraintName ?? "unknown";
                var (message, invariantCode) = CheckConstraintMessages.GetUserMessage(constraintName);

                // Conservamos la excepcion original como InnerException para que
                // el handler la pueda logguear con detalle completo si esta en dev.
                throw new BusinessInvariantViolationException(
                    message,
                    invariantCode,
                    constraintName,
                    exception);
            }
            current = current.InnerException;
        }
    }
}
