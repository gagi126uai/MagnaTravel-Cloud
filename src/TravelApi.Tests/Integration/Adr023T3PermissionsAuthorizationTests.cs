using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// ADR-023 TANDA T3: tests E2E del gating de permisos finos que antes faltaba.
///
/// Antes de T3, estos endpoints eran [Authorize] sin permiso fino -> cualquier
/// usuario autenticado leia el libro de caja completo, la cuenta de cualquier
/// cliente y el pipeline de Leads. T3 agrega:
///  - treasury (GET /summary, /cash-summary, /movements)  -> caja.view
///  - customers (lista, detalle, /account/*)              -> clientes.view (+ cobranzas.view en /account)
///  - leads (GET)                                          -> crm.view
///
/// Invariante INV-T3-1: un usuario SIN el permiso correspondiente recibe 403;
/// un usuario CON el permiso atraviesa el gate (no 403; puede dar 404/200 segun
/// exista o no la entidad, pero nunca 403 por falta de permiso).
///
/// Patron: identico a PaymentsControllerAuthorizationTests (CustomWebApplicationFactory
/// InMemory + TestAuthHandler + RolePermissions sembrados por rol). Se usa un rol
/// fresco SIN permisos para el caso 403 (fail-closed garantizado, independiente de
/// los seeds default) y el MISMO rol con el permiso sembrado para el caso "no 403"
/// (prueba que el gate es exactamente ese permiso, no el rol).
/// </summary>
public class Adr023T3PermissionsAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public Adr023T3PermissionsAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Crea un usuario en un rol fresco (sin permisos) y le siembra exactamente los
    /// permisos pedidos. Devuelve el userId para setear los headers del cliente.
    /// El rol es unico por llamada para que un test no herede permisos de otro.
    /// </summary>
    private async Task<string> SeedUserWithPermissionsAsync(string roleName, string userId, params string[] permissions)
    {
        using var scope = _factory.Services.CreateScope();
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
                FullName = "ADR-023 T3 Test User",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, roleName);
        }

        foreach (var perm in permissions)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == roleName && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = roleName, Permission = perm });
            }
        }
        await db.SaveChangesAsync();

        // El resolver de permisos cachea por TTL; invalidar para que el rol recien
        // sembrado se vea de inmediato en esta corrida.
        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return userId;
    }

    /// <summary>
    /// Crea un Customer real y devuelve su PublicId, para que los endpoints de cuenta
    /// que primero resuelven el id no respondan 404 en el caso "con permiso" y se pueda
    /// observar el 200/2xx real (no un 404 ambiguo).
    /// </summary>
    private async Task<Guid> SeedCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new Customer
        {
            PublicId = Guid.NewGuid(),
            FullName = "Cliente T3 " + Guid.NewGuid().ToString("N")[..6],
            IsActive = true
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.PublicId;
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

    // ----------------------------------------------------------------------
    // T3.1 — TreasuryController (caja.view)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GET_TreasuryMovements_WithoutCajaView_Returns403()
    {
        var role = "T3NoCaja-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-treas-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin caja.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/treasury/movements");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_TreasurySummary_WithoutCajaView_Returns403()
    {
        var role = "T3NoCaja-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-treas-sum-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin caja.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/treasury/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_TreasuryMovements_WithCajaView_NotForbidden()
    {
        var role = "T3Caja-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-treas-ok-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.CajaView);

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/treasury/movements");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_TreasuryMovements_AdminBypass_NotForbidden()
    {
        var client = _factory.CreateClient(); // sin headers -> default Admin
        var response = await client.GetAsync("/api/treasury/movements");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // T3.2 — CustomersController (clientes.view, + cobranzas.view en /account)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GET_Customers_WithoutClientesView_Returns403()
    {
        var role = "T3NoCli-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-cust-list-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin clientes.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/customers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_Customers_WithClientesView_NotForbidden()
    {
        var role = "T3Cli-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-cust-list-ok-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.ClientesView);

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/customers");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_CustomerAccount_WithoutClientesView_Returns403()
    {
        var customerPublicId = await SeedCustomerAsync();
        var role = "T3NoCli-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-acct-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin clientes.view ni cobranzas.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync($"/api/customers/{customerPublicId}/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_CustomerAccount_WithClientesViewButNoCobranzasView_Returns403()
    {
        // El AND apilado exige AMBOS: con clientes.view pero sin cobranzas.view, la
        // cuenta (que muestra montos) sigue cerrada.
        var customerPublicId = await SeedCustomerAsync();
        var role = "T3CliNoCob-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-acct-partial-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.ClientesView);

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync($"/api/customers/{customerPublicId}/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_CustomerAccount_WithBothPermissions_NotForbidden()
    {
        var customerPublicId = await SeedCustomerAsync();
        var role = "T3CliCob-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-acct-ok-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.ClientesView, Permissions.CobranzasView);

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync($"/api/customers/{customerPublicId}/account");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_CustomerAccountReservas_WithoutClientesView_Returns403()
    {
        var customerPublicId = await SeedCustomerAsync();
        var role = "T3NoCli-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-acctres-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin clientes.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync($"/api/customers/{customerPublicId}/account/reservas");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_Customer_WithoutClientesEdit_Returns403()
    {
        var role = "T3CliView-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-cust-create-deny-" + Guid.NewGuid().ToString("N")[..8];
        // Solo lectura: tiene clientes.view pero NO clientes.edit.
        await SeedUserWithPermissionsAsync(role, userId, Permissions.ClientesView);

        var client = CreateClientAs(userId, role);
        var body = new StringContent("{ \"FullName\": \"Nuevo\" }", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/customers", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----------------------------------------------------------------------
    // T3.3 — LeadsController (crm.view / crm.edit)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GET_Leads_WithoutCrmView_Returns403()
    {
        var role = "T3NoCrm-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-leads-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin crm.view

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/leads");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_Leads_WithCrmView_NotForbidden()
    {
        var role = "T3Crm-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-leads-ok-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.CrmView);

        var client = CreateClientAs(userId, role);
        var response = await client.GetAsync("/api/leads");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_Lead_WithCrmViewButNoCrmEdit_Returns403()
    {
        // Una escritura con solo crm.view (lectura) debe seguir cerrada.
        var role = "T3CrmView-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-leads-create-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId, Permissions.CrmView);

        var client = CreateClientAs(userId, role);
        var body = new StringContent("{ \"Name\": \"Lead Test\", \"Phone\": \"123\" }", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/leads", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_LeadWhatsAppMessage_WithoutCrmEdit_Returns403()
    {
        var role = "T3NoCrm-" + Guid.NewGuid().ToString("N")[..8];
        var userId = "t3-leadmsg-deny-" + Guid.NewGuid().ToString("N")[..8];
        await SeedUserWithPermissionsAsync(role, userId); // sin crm.edit

        var client = CreateClientAs(userId, role);
        var body = new StringContent("{ \"Message\": \"Hola\" }", Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"/api/leads/{Guid.NewGuid()}/whatsapp-message", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
