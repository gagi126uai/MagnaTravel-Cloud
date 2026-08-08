using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Quien puede TOCAR el tarifario (decision firmada 2026-08-06): cargar un producto a mano y renombrarlo
/// exigen <c>tarifario.edit</c>. Mirar el tarifario sigue siendo <c>tarifario.view</c>.
///
/// <para><b>Por que importa</b>: el tarifario es la memoria de precios de TODA la agencia — un producto
/// inventado o un renombre equivocado le cambia las sugerencias a todos los vendedores. Los roles default
/// NO reciben <c>tarifario.edit</c> (solo el Admin, que ademas pasa cualquier permiso por ser Admin).</para>
///
/// <para>Invariante: sin el permiso => 403. Con el permiso => atraviesa el gate (puede dar 201/400/404/409
/// segun el caso, pero NUNCA 403).</para>
/// </summary>
public class TarifarioEdicionAuthorizationTests : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    /// <summary>
    /// IP de conexion PROPIA de esta clase (rango privado, como el contenedor "web"). Cada suite que pega
    /// por HTTP usa la suya: asi los baldes del limitador de pedidos se reparten por clase y dos suites no
    /// pueden gastarse el limite entre ellas. Patron copiado de RateLimitingTests.
    /// </summary>
    private static readonly IPAddress SuitePeerIp = IPAddress.Parse("172.20.0.77");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly CustomWebApplicationFactory _baseFactory;

    public TarifarioEdicionAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _baseFactory = factory;
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new FixedRemoteIpStartupFilter(SuitePeerIp))));
    }

    /// <summary>
    /// Al terminar la clase se limpia el cache de permisos que sembraron estos tests. Los roles/permisos
    /// que crea esta suite son suyos (nombres unicos), pero el cache del resolver es del proceso: dejarlo
    /// sucio es la clase de detalle que despues aparece como un test ajeno fallando "sin motivo".
    /// </summary>
    public void Dispose()
    {
        using var scope = _baseFactory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Fija la IP de conexion del pedido antes de toda la tuberia (un TestServer no usa sockets, asi que
    /// sin esto <c>Connection.RemoteIpAddress</c> es null y todas las suites caen en la misma particion).
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

    private async Task<string> SeedUserWithPermissionsAsync(string roleName, string userId, params string[] permissions)
    {
        using var scope = _baseFactory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync(roleName))
        {
            await roleMgr.CreateAsync(new IdentityRole(roleName));
        }

        var existing = await userMgr.FindByIdAsync(userId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@test.local",
                Email = userId + "@test.local",
                FullName = "Tarifario Test User",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, roleName);
        }

        foreach (var permission in permissions)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == roleName && rp.Permission == permission))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = roleName, Permission = permission });
            }
        }
        await db.SaveChangesAsync();

        // El resolver cachea por TTL: se invalida para que el rol recien sembrado valga ya mismo.
        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return userId;
    }

    private HttpClient CreateClientAs(string userId, string roleName)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, roleName);
        return client;
    }

    private static CreateSimpleProductRequest NuevoHotel() => new()
    {
        ServiceType = "Hotel",
        Name = "Hotel permiso " + Guid.NewGuid().ToString("N")[..6],
        City = "Posadas",
        Price = 100m
    };

    // ----------------------------------------------------------------------
    // Alta simple de producto
    // ----------------------------------------------------------------------

    [Fact]
    public async Task POST_AltaSimple_SinTarifarioEdit_Devuelve403()
    {
        var role = "TarSoloVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-deny-" + Guid.NewGuid().ToString("N")[..8];
        // Tiene tarifario.view (puede MIRAR) pero no tarifario.edit.
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        var response = await client.PostAsJsonAsync("/api/rates/simple", NuevoHotel());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_AltaSimple_ConTarifarioEdit_AtraviesaElGate()
    {
        var role = "TarEdita-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-allow-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        var response = await client.PostAsJsonAsync("/api/rates/simple", NuevoHotel());

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // Renombrar producto
    // ----------------------------------------------------------------------

    [Fact]
    public async Task POST_Renombrar_SinTarifarioEdit_Devuelve403()
    {
        var role = "TarRenSoloVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-ren-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        var response = await client.PostAsJsonAsync("/api/rates/learned-products/rename", new RenameLearnedProductRequest
        {
            ServiceType = "Hotel",
            Name = "Cualquiera",
            City = "Posadas",
            NewName = "Otro nombre",
            NewCity = "Posadas"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_Renombrar_ConTarifarioEdit_AtraviesaElGate()
    {
        var role = "TarRenEdita-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-ren-allow-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        // Producto que no existe: lo que importa es que NO corte por permiso (404, no 403).
        var response = await client.PostAsJsonAsync("/api/rates/learned-products/rename", new RenameLearnedProductRequest
        {
            ServiceType = "Hotel",
            Name = "Producto inexistente " + Guid.NewGuid().ToString("N")[..6],
            City = "Posadas",
            NewName = "Nombre nuevo",
            NewCity = "Posadas"
        });

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // Editar una tarifa (formulario largo): tambien es tarifario.edit
    // ----------------------------------------------------------------------

    [Fact]
    public async Task PUT_Tarifa_SinTarifarioEdit_Devuelve403()
    {
        var role = "TarPutSoloVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-put-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        var response = await client.PutAsJsonAsync($"/api/rates/{Guid.NewGuid()}", new
        {
            serviceType = "Hotel",
            productName = "Hotel editado",
            netCost = 100m,
            tax = 0m,
            salePrice = 150m,
            isActive = true
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PUT_Tarifa_ConTarifarioEdit_AtraviesaElGate()
    {
        var role = "TarPutEdita-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-put-allow-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        // Tarifa inexistente: lo que importa es que NO corte por permiso (404, no 403).
        var response = await client.PutAsJsonAsync($"/api/rates/{Guid.NewGuid()}", new
        {
            serviceType = "Hotel",
            productName = "Hotel editado",
            netCost = 100m,
            tax = 0m,
            salePrice = 150m,
            isActive = true
        });

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // Ordenar el tarifario (unir repetidos, deshacer, corregir habitación): tarifario.edit
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("/api/rates/duplicates/merge")]
    [InlineData("/api/rates/duplicates/not-duplicates")]
    [InlineData("/api/rates/librarian/tidy-up")]
    [InlineData("/api/rates/learned-products/variants/rename")]
    [InlineData("/api/rates/tidy-up-log/11111111-1111-1111-1111-111111111111/undo")]
    public async Task POST_OrdenarElTarifario_SinTarifarioEdit_Devuelve403(string url)
    {
        var role = "TarOrdenSoloVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-orden-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        // El cuerpo da igual: el permiso se chequea ANTES de mirar nada del contenido.
        var response = await client.PostAsJsonAsync(url, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_PasadaDelBibliotecario_ConTarifarioEdit_AtraviesaElGate()
    {
        var role = "TarOrdenEdita-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-orden-allow-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        var response = await client.PostAsync("/api/rates/librarian/tidy-up", content: null);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Un movimiento que no existe responde "no lo encontramos" (404), NUNCA un error técnico.
    /// </summary>
    [Fact]
    public async Task POST_Deshacer_DeUnMovimientoQueNoExiste_Devuelve404Criollo()
    {
        var role = "TarUndo404-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-undo-404-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        var response = await client.PostAsync($"/api/rates/tidy-up-log/{Guid.NewGuid()}/undo", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MensajeDeRespuesta>();
        Assert.Equal("No encontramos ese movimiento para deshacer.", body?.Message);
    }

    /// <summary>
    /// Un movimiento que YA no se puede deshacer (hubo ventas nuevas encima) tiene que responder 409 con la
    /// frase en criollo — no un 500 genérico, que dejaba al usuario sin saber qué pasó.
    /// </summary>
    [Fact]
    public async Task POST_Deshacer_CuandoYaNoSePuede_Devuelve409ConElMotivoEnCriollo()
    {
        var role = "TarUndo409-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-undo-409-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView, Permissions.TarifarioEdit);
        var client = CreateClientAs(userId, role);

        var actionPublicId = await SeedUnionConVentaNuevaEncimaAsync();

        var response = await client.PostAsync($"/api/rates/tidy-up-log/{actionPublicId}/undo", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MensajeDeRespuesta>();
        Assert.Equal("Después de esto hubo ventas nuevas; ya no se puede deshacer solo.", body?.Message);
    }

    /// <summary>El cuerpo que devuelven los endpoints del tarifario cuando algo no se puede hacer.</summary>
    private sealed record MensajeDeRespuesta(string Message);

    /// <summary>
    /// Siembra dos hoteles repetidos, los une, y después hace que entre una venta nueva sobre el precio
    /// que la unión movió: ese es el escenario en el que deshacer deja de ser fiel.
    /// </summary>
    private async Task<Guid> SeedUnionConVentaNuevaEncimaAsync()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..6];
        using var scope = _baseFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var librarian = scope.ServiceProvider.GetRequiredService<ICatalogLibrarianService>();

        var supplier = new Supplier { Name = "Ola " + sufijo };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var survivor = HotelDePrueba($"Sheraton {sufijo}", "Puerto Iguazú");
        var absorbed = HotelDePrueba($"Sheraton {sufijo} - Doble Superior", "Puerto Iguazú");
        db.Rates.AddRange(survivor, absorbed);
        await db.SaveChangesAsync();

        db.RateSupplierSales.Add(new RateSupplierSale
        {
            RateId = absorbed.Id,
            SupplierId = supplier.Id,
            LastSoldAt = DateTime.UtcNow.AddDays(-3),
            LastNetCost = 55m,
            LastSalePrice = 75m,
            LastCurrency = "USD",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1
        });
        await db.SaveChangesAsync();

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        // Venta nueva encima de la fila que la unión movió.
        var movida = await db.RateSupplierSales.SingleAsync(sale => sale.RateId == survivor.Id);
        movida.LastSoldAt = DateTime.UtcNow.AddMinutes(5);
        movida.LastNetCost = 60m;
        await db.SaveChangesAsync();

        return merge.TidyUpActionPublicId;
    }

    private static Rate HotelDePrueba(string name, string city) => new()
    {
        ServiceType = "Hotel",
        ProductName = name,
        HotelName = name,
        City = city,
        MealPlan = "Desayuno",
        NetCost = 100m,
        SalePrice = 160m,
        Currency = "USD",
        PriceUnit = "noche_habitacion",
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-60)
    };

    [Theory]
    [InlineData("/api/rates/duplicates")]
    [InlineData("/api/rates/tidy-up-log")]
    [InlineData("/api/rates/variant-names?serviceType=Hotel")]
    public async Task GET_LasPantallasDeRevision_ConSoloTarifarioView_Funcionan(string url)
    {
        var role = "TarRevisaVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-revisa-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // Mirar el tarifario NO cambio: sigue alcanzando tarifario.view
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GET_ProductosAprendidos_ConSoloTarifarioView_Funciona()
    {
        var role = "TarVer-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "tar-ver-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.TarifarioView);
        var client = CreateClientAs(userId, role);

        var response = await client.GetAsync("/api/rates/learned-products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
