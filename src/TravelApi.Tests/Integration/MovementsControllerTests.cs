using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// B1.15 Fase D' (2026-05-11): tests E2E del endpoint /api/movements.
///
/// Cubre:
///  - Admin → lista movimientos de cualquier reserva.
///  - Filter por reservaId → solo movimientos de esa reserva.
///  - Filter por kinds → solo el subset pedido.
///  - Vendedor con cobranzas.view → solo ve movimientos de SUS reservas (filter mine).
///  - Sin permiso cobranzas.view → 403.
/// </summary>
public class MovementsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MovementsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<SeedData> SeedAsync(string vendedorSuffix)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var vendedorId = "vend-mov-" + vendedorSuffix;
        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId, UserName = vendedorId + "@t", Email = vendedorId + "@t",
                FullName = "v", IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == Permissions.CobranzasView))
        {
            db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = Permissions.CobranzasView });
            await db.SaveChangesAsync();
        }

        var ownReserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Own " + Guid.NewGuid().ToString("N")[..6],
            NumeroReserva = "MOV-OWN-" + Guid.NewGuid().ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            Status = EstadoReserva.Confirmed
        };
        var ajenaReserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Ajena " + Guid.NewGuid().ToString("N")[..6],
            NumeroReserva = "MOV-OTH-" + Guid.NewGuid().ToString("N")[..6],
            ResponsibleUserId = "owner-other",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.AddRange(ownReserva, ajenaReserva);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
        {
            ReservaId = ownReserva.Id, Amount = 500m, PaidAt = DateTime.UtcNow.AddDays(-1),
            Method = "Transfer", Status = "Paid", EntryType = PaymentEntryTypes.Payment,
        });
        db.Payments.Add(new Payment
        {
            ReservaId = ajenaReserva.Id, Amount = 750m, PaidAt = DateTime.UtcNow.AddDays(-2),
            Method = "Cash", Status = "Paid", EntryType = PaymentEntryTypes.Payment,
        });
        db.Invoices.Add(new Invoice
        {
            ReservaId = ownReserva.Id, TipoComprobante = 6, PuntoDeVenta = 1,
            NumeroComprobante = 1, ImporteTotal = 1000m, Resultado = "A",
            CAE = "1", VencimientoCAE = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return new SeedData(vendedorId, ownReserva.PublicId, ownReserva.Id, ajenaReserva.PublicId, ajenaReserva.Id);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task GET_Movements_Admin_ReturnsAll()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient(); // default Admin

        var resp = await client.GetFromJsonAsync<PagedResponse<MovementDto>>("/api/movements");
        Assert.NotNull(resp);
        // 2 pagos + 1 factura = 3 movimientos minimo (mas de otras seeds compartidas).
        Assert.True(resp!.TotalCount >= 3);
    }

    [Fact]
    public async Task GET_Movements_FilterByReservaId_ReturnsOnlyThat()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();

        var resp = await client.GetFromJsonAsync<PagedResponse<MovementDto>>(
            $"/api/movements?reservaId={seed.OwnReservaLegacyId}");
        Assert.NotNull(resp);
        Assert.All(resp!.Items, m => Assert.Equal(seed.OwnReservaLegacyId, m.ReservaLegacyId));
        Assert.Contains(resp.Items, m => m.Kind == "payment");
        Assert.Contains(resp.Items, m => m.Kind == "invoice");
    }

    [Fact]
    public async Task GET_Movements_FilterByKind_OnlyInvoices()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();

        var resp = await client.GetFromJsonAsync<PagedResponse<MovementDto>>(
            $"/api/movements?reservaId={seed.OwnReservaLegacyId}&kinds=invoice");
        Assert.NotNull(resp);
        Assert.All(resp!.Items, m => Assert.Equal("invoice", m.Kind));
    }

    [Fact]
    public async Task GET_Movements_Vendedor_OnlyOwnReserva()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetFromJsonAsync<PagedResponse<MovementDto>>("/api/movements");
        Assert.NotNull(resp);
        // No deberia incluir la reserva ajena.
        Assert.DoesNotContain(resp!.Items, m => m.ReservaLegacyId == seed.AjenaReservaLegacyId);
    }

    [Fact]
    public async Task GET_Movements_WithoutPermission_Returns403()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);

        // Crear vendedor SIN permiso cobranzas.view.
        using (var scope = _factory.Services.CreateScope())
        {
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleMgr.RoleExistsAsync("Sin"))
                await roleMgr.CreateAsync(new IdentityRole("Sin"));
            var userId = "no-perm-" + Guid.NewGuid().ToString("N")[..6];
            var user = new ApplicationUser { Id = userId, UserName = userId + "@t", Email = userId + "@t", FullName = "x", IsActive = true };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Sin");
            scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Sin");
            var resp = await client.GetAsync("/api/movements");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    private sealed record SeedData(
        string VendedorId,
        Guid OwnReservaPublicId,
        int OwnReservaLegacyId,
        Guid AjenaReservaPublicId,
        int AjenaReservaLegacyId);
}
