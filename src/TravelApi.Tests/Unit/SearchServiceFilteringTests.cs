using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (FIX 3): SearchService filtra reservas/payments segun permisos.
///  - Vendedor sin reservas.view_all -> solo ve sus reservas en el resultado.
///  - Vendedor sin cobranzas.view_all -> solo ve sus payments.
///  - Vendedor sin clientes.view -> no devuelve customers.
///  - Admin (rol) -> ve todo (bypass).
/// </summary>
public class SearchServiceFilteringTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SearchServiceFilteringTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    private static async Task SeedAsync(AppDbContext context)
    {
        context.Customers.Add(new Customer { Id = 1, FullName = "Maria Buscable", Email = "maria@example.com" });
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-MIA-0001",
                Name = "Reserva mia maria",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-A"
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-AJE-0001",
                Name = "Reserva ajena maria",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-B"
            });
        context.Payments.AddRange(
            new Payment
            {
                Id = 1,
                ReservaId = 1,
                Amount = 100m,
                Method = "Transfer",
                Status = "Paid",
                EntryType = PaymentEntryTypes.Payment,
                PaidAt = DateTime.UtcNow
            },
            new Payment
            {
                Id = 2,
                ReservaId = 2,
                Amount = 200m,
                Method = "Transfer",
                Status = "Paid",
                EntryType = PaymentEntryTypes.Payment,
                PaidAt = DateTime.UtcNow
            });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_VendedorWithoutViewAll_OnlyReturnsOwnReservas()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        // sin reservas.view_all, con clientes.view + cobranzas.view base.
        var resolver = BuildResolver("vendedor-A", Permissions.ClientesView, Permissions.CobranzasView);

        var service = new SearchService(context, resolver, accessor);
        var result = await service.SearchAsync("reserva", CancellationToken.None);

        // Solo aparece la reserva mia.
        Assert.Single(result.Reservas);
        Assert.Equal("F-MIA-0001", result.Reservas[0].NumeroReserva);
    }

    [Fact]
    public async Task Search_VendedorWithoutCobranzasViewAll_OnlyReturnsOwnPayments()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.CobranzasView, Permissions.ClientesView);

        var service = new SearchService(context, resolver, accessor);
        var result = await service.SearchAsync("transfer", CancellationToken.None);

        // Solo aparece el payment mio (reserva 1).
        Assert.Single(result.Payments);
        Assert.Equal(100m, result.Payments[0].Amount);
    }

    [Fact]
    public async Task Search_VendedorWithoutClientesView_DoesNotReturnCustomers()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        // sin clientes.view -> customers vacio.
        var resolver = BuildResolver("vendedor-A");

        var service = new SearchService(context, resolver, accessor);
        var result = await service.SearchAsync("maria", CancellationToken.None);

        Assert.Empty(result.Customers);
    }

    [Fact]
    public async Task Search_Admin_ReturnsAllResults()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");

        var service = new SearchService(context, resolver, accessor);
        var result = await service.SearchAsync("reserva", CancellationToken.None);

        Assert.Equal(2, result.Reservas.Count);
    }

    [Fact]
    public async Task Search_VendedorWithoutCobranzasView_DoesNotReturnPayments()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        // sin cobranzas.view base -> payments vacio.
        var resolver = BuildResolver("vendedor-A", Permissions.ClientesView);

        var service = new SearchService(context, resolver, accessor);
        var result = await service.SearchAsync("transfer", CancellationToken.None);

        Assert.Empty(result.Payments);
    }
}
