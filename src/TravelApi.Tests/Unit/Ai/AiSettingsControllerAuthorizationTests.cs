using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// La puerta de "Configuracion → Inteligencia artificial": <b>solo Admin</b>, leer y escribir
/// (guarda de seguridad de §15.10 de la spec firmada 2026-08-07).
///
/// <para>El punto no es que la solapa no se dibuje para un vendedor —eso es cosmetico y se puede
/// saltear con el navegador—: el punto es que el SERVIDOR le cierre la puerta igual.</para>
/// </summary>
public class AiSettingsControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AiSettingsControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient BuildClientAs(string roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, roles);
        return client;
    }

    [Fact]
    public async Task UnVendedor_NoPuedeVerLaConfiguracionDeIa()
    {
        var client = BuildClientAs("Vendedor");

        var response = await client.GetAsync("/api/settings/ai");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnVendedor_NoPuedeGuardarLaConfiguracionDeIa()
    {
        var client = BuildClientAs("Vendedor");

        var response = await client.PutAsJsonAsync(
            "/api/settings/ai",
            new UpdateAiSettingsRequest { ProviderCode = "groq", ApiKey = "gsk_intento" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnVendedor_NoPuedeProbarLaConexion()
    {
        var client = BuildClientAs("Vendedor");

        var response = await client.PostAsJsonAsync(
            "/api/settings/ai/test-connection",
            new TestAiConnectionRequest { ProviderCode = "groq", ApiKey = "gsk_intento" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnAdmin_SiPuedeVerLaConfiguracion_YNoLeLlegaNingunaClave()
    {
        var client = BuildClientAs("Admin");

        var response = await client.GetAsync("/api/settings/ai");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Sin nada configurado, la foto dice "sin configurar" y no viaja ninguna clave.
        Assert.Contains("sinConfigurar", body);
        Assert.DoesNotContain("apiKey\":", body);
    }

    [Fact]
    public async Task UnAdmin_VeLaListaDeProveedores_SinLosQueNoSePuedenConectar()
    {
        var client = BuildClientAs("Admin");

        var response = await client.GetAsync("/api/settings/ai/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("groq", body);
        Assert.DoesNotContain("Copilot", body);
        Assert.DoesNotContain("Codex", body);
    }
}
