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
            Detail = required.Count == 1
                ? $"Se requiere el permiso '{required[0]}' para realizar esta accion."
                : $"Se requiere alguno de los permisos: {string.Join(", ", required)}."
        };
        problem.Extensions["code"] = PermissionRequiredCode;
        problem.Extensions["missingPermission"] = required.Count == 1 ? required[0] : required;
        return problem;
    }

    public static ProblemDetails OwnershipRequired(string entity)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Recurso no asignado al usuario",
            Detail = $"El recurso '{entity}' no esta asignado al usuario actual."
        };
        problem.Extensions["code"] = OwnershipRequiredCode;
        problem.Extensions["entity"] = entity;
        return problem;
    }
}
