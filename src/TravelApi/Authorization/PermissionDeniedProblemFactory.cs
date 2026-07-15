using Microsoft.AspNetCore.Mvc;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: helper para construir ProblemDetails 403 estructurados.
/// Compatible con el contrato definido en el plan:
///   { "code": "permission_required", "missingPermission": "..." }
///   { "code": "ownership_required" }
/// </summary>
public static class PermissionDeniedProblemFactory
{
    public const string PermissionRequiredCode = "permission_required";
    public const string OwnershipRequiredCode = "ownership_required";

    public static ProblemDetails MissingPermission(IReadOnlyList<string> required)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Permiso insuficiente",
            Detail = "No tenés autorización para realizar esta acción."
        };
        problem.Extensions["code"] = PermissionRequiredCode;
        return problem;
    }

    public static ProblemDetails OwnershipRequired(string entity)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Recurso no asignado al usuario",
            Detail = "Este registro no está asignado a tu usuario."
        };
        problem.Extensions["code"] = OwnershipRequiredCode;
        return problem;
    }
}
