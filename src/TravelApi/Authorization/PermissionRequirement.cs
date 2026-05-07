using Microsoft.AspNetCore.Authorization;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: requirement que pide al menos uno de los permisos listados.
/// "OR" entre permisos del mismo attribute. Para semantica AND apilar
/// <c>RequirePermission</c> attributes en el endpoint.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(IReadOnlyList<string> permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            throw new ArgumentException("Debe especificarse al menos un permiso.", nameof(permissions));
        }
        Permissions = permissions;
    }

    public IReadOnlyList<string> Permissions { get; }
}
