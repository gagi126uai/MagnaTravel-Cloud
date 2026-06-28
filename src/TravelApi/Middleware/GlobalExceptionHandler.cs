using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Domain.Exceptions;
using TravelApi.Errors;

namespace TravelApi.Middleware;

public class GlobalExceptionHandler : IExceptionHandler
{
    // Mensaje generico para CUALQUIER excepcion no controlada. Es el unico texto que ve el usuario:
    // nunca el mensaje de la excepcion, el stack trace ni el nombre del tipo. El detalle tecnico va
    // SOLO al logger del servidor (cruzable despues con el "codigo de referencia" que devolvemos).
    private const string UnexpectedErrorMessage =
        "Ocurrió un error inesperado. Volvé a intentar; si el problema sigue, escribinos.";

    private const string UnexpectedErrorTitle = "Ocurrió un error inesperado.";

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // FC1 (review BR3, 2026-05-14): los CHECK constraints del modulo de
        // cancelacion / refund (PG SqlState 23514) son re-lanzados como
        // BusinessInvariantViolationException por el SaveChangesInterceptor.
        // Aca se mapean a 409 Conflict ANTES de pasar por el clasificador de
        // "database unavailable", que sino los confundiria con un 503.
        if (exception is BusinessInvariantViolationException invariant)
        {
            _logger.LogWarning(exception,
                "Business invariant violated: {Invariant} ({Constraint})",
                invariant.InvariantCode,
                invariant.ConstraintName);

            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Operacion rechazada por una regla de negocio.",
                Detail = invariant.Message,
            };

            // Los codigos de invariante y constraint van como extensions para que
            // el frontend pueda decidir si mostrar un copy especifico o un link a
            // ayuda. NO incluimos InnerException — puede contener nombres de columna.
            if (invariant.InvariantCode is not null)
                problem.Extensions["invariantCode"] = invariant.InvariantCode;
            if (invariant.ConstraintName is not null)
                problem.Extensions["constraintName"] = invariant.ConstraintName;
            problem.Extensions["code"] = "business_invariant_violation";

            httpContext.Response.StatusCode = problem.Status.Value;
            await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
            return true;
        }

        // FC1.3.2 (ADR-009 §2.10, 2026-05-21): los services que validan reglas de
        // negocio simples sobre el request (por ejemplo, GR-002 en
        // OperationalFinanceSettingsService.UpdateAsync) tiran ValidationException
        // de System.ComponentModel.DataAnnotations. Sin este mapeo caerian a 500.
        // El ADR garantiza HTTP 400 al admin que intenta una combinacion invalida
        // de flags, asi que mapeamos a ProblemDetails 400 con el mensaje real
        // (los ValidationException llevan mensajes pensados para el usuario, no
        // exponen detalles internos). Idem mensaje en idioma del modulo.
        if (exception is ValidationException validation)
        {
            _logger.LogWarning(exception, "Validation failed: {Message}", validation.Message);

            var validationProblem = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Solicitud invalida.",
                Detail = validation.Message,
            };
            validationProblem.Extensions["code"] = "validation_failed";

            httpContext.Response.StatusCode = validationProblem.Status.Value;
            await httpContext.Response.WriteAsJsonAsync(validationProblem, cancellationToken);
            return true;
        }

        // "Codigo de referencia" OPACO: NO es un stack, ni un tipo, ni el mensaje de la excepcion.
        // Es el TraceIdentifier de la request, que tambien queda en el log del servidor. Permite que el
        // usuario nos pase un codigo y nosotros crucemos con el log para ver el detalle tecnico real.
        var referenceCode = httpContext.TraceIdentifier;

        // TODO el detalle tecnico (mensaje + stack) va SOLO al logger del servidor, NUNCA al body.
        _logger.LogError(exception,
            "Unhandled exception (ref {ReferenceCode}): {Message}", referenceCode, exception.Message);

        ProblemDetails problemDetails;
        if (DatabaseExceptionClassifier.IsDatabaseUnavailable(exception))
        {
            // 503 amable de "base no disponible". NO pasamos exception.Message en NINGUN entorno
            // (antes en Development se filtraba el texto crudo del driver de la base).
            problemDetails = DatabaseExceptionClassifier.CreateProblemDetails();
        }
        else
        {
            // 500 generico en espanol, identico en TODOS los entornos (incluido Development): el body
            // jamas contiene exception.Message ni stack trace. El detalle vive solo en el log del servidor.
            problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = UnexpectedErrorTitle,
                Detail = UnexpectedErrorMessage,
            };
            problemDetails.Extensions["code"] = "internal_error";
            problemDetails.Extensions["reference"] = referenceCode;
        }

        // Ambas ramas setean Status (500 o el 503 del clasificador); el fallback es solo para el compilador.
        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
