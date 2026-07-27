using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hotfix 2026-07-25: el buscador global (Ctrl+K) tiraba 500 en PROD para CUALQUIER consulta.
/// Causa: <c>p.Status.ToString()</c> dentro del Where de Payments — Payment.Status ya ES string,
/// el ToString() era un no-op que Npgsql no puede traducir ("Translation of method 'object.ToString'
/// failed"). El proveedor InMemory ejecuta la expresion como C# directo y la tolera, por eso la
/// suite unit dio verde con el bug vivo (misma trampa que el hallazgo #47 de la Tanda 5).
///
/// <para>Este test es la red REAL: ejecuta <see cref="SearchService.SearchAsync"/> contra Postgres,
/// lo que obliga a EF a TRADUCIR las tres consultas (customers, reservas, payments) a SQL. No hace
/// falta sembrar datos: si alguna expresion no es traducible, ToListAsync explota aunque las tablas
/// esten vacias — que es exactamente como se rompio PROD.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SearchServiceSqlTranslationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public SearchServiceSqlTranslationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SearchAsync_ComoAdmin_TraduceLasTresConsultasASqlSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        // Un user Admin bypassea todos los recortes de permisos, asi que las TRES ramas
        // (customers, reservas y payments) se compilan y ejecutan contra Postgres.
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-integration-test"),
                    new Claim(ClaimTypes.Role, "Admin"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var service = new SearchService(ctx, permissionResolver: null, httpContextAccessor: accessor);

        // Si alguna de las tres consultas tiene una expresion no traducible, esto tira
        // InvalidOperationException aca mismo (el bug de PROD). Con tablas vacias el
        // resultado esperado es simplemente "sin resultados", nunca una excepcion.
        var result = await service.SearchAsync("prueba", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Customers);
        Assert.Empty(result.Reservas);
        Assert.Empty(result.Payments);
    }

    /// <summary>
    /// H18 (barrido E2E 2026-07-25, decision firmada de Gaston): el buscador global ahora TAMBIEN busca
    /// por nombre de servicio. Este test siembra las 6 tablas de servicio (algunas CON el campo de
    /// nombre en null, ej. un vuelo sin <c>ProductName</c> cargado) para blindar DOS cosas a la vez:
    /// que la busqueda por "Palace" encuentra la reserva a traves del hotel, y que los <c>.Any(...)</c>
    /// con checks de null de las otras 5 tablas TRADUCEN a SQL sin explotar (la misma red que el
    /// hotfix del buscador, commit 48b15347 — un <c>.Any()</c> mal armado tambien podria romper en
    /// Postgres aunque InMemory lo tolere).
    /// </summary>
    [Fact]
    public async Task SearchAsync_BuscaPorNombreDeHotel_DevuelveLaReservaContenedora()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Hotelera Search SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-SEARCH-{Guid.NewGuid():N}"[..14],
            Name = "Reserva con hotel buscable",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace",
            City = "Bariloche",
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        // Un vuelo SIN ProductName cargado (null): ejercita la rama "v.ProductName != null" del Where
        // contra Postgres real, para blindar que el null-check es traducible.
        ctx.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "NN",
            DepartureTime = DateTime.UtcNow,
        });
        // Una asistencia SIN PlanType cargado (null): misma razon, para la rama de Asistencia.
        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-integration-test"),
                    new Claim(ClaimTypes.Role, "Admin"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var service = new SearchService(ctx, permissionResolver: null, httpContextAccessor: accessor);

        var result = await service.SearchAsync("Palace", CancellationToken.None);

        var match = Assert.Single(result.Reservas);
        Assert.Equal(reserva.PublicId, match.PublicId);
    }

    /// <summary>
    /// Reviews pendientes (2026-07-27): el caso de PERMISOS del test de arriba
    /// (<see cref="SearchAsync_BuscaPorNombreDeHotel_DevuelveLaReservaContenedora"/>) solo corre como
    /// Admin (bypass total). Falta la red contra Postgres real de un VENDEDOR sin
    /// <c>reservas.view_all</c> buscando el hotel de una reserva AJENA: debe traducir igual a SQL (nada
    /// nuevo ahi, ya cubierto por el test de arriba) pero el resultado tiene que venir VACIO. Lo
    /// importante de correrlo contra Postgres real (y no solo InMemory) es confirmar que el AND del
    /// owner-filter se compone bien con el OR de los 6 <c>.Any()</c> de servicio dentro del mismo Where
    /// (SearchService.cs:96-114) al traducir a SQL real, no solo en el motor tolerante de InMemory.
    /// </summary>
    [Fact]
    public async Task SearchAsync_ComoVendedorSinViewAll_BuscandoHotelDeReservaAjena_NoLaEncuentra()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Hotelera Search SA" };
        ctx.Suppliers.Add(supplier);
        var reservaAjena = new Reserva
        {
            NumeroReserva = $"F-SEARCH-{Guid.NewGuid():N}"[..14],
            Name = "Reserva ajena con hotel buscable",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-B",
        };
        ctx.Reservas.Add(reservaAjena);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaAjena.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace Ajeno",
            City = "Bariloche",
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        await ctx.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "vendedor-A"),
                    new Claim(ClaimTypes.Role, "Vendedor"),
                },
                authenticationType: "Test")),
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        // Sin reservas.view_all: el resolver devuelve un conjunto de permisos que NO lo incluye.
        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permisos = new HashSet<string> { Permissions.ClientesView, Permissions.CobranzasView };
        resolverMock.Setup(r => r.GetPermissionsAsync("vendedor-A", It.IsAny<CancellationToken>()))
            .ReturnsAsync(permisos);

        var service = new SearchService(ctx, resolverMock.Object, accessor);

        var result = await service.SearchAsync("Hotel Palace Ajeno", CancellationToken.None);

        Assert.Empty(result.Reservas);
    }
}
