using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
/// B1.15 Fase 0.2 — masking de costos en POST/PUT de HotelBookings.
///
/// Antes el backend exponia <c>NetCost</c> del proveedor en el response de
/// <c>POST /api/reservas/{reservaId}/hotels</c> y <c>PUT</c> a usuarios sin
/// <see cref="Permissions.CobranzasSeeCost"/>. La mascara ya existia en
/// <c>GET</c> de detalle (ReservaService.ApplyCostMaskingAsync); este pin
/// extiende la cobertura a las mutaciones.
///
/// Cubre:
///  - Vendedor (sin cobranzas.see_cost) crea hotel -> response NetCost = 0.
///  - Admin crea hotel -> response NetCost real.
///
/// Nota sobre la fixture compartida: el factory mantiene la BD InMemory
/// entre tests del mismo archivo, asi que cada test sembramos vendedor y
/// reserva con identifiers nuevos para evitar contaminacion.
/// </summary>
public class HotelBookingsControllerCostMaskingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HotelBookingsControllerCostMaskingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<SeedData> SeedAsync(string vendedorId, string vendedorEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorEmail,
                Email = vendedorEmail,
                FullName = "Vendedor Mask Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        async Task EnsurePermAsync(string perm)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = perm });
                await db.SaveChangesAsync();
            }
        }
        // Permisos minimos para llegar al endpoint y pasar ownership.
        // NO se otorga CobranzasSeeCost — ese es el punto del test.
        await EnsurePermAsync(Permissions.ReservasView);
        await EnsurePermAsync(Permissions.ReservasEdit);

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Reserva mask " + Guid.NewGuid().ToString("N")[..6],
            NumeroReserva = "F-MSK-" + Guid.NewGuid().ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            // Confirmed: no fuerza "Solicitado" ni exige status confirmado en el servicio nuevo.
            Status = EstadoReserva.Confirmed
        };
        var supplier = new Supplier
        {
            PublicId = Guid.NewGuid(),
            Name = "Hotel Supplier Mask " + Guid.NewGuid().ToString("N")[..6]
        };
        db.Reservas.Add(reserva);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new SeedData(
            VendedorId: vendedorId,
            ReservaPublicId: reserva.PublicId,
            SupplierPublicId: supplier.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    private static CreateHotelRequest BuildCreateRequest(Guid supplierPublicId)
    {
        return new CreateHotelRequest(
            SupplierId: supplierPublicId.ToString(),
            HotelName: "Hotel Test",
            StarRating: 4,
            City: "Bariloche",
            Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10),
            CheckOut: DateTime.UtcNow.Date.AddDays(13),
            RoomType: "Doble",
            MealPlan: "Desayuno",
            Adults: 2,
            Children: 0,
            Rooms: 1,
            ConfirmationNumber: null,
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Notes: null
        );
    }

    [Fact]
    public async Task POST_Hotel_VendedorSinSeeCost_ReceivesNetCostMaskedToZero()
    {
        var vendedorId = "vend-mask-post-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var resp = await client.PostAsJsonAsync(
            $"/api/reservas/{seed.ReservaPublicId}/hotels",
            BuildCreateRequest(seed.SupplierPublicId));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<HotelBookingDto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(dto);
        // El precio de venta NO se enmascara — el vendedor lo necesita.
        Assert.Equal(800m, dto!.SalePrice);
        // El costo del proveedor SI se enmascara para usuarios sin cobranzas.see_cost.
        Assert.Equal(0m, dto.NetCost);
    }

    [Fact]
    public async Task POST_Hotel_Admin_ReceivesRealNetCost()
    {
        // Admin = factory default sin headers.
        var vendedorId = "vend-mask-admin-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        // Sin SetVendedorHeaders -> default Admin (bypass).

        var resp = await client.PostAsJsonAsync(
            $"/api/reservas/{seed.ReservaPublicId}/hotels",
            BuildCreateRequest(seed.SupplierPublicId));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<HotelBookingDto>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(dto);
        Assert.Equal(800m, dto!.SalePrice);
        // Admin bypass: ve el costo real del proveedor.
        Assert.Equal(500m, dto.NetCost);
    }

    private sealed record SeedData(
        string VendedorId,
        Guid ReservaPublicId,
        Guid SupplierPublicId);
}
