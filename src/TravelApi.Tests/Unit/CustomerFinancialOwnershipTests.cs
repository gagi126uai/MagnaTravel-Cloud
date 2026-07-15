using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class CustomerFinancialOwnershipTests
{
    [Fact]
    public async Task Seller_without_view_all_only_sees_financial_rows_from_owned_reservas()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new AppDbContext(options);

        var customer = new Customer { FullName = "Cliente compartido", IsActive = true };
        var mine = new Reserva
        {
            NumeroReserva = "R-MIA", Name = "Mia", Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "seller-a", Payer = customer
        };
        var theirs = new Reserva
        {
            NumeroReserva = "R-AJENA", Name = "Ajena", Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "seller-b", Payer = customer
        };
        context.AddRange(customer, mine, theirs);
        context.Invoices.AddRange(
            InvoiceFor(mine, 100m, 1),
            InvoiceFor(theirs, 900m, 2));
        await context.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "seller-a") }, "Test"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasView };
        resolver.Setup(x => x.GetPermissionsAsync("seller-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(permissions);

        var service = new CustomerService(
            context,
            new FinancePositionService(context),
            permissionResolver: resolver.Object,
            httpContextAccessor: accessor);

        var statement = await service.GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);
        var overview = await service.GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);
        var list = await service.GetCustomersAsync(new CustomerListQuery(), CancellationToken.None);
        var reservas = await service.GetCustomerAccountReservasAsync(customer.Id, new PagedQuery(), CancellationToken.None);
        var invoices = await service.GetCustomerAccountInvoicesAsync(customer.Id, new PagedQuery(), CancellationToken.None);

        Assert.Equal(100m, Assert.Single(statement.Currencies).ClosingBalance);
        Assert.Equal(100m, Assert.Single(Assert.Single(list.Items).BalancesByCurrency).Amount);
        Assert.Equal(1, overview.Summary.ReservaCount);
        Assert.Equal(1, overview.Summary.InvoiceCount);
        Assert.Equal(mine.PublicId, Assert.Single(reservas.Items).PublicId);
        Assert.Equal(mine.PublicId, Assert.Single(invoices.Items).ReservaPublicId);
    }

    private static Invoice InvoiceFor(Reserva reserva, decimal amount, int number) => new()
    {
        Reserva = reserva,
        TipoComprobante = 11,
        PuntoDeVenta = 1,
        NumeroComprobante = number,
        Resultado = "A",
        ImporteTotal = amount,
        MonId = "PES",
        IssuedAt = new DateTime(2026, 1, number, 0, 0, 0, DateTimeKind.Utc)
    };
}
