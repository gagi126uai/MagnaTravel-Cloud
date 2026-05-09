using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TravelApi.Application.Interfaces;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: filter de autorizacion por ownership. Resuelve el id de la
/// entidad desde un route param y verifica con <see cref="IOwnershipResolver"/>
/// que el usuario actual sea el responsable.
///
/// Bypass Admin in-filter: rol "Admin" salta el chequeo. Para usuarios no-Admin,
/// si se especifica <see cref="BypassPermission"/> y el user lo tiene, tambien
/// se saltea el chequeo (B1.15 Fase 2a — habilita el patron <c>view_all</c> para
/// roles intermedios como Colaborador).
///
/// Si la entidad no tiene ResponsibleUserId (legacy), <see cref="OwnershipResolver"/>
/// rechaza. Esto fuerza al backend a backfillear antes de migrar controllers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireOwnershipAttribute : Attribute, IAsyncAuthorizationFilter
{
    private const string DefaultRouteParam = "publicIdOrLegacyId";

    private readonly OwnedEntity _entity;
    private readonly string _routeParam;

    /// <summary>
    /// Permiso opcional que, si lo posee el usuario actual, hace bypass del
    /// chequeo de ownership. Tipico: <c>reservas.view_all</c>, <c>cobranzas.view_all</c>.
    /// </summary>
    public string? BypassPermission { get; }

    public RequireOwnershipAttribute(OwnedEntity entity, string routeParam = DefaultRouteParam, string? bypassPermission = null)
    {
        _entity = entity;
        _routeParam = routeParam;
        BypassPermission = bypassPermission;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user?.Identity is null || !user.Identity.IsAuthenticated)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // Admin bypass: no requiere ownership.
        if (user.IsInRole("Admin"))
        {
            return;
        }

        // Bypass por permiso (ej: reservas.view_all). Consulta el resolver de
        // permisos del DI scope. Si no esta registrado, ignora el bypass.
        if (!string.IsNullOrEmpty(BypassPermission))
        {
            var permResolver = context.HttpContext.RequestServices.GetService(typeof(IUserPermissionResolver)) as IUserPermissionResolver;
            if (permResolver is not null)
            {
                var perms = await permResolver.GetPermissionsAsync(userId, context.HttpContext.RequestAborted);
                if (perms.Contains(BypassPermission))
                {
                    return;
                }
            }
        }

        if (!context.RouteData.Values.TryGetValue(_routeParam, out var raw) || raw is null)
        {
            context.Result = new BadRequestObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Parametro de ruta requerido",
                Detail = $"Falta el parametro '{_routeParam}' para validar ownership."
            });
            return;
        }

        var resolver = context.HttpContext.RequestServices.GetService(typeof(IOwnershipResolver)) as IOwnershipResolver;
        if (resolver is null)
        {
            // Defensa: si no esta registrado, fallar cerrado.
            context.Result = new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(_entity.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        var idText = raw.ToString() ?? string.Empty;
        var ct = context.HttpContext.RequestAborted;
        var isOwner = await resolver.IsOwnerAsync(userId, _entity, idText, ct);
        if (!isOwner)
        {
            context.Result = new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(_entity.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }
}
