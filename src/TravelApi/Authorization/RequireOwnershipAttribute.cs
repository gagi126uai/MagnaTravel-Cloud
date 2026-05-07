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
/// Bypass Admin en handler de permisos NO aplica aca: el ownership es
/// ortogonal — un Admin de todos modos puede ver el recurso si tiene
/// <c>*_view_all</c>; este attribute solo se usa en endpoints de scope "mio".
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

    public RequireOwnershipAttribute(OwnedEntity entity, string routeParam = DefaultRouteParam)
    {
        _entity = entity;
        _routeParam = routeParam;
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
