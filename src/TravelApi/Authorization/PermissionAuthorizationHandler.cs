using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TravelApi.Application.Interfaces;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: handler que evalua <see cref="PermissionRequirement"/> contra
/// el set de permisos del usuario (cargados desde
/// <see cref="IUserPermissionResolver"/>, cache TTL 15s).
///
/// Bypass Admin in-handler: si el usuario tiene el rol "Admin", succeed.
/// Documentado para futuro permiso "system.bypass_authorization" (NO
/// implementado en esta fase — review B1.15 Fase 1).
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IUserPermissionResolver _resolver;

    public PermissionAuthorizationHandler(IUserPermissionResolver resolver)
    {
        _resolver = resolver;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            return;
        }

        // Admin bypass. Cuando exista "system.bypass_authorization" se reemplaza
        // por una verificacion sobre ese permiso.
        if (user.IsInRole("Admin"))
        {
            context.Succeed(requirement);
            return;
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        var perms = await _resolver.GetPermissionsAsync(userId);
        if (perms.Count == 0)
        {
            return;
        }

        // OR entre los permisos del requirement (mismo attribute).
        foreach (var p in requirement.Permissions)
        {
            if (perms.Contains(p))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
