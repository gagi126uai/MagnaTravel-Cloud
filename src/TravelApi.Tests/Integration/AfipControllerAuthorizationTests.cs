using System;
using System.Net;
using System.Net.Http;
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
/// B1.15 Fase 0 (factura Vendedor 2026-05-10): tests E2E del gating del AfipController.
///
/// Antes el controller era [Authorize(Roles = "Admin")] a nivel de clase — el
/// Vendedor recibia 403 en GET /api/afip/settings al abrir el modal "emitir
/// factura" y veia la notificacion "No se pudo obtener la configuracion de AFIP",
/// rompiendo la decision 1 del roadmap B1.15 (el Vendedor SI factura).
///
/// Ahora:
///  - GET  /api/afip/status   -> RequirePermission(CobranzasInvoice)
///  - GET  /api/afip/settings -> RequirePermission(CobranzasInvoice) (el response
///         no expone secretos: cert binario y password nunca salen, tokens son
///         booleanos HasXxx).
///  - POST /api/afip/settings -> RequirePermission(ConfiguracionAfip) (Admin only,
///         protege cambios de cert/CUIT/punto de venta).
/// </summary>
public class AfipControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AfipControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> SeedVendedorWithCobranzasInvoiceAsync(string suffix)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var userId = "vend-afip-" + suffix;
        var existing = await userMgr.FindByIdAsync(userId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@test.local",
                Email = userId + "@test.local",
                FullName = "Vendedor Afip Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        if (!await db.RolePermissions.AnyAsync(rp =>
                rp.RoleName == "Vendedor" && rp.Permission == Permissions.CobranzasInvoice))
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleName = "Vendedor",
                Permission = Permissions.CobranzasInvoice
            });
            await db.SaveChangesAsync();
        }

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return userId;
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task GET_Settings_VendedorWithCobranzasInvoice_NotForbidden()
    {
        var userId = await SeedVendedorWithCobranzasInvoiceAsync(Guid.NewGuid().ToString("N")[..8]);

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, userId);

        var resp = await client.GetAsync("/api/afip/settings");

        // El Vendedor pasa el gating: el endpoint responde 200 (si hay settings
        // seedeados) o 404 (si no hay). Lo critico: NO debe ser 403 Forbidden.
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Settings_VendedorWithoutConfiguracionAfip_Returns403()
    {
        var userId = await SeedVendedorWithCobranzasInvoiceAsync(Guid.NewGuid().ToString("N")[..8]);

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, userId);

        // POST sin form data: la policy de autorizacion corre antes del model
        // binding, por lo que esperamos 403 (no 415 ni 400).
        var resp = await client.PostAsync("/api/afip/settings", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Settings_AdminDefault_NotForbidden()
    {
        var client = _factory.CreateClient();
        // Sin headers: defaults a Admin (TestAuthHandler).
        var resp = await client.GetAsync("/api/afip/settings");
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
