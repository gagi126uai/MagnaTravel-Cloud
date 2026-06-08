using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// Bugfix 2026-06-06: tests de <c>GET /api/settings/operational-flags</c>, el endpoint liviano
/// que reemplaza a <c>GET /afip/settings</c> como fuente de flags para el frontend.
///
/// <para>El bug original tenia dos mitades: (1) /afip/settings no proyectaba
/// <c>EnableCatalogFindOrCreate</c>, y (2) estaba gateado por el permiso <c>cobranzas.invoice</c>,
/// asi que un vendedor sin ese permiso recibia 403 y veia todos los flags en false. Por eso aca
/// lo central es: un Vendedor SIN NINGUN permiso recibe 200 con los 5 flags reales, y el response
/// no filtra ningun dato fiscal (shape = exactamente 5 booleanos).</para>
///
/// <para>Corren con CustomWebApplicationFactory (host completo + InMemory), igual que los tests
/// de Cancellation/Http; el namespace NO contiene "Integration" a proposito para que entren en
/// la suite unit (filtro <c>FullyQualifiedName!~Integration</c>).</para>
/// </summary>
public class OperationalFlagsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OperationalFlagsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Deja la fila de settings (compartida por toda la factory) en un estado conocido.
    /// Cada test setea TODOS los flags que le importan para no depender del orden de ejecucion.
    /// </summary>
    private async Task SetFlagsAsync(
        bool multiCurrency,
        bool cancellationDebitNote,
        bool catalogFindOrCreate,
        bool serviceDeadlineAlerts)
    {
        using var scope = _factory.Services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableMultiCurrencyInvoicing = multiCurrency;
        settings.EnableCancellationDebitNote = cancellationDebitNote;
        settings.EnableCatalogFindOrCreate = catalogFindOrCreate;
        settings.EnableServiceDeadlineAlerts = serviceDeadlineAlerts;
        await db.SaveChangesAsync();
    }

    /// <summary>Cliente autenticado como Vendedor SIN ningun permiso (el caso del bug: antes 403).</summary>
    private HttpClient CreateVendedorClientWithoutPermissions()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, "vend-flags-1");
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
        // A proposito NO se manda X-Test-User-Permissions: cero permisos.
        return client;
    }

    [Fact]
    public async Task Get_VendedorWithoutAnyPermission_Returns200WithRealFlagValues()
    {
        // Mezcla a proposito (catalogo ON, ND ON, resto OFF) para detectar proyecciones cruzadas.
        await SetFlagsAsync(
            multiCurrency: false,
            cancellationDebitNote: true,
            catalogFindOrCreate: true,
            serviceDeadlineAlerts: false);

        var client = CreateVendedorClientWithoutPermissions();
        var response = await client.GetAsync("/api/settings/operational-flags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.False(root.GetProperty("enableMultiCurrencyInvoicing").GetBoolean());
        Assert.True(root.GetProperty("enableCancellationDebitNote").GetBoolean());
        Assert.True(root.GetProperty("enableCatalogFindOrCreate").GetBoolean());
        Assert.False(root.GetProperty("enableServiceDeadlineAlerts").GetBoolean());
    }

    [Fact]
    public async Task Get_AllFlagsOff_ReturnsAllFalse()
    {
        await SetFlagsAsync(
            multiCurrency: false,
            cancellationDebitNote: false,
            catalogFindOrCreate: false,
            serviceDeadlineAlerts: false);

        var client = CreateVendedorClientWithoutPermissions();
        var response = await client.GetAsync("/api/settings/operational-flags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var property in body.RootElement.EnumerateObject())
        {
            Assert.False(property.Value.GetBoolean());
        }
    }

    /// <summary>
    /// Guarda anti-fuga: el JSON tiene EXACTAMENTE las 5 keys esperadas y nada mas.
    /// Si alguien proyecta un campo extra (CUIT, dias, montos), este test rompe.
    /// </summary>
    [Fact]
    public async Task Get_ResponseShape_ExactlyFiveBooleanKeys_NoFiscalData()
    {
        await SetFlagsAsync(
            multiCurrency: true,
            cancellationDebitNote: true,
            catalogFindOrCreate: true,
            serviceDeadlineAlerts: true);

        var client = CreateVendedorClientWithoutPermissions();
        var response = await client.GetAsync("/api/settings/operational-flags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = body.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        var expectedKeys = new[]
        {
            "enableCancellationDebitNote",
            "enableCatalogFindOrCreate",
            "enableMultiCurrencyInvoicing",
            "enableServiceDeadlineAlerts",
        };
        Assert.Equal(expectedKeys, keys);

        foreach (var property in body.RootElement.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.True, property.Value.ValueKind);
        }
    }

    /// <summary>
    /// Misma guarda pero a nivel de tipo (no requiere host): el DTO solo puede tener propiedades
    /// bool. Evita que un refactor agregue un campo no-booleano "inocente" (p. ej. dias de alerta)
    /// que despues alguien proyecte en el controller.
    /// </summary>
    [Fact]
    public void OperationalFlagsResponse_AllPublicPropertiesAreBoolean()
    {
        var properties = typeof(OperationalFlagsResponse).GetProperties();

        // ADR-020: bajo de 5 a 4 (murio EnableSoldToSettleStates).
        Assert.Equal(4, properties.Length);
        Assert.All(properties, p => Assert.Equal(typeof(bool), p.PropertyType));
    }
}
