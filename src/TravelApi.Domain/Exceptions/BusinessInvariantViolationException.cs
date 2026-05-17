namespace TravelApi.Domain.Exceptions;

/// <summary>
/// FC1 (review BR3, 2026-05-14): excepcion de dominio que indica que una operacion
/// violo un invariante de negocio.
///
/// Se origina en dos situaciones:
///  1. Una restriccion CHECK SQL fallo en Postgres (<c>SqlState=23514</c>): el
///     <c>BusinessInvariantInterceptor</c> de EF Core traduce la <c>DbUpdateException</c>
///     a esta excepcion antes de que escape al caller.
///  2. Un service de dominio detecta una violacion explicita antes de tocar la BD
///     (ej. <c>OperatorRefundService</c> rechaza una allocation cuyo neto > disponible).
///
/// Mapeo HTTP: el <c>GlobalExceptionHandler</c> reconoce esta excepcion y devuelve
/// <c>409 Conflict</c> con un <c>ProblemDetails</c> que incluye el mensaje en
/// espanol pensado para el usuario final, el codigo de invariante (INV-XXX) y
/// el nombre del CHECK constraint que disparo (si aplica). No es 500: el cliente
/// puede corregir su entrada y reintentar.
///
/// IMPORTANTE: el <c>Message</c> es lo que ve el usuario, asi que NO debe
/// contener stack traces, valores internos ni datos sensibles. Para diagnostico
/// el handler logguea la cadena completa <c>InnerException</c>.
/// </summary>
public sealed class BusinessInvariantViolationException : Exception
{
    /// <summary>
    /// Codigo de la invariante violada (ej. "INV-084", "INV-118"). Permite trazar
    /// la regla en el catalogo Bucket G (ADR-002 §10) y en el roadmap B1.15.
    /// </summary>
    public string? InvariantCode { get; }

    /// <summary>
    /// Nombre del CHECK constraint SQL que disparo la violacion (ej.
    /// <c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>). Null si la
    /// excepcion la lanzo un guard de dominio sin pasar por la BD.
    /// </summary>
    public string? ConstraintName { get; }

    public BusinessInvariantViolationException(
        string message,
        string? invariantCode = null,
        string? constraintName = null,
        Exception? inner = null)
        : base(message, inner)
    {
        InvariantCode = invariantCode;
        ConstraintName = constraintName;
    }
}
