using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TravelApi.Middleware;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// Hallazgo 2026-08-06 (revision de seguridad, bloqueante B1): prueba la semantica REAL de
/// <see cref="ForwardedHeadersConfiguration.Build"/> corriendo el middleware de ASP.NET Core
/// directo (sin pasar por un TestServer/socket real, que no permite elegir a mano la IP con la
/// que "llega" el pedido). Se le da al middleware un <see cref="DefaultHttpContext"/> con la IP
/// de conexion que uno mismo controla -simulando exactamente lo que ve "api" en produccion,
/// donde su unico peer real es el contenedor "web"- y se verifica en que IP termina apoyando
/// <see cref="HttpContext.Connection.RemoteIpAddress"/>, que es la que despues usa el rate
/// limiter para particionar.
/// </summary>
public sealed class ForwardedHeadersConfigurationTests
{
    // Simula la IP que ve "api" cuando el pedido le llega desde el contenedor "web" (rango
    // docker-compose tipico). Con la config nueva, esta IP es "conocida" (KnownNetworks) y el
    // middleware SIGUE pelando el header hacia la izquierda.
    private static readonly IPAddress WebContainerIp = IPAddress.Parse("172.20.0.5");

    // Simula el segundo salto interno (nginx del HOST del VPS conectandose al contenedor "web"
    // via loopback, patron tipico de "nginx afuera de docker, proxy_pass a localhost:puerto").
    private const string HostNginxIp = "127.0.0.1";

    private static async Task<IPAddress?> RunMiddlewareAsync(IPAddress connectionRemoteIp, string? forwardedForHeader)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = connectionRemoteIp;
        if (forwardedForHeader is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedForHeader;
        }

        var options = ForwardedHeadersConfiguration.Build();
        var middleware = new ForwardedHeadersMiddleware(
            next: _ => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options));

        await middleware.Invoke(context);

        return context.Connection.RemoteIpAddress;
    }

    // (a) Caso "cliente real": la cadena tiene el IP publico del cliente y, despues, los dos
    // saltos internos que lo appendearon (host nginx -> web -> api). El resultado tiene que
    // ser EXACTAMENTE el IP publico del cliente: ni un salto interno de mas, ni el prefijo
    // recortado de menos.
    [Fact]
    public async Task ClienteReal_CadenaConDosSaltosInternos_TerminaEnElIpPublicoDelCliente()
    {
        var clientRealIp = IPAddress.Parse("203.0.113.7");
        var forwardedFor = $"{clientRealIp}, {HostNginxIp}";

        var resolved = await RunMiddlewareAsync(WebContainerIp, forwardedFor);

        Assert.Equal(clientRealIp, resolved);
    }

    // (a) Caso "XFF inventado apilado": el atacante escribe un prefijo falso ANTES de lo que
    // los nginx reales appendearon. Como ForwardLimit=2 corta justo despues de pelar los DOS
    // saltos internos reales (host nginx, y el propio peer "web" ya esta afuera del header),
    // el prefijo inventado por el atacante NUNCA se alcanza -> el resultado sigue siendo el IP
    // publico REAL del atacante (el que los nginx vieron de verdad), no el que el eligio
    // escribir.
    [Fact]
    public async Task XffInventadoApilado_PrefijoFalsoNuncaSeAlcanza_QuedaElIpRealDelAtacante()
    {
        var attackerRealIp = IPAddress.Parse("198.51.100.9");
        var forgedPrefix = "1.2.3.4";
        var forwardedFor = $"{forgedPrefix}, {attackerRealIp}, {HostNginxIp}";

        var resolved = await RunMiddlewareAsync(WebContainerIp, forwardedFor);

        Assert.Equal(attackerRealIp, resolved);
        Assert.NotEqual(IPAddress.Parse(forgedPrefix), resolved);
    }

    // Defensa en profundidad: aunque el atacante NO agregue saltos internos privados (los
    // omite, o llega directo con un header de una sola entrada), el resultado sigue sin ser el
    // valor que el atacante controla, porque KnownNetworks tambien corta por "esto ya no es
    // una IP privada" ademas de por ForwardLimit.
    [Fact]
    public async Task XffConUnaSolaEntradaPublica_SeAsumeComoElClienteReal()
    {
        var onlyEntry = IPAddress.Parse("203.0.113.55");

        var resolved = await RunMiddlewareAsync(WebContainerIp, onlyEntry.ToString());

        Assert.Equal(onlyEntry, resolved);
    }

    // Sin header X-Forwarded-For, el middleware no tiene nada que pelar: la IP de conexion
    // (el peer inmediato, "web") queda tal cual.
    [Fact]
    public async Task SinHeaderForwardedFor_QuedaLaIpDeConexionInmediata()
    {
        var resolved = await RunMiddlewareAsync(WebContainerIp, forwardedForHeader: null);

        Assert.Equal(WebContainerIp, resolved);
    }
}
