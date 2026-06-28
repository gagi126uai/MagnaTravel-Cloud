using System;
using System.Net;
using System.Net.Http;
using System.Threading;
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
/// ADR-041 TANDA 4 — B1 (review backend+seguridad 2026-06-28): los endpoints de "reembolsos a cobrar del operador"
/// exponen el NOMBRE del cliente que origino cada anulacion. Por eso NO se gatean con <c>proveedores.view</c>
/// (que tiene el rol Vendedor -> fuga horizontal de clientes de otros), sino con <c>tesoreria.supplier_payments</c>.
///
/// <para>Tests: un usuario con SOLO <c>proveedores.view</c> recibe 403 en el endpoint por-operador Y en la bandeja
/// global; un usuario con <c>tesoreria.supplier_payments</c> recibe 200 en ambos. Patron de roles dedicados (no se
/// toca el rol Vendedor) igual que <c>OperatorRefundsControllerTests</c>.</para>
/// </summary>
public class OperatorRefundPendingAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ProvOnlyRole = "T4ProvViewOnly";
    private const string TreasuryOnlyRole = "T4TreasuryOnly";

    private readonly CustomWebApplicationFactory _factory;

    public OperatorRefundPendingAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Crea (idempotente) los 2 roles con sus permisos, un usuario por rol y un supplier. Devuelve sus ids.</summary>
    private async Task<(Guid SupplierPublicId, string ProvUserId, string TreasuryUserId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        await EnsureRoleWithPermissionAsync(db, roleMgr, ProvOnlyRole, Permissions.ProveedoresView);
        await EnsureRoleWithPermissionAsync(db, roleMgr, TreasuryOnlyRole, Permissions.TesoreriaSupplierPayments);

        var provUserId = await EnsureUserInRoleAsync(userMgr, "t4-prov", ProvOnlyRole);
        var treasuryUserId = await EnsureUserInRoleAsync(userMgr, "t4-treasury", TreasuryOnlyRole);

        var supplier = new Supplier { Name = "Operador T4 " + Guid.NewGuid().ToString("N")[..6], IsActive = true };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return (supplier.PublicId, provUserId, treasuryUserId);
    }

    private static async Task EnsureRoleWithPermissionAsync(
        AppDbContext db, RoleManager<IdentityRole> roleMgr, string role, string permission)
    {
        if (!await roleMgr.RoleExistsAsync(role))
            await roleMgr.CreateAsync(new IdentityRole(role));

        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == role && rp.Permission == permission))
        {
            db.RolePermissions.Add(new RolePermission { RoleName = role, Permission = permission });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<string> EnsureUserInRoleAsync(
        UserManager<ApplicationUser> userMgr, string idPrefix, string role)
    {
        var userId = idPrefix + "-" + role;
        if (await userMgr.FindByIdAsync(userId) is null)
        {
            var user = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@t.local",
                Email = userId + "@t.local",
                FullName = "User " + userId,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, role);
        }
        return userId;
    }

    private HttpClient ClientFor(string userId, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, role);
        return client;
    }

    [Fact]
    public async Task PerSupplier_WithProveedoresViewOnly_Returns403()
    {
        var seed = await SeedAsync();
        var client = ClientFor(seed.ProvUserId, ProvOnlyRole);

        var resp = await client.GetAsync($"/api/suppliers/{seed.SupplierPublicId}/operator-refunds/pending");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Global_WithProveedoresViewOnly_Returns403()
    {
        var seed = await SeedAsync();
        var client = ClientFor(seed.ProvUserId, ProvOnlyRole);

        var resp = await client.GetAsync("/api/operator-refunds/pending");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task PerSupplier_WithTreasuryPermission_Returns200()
    {
        var seed = await SeedAsync();
        var client = ClientFor(seed.TreasuryUserId, TreasuryOnlyRole);

        var resp = await client.GetAsync($"/api/suppliers/{seed.SupplierPublicId}/operator-refunds/pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Global_WithTreasuryPermission_Returns200()
    {
        var seed = await SeedAsync();
        var client = ClientFor(seed.TreasuryUserId, TreasuryOnlyRole);

        var resp = await client.GetAsync("/api/operator-refunds/pending");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
