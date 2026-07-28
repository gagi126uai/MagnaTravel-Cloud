using TravelApi.Application.Interfaces;

namespace TravelApi.Middleware;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + hardening de la revisión de seguridad/funcional
/// del mismo día: mientras <see cref="IMaintenanceModeService.IsActive"/> es <c>true</c>, responde 503 a CASI
/// todos los pedidos bajo <c>/api/**</c> y <c>/hubs/**</c> — el sistema queda "fuera de servicio" de forma
/// visible en vez de aceptar pedidos (HTTP o conexiones en tiempo real) contra una base que se está
/// reemplazando.
///
/// <para><b>Excepciones, a propósito</b>:</para>
/// <list type="bullet">
///   <item><c>GET /api/system/status</c>: para que la pantalla especial "estamos restaurando, volvemos en un
///   minuto" del front pueda consultar CUÁNDO termina (sondeándolo cada pocos segundos).</item>
///   <item><c>POST /api/admin/danger/restore</c>: para que un Admin pueda REINTENTAR una restauración si el
///   proceso de la API se reinició a mitad de camino.</item>
///   <item><b><c>POST /api/auth/login</c> y <c>POST /api/auth/refresh</c></b> (hallazgo B-11b, revisión
///   funcional 2026-07-28, "el sistema queda tapiado sin salida"): el access token dura 15 minutos — sin esta
///   excepción, apenas expirara el token de CUALQUIER sesión abierta (incluida la del Admin que necesita
///   arreglar algo), nadie podría volver a autenticarse mientras el mantenimiento sigue activo, ni siquiera
///   para usar la excepción de <c>POST /api/admin/danger/restore</c> de arriba (que igual exige estar
///   logueado). Dejar pasar el login NO es un riesgo: el login en sí no lee/escribe datos de negocio, solo
///   valida credenciales contra Identity.</item>
/// </list>
///
/// <para><b>Por qué también <c>/hubs/**</c></b> (hallazgo menor, revisión funcional): los hubs de SignalR
/// (notificaciones, logs) no están bajo <c>/api</c>, así que sin este agregado un cliente podría seguir
/// abriendo conexiones en tiempo real mientras la base se reemplaza — code paths dentro de esos hubs podrían
/// disparar consultas contra una base a medio restaurar. Esto bloquea CONEXIONES NUEVAS; una conexión que ya
/// estaba abierta ANTES de activar el mantenimiento no se corta acá (limitación conocida, documentada como
/// riesgo residual).</para>
///
/// <para><b>No toca la base de datos</b>: la decisión sale ENTERA de un flag en memoria (<see cref="_maintenanceMode"/>),
/// por eso se registra lo más arriba posible en el pipeline (ver <c>Program.cs</c>, justo después de CORS) —
/// ni siquiera necesita que el routing/autenticación/autorización hayan corrido todavía.</para>
/// </summary>
public class MaintenanceModeMiddleware
{
    private const string RestoreEndpointPath = "/api/admin/danger/restore";
    private const string StatusEndpointPath = "/api/system/status";
    private const string LoginEndpointPath = "/api/auth/login";
    private const string RefreshEndpointPath = "/api/auth/refresh";

    private const string MaintenanceMessage = "Estamos restaurando el sistema. Volvemos en un minuto.";

    private readonly RequestDelegate _next;
    private readonly IMaintenanceModeService _maintenanceMode;

    public MaintenanceModeMiddleware(RequestDelegate next, IMaintenanceModeService maintenanceMode)
    {
        _next = next;
        _maintenanceMode = maintenanceMode;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_maintenanceMode.IsActive || !IsAffectedPath(context) || IsExemptRequest(context))
        {
            await _next(context);
            return;
        }

        await WriteMaintenanceResponseAsync(context);
    }

    private static bool IsAffectedPath(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    private static bool IsExemptRequest(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            return context.Request.Path.Equals(StatusEndpointPath, StringComparison.OrdinalIgnoreCase);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            return context.Request.Path.Equals(RestoreEndpointPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(LoginEndpointPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(RefreshEndpointPath, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private async Task WriteMaintenanceResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            code = "MAINTENANCE",
            message = MaintenanceMessage,
            // "desde" viaja como DateTime UTC: System.Text.Json lo serializa en formato ISO-8601 con sufijo
            // "Z" (ej. "2026-07-28T12:00:00.1234567Z") - el front lo puede parsear directo con `new Date(...)`
            // sin conversion manual, JavaScript interpreta el sufijo "Z" como UTC automaticamente.
            desde = _maintenanceMode.SinceUtc,
        });
    }
}
