using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using TravelApi.Middleware;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + hardening de seguridad/funcional del mismo día:
/// cubre <see cref="MaintenanceModeMiddleware"/> — el 503 mientras el sistema está en mantenimiento, y las
/// excepciones (status anónimo, el propio endpoint de restauración, login/refresh) que tienen que seguir
/// funcionando igual, más el nuevo gating de <c>/hubs/**</c>.
/// </summary>
public class MaintenanceModeMiddlewareTests
{
    private static (DefaultHttpContext Context, bool NextCalled) InvokeMiddleware(
        RecordingMaintenanceModeService maintenanceMode, string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = new MaintenanceModeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, maintenanceMode);

        middleware.InvokeAsync(context).GetAwaiter().GetResult();

        return (context, nextCalled);
    }

    [Fact]
    public void SinMantenimientoActivo_DejaPasarCualquierPedido()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();

        var (context, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/api/reservas/123");

        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode); // default de DefaultHttpContext, nunca lo tocamos.
    }

    [Fact]
    public void ConMantenimientoActivo_UnPedidoApiCualquieraRecibe503ConElCodigoMaintenance()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("Restauración total del sistema en curso.");

        var (context, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/api/reservas/123");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("MAINTENANCE", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Estamos restaurando el sistema. Volvemos en un minuto.", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void ConMantenimientoActivo_UnPedidoFueraDeApiPasaDeLargo()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/health");

        Assert.True(nextCalled);
    }

    [Fact]
    public void ConMantenimientoActivo_GetStatusSigueRespondiendo()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/api/system/status");

        Assert.True(nextCalled);
    }

    [Fact]
    public void ConMantenimientoActivo_PostRestoreSigueRespondiendo()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "POST", "/api/admin/danger/restore");

        Assert.True(nextCalled);
    }

    [Fact]
    public void ConMantenimientoActivo_GetRestoreVerifyNoQuedaExento_SigueBloqueado()
    {
        // Solo el endpoint EXACTO de restauracion (POST /api/admin/danger/restore) esta exento - cualquier
        // otro endpoint del mismo controller (ej. verify) sigue bloqueado durante el mantenimiento.
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (context, nextCalled) = InvokeMiddleware(maintenanceMode, "POST", "/api/admin/danger/restore/verify");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public void ConMantenimientoActivo_PostLoginSigueRespondiendo()
    {
        // Hallazgo B-11b (revision funcional, "el sistema queda tapiado sin salida"): el access token dura 15
        // minutos - sin esta excepcion, nadie podria volver a autenticarse mientras dura el mantenimiento.
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "POST", "/api/auth/login");

        Assert.True(nextCalled);
    }

    [Fact]
    public void ConMantenimientoActivo_PostRefreshSigueRespondiendo()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "POST", "/api/auth/refresh");

        Assert.True(nextCalled);
    }

    [Fact]
    public void ConMantenimientoActivo_HubsQuedaBloqueado()
    {
        // Hallazgo menor (revision funcional): /hubs/** no esta bajo /api, pero tambien tiene que cortarse
        // durante el mantenimiento (conexiones nuevas de SignalR podrian disparar consultas contra una base
        // a medio restaurar).
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("motivo");

        var (context, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/hubs/notifications");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public void SinMantenimientoActivo_HubsPasaDeLargo()
    {
        var maintenanceMode = new RecordingMaintenanceModeService();

        var (_, nextCalled) = InvokeMiddleware(maintenanceMode, "GET", "/hubs/notifications");

        Assert.True(nextCalled);
    }
}
