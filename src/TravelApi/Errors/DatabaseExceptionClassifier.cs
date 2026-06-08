using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TravelApi.Errors;

public static class DatabaseExceptionClassifier
{
    /// <summary>
    /// BUG 4 (2026-06-08): clases de SqlState de Postgres que SI indican que la base no esta disponible
    /// (problema de conectividad/operativa), NO un error de datos del request. Ver
    /// https://www.postgresql.org/docs/current/errcodes-appendix.html
    ///  - "08" Connection Exception (conexion caida / rechazada)
    ///  - "53" Insufficient Resources (sin memoria, demasiadas conexiones, disco lleno)
    ///  - "57" Operator Intervention (admin_shutdown, cannot_connect_now, crash_shutdown)
    ///  - "58" System Error (errores de I/O del sistema operativo)
    /// Una violacion de constraint (clase "23"), un dato fuera de rango (clase "22"), etc., NO entran aca:
    /// eso es un error del REQUEST (debe ser 400/422 con mensaje claro), no un 503 "base no disponible".
    /// </summary>
    private static readonly string[] UnavailableSqlStateClasses = { "08", "53", "57", "58" };

    public static bool IsDatabaseUnavailable(Exception exception)
    {
        return exception switch
        {
            TimeoutException => true,
            // BUG 4: PostgresException (subclase de NpgsqlException) representa un error DEVUELTO por el
            // servidor — la conexion funciono. Solo es "DB no disponible" si su SqlState es de una clase
            // de conectividad/operativa; una violacion de constraint NO lo es (la trata el caller como
            // error de datos). Este case va ANTES del NpgsqlException generico (que sigue cubriendo los
            // errores de transporte reales: socket cerrado, timeout de red, etc.).
            PostgresException postgresException => IsConnectivitySqlState(postgresException.SqlState),
            NpgsqlException => true,
            DbUpdateException dbUpdateException when dbUpdateException.InnerException is not null
                => IsDatabaseUnavailable(dbUpdateException.InnerException),
            InvalidOperationException invalidOperationException
                when invalidOperationException.Message.Contains("connection", StringComparison.OrdinalIgnoreCase)
                => true,
            _ when exception.InnerException is not null => IsDatabaseUnavailable(exception.InnerException),
            _ => false
        };
    }

    /// <summary>
    /// BUG 4: true solo si el SqlState pertenece a una clase de conectividad/operativa
    /// (ver <see cref="UnavailableSqlStateClasses"/>). Para un constraint/dato invalido devuelve false.
    /// </summary>
    private static bool IsConnectivitySqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState) || sqlState.Length < 2) return false;
        var stateClass = sqlState.Substring(0, 2);
        return Array.IndexOf(UnavailableSqlStateClasses, stateClass) >= 0;
    }

    public static ProblemDetails CreateProblemDetails(string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Base de datos no disponible.",
            Detail = detail ?? "El servicio de base de datos no esta disponible en este momento."
        };

        problem.Extensions["code"] = "database_unavailable";
        return problem;
    }
}
