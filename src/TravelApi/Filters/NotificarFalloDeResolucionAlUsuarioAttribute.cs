using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Filters;

/// <summary>
/// Decision firmada 2026-08-18 (Gaston): cuando un vendedor intenta "Marcar confirmado / emitido / No
/// requiere confirmacion" sobre un servicio (aereo, hotel, traslado, paquete o asistencia) y el intento
/// FALLA, ese error TAMBIEN queda en la campanita de notificaciones — ademas del mensaje que ya se ve en
/// la fila del servicio. Los EXITOS nunca generan un aviso nuevo (serian ruido); si el servicio se resuelve
/// bien despues de haber fallado antes, el aviso viejo se apaga solo.
///
/// <para><b>Por que un action filter y no tocar cada controller</b>: son 7 endpoints repartidos en 5
/// controllers (FlightSegments, HotelBookings, TransferBookings, PackageBookings, AssistanceBookings) que
/// YA atrapan sus propias excepciones de negocio (<c>ArgumentException</c> -&gt; 400, <c>InvalidOperationException</c>
/// -&gt; 409) y devuelven <c>{ message = ex.Message }</c>. Pegar "crear notificacion" adentro de cada catch
/// hubiera significado repetir el mismo codigo 7 veces. Este filter mira el resultado final de la accion
/// (el JSON que ya armo el controller) en un unico lugar.</para>
///
/// <para><b>Que mira exactamente</b>: un <c>ObjectResult</c> con status 400 o 409 y una propiedad
/// <c>message</c> — es el patron EXACTO que usan estos 7 endpoints para reportar un rechazo de negocio (ver
/// los catch de <c>ArgumentException</c>/<c>InvalidOperationException</c> en cada controller). Un 404
/// (servicio o reserva no encontrada) NO se considera "fallo de resolucion" — es un problema distinto (el
/// vendedor esta apuntando a algo que ya no existe) y no genera aviso. Tambien se cubre, a modo de red de
/// seguridad, el caso de una <see cref="ValidationException"/> sin atrapar por el controller: hoy ninguno de
/// estos 7 metodos la usa (todos usan <c>ArgumentException</c>/<c>InvalidOperationException</c>, verificado
/// leyendo <c>BookingService</c>), pero si algun dia alguno empieza a tirarla, este filter la toma igual sin
/// dejar un agujero silencioso.</para>
///
/// <para><b>DI scope PROPIO</b>: la notificacion se arma en un scope de DI nuevo
/// (<see cref="IServiceScopeFactory"/>), nunca en el <c>DbContext</c> del request que acaba de fallar — ese
/// contexto puede haber quedado con entidades trackeadas en un estado raro despues de la excepcion.</para>
///
/// <para><b>Nunca rompe la respuesta</b>: todo lo que hace este filter despues de que la accion ya
/// respondio va envuelto en try/catch. Si armar o apagar el aviso falla (DB caida, DI mal configurado,
/// etc.), queda solo logueado — la respuesta que el vendedor ya recibio no se toca.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NotificarFalloDeResolucionAlUsuarioAttribute : Attribute, IAsyncActionFilter
{
    private readonly ServiceResolutionKind _kind;
    private readonly string _serviceIdRouteKey;

    /// <param name="kind">Tipo de servicio que resuelve este endpoint (en que tabla buscarlo).</param>
    /// <param name="serviceIdRouteKey">
    /// Nombre del route param que trae el id del SERVICIO (no el de la reserva). Ej. "id" en
    /// <c>POST .../flights/{id}/mark-issued</c>, o "publicIdOrLegacyId" en <c>PATCH .../flight-segments/{publicIdOrLegacyId}/status</c>.
    /// </param>
    public NotificarFalloDeResolucionAlUsuarioAttribute(ServiceResolutionKind kind, string serviceIdRouteKey)
    {
        _kind = kind;
        _serviceIdRouteKey = serviceIdRouteKey;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var resultContext = await next();

        // El id del servicio ya lo uso el controller para buscarlo; si no esta en la ruta (no deberia
        // pasar en estos 7 endpoints) no hay nada que avisar.
        if (!context.RouteData.Values.TryGetValue(_serviceIdRouteKey, out var raw) || raw is null)
            return;

        var servicePublicIdOrLegacyId = raw.ToString();
        if (string.IsNullOrWhiteSpace(servicePublicIdOrLegacyId))
            return;

        var ct = context.HttpContext.RequestAborted;

        try
        {
            var scopeFactory = context.HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
            using var scope = scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<IServiceResolutionFailureNotifier>();

            var businessErrorMessage = TryExtractBusinessErrorMessage(resultContext.Result);
            if (businessErrorMessage != null)
            {
                await notifier.NotifyFailureAsync(_kind, servicePublicIdOrLegacyId, businessErrorMessage, ct);
                return;
            }

            // Red de seguridad (ver XML-doc de la clase): una ValidationException que se le escapo al
            // controller. Se OBSERVA nomas — no se toca resultContext.Exception ni se marca
            // ExceptionHandled, GlobalExceptionHandler sigue siendo quien responde el 400 de siempre.
            if (resultContext.Exception is ValidationException validationEx && !resultContext.ExceptionHandled)
            {
                await notifier.NotifyFailureAsync(_kind, servicePublicIdOrLegacyId, validationEx.Message, ct);
                return;
            }

            // Sin excepcion y con un resultado 2xx = el servicio quedo resuelto. Si habia un aviso de un
            // fallo anterior sobre este mismo servicio, se apaga solo (decision firmada: el exito NO crea
            // aviso nuevo, pero SI apaga el viejo).
            if (resultContext.Exception is null && IsSuccessResult(resultContext.Result))
            {
                await notifier.NotifyResolvedAsync(_kind, servicePublicIdOrLegacyId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // el apagado del request no se traga
        }
        catch (Exception ex)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<NotificarFalloDeResolucionAlUsuarioAttribute>>();
            logger?.LogError(ex,
                "NotificarFalloDeResolucionAlUsuario: fallo generando/apagando el aviso para {Kind} {ServiceIdOrPublicId}.",
                _kind, servicePublicIdOrLegacyId);
        }
    }

    /// <summary>
    /// Estos 7 endpoints reportan un rechazo de negocio devolviendo <c>BadRequest(new { message })</c>
    /// (400) o <c>Conflict(new { message })</c> (409) — ver el catch de cada controller. Leemos "message"
    /// por reflection porque es un tipo anonimo armado inline en cada catch (no hay una clase DTO
    /// compartida para esta forma de respuesta).
    /// </summary>
    private static string? TryExtractBusinessErrorMessage(IActionResult? result)
    {
        if (result is not ObjectResult objectResult)
            return null;

        if (objectResult.StatusCode is not (StatusCodes.Status400BadRequest or StatusCodes.Status409Conflict))
            return null;

        var value = objectResult.Value;
        if (value is null)
            return null;

        var messageProperty = value.GetType().GetProperty("message");
        return messageProperty?.GetValue(value) as string;
    }

    /// <summary>2xx = el servicio quedo resuelto. Cubre tanto <c>Ok(dto)</c> (lo que devuelven los 7 endpoints) como cualquier otro ObjectResult exitoso.</summary>
    private static bool IsSuccessResult(IActionResult? result)
    {
        return result switch
        {
            ObjectResult objectResult => objectResult.StatusCode is null or (>= 200 and < 300),
            OkResult => true,
            _ => false,
        };
    }
}
