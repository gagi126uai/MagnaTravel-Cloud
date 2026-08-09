using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Ai;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Los dos candados de los clientes HTTP de la IA, verificados sobre la configuracion REAL que arma
/// <c>Program.cs</c> (no sobre una copia escrita en el test):
///
/// <list type="number">
///   <item><b>No seguir redirecciones</b>: sin esto, un servidor de afuera contesta "seguime a
///   https://169.254.169.254/" y el servidor termina pegandole a la red interna, esquivando por
///   completo la revision de direccion.</item>
///   <item><b>Tachar la cabecera <c>Authorization</c> en los logs</b>: es donde viaja la clave del
///   proveedor; sin esto, subir el detalle del log del cliente HTTP la escribiria en el archivo.</item>
/// </list>
/// </summary>
public class AiHttpClientsHardeningTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiHttpClientsHardeningTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Truco de framework: <c>AddHttpClient&lt;TCliente, TImplementacion&gt;()</c> registra la
    /// configuracion bajo un NOMBRE (el del tipo del cliente). Para leerla se pide
    /// <c>HttpClientFactoryOptions</c> con ese nombre y se corren sus "acciones de armado" sobre un
    /// constructor de handlers propio: lo que quede ahi es exactamente lo que usa la app.
    /// </summary>
    private sealed class InspectableHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new SocketsHttpHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    private (HttpMessageHandler Primary, HttpClientFactoryOptions Options) BuildConfigurationFor(string clientName)
    {
        var options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(clientName);

        var builder = new InspectableHandlerBuilder { Name = clientName };
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        return (builder.PrimaryHandler, options);
    }

    [Theory]
    // Los dos clientes tipados de la IA: el que habla con el modelo y el que prueba la conexion.
    [InlineData(nameof(IAiChatProvider))]
    [InlineData(nameof(IAiConnectionTester))]
    public void LosClientesDeIa_NoSiguenRedirecciones(string clientName)
    {
        var (primary, _) = BuildConfigurationFor(clientName);

        var socketsHandler = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.False(socketsHandler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(nameof(IAiChatProvider))]
    [InlineData(nameof(IAiConnectionTester))]
    public void LosClientesDeIa_TachanLaCabeceraDondeViajaLaClave(string clientName)
    {
        var (_, options) = BuildConfigurationFor(clientName);

        Assert.Contains(
            "Authorization",
            options.ShouldRedactHeaderValue is null
                ? new List<string>()
                : new List<string> { "Authorization" }.Where(header => options.ShouldRedactHeaderValue(header)).ToList());
    }
}
