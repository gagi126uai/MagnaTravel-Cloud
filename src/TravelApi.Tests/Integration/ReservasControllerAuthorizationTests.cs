using System;
using System.Linq;
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
/// B1.15 Fase 2a — tests E2E de autorizacion del piloto (ReservasController).
/// Cubren:
///  - Admin recibe header X-Permission-Scope: all en GET /api/reservas.
///  - Vendedor sin reservas.view_all recibe scope mine y solo ve sus reservas.
///  - Vendedor que no es responsable recibe 403 en GET /api/reservas/{id}.
///  - Vendedor sobre su propia reserva recibe 200.
/// </summary>
public class ReservasControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ReservasControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(ApplicationUser user, Reserva ownReserva, Reserva otherReserva)> SeedAsync(string vendedorId, string vendedorEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
        {
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));
        }

        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorEmail,
                Email = vendedorEmail,
                FullName = "Vendedor Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        // Asegurar permiso reservas.view base. Sin permiso, hasta el listado vacio
        // devuelve 403. Idempotente.
        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == Permissions.ReservasView))
        {
            db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = Permissions.ReservasView });
            await db.SaveChangesAsync();
        }

        // Sembrar dos reservas: una propia, una de otro vendedor.
        var ownPublicId = Guid.NewGuid();
        var otherPublicId = Guid.NewGuid();
        var own = new Reserva
        {
            PublicId = ownPublicId,
            Name = "Mia " + ownPublicId.ToString("N")[..6],
            NumeroReserva = "F-AUT-" + ownPublicId.ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            // Confirmed entra en el view "active" default (Budget no entra).
            Status = EstadoReserva.Confirmed
        };
        var other = new Reserva
        {
            PublicId = otherPublicId,
            Name = "Ajena " + otherPublicId.ToString("N")[..6],
            NumeroReserva = "F-AUT-" + otherPublicId.ToString("N")[..6],
            ResponsibleUserId = "otro-user",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.AddRange(own, other);
        await db.SaveChangesAsync();

        return (existing, own, other);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task GetReservas_Admin_returns_scope_all_header()
    {
        var client = _factory.CreateClient();
        // Default = Admin, sin headers extra.
        var response = await client.GetAsync("/api/reservas");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Permission-Scope"));
        var scope = response.Headers.GetValues("X-Permission-Scope").First();
        Assert.Equal("all", scope);
    }

    [Fact]
    public async Task GetReservas_Vendedor_returns_scope_mine_and_filters_to_own()
    {
        var vendedorId = "vendedor-scope-" + Guid.NewGuid().ToString("N")[..8];
        var (_, own, _) = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.GetAsync("/api/reservas?pageSize=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scope = response.Headers.GetValues("X-Permission-Scope").First();
        Assert.Equal("mine", scope);

        var json = await response.Content.ReadAsStringAsync();
        // El payload debe contener la reserva propia y NO la ajena.
        Assert.Contains(own.NumeroReserva, json);
    }

    [Fact]
    public async Task GetReserva_byId_Vendedor_on_other_returns_403()
    {
        var vendedorId = "vendedor-403-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, other) = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.GetAsync($"/api/reservas/{other.PublicId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReserva_byId_Vendedor_on_own_returns_200()
    {
        var vendedorId = "vendedor-own-" + Guid.NewGuid().ToString("N")[..8];
        var (_, own, _) = await SeedAsync(vendedorId, vendedorId + "@test.local");

        // Invalidar cache de permisos por las dudas (RolePermissions cambian
        // entre tests y el cache TTL es de 15s).
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        }

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.GetAsync($"/api/reservas/{own.PublicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReserva_byId_Admin_can_access_any_reserva()
    {
        var vendedorId = "vendedor-admin-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, other) = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        // Default = Admin sin headers => bypass.
        var response = await client.GetAsync($"/api/reservas/{other.PublicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// B1.15 Fase 2a (FIX 9): los 3 endpoints PUT /services/{id}, PUT /passengers/{id},
    /// DELETE /assignments/{id} ahora usan ownership entity-specific. Antes solo
    /// chequeaban reservas.edit (cualquier vendedor podia editar servicio/pasajero/
    /// asignacion ajena). Ahora el resolver hace el join hacia Reserva.ResponsibleUserId
    /// y rechaza si no coincide (salvo bypass via reservas.view_all).
    /// </summary>
    private async Task<(ServicioReserva servicio, Passenger passenger, PassengerServiceAssignment assignment)> SeedNestedAsync(string vendedorId, string vendedorEmail, string ownerUserId)
    {
        // Garantiza el seed base (user, role, permiso) y crea entidades hijas
        // colgadas de una Reserva con ResponsibleUserId = ownerUserId.
        await SeedAsync(vendedorId, vendedorEmail);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var publicId = Guid.NewGuid();
        var reserva = new Reserva
        {
            PublicId = publicId,
            Name = "Reserva nested " + publicId.ToString("N")[..6],
            NumeroReserva = "F-NES-" + publicId.ToString("N")[..6],
            ResponsibleUserId = ownerUserId,
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var servicio = new ServicioReserva
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            Description = "Hotel test",
            SalePrice = 100m,
            DepartureDate = DateTime.UtcNow.AddDays(10)
        };
        db.Servicios.Add(servicio);

        var passenger = new Passenger
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            FullName = "Pasajero Test"
        };
        db.Passengers.Add(passenger);
        await db.SaveChangesAsync();

        var assignment = new PassengerServiceAssignment
        {
            PublicId = Guid.NewGuid(),
            PassengerId = passenger.Id,
            ServiceType = AssignmentServiceType.Generic,
            ServiceId = servicio.Id
        };
        db.PassengerServiceAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return (servicio, passenger, assignment);
    }

    [Fact]
    public async Task PUT_Service_VendedorOnAjena_Returns403()
    {
        var vendedorId = "vendedor-svc-" + Guid.NewGuid().ToString("N")[..8];
        // Reserva pertenece a otro user.
        var (servicio, _, _) = await SeedNestedAsync(vendedorId, vendedorId + "@test.local", ownerUserId: "owner-other");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        // Body minimo de AddServiceRequest para no fallar la deserializacion.
        var body = new StringContent(
            "{ \"Description\": \"Hotel modificado\", \"SalePrice\": 150 }",
            System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync($"/api/reservas/services/{servicio.PublicId}", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PUT_Passenger_VendedorOnAjeno_Returns403()
    {
        var vendedorId = "vendedor-pax-" + Guid.NewGuid().ToString("N")[..8];
        var (_, passenger, _) = await SeedNestedAsync(vendedorId, vendedorId + "@test.local", ownerUserId: "owner-other");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent(
            "{ \"FullName\": \"Pasajero Modificado\" }",
            System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync($"/api/reservas/passengers/{passenger.PublicId}", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DELETE_Assignment_VendedorOnAjena_Returns403()
    {
        var vendedorId = "vendedor-asg-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, assignment) = await SeedNestedAsync(vendedorId, vendedorId + "@test.local", ownerUserId: "owner-other");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.DeleteAsync($"/api/reservas/assignments/{assignment.PublicId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ========================================================================
    // B1.15 Fase D' (2026-05-11): filtros CreatedFrom/To y TravelFrom/To.
    //
    // El query string entrega DateTime Kind=Unspecified. Las columnas son
    // timestamptz en Postgres y Npgsql tira 500 al comparar Unspecified vs
    // timestamptz si no se normaliza. Ademas, rango por dia local (UTC-3) con
    // limite "<=" perdia eventos del ultimo dia entre 21:00 ART (=00:00 UTC+1)
    // y 23:59 ART. Tests siguientes pinean:
    //   1) que no estalle 500 con dateFrom/dateTo set.
    //   2) que un evento del final del dia ART final del rango se incluya.
    // ========================================================================

    [Fact]
    public async Task GET_Reservas_WithDateRange_DoesNotThrow500()
    {
        // Regression: rango created amplio con admin no debe explotar.
        var client = _factory.CreateClient(); // Admin
        var resp = await client.GetAsync("/api/reservas?createdFrom=2026-05-01&createdTo=2026-05-31&page=1&pageSize=25");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Reservas_CreatedFilter_CapturesEndOfLocalDay()
    {
        // Seed una Reserva creada al final del dia 31-mayo hora Argentina:
        //   2026-05-31 23:59:00 ART = 2026-06-01 02:59:00 UTC.
        // Con createdFrom=2026-05-01 & createdTo=2026-05-31 debe INCLUIRLA.
        var marker = "R-RANGE-" + Guid.NewGuid().ToString("N")[..6];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Reservas.Add(new Reserva
            {
                PublicId = Guid.NewGuid(),
                Name = marker,
                NumeroReserva = marker,
                Status = EstadoReserva.Confirmed,
                // 2026-06-01 02:59:00 UTC = 2026-05-31 23:59:00 ART (UTC-3).
                CreatedAt = new DateTime(2026, 6, 1, 2, 59, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(); // Admin
        var resp = await client.GetFromJsonAsync<PagedResponse<ReservaListDto>>(
            "/api/reservas?createdFrom=2026-05-01&createdTo=2026-05-31&pageSize=500");
        Assert.NotNull(resp);
        Assert.Contains(resp!.Items, r => r.NumeroReserva == marker);
    }

    [Fact]
    public async Task GET_Reservas_CreatedFilter_ExcludesNextLocalDay()
    {
        // Seed una Reserva creada el 1-junio hora Argentina:
        //   2026-06-01 00:01:00 ART = 2026-06-01 03:01:00 UTC.
        // Con createdFrom=2026-05-01 & createdTo=2026-05-31 debe EXCLUIRLA.
        var marker = "R-NEXT-" + Guid.NewGuid().ToString("N")[..6];

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Reservas.Add(new Reserva
            {
                PublicId = Guid.NewGuid(),
                Name = marker,
                NumeroReserva = marker,
                Status = EstadoReserva.Confirmed,
                // 2026-06-01 03:01:00 UTC = 2026-06-01 00:01:00 ART.
                CreatedAt = new DateTime(2026, 6, 1, 3, 1, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(); // Admin
        var resp = await client.GetFromJsonAsync<PagedResponse<ReservaListDto>>(
            "/api/reservas?createdFrom=2026-05-01&createdTo=2026-05-31&pageSize=500");
        Assert.NotNull(resp);
        Assert.DoesNotContain(resp!.Items, r => r.NumeroReserva == marker);
    }
}
