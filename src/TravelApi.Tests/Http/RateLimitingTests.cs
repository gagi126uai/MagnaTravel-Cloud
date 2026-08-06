using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

// OJO 2026-08-06 (hallazgo del reviewer de seguridad, corrigiendo un intento previo de este
// mismo archivo): estos tests prueban limites EXACTOS (el pedido numero 10, el 60, el 11) del
// rate limiter. El balde vive en memoria DENTRO del host de la app, y con IClassFixture
// compartido entre TODOS los metodos [Fact] de la clase, un test que gasta pedidos de una
// politica contamina el balde que otro test de la MISMA politica espera encontrar fresco. Por
// eso cada test arma su PROPIA CustomWebApplicationFactory (no comparte fixture): mas lento que
// compartir un host, pero es la unica forma de que "el pedido N cae en 429" sea determinista.
public sealed class RateLimitingTests
{
    [Fact]
    public async Task Login_EleventhAttemptWithinWindow_IsRateLimited()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        var payload = new
        {
            email = "inexistente@example.com",
            password = "Invalid123!",
            rememberMe = false,
        };

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", payload);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // Hallazgo 2026-08-06 (sesiones muertas en cada deploy): /auth/login y /auth/refresh
    // compartian la MISMA politica ("auth", 10 pedidos/5min). Login es blanco de fuerza
    // bruta y necesita ese limite chico; refresh es trafico automatico de cada pestaña
    // abierta (se dispara solo cuando vence el token de acceso o al reconectar tras un
    // corte de red, como el reinicio del contenedor en cada deploy) y necesita mucho mas
    // margen. Este test fija que refresh NO comparte el balde de 10 de login: manda mas
    // de 10 pedidos seguidos (todos sin cookie de sesion, asi que 401 legitimo, no 429)
    // y confirma que el limite mas generoso de "auth-refresh" sigue sin activarse.
    [Fact]
    public async Task Refresh_MoreThanTenAttemptsWithinWindow_IsNotRateLimitedLikeLogin()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 15; attempt++)
        {
            using var response = await client.PostAsync("/api/auth/refresh", content: null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // (b) Reviewer 2026-08-06 (bloqueante B1, test exigido): la politica "auth-refresh" tiene
    // limite propio de 60 pedidos/5min (ver Program.cs). Este test empuja hasta el limite REAL
    // (no solo "algunos pedidos mas que 10" como el test de arriba) y confirma que el pedido
    // numero 61 SI cae en 429 — la politica generosa sigue siendo un limite real, no "sin limite".
    [Fact]
    public async Task Refresh_SixtyFirstAttemptWithinWindow_IsRateLimited()
    {
        await using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 60; attempt++)
        {
            using var response = await client.PostAsync("/api/auth/refresh", content: null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limited = await client.PostAsync("/api/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // El 429 tiene que hablarle al usuario en castellano y no filtrar nada interno: ni el
        // nombre de la politica del limitador ("auth-refresh") ni jerga tecnica ("rate limit").
        var limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.Contains("Demasiadas solicitudes", limitedBody);
        Assert.DoesNotContain("auth-refresh", limitedBody);
        Assert.DoesNotContain("rate", limitedBody, StringComparison.OrdinalIgnoreCase);
    }

    // (c) Reviewer 2026-08-06 (bloqueante B1, test exigido): "XFF inventado NO abre balde
    // nuevo para login". La cadena que se manda tiene TRES entradas, imitando la topologia
    // real (nginx del host del VPS -> contenedor "web" -> "api", ForwardLimit=2): las DOS
    // entradas de la DERECHA quedan FIJAS en todos los pedidos (representan los saltos
    // internos + el IP real -inmutable- del que manda los pedidos), y SOLO la entrada de la
    // IZQUIERDA (la que un atacante podria inventar) cambia en cada intento. Si el prefijo
    // inventado alcanzara a pisar la IP real, cada intento abriria su PROPIO balde y ninguno
    // caeria en 429. Como ForwardLimit=2 corta ANTES de llegar a esa entrada (ver
    // ForwardedHeadersConfigurationTests para la prueba directa de esta semantica), los 11
    // intentos comparten balde igual, sin importar que puso el atacante a la izquierda.
    //
    // CORRECCION 2026-08-06 (segunda revision): antes este test no probaba lo que decia. El
    // TestServer no abre una conexion TCP real, asi que Connection.RemoteIpAddress llegaba en
    // NULL, el middleware de ForwardedHeaders descartaba el header entero (un peer desconocido
    // no es un proxy de confianza) y los 11 intentos caian en el balde "unknown" -> el 429
    // aparecia por motivos equivocados y el test habria pasado igual con la config rota. Ahora
    // se inyecta a mano una IP de peer PRIVADA (la del contenedor "web" en docker-compose), que
    // es la unica forma de que el middleware realmente procese el X-Forwarded-For en un test.
    [Fact]
    public async Task Login_WithForgedLeftmostForwardedForPerAttempt_StillSharesOneBucket()
    {
        await using var factory = new CustomWebApplicationFactory();
        await using var hostWithPrivatePeerIp = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(WebContainerPeerIp))));
        using var client = hostWithPrivatePeerIp.CreateClient();
        var payload = new
        {
            email = "otro-inexistente@example.com",
            password = "Invalid123!",
            rememberMe = false,
        };
        const string fixedRealAttackerIp = "198.51.100.50";
        const string fixedInternalHop = "127.0.0.1";

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt}, {fixedRealAttackerIp}, {fixedInternalHop}");

            using var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var limitedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(payload),
        };
        limitedRequest.Headers.Add("X-Forwarded-For", $"203.0.113.99, {fixedRealAttackerIp}, {fixedInternalHop}");

        using var limited = await client.SendAsync(limitedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // Control negativo del test de arriba (sin esto, aquel podria pasar por el motivo equivocado):
    // si el X-Forwarded-For NO se estuviera procesando, todos los pedidos caerian en un mismo balde
    // y el pedido 11 daria 429. Aca cambia la IP REAL (la que los nginx appendearon, anteultima
    // entrada) en cada intento: como cada IP real tiene su PROPIO balde, ninguno de los 11 llega a
    // 429. Es decir: este test solo puede pasar si la IP del cliente se esta leyendo de verdad.
    [Fact]
    public async Task Login_WithDifferentRealClientIpPerAttempt_EachOneGetsItsOwnBucket()
    {
        await using var factory = new CustomWebApplicationFactory();
        await using var hostWithPrivatePeerIp = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(WebContainerPeerIp))));
        using var client = hostWithPrivatePeerIp.CreateClient();
        var payload = new
        {
            email = "control@example.com",
            password = "Invalid123!",
            rememberMe = false,
        };

        // 11 = uno MAS que el limite de la politica "auth" (10/5min): si compartieran balde,
        // el ultimo caeria en 429.
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("X-Forwarded-For", $"203.0.113.1, 198.51.100.{attempt}, 127.0.0.1");

            using var response = await client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    // IP con la que "llega" el pedido al host de tests: una direccion del rango privado de
    // docker-compose, igual que la del contenedor "web" en produccion. Tiene que ser privada
    // para que ForwardedHeadersConfiguration la considere un salto de confianza y recien ahi
    // procese el X-Forwarded-For.
    private static readonly IPAddress WebContainerPeerIp = IPAddress.Parse("172.20.0.5");

    /// <summary>
    /// Mete un middleware ANTES de toda la tuberia de la app (eso hace un IStartupFilter) que
    /// fija la IP de conexion del pedido. Es la unica forma no invasiva de simular un peer real
    /// en un TestServer, que no usa sockets: sin esto, Connection.RemoteIpAddress es null.
    /// </summary>
    private sealed class FixedRemoteIpStartupFilter : IStartupFilter
    {
        private readonly IPAddress _remoteIpAddress;

        public FixedRemoteIpStartupFilter(IPAddress remoteIpAddress) => _remoteIpAddress = remoteIpAddress;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = _remoteIpAddress;
                await nextMiddleware();
            });

            next(app);
        };
    }
}
