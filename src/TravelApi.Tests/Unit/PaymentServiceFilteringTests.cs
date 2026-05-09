using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (FIX 5): PaymentService filtra por owner cuando el user NO tiene
/// cobranzas.view_all. Cubre listings + summary + worklist + history.
/// </summary>
public class PaymentServiceFilteringTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceFilteringTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
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
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private PaymentService BuildService(AppDbContext context, IHttpContextAccessor accessor, IUserPermissionResolver resolver)
        => new(context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object,
               NullLogger<PaymentService>.Instance, resolver, accessor);

    private static async Task SeedAsync(AppDbContext context)
    {
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-PAY-0001",
                Name = "Reserva mia",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-A",
                TotalSale = 1000m,
                TotalPaid = 100m,
                Balance = 900m,
                StartDate = DateTime.UtcNow.AddDays(3)
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-PAY-0002",
                Name = "Reserva ajena",
                Status = EstadoReserva.Confirmed,
                ResponsibleUserId = "vendedor-B",
                TotalSale = 2000m,
                TotalPaid = 500m,
                Balance = 1500m,
                StartDate = DateTime.UtcNow.AddDays(4)
            });
        var startMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Payments.AddRange(
            new Payment
            {
                Id = 1, ReservaId = 1, Amount = 100m, Method = "Transfer", Status = "Paid",
                EntryType = PaymentEntryTypes.Payment, PaidAt = startMonth.AddDays(2)
            },
            new Payment
            {
                Id = 2, ReservaId = 2, Amount = 500m, Method = "Transfer", Status = "Paid",
                EntryType = PaymentEntryTypes.Payment, PaidAt = startMonth.AddDays(2)
            });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPayments_VendedorWithoutCobranzasViewAll_OnlyReturnsOwnPayments()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.CobranzasView);

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetAllPaymentsAsync(new PaymentsListQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(100m, page.Items.First().Amount);
    }

    [Fact]
    public async Task GetCollectionsSummary_VendedorWithoutCobranzasViewAll_TotalsScopedToOwn()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.CobranzasView);

        var service = BuildService(context, accessor, resolver);
        var summary = await service.GetCollectionsSummaryAsync(CancellationToken.None);

        // Solo el balance pendiente de mi reserva (900) y mi cobro del mes (100).
        Assert.Equal(900m, summary.PendingAmount);
        Assert.Equal(100m, summary.CollectedThisMonth);
    }

    [Fact]
    public async Task GetCollectionsWorklist_VendedorWithoutViewAll_OnlyReturnsOwnReservas()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.CobranzasView);

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetCollectionsWorklistAsync(new CollectionWorklistQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal("F-PAY-0001", page.Items.First().NumeroReserva);
    }

    [Fact]
    public async Task GetPayments_AdminBypass_ReturnsAll()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetAllPaymentsAsync(new PaymentsListQuery(), CancellationToken.None);

        Assert.Equal(2, page.Items.Count());
    }

    [Fact]
    public async Task GetPayments_ColaboradorWithCobranzasViewAll_ReturnsAll()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CobranzasView, Permissions.CobranzasViewAll);

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetAllPaymentsAsync(new PaymentsListQuery(), CancellationToken.None);

        Assert.Equal(2, page.Items.Count());
    }
}
