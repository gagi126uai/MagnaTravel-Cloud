using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Ai;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// "Probar conexion" (M-31): que cada cosa que puede pasar termine en el codigo correcto y que
/// <b>nunca</b> se filtre lo que dijo el proveedor.
///
/// <para>No se llama a ningun proveedor real: se intercepta el pedido HTTP.</para>
/// </summary>
public class AiConnectionTesterTests
{
    /// <summary>
    /// Intercepta el pedido HTTP y contesta lo que le digan, sin salir a la red. Ademas cuenta
    /// cuantas veces lo llamaron, para verificar los casos en los que NI SIQUIERA hay que intentar.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public int CallCount { get; private set; }

        public StubHandler(HttpStatusCode statusCode, string body = "{}")
            : this(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(body) })
        {
        }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private static AiConnectionTester BuildTester(StubHandler handler, AiEndpointGuard? guard = null) =>
        new(new HttpClient(handler), guard ?? AiTestDoubles.BuildGuard(), NullLogger<AiConnectionTester>.Instance);

    private static AiConnectionProbe GroqProbe(string? apiKey = "gsk_clave") =>
        new("https://api.groq.com/openai/v1", apiKey, "llama-3.3-70b-versatile");

    [Fact]
    public async Task ElProveedorContesta_EsOk()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.Ok, result.ResultCode);
        Assert.True(result.ElapsedMilliseconds >= 0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ElProveedorRechazaLaCredencial_EsClaveInvalida(HttpStatusCode statusCode)
    {
        var handler = new StubHandler(statusCode, "{\"error\":{\"message\":\"Invalid API Key provided: gsk_1234\"}}");

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.InvalidKey, result.ResultCode);
    }

    [Fact]
    public async Task ModeloQueNoExiste_EsModeloInexistente()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"model not found\"}}");

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.ModelNotFound, result.ResultCode);
    }

    [Fact]
    public async Task ModeloQueNoExiste_CuandoElProveedorLoDiceComoPedidoInvalido_TambienEsModeloInexistente()
    {
        var handler = new StubHandler(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"model_not_found\",\"message\":\"The model `xyz` does not exist\"}}");

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.ModelNotFound, result.ResultCode);
    }

    [Fact]
    public async Task ProveedorCaido_EsNoResponde()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.NoResponse, result.ResultCode);
    }

    [Fact]
    public async Task RedCaida_EsNoResponde()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.NoResponse, result.ResultCode);
    }

    [Fact]
    public async Task SinClave_NiSeIntenta()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        var result = await BuildTester(handler).TestAsync(GroqProbe(apiKey: null), CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.InvalidKey, result.ResultCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DireccionInterna_NiSeIntenta()
    {
        // El candado de verdad: si el revisor de direcciones dice que no, el servidor NO abre
        // ninguna conexion. Es lo que impide usar este boton para espiar la red interna.
        var handler = new StubHandler(HttpStatusCode.OK);
        var probe = new AiConnectionProbe("https://169.254.169.254/latest/meta-data", "clave", "modelo");

        var result = await BuildTester(handler).TestAsync(probe, CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.InvalidAddress, result.ResultCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task DireccionSinHttps_NiSeIntenta()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var probe = new AiConnectionProbe("http://api.groq.com/openai/v1", "clave", "modelo");

        var result = await BuildTester(handler).TestAsync(probe, CancellationToken.None);

        Assert.Equal(AiConnectionTestCodes.InvalidAddress, result.ResultCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task UnaRedireccionHaciaLaRedInterna_NoSeSigue()
    {
        // El agujero que cierra: si el cliente HTTP siguiera las redirecciones (que es lo que hace
        // por default), un servidor de afuera podia contestar "seguime a https://169.254.169.254/"
        // y el servidor terminaba pegandole igual a la red interna, esquivando por completo la
        // revision de direccion. Con las redirecciones apagadas, un 302 es una respuesta mas.
        var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent(string.Empty) };
            response.Headers.Location = new Uri("https://169.254.169.254/latest/meta-data");
            return response;
        });

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        // UN solo pedido: nadie fue atras del "seguime". Y para el usuario, el proveedor no contesto.
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(AiConnectionTestCodes.NoResponse, result.ResultCode);
    }

    [Fact]
    public async Task UnaClaveConSaltoDeLinea_NoRompeElPedidoNiFiltraNada()
    {
        // Una cabecera HTTP con un salto de linea adentro es un pedido invalido. Antes esto tiraba
        // una excepcion que terminaba en el manejador global CON la clave en el mensaje.
        var handler = new StubHandler(HttpStatusCode.OK);
        var probe = new AiConnectionProbe(
            "https://api.groq.com/openai/v1", "gsk_clave\ncon_salto", "llama-3.3-70b-versatile");

        var result = await BuildTester(handler).TestAsync(probe, CancellationToken.None);

        // O sale limpia (se le sacaron los saltos) o se contesta "la clave no sirve": nunca explota.
        Assert.Contains(
            result.ResultCode,
            new[] { AiConnectionTestCodes.Ok, AiConnectionTestCodes.InvalidKey });
    }

    [Fact]
    public async Task ElResultado_NoTraeNadaDelProveedor()
    {
        // Candado de exposicion de datos: el contrato de salida tiene DOS campos, un codigo de una
        // lista cerrada y un numero. No hay por donde se cuele el mensaje del proveedor.
        var handler = new StubHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Incorrect API key provided: gsk_SECRETO. You can find your API key at...\"}}");

        var result = await BuildTester(handler).TestAsync(GroqProbe(), CancellationToken.None);

        var propertyNames = typeof(AiConnectionTestResultDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "ResultCode", "ElapsedMilliseconds" }, propertyNames);
        Assert.DoesNotContain("SECRETO", result.ResultCode, StringComparison.OrdinalIgnoreCase);
    }
}
