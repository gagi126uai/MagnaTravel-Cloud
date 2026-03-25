using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TravelApi.Errors;

public static class DatabaseExceptionClassifier
{
    public static bool IsDatabaseUnavailable(Exception exception)
    {
        return exception switch
        {
            TimeoutException => true,
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
