using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TravelApi.Infrastructure.Ai;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// El candado de la direccion (guarda de seguridad de §15.10).
///
/// <para><b>Que se esta cuidando, en criollo</b>: el boton "Probar conexion" hace que el SERVIDOR
/// abra una conexion a una direccion que escribe una persona. Sin control, ese boton sirve para
/// espiar la red interna del servidor — bases de datos, paneles internos, o el servicio de la nube
/// que en 169.254.169.254 entrega credenciales de la maquina.</para>
///
/// <para>La resolucion de nombres esta simulada a proposito: la suite no puede depender de que la
/// maquina que la corre tenga internet.</para>
/// </summary>
public class AiEndpointGuardTests
{
    private static AiEndpointGuard GuardWith(params (string Host, string Address)[] entries)
    {
        var map = new Dictionary<string, string>();
        foreach (var entry in entries)
        {
            map[entry.Host] = entry.Address;
        }

        return new AiEndpointGuard((host, _) =>
        {
            if (!map.TryGetValue(host, out var address))
            {
                return Task.FromResult(System.Array.Empty<IPAddress>());
            }

            return Task.FromResult(new[] { IPAddress.Parse(address) });
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.groq.com/openai/v1")]          // sin esquema: no es una direccion completa
    [InlineData("http://api.groq.com/openai/v1")]   // sin https la clave viajaria en claro
    [InlineData("ftp://api.groq.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://usuario:clave@api.groq.com/v1")] // credenciales metidas en la direccion
    public async Task DireccionesMalArmadas_SeRechazan(string? baseUrl)
    {
        var guard = GuardWith(("api.groq.com", "93.184.216.34"));

        var verdict = await guard.CheckAsync(baseUrl, CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.Malformed, verdict);
    }

    [Theory]
    [InlineData("https://127.0.0.1/v1")]              // la propia maquina
    [InlineData("https://10.0.0.5/v1")]               // red privada
    [InlineData("https://172.16.3.9/v1")]             // red privada
    [InlineData("https://192.168.1.10/v1")]           // red de oficina
    [InlineData("https://169.254.169.254/latest/")]   // metadatos de la nube: el peor caso
    [InlineData("https://100.72.0.1/v1")]             // red compartida del proveedor de internet
    [InlineData("https://[::1]/v1")]                  // la propia maquina, en IPv6
    [InlineData("https://[fd00::1]/v1")]              // red privada IPv6
    [InlineData("https://[fe80::1]/v1")]              // link-local IPv6
    [InlineData("https://[::ffff:10.0.0.5]/v1")]      // red privada disfrazada de IPv6
    public async Task DireccionesDeLaRedInterna_SeRechazan(string baseUrl)
    {
        var guard = GuardWith();

        var verdict = await guard.CheckAsync(baseUrl, CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.PrivateOrInternal, verdict);
    }

    [Fact]
    public async Task LocalhostPorNombre_TambienSeRechaza()
    {
        var guard = GuardWith(("localhost", "127.0.0.1"));

        var verdict = await guard.CheckAsync("https://localhost/v1", CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.PrivateOrInternal, verdict);
    }

    [Fact]
    public async Task UnNombreQueApuntaAdentro_TambienSeRechaza()
    {
        // El disfraz mas comun: un nombre que parece de internet pero resuelve a la red interna.
        var guard = GuardWith(("panel.interno.example", "10.1.2.3"));

        var verdict = await guard.CheckAsync("https://panel.interno.example/v1", CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.PrivateOrInternal, verdict);
    }

    [Fact]
    public async Task UnNombreQueNoResuelve_QuedaComoNoResuelve()
    {
        var guard = GuardWith();

        var verdict = await guard.CheckAsync("https://no-existe.example/v1", CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.Unresolvable, verdict);
    }

    [Fact]
    public async Task UnaDireccionDeInternetDeVerdad_Pasa()
    {
        var guard = GuardWith(("api.groq.com", "104.18.5.10"));

        var verdict = await guard.CheckAsync("https://api.groq.com/openai/v1", CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.Ok, verdict);
    }

    [Fact]
    public async Task UnaDireccionNumericaPublica_Pasa()
    {
        var guard = GuardWith();

        var verdict = await guard.CheckAsync("https://8.8.8.8/v1", CancellationToken.None);

        Assert.Equal(AiEndpointVerdict.Ok, verdict);
    }
}
