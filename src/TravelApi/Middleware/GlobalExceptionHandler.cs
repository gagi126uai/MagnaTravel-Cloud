using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Domain.Exceptions;
using TravelApi.Errors;

namespace TravelApi.Middleware;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
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

        _logger.LogError(exception, "Exception occurred: {Message}", exception.Message);

        var problemDetails = DatabaseExceptionClassifier.IsDatabaseUnavailable(exception)
            ? DatabaseExceptionClassifier.CreateProblemDetails(_env.IsDevelopment() ? exception.Message : null)
            : new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An error occurred while processing your request.",
                Detail = _env.IsDevelopment() ? exception.Message : "Please contact support if the problem persists."
            };

        httpContext.Response.StatusCode = problemDetails.Status.Value;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }
}
