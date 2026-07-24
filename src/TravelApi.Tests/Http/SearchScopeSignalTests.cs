using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// FIX #39 (Tanda 3 del barrido de PROD, 2026-07-23): <c>GET /api/search</c> recortaba resultados por
/// permisos (owner scope) pero devolvia una lista vacia INDISTINGUIBLE de "no hay resultados" — un
/// usuario sin <c>reservas.view_all</c> no tenia forma de saber si el buscador realmente no encontro
/// nada, o si encontro algo ajeno que no le mostro. Estos tests pinean el contrato nuevo:
///  - un usuario CON <c>reservas.view_all</c> encuentra una reserva AJENA por su numero;
///  - un usuario SIN <c>reservas.view_all</c> solo ve la propia, y el response trae
///    <c>scope.reservasScopedToOwn = true</c> (señal estructurada, P-8/T-13 — el front NO tiene que
///    adivinar a partir de una lista corta).
/// </summary>
public class SearchScopeSignalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SearchScopeSignalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Crea un rol + usuario Identity real con los permisos indicados (RolePermissions), porque
    /// <c>IUserPermissionResolver</c> se resuelve contra la DB, no contra los claims de test (ver el
    /// XML-doc de <c>TestAuthHandler</c>).
    /// </summary>
    private async Task<string> SeedUserWithPermissionsAsync(string userId, string roleName, params string[] permissions)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync(roleName))
            await roleMgr.CreateAsync(new IdentityRole(roleName));

        foreach (var permission in permissions)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == roleName && rp.Permission == permission))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = roleName, Permission = permission });
            }
        }
        await db.SaveChangesAsync();

        if (await userMgr.FindByIdAsync(userId) is null)
        {
            var user = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@t.local",
                Email = userId + "@t.local",
                FullName = "Usuario " + userId,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, roleName);
        }

        return userId;
    }

    private async Task<Reserva> SeedReservaAsync(string numeroReserva, string? responsibleUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Reserva " + numeroReserva,
            NumeroReserva = numeroReserva,
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = responsibleUserId,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();
        return reserva;
    }

    private HttpClient CreateClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        // Sin rol "Admin": el bypass de admin NO debe activarse para estos tests — lo que se mide es
        // el recorte por PERMISOS (resuelto contra RolePermissions), no el bypass de rol.
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "SinAdmin");
        return client;
    }

    [Fact]
    public async Task User_with_view_all_finds_a_reserva_owned_by_someone_else()
    {
        const string numero = "F-1055-BUSCADOR";
        await SeedReservaAsync(numero, responsibleUserId: "vendedor-ajeno");
        var userId = await SeedUserWithPermissionsAsync(
            "buscador-view-all", "BuscadorConVerTodo", Permissions.ReservasView, Permissions.ReservasViewAll);

        var client = CreateClient(userId);
        var response = await client.GetAsync("/api/search?query=1055");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsResponse>();
        Assert.NotNull(results);
        Assert.Contains(results!.Reservas, r => r.NumeroReserva == numero);
        Assert.False(results.Scope.ReservasScopedToOwn);
    }

    [Fact]
    public async Task User_without_view_all_only_sees_own_reserva_and_gets_scoped_signal()
    {
        const string numeroPropia = "F-1055-PROPIA";
        const string numeroAjena = "F-1055-AJENA";
        var userId = await SeedUserWithPermissionsAsync(
            "buscador-mine", "BuscadorPropio", Permissions.ReservasView);
        await SeedReservaAsync(numeroPropia, responsibleUserId: userId);
        await SeedReservaAsync(numeroAjena, responsibleUserId: "otro-vendedor");

        var client = CreateClient(userId);
        var response = await client.GetAsync("/api/search?query=1055");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsResponse>();
        Assert.NotNull(results);
        Assert.Contains(results!.Reservas, r => r.NumeroReserva == numeroPropia);
        Assert.DoesNotContain(results.Reservas, r => r.NumeroReserva == numeroAjena);
        Assert.True(results.Scope.ReservasScopedToOwn);
    }

    [Fact]
    public async Task User_without_clientes_view_gets_customers_hidden_signal()
    {
        var userId = await SeedUserWithPermissionsAsync(
            "buscador-sin-clientes", "BuscadorSinClientes", Permissions.ReservasView, Permissions.ReservasViewAll);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Customers.Add(new Customer { FullName = "Cliente Buscador 1055", IsActive = true });
            await db.SaveChangesAsync();
        }

        var client = CreateClient(userId);
        var response = await client.GetAsync("/api/search?query=1055");
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<SearchResultsResponse>();
        Assert.NotNull(results);
        Assert.Empty(results!.Customers);
        Assert.True(results.Scope.CustomersHidden);
    }
}
